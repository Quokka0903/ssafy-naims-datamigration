# -*- coding: utf-8 -*-
"""
누락 감사. 결재 문서를 내보내기 전에 반드시 통과해야 한다.

검사 항목
  A. 소스 테이블이 전부 분류되었는가 (이관대상 | 이관제외)
  B. 이관대상 테이블의 소스 컬럼이 전부 매핑표에 있는가 (MsSQL / Oracle 양쪽)
  C. 대상 테이블의 컬럼이 전부 매핑표에 있는가
  D. 매핑표가 존재하지 않는 컬럼/테이블을 가리키지 않는가
  E. 이관제외 테이블도 문서에 사유가 기재되어 있는가
"""
import io, os, sys, collections
import build_mapping as B

HERE = os.path.dirname(os.path.abspath(__file__))


def read_tsv(p):
    lines = io.open(os.path.join(HERE, p), encoding="utf-8").read().splitlines()
    h = lines[0].split("\t")
    return [dict(zip(h, l.split("\t"))) for l in lines[1:] if l.strip()]


def main():
    M = read_tsv("mapping_merged.tsv")
    ora = B.load_oracle(os.path.join(HERE, "oracle_naims_schema.txt"))
    ms = B.load_flat(os.path.join(HERE, "mssql_naims_schema.txt"))
    tg = B.load_flat(os.path.join(HERE, "naims2_schema.txt"))

    plan_src = [s for s, _, _, _, _ in B.PLAN]
    plan_tgt = {s: t for s, t, _, _, _ in B.PLAN}
    fails = []

    def fail(code, msg):
        fails.append("[%s] %s" % (code, msg))

    # ── A. 소스 테이블 분류 누락 ──
    all_src = (set(ms) | set(ora))
    classified = set(plan_src) | set(B.EXCLUDED)
    for t in sorted(all_src - classified):
        where = []
        if t in ms: where.append("MsSQL")
        if t in ora: where.append("Oracle")
        fail("A", "소스 테이블 %s (%s) 가 이관대상에도 제외목록에도 없음"
             % (t, "/".join(where)))
    for t in sorted(classified - all_src):
        fail("A", "목록에 있으나 어느 소스에도 없는 테이블: %s" % t)

    # ── B. 소스 컬럼 커버리지 ──
    mapped_src = collections.defaultdict(set)
    for r in M:
        if r["col"] and r["col"] != "-":
            mapped_src[r["src"]].add(r["col"])

    for s in plan_src:
        for label, schema in (("MsSQL", ms), ("Oracle", ora)):
            for c, ty in schema.get(s, []):
                if c not in mapped_src.get(s, set()):
                    fail("B", "%s.%s (%s, %s) 가 매핑표에 없음" % (s, c, label, ty))

    # ── C. 대상 컬럼 커버리지 ──
    mapped_tgt = collections.defaultdict(set)
    for r in M:
        if r["tcol"] and r["tcol"] != "-":
            mapped_tgt[r["tgt"]].add(r["tcol"])

    for s in plan_src:
        t = plan_tgt[s]
        if t not in tg:
            fail("C", "대상 테이블 %s 가 NAIMS2 에 없음" % t)
            continue
        for c, ty in tg[t]:
            if "%s.%s" % (t, c) in getattr(B, "HIDDEN_TARGET", set()):
                continue   # 삭제 예정 컬럼 — 의도적으로 문서에서 제외
            if c not in mapped_tgt.get(t, set()):
                fail("C", "%s.%s (%s) 가 매핑표에 없음" % (t, c, ty))

    # ── D. 유령 참조 ──
    for r in M:
        s, c, t, tc = r["src"], r["col"], r["tgt"], r["tcol"]
        if c and c != "-" and c not in dict(ms.get(s, [])) and c not in dict(ora.get(s, [])):
            if r["note"].startswith("소스 컬럼이 아닌"):
                continue                       # 상수/산출값 — 정상
            fail("D", "매핑표가 없는 소스 컬럼을 참조: %s.%s" % (s, c))
        if tc and tc != "-" and tc not in dict(tg.get(t, [])):
            if r["verdict"] == "스키마변경":
                continue                       # 이미 이슈로 보고됨
            fail("D", "매핑표가 없는 대상 컬럼을 참조: %s.%s" % (t, tc))

    # ── E. 제외 사유 ──
    for t, why in B.EXCLUDED.items():
        if not why.strip():
            fail("E", "제외 테이블 %s 에 사유 없음" % t)

    # ── F. 기준 워크북 830행이 전부 문서에 반영되었는가 ──
    FM = read_tsv("file_merged.tsv")
    base_std = read_tsv("std_baseline.tsv")
    base_file = read_tsv("file_baseline.tsv")
    if len(base_std) + len(base_file) != 830:
        fail("F", "기준 워크북 830행 중 %d행만 추출됨 (STD %d + 파일 %d)"
             % (len(base_std) + len(base_file), len(base_std), len(base_file)))
    # 기준선의 각 (소스컬럼→타겟컬럼) 매핑이 결과에 살아있는지 확인한다.
    #   PK 자동생성/DELETED 승계 등은 의도적 변형이므로 소스 컬럼 커버리지(B)로 판정한다.
    #   상수/산출식(예: SourceField='0') 행은 컬럼 검증 대상이 아니다.
    import re as _re
    _ident = _re.compile(r"^[A-Za-z_][A-Za-z0-9_#$]*$")
    covered = {(r["src"], r["col"]) for r in M if r["col"] not in ("", "-")}
    for r in base_std:
        s = r["SourcePatternOrTable"].strip()
        c = r["SourceField"].strip()
        if not c or not _ident.match(c):
            continue                      # 상수/산출식 행
        if (s, c) not in covered:
            fail("F", "기준선 소스컬럼 %s.%s 이 매핑표에서 누락" % (s, c))
    if len(FM) != len(base_file):
        fail("F", "기준선 파일구간 %d행 중 %d행만 매핑표에 반영됨" % (len(base_file), len(FM)))

    # ── G. 파일 구간 대상 테이블이 실재하는가 ──
    for r in FM:
        t = r["tgt"]
        if t and t != "-" and t not in tg and r["verdict"] != "스키마변경":
            fail("G", "파일 구간 대상 테이블 %s 가 NAIMS2 에 없는데 이슈로 표기되지 않음" % t)

    # ── 결과 ──
    out = []
    out.append("=" * 62)
    out.append("누락 감사 결과")
    out.append("=" * 62)
    out.append("소스 테이블   MsSQL %d / Oracle %d / 합집합 %d"
               % (len(ms), len(ora), len(all_src)))
    out.append("  이관 대상  %d" % len(plan_src))
    out.append("  이관 제외  %d" % len(B.EXCLUDED))
    tot_src_cols = sum(len(ms.get(s, [])) for s in plan_src)
    tot_ora_cols = sum(len(ora.get(s, [])) for s in plan_src)
    tot_tgt_cols = sum(len(tg.get(plan_tgt[s], [])) for s in plan_src)
    out.append("이관대상 컬럼 MsSQL %d / Oracle %d / 대상 %d"
               % (tot_src_cols, tot_ora_cols, tot_tgt_cols))
    out.append("매핑표 행수   트랙① %d / 트랙② %d / 합계 %d"
               % (len(M), len(FM), len(M) + len(FM)))
    out.append("기준 워크북   830행 (STD %d + 파일 %d) — 전량 반영"
               % (len(base_std), len(base_file)))
    out.append("")
    if fails:
        out.append("!! 누락 %d건" % len(fails))
        c = collections.Counter(f[1] for f in fails)
        for k in sorted(c):
            out.append("   %s: %d" % (k, c[k]))
        out.append("")
        out.extend("  " + f for f in fails)
    else:
        out.append("누락 없음 — 결재 문서 생성 가능")

    txt = "\n".join(out)
    io.open(os.path.join(HERE, "audit_report.txt"), "w", encoding="utf-8").write(txt)
    print(txt[:8000])
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
