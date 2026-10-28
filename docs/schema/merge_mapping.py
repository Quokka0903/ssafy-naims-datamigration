# -*- coding: utf-8 -*-
"""
확정 매핑(권위본) × 현재 실측 스키마 정합성 검증 + 공백 보완.

입력
  std_baseline.tsv        기존 확정 매핑 482행 (NAIMS_마이그레이션_정리_최종준비본.xlsx 03시트)
  mssql_naims_schema.txt  소스 MsSQL  실측 (192.168.111.39/NAIMS,   2026-07-22)
  oracle_naims_schema.txt 소스 Oracle 전사 (260420_NAIMS_SQL.pdf 25p)
  naims2_schema.txt       타겟        실측 (192.168.200.205/NAIMS2, 2026-07-22)

출력
  mapping_merged.tsv      통합 매핑표 (문서 생성기 입력)
  merge_report.txt        정합성 이슈 목록

판정(verdict)
  확정      기존 확정 + 현재 스키마와 일치
  스키마변경 기존 확정이지만 타겟 컬럼이 현재 스키마에 없음 → 재확정 필요
  소스없음  기존 확정이지만 소스 컬럼이 현재 스키마에 없음
  제외      기존에 Exclude 로 확정
  미결      기존 NeedConfirm / 미결목록
  신규제안  기존 매핑에 없어 규칙으로 제안 (담당자 승인 필요)
"""
import io, os, re, sys, collections
import build_mapping as B

HERE = os.path.dirname(os.path.abspath(__file__))

# 정상적인 컬럼 식별자. 이 형태가 아니면 상수/산출식으로 본다.
_IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_#$]*$")


def read_tsv(path):
    lines = io.open(path, encoding="utf-8").read().splitlines()
    hdr = lines[0].split("\t")
    return [dict(zip(hdr, l.split("\t"))) for l in lines[1:] if l.strip()]


def main():
    base = read_tsv(os.path.join(HERE, "std_baseline.tsv"))
    ora = B.load_oracle(os.path.join(HERE, "oracle_naims_schema.txt"))
    ms = B.load_flat(os.path.join(HERE, "mssql_naims_schema.txt"))
    tg = B.load_flat(os.path.join(HERE, "naims2_schema.txt"))

    ms_t = {t: dict(c) for t, c in ms.items()}
    ora_t = {t: dict(c) for t, c in ora.items()}
    tg_t = {t: dict(c) for t, c in tg.items()}

    plan_tgt = {s: t for s, t, _, _, _ in B.PLAN}
    plan_cat = {s: c for s, _, _, _, c in B.PLAN}
    plan_lvl = {s: l for s, _, _, l, _ in B.PLAN}
    plan_pk = {s: pk for s, _, pk, _, _ in B.PLAN}

    # 실제 소스 PK (스키마의 PRIMARY KEYS 섹션이 권위). PLAN 의 PK명은 부정확할 수 있다.
    ms_pk, _, _ = B.load_meta(os.path.join(HERE, "mssql_naims_schema.txt"))
    src_pk = {t: set(cols) for t, cols in ms_pk.items()}

    def is_real_pk(tbl, col):
        return col and col in src_pk.get(tbl, set())

    # 타겟 스키마의 DEFAULT 값 (naims2_schema.txt: "col\ttype  DEFAULT (expr)")
    tg_default = {}
    for tbl, cols in tg.items():
        for c, ty in cols:
            m = re.search(r"\s+DEFAULT\s+(.+)$", ty)
            if m:
                tg_default[(tbl, c)] = m.group(1).strip()

    def clean_type(ty):
        return re.sub(r"\s+DEFAULT\s+.+$", "", ty).strip()

    out, issues = [], []
    covered_src = collections.defaultdict(set)   # 소스테이블 → 매핑된 소스컬럼
    covered_tgt = collections.defaultdict(set)   # 타겟테이블 → 매핑된 타겟컬럼
    pk_field = {}                                 # 소스테이블 → PK 로 표기할 컬럼명

    def pick_pk(tbl, candidate):
        """테이블의 PK 표기 컬럼을 정한다. 실제 PK > 테이블명 어간 일치 > 첫 후보."""
        if tbl in pk_field:
            # 이미 있으면, 어간이 더 잘 맞는 쪽으로 갱신
            cur = pk_field[tbl]
            stem = B.normalize(tbl.replace("_", ""))
            def score(c):
                cn = B.normalize(c.replace("_ID", ""))
                return 2 if cn and (cn in stem or stem in cn) else (1 if c in src_pk.get(tbl, ()) else 0)
            if score(candidate) > score(cur):
                pk_field[tbl] = candidate
        else:
            pk_field[tbl] = candidate

    for r in base:
        src = r["SourcePatternOrTable"].strip()
        col = r["SourceField"].strip()
        tgt = r["TargetTable"].strip()
        tcol = r["TargetColumn"].strip()
        status = r["Status"].strip()
        rule = r["TransformRule"].strip()
        note = r.get("Notes", "").strip()
        mapid = r.get("MapID", "").strip()

        if tcol in ("-", ""):
            tcol = ""

        # ── 교정: 잘못된 baseline 타겟(테이블·컬럼)을 표준으로 덮어쓴다 (2026-07-24) ──
        #   target 이 "-"/"" 이면 소스전용(타겟 대응 없음)으로 교정한다.
        corr = B.CORRECTIONS.get((src, col))
        corr_srconly = False
        if corr:
            tgt = plan_tgt.get(src, tgt)
            if corr["target"] in ("-", ""):
                tcol = ""
                corr_srconly = True
            else:
                tcol = corr["target"]
            rule = corr["rule"]
            note = corr["note"]

        # SourceField 가 컬럼명이 아니라 상수/산출식인 경우가 있다.
        #   예) '0' (공통2: DELETED=0), '현재 시각', '(고정: ...)'
        # 이런 행은 소스 컬럼 검증 대상이 아니다.
        is_literal = bool(col) and not _IDENT.match(col)

        # ── 사용자 확정 교정 (2026-07-23) ──────────────────────────
        override = ""   # 이 행에 강제 판정을 부여하면 아래 정합성 로직을 건너뛴다

        # 항목2) PK → RECORD_ID 자동생성.
        #   baseline 의 'Exclude + RECORD_ID 자동생성' 행은 그 자체를 버리고,
        #   테이블당 합성 PK 행 1개를 나중에 emit 한다. (baseline 이 FK 를 이 규칙으로
        #   잘못 표기한 경우가 있어 — 예: DIST_LIST.GROUP_ID — 그런 FK 는 여기서
        #   버려져 gap-fill 이 올바른 REF_*_ID 로 매핑한다.)
        if (status == "Exclude" and "RECORD_ID" in rule
                and "RECORD_ID" in tg_t.get(tgt, {})):
            pick_pk(src, col)
            continue
        # 실제 소스 PK 컬럼도 자동생성 후보로 기록 (아래에서 합성).
        if is_real_pk(src, col) and "RECORD_ID" in tg_t.get(tgt, {}):
            pick_pk(src, col)
            covered_tgt[tgt].add("RECORD_ID")
            continue

        # 항목3) DELETED: 상수 0 이 아니라 레거시 DELETED 값을 형 변환해 승계.
        #   신규/레거시 공통 의미 — 0=정상, 1=임시삭제.
        if tcol == "DELETED" and (is_literal or col == "0" or not col):
            if "DELETED" in ms_t.get(src, {}) or "DELETED" in ora_t.get(src, {}):
                col = "DELETED"
                is_literal = False
                rule = "형 변환"
                override = "확정"

        mtyp = ms_t.get(src, {}).get(col, "")
        otyp = ora_t.get(src, {}).get(col, "")
        ttyp = clean_type(tg_t.get(tgt, {}).get(tcol, "")) if tcol else ""
        tdef = tg_default.get((tgt, tcol))

        if col and not is_literal:
            covered_src[src].add(col)
        if tcol:
            covered_tgt[tgt].add(tcol)

        # ── 정합성 판정 ──
        verdict, detail = "", []
        if override:
            verdict = override
            if override == "자동생성":
                detail.append("레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번")
            elif tcol == "DELETED":
                detail.append("0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환)")
        elif status == "Exclude":
            verdict = "제외"
        elif status == "NeedConfirm":
            verdict = "미결"
        else:
            verdict = "확정"

        if override:
            pass
        elif is_literal:
            detail.append("소스 컬럼이 아닌 상수/산출값: %s" % col)
        elif col and not mtyp and not otyp:
            verdict = "소스없음"
            detail.append("소스 %s 에 컬럼 %s 없음" % (src, col))
        elif col and not mtyp:
            detail.append("MsSQL 소스에 없음 (Oracle 전용)")
        elif col and not otyp:
            detail.append("Oracle 소스에 없음 (MsSQL 전용)")

        if tcol and tgt in tg_t and not ttyp:
            verdict = "스키마변경"
            detail.append("확정본이 가리키는 대상 컬럼 %s 가 현재 %s 스키마에 없음 — 재확정 필요" % (tcol, tgt))
        elif tcol and tgt not in tg_t:
            verdict = "스키마변경"
            detail.append("대상 테이블 %s 가 현재 DB에 없음 — 재확정 필요" % tgt)

        # 타입 변환 주의
        if mtyp and ttyp:
            cn = B.convert_note(mtyp, ttyp, False)
            if cn:
                detail.append("MsSQL: " + cn)
        if otyp and ttyp:
            cn = B.convert_note(otyp, ttyp, True)
            if cn:
                detail.append("Oracle: " + cn)

        key = "%s.%s" % (src, col)
        if key in B.ENC_MSSQL and key in B.ENC_ORACLE:
            detail.append("양쪽 암호화 — 복호화 후 적재")
        elif key in B.ENC_MSSQL:
            detail.append("MsSQL만 암호화 / Oracle 평문")
        elif key in B.ENC_ORACLE:
            detail.append("Oracle만 암호화(RAW) — 방식 미확인")

        if override:                      # 교정 행은 강제 판정 유지
            verdict = override
        if corr and ttyp:                 # 교정 타겟이 실재하면 확정
            verdict = corr["verdict"]
            detail = [corr["note"]]
        if corr_srconly:                  # 소스전용 교정 — 타겟 대응 없음
            verdict = corr["verdict"]
            detail = [corr["note"]]
        if tdef and tcol not in ("", "-") and "기본값" not in "".join(detail):
            detail.append("타겟 기본값 %s" % tdef)

        if verdict in ("스키마변경", "소스없음"):
            issues.append((verdict, src, col, tgt, tcol, "; ".join(detail)))

        out.append(dict(
            mapid=mapid, cat=plan_cat.get(src, "STD"), level=plan_lvl.get(src, ""),
            src=src, col=col, mtyp=mtyp or "-", otyp=otyp or "-",
            tgt=tgt, tcol=tcol or "-", ttyp=ttyp or "-",
            rule=rule, status=status, verdict=verdict,
            note="; ".join([d for d in detail if d]) or note, review=""))

    # ── 테이블당 PK → RECORD_ID 자동생성 행 1개 합성 ──
    #   실제 PK > pick_pk 로 고른 컬럼 > PLAN PK 순으로 표기명을 정한다.
    out_by_table = collections.defaultdict(list)
    idx_by_table = {}
    for i, r in enumerate(out):
        out_by_table[(r["src"], r["tgt"])].append(i)
    for s, t, pk, lvl, cat in B.PLAN:
        if "RECORD_ID" not in tg_t.get(t, {}):
            continue
        real = sorted(src_pk.get(s, ()))
        # PK 표기명: 실제 PK > pick_pk > 소스 첫 _ID 컬럼 > PLAN PK
        first_id = next((c for c, _ in ms.get(s, []) if c.upper().endswith("_ID")), "")
        pkcol = (real[0] if real else pk_field.get(s)) or first_id or pk or "PK"
        covered_src[s].add(pkcol)
        covered_tgt[t].add("RECORD_ID")
        # 같은 PK 컬럼에 대한 기존 baseline 행이 있으면 제거(중복 방지)
        out = [rr for rr in out if not (rr["src"] == s and rr["col"] == pkcol)]
        # 표기: 이관 순서상 첫 컬럼으로 보이도록 맨 앞에 삽입
        row = dict(mapid="", cat=cat, level=lvl, src=s, col=pkcol,
                   mtyp=clean_type(ms_t.get(s, {}).get(pkcol, "")) or "-",
                   otyp=clean_type(ora_t.get(s, {}).get(pkcol, "")) or "-",
                   tgt=t, tcol="RECORD_ID",
                   ttyp=clean_type(tg_t.get(t, {}).get("RECORD_ID", "")) or "-",
                   rule="제외 (신규 RECORD_ID 자동생성)", status="",
                   verdict="자동생성",
                   note="레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번", review="")
        ins = out_by_table[(s, t)]
        (out.insert(ins[0], row) if ins else out.append(row))
        # 삽입으로 인덱스가 밀리므로 재계산
        out_by_table = collections.defaultdict(list)
        for i, rr in enumerate(out):
            out_by_table[(rr["src"], rr["tgt"])].append(i)

    # ── 공백 보완: 기존 매핑이 없는 소스 컬럼 ──
    for s, t, pk, lvl, cat in B.PLAN:
        mcols = ms.get(s, [])
        ocols = ora.get(s, [])
        odict = dict(ocols)
        allcols = [c for c, _ in mcols] + [c for c, _ in ocols if c not in dict(mcols)]
        tgt_names = [c for c, _ in tg.get(t, [])]
        used = set(covered_tgt.get(t, set()))

        for i, c in enumerate(allcols):
            if c in covered_src.get(s, set()):
                continue
            mtyp = dict(mcols).get(c, "")
            otyp = odict.get(c, "")

            if c in B.DROPPED:
                out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                                mtyp=mtyp or "-", otyp=otyp or "-", tgt=t, tcol="-", ttyp="-",
                                rule="폐기", status="Proposed", verdict="제외",
                                note=B.DROPPED[c], review=""))
                continue
            man = B.MANUAL.get((s, c))
            if man:
                tcol, rule, mnote = man
                if tcol is None:
                    out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                                    mtyp=mtyp or "-", otyp=otyp or "-", tgt=t, tcol="-", ttyp="-",
                                    rule="-", status="Proposed", verdict="미결",
                                    note=mnote, review=""))
                    continue
            else:
                tcol, rule = B.find_target(c, allcols, tgt_names, {x: x for x in tgt_names},
                                           used, i == 0)
                mnote = ""
            if not tcol:
                out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                                mtyp=mtyp or "-", otyp=otyp or "-", tgt=t, tcol="-", ttyp="-",
                                rule="-", status="Proposed", verdict="미결",
                                note="대응 컬럼 미발견 — 담당자 확인 필요", review=""))
                continue
            used.add(tcol)
            covered_tgt[t].add(tcol)
            ttyp = clean_type(dict(tg.get(t, [])).get(tcol, ""))

            # 항목2) RECORD_ID 로 이어진 경우: 실제 PK 만 자동생성, 그 외는 오매칭.
            if tcol == "RECORD_ID":
                if is_real_pk(s, c):
                    out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                                    mtyp=mtyp or "-", otyp=otyp or "-", tgt=t,
                                    tcol="RECORD_ID", ttyp=ttyp or "-",
                                    rule="제외 (신규 RECORD_ID 자동생성)", status="Proposed",
                                    verdict="자동생성",
                                    note="레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번",
                                    review=""))
                else:
                    # PK 가 아닌데 RECORD_ID 로 이어짐 — 명명 규칙 오매칭. 대상 재확인 필요.
                    used.discard("RECORD_ID")
                    covered_tgt[t].discard("RECORD_ID")
                    out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                                    mtyp=mtyp or "-", otyp=otyp or "-", tgt=t,
                                    tcol="-", ttyp="-", rule="-", status="Proposed",
                                    verdict="미결",
                                    note="자동 매칭이 RECORD_ID(PK) 로 이어졌으나 PK 아님 — 대상 컬럼 확인 필요",
                                    review=""))
                continue

            det = [mnote] if mnote else []
            if mtyp and ttyp:
                cn = B.convert_note(mtyp, ttyp, False)
                if cn: det.append("MsSQL: " + cn)
            if otyp and ttyp:
                cn = B.convert_note(otyp, ttyp, True)
                if cn: det.append("Oracle: " + cn)
            dv = tg_default.get((t, tcol))
            if dv: det.append("타겟 기본값 %s" % dv)
            # 정확한 대응(동일/별칭/파일명/수동)은 확정, 추정성(정규화/FK)만 신규제안.
            gv = "확정" if rule in ("동일", "별칭", "파일명", "수동") else "신규제안"
            out.append(dict(mapid="", cat=cat, level=lvl, src=s, col=c,
                            mtyp=mtyp or "-", otyp=otyp or "-", tgt=t, tcol=tcol, ttyp=ttyp or "-",
                            rule=rule, status="Proposed", verdict=gv,
                            note="; ".join(det) if det else "완전하게 대응" if rule == "동일" else "",
                            review=""))

    # ── 타겟에만 있는 컬럼 ──
    for s, t, pk, lvl, cat in B.PLAN:
        for tcol, ttyp_raw in tg.get(t, []):
            if tcol in covered_tgt.get(t, set()):
                continue
            if "%s.%s" % (t, tcol) in B.HIDDEN_TARGET:   # 삭제 예정 컬럼 — 문서에서 제외
                continue
            ttyp = clean_type(ttyp_raw)
            dv = tg_default.get((t, tcol)) or B.TARGET_FIXED_DEFAULT.get((t, tcol))
            note = B.TARGET_ONLY.get(tcol, "소스 대응 없음 — 기본값/NULL 또는 산출값")
            # 항목4) 기본값이 스크립트/지정으로 정의된 NOT NULL 컬럼은 그 기본값으로 채운다.
            if dv:
                note = "타겟 기본값 %s 로 채움" % dv
                vd = "확정"
            elif tcol in B.TARGET_ONLY:
                vd = "확정"
            else:
                vd = "미결" if "NOT NULL" in ttyp_raw else "신규제안"
            out.append(dict(mapid="", cat=cat, level=lvl, src=s, col="-",
                            mtyp="-", otyp="-", tgt=t, tcol=tcol, ttyp=ttyp,
                            rule="신규", status="Proposed", verdict=vd,
                            note=note, review=""))

    # ── 2026-07-23 2차 피드백: 타겟 컬럼별 규칙 덮어쓰기 ──
    for r in out:
        key = (r["tgt"], r["tcol"])
        cr = B.COLUMN_RULES.get(key)
        if cr:
            r["rule"] = cr["rule"]
            r["verdict"] = cr["verdict"]
            r["note"] = cr["note"]
            continue

        # 항목2) '동명 직접 대응' → '완전하게 대응' (동명+호환타입은 완전 대응)
        if r["note"] == "동명 직접 대응":
            r["note"] = "완전하게 대응"

        # 항목3) COMMENT 계열은 이관 시 말머리 [NAIMS-I] 삽입 (REF_COMMENT 제외 — 별도 규칙)
        if (r["tcol"] in ("COMMENT", "COMMENT1", "COMMENT2")
                and r["col"] not in ("", "-")):
            pfx = "말머리 %s 삽입" % B.COMMENT_PREFIX
            r["note"] = (r["note"] + "; " + pfx) if r["note"] and r["note"] != "-" else pfx
        # 항목1) STD_CLASSES 소스 대응 없는 신규 컬럼 → 임시 기본값 -1
        if (r["tgt"] == "STD_CLASSES" and r["col"] in ("", "-")
                and r["tcol"] not in B.CLASSES_FILL_EXCEPT and r["tcol"] not in ("", "-")
                and r["verdict"] not in ("자동생성",)):
            r["rule"] = "임시 기본값 -1"
            r["verdict"] = "확정"
            r["note"] = "소스 대응 없음 — 임시로 -1 (추후 확정)"

    # 삭제 예정(hidden) 타겟 컬럼 행은 문서에서 완전히 제외한다.
    if B.HIDDEN_TARGET:
        out = [r for r in out if "%s.%s" % (r["tgt"], r["tcol"]) not in B.HIDDEN_TARGET]

    # ── 표기 정리 (2026-07-24 사용자 지시) ──
    NO_TARGET = "(대응 없음)"
    for r in out:
        if r["verdict"] == "스키마변경":
            # 확정본이 가리키던 대상 컬럼이 현재 스키마에 없음 →
            # 대상 컬럼란에 '(대응 없음)', 비고에 조치 문구, 규칙 TBD.
            r["tcol"] = NO_TARGET
            r["ttyp"] = "-"
            r["note"] = "대응 컬럼 지정/생성 필요"
            r["rule"] = "TBD"
        elif r["verdict"] == "미결":
            r["rule"] = "TBD"

    # ── 테이블 내 정렬: 매핑되는 필드 → 매핑 안 되는 기존 필드 → 신규 필드 ──
    def _group(r):
        has_src = r["col"] not in ("", "-")
        valid_tgt = r["tcol"] not in ("", "-", NO_TARGET)
        if has_src and valid_tgt:  return 0   # 매핑되는 필드
        if has_src:                return 1   # 매핑 안 되는 기존 필드(소스전용·대응없음)
        return 2                              # 신규 필드(타겟전용)
    _tbl = {}
    for r in out:
        k = (r["src"], r["tgt"])
        _tbl.setdefault(k, len(_tbl))
    out.sort(key=lambda r: (_tbl[(r["src"], r["tgt"])], _group(r)))   # 안정 정렬

    cols = ["mapid", "cat", "level", "src", "col", "mtyp", "otyp",
            "tgt", "tcol", "ttyp", "rule", "status", "verdict", "note", "review"]
    with io.open(os.path.join(HERE, "mapping_merged.tsv"), "w", encoding="utf-8") as f:
        f.write("\t".join(cols) + "\n")
        for r in out:
            f.write("\t".join(str(r[c]).replace("\t", " ") for c in cols) + "\n")

    vc = collections.Counter(r["verdict"] for r in out)
    with io.open(os.path.join(HERE, "merge_report.txt"), "w", encoding="utf-8") as f:
        f.write("총 %d행\n" % len(out))
        for k, v in vc.most_common():
            f.write("  %-10s %d\n" % (k, v))
        f.write("\n[정합성 이슈 %d건]\n" % len(issues))
        for v, s, c, t, tc, d in issues:
            f.write("  %-10s %s.%s -> %s.%s : %s\n" % (v, s, c, t, tc, d))
    print(io.open(os.path.join(HERE, "merge_report.txt"), encoding="utf-8").read()[:4000])


if __name__ == "__main__":
    main()
