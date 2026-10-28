# -*- coding: utf-8 -*-
"""
파일 기반 이관 구간(.sd / .nb → RAW_* / PREC_*)의 정합성 검증.

DB→DB 구간(merge_mapping.py)과 달리 소스가 파일이므로 소스측 스키마 검증은 불가하다.
대상측(NAIMS2)만 실측 스키마와 대조한다.

입력  file_baseline.tsv   (SD 231 + NB 117 = 348행)
출력  file_merged.tsv, file_report.txt
"""
import io, os, collections
import build_mapping as B

HERE = os.path.dirname(os.path.abspath(__file__))

# 파일 종류별 구분
KIND = {"SD": "단주기(.sd)", "NB": "협대역(.nb)"}


def read_tsv(p):
    lines = io.open(os.path.join(HERE, p), encoding="utf-8").read().splitlines()
    h = lines[0].split("\t")
    return [dict(zip(h, l.split("\t"))) for l in lines[1:] if l.strip()]


def main():
    base = read_tsv("file_baseline.tsv")
    tg = B.load_flat(os.path.join(HERE, "naims2_schema.txt"))
    tg_t = {t: dict(c) for t, c in tg.items()}

    out, issues = [], []
    for r in base:
        kind = r["구분"].strip()
        ent = r["SourceEntity"].strip()
        pat = r["SourcePatternOrTable"].strip()
        obj = r["ParsedObject"].strip()
        fld = r["SourceField"].strip()
        rule = r["TransformRule"].strip()
        tgt = r["TargetTable"].strip()
        tcol = r["TargetColumn"].strip()
        status = r["Status"].strip()
        order = r.get("LoadOrder", "").strip()
        note = r.get("Notes", "").strip()
        mapid = r.get("MapID", "").strip()

        if tcol in ("-", ""):
            tcol = ""
        if tgt in ("-", ""):
            tgt = ""

        ttyp = tg_t.get(tgt, {}).get(tcol, "") if (tgt and tcol) else ""

        verdict, detail = "확정", []
        if status == "Exclude":
            verdict = "제외"
        elif status == "NeedConfirm":
            verdict = "미결"

        if tgt and tgt not in tg_t:
            verdict = "스키마변경"
            detail.append("대상 테이블 %s 가 현재 NAIMS2 에 없음" % tgt)
        elif tgt and tcol and not ttyp:
            verdict = "스키마변경"
            detail.append("대상 %s 에 컬럼 %s 없음 — 확정 이후 스키마 변경" % (tgt, tcol))
        elif not tgt and status != "Exclude":
            verdict = "미결"
            detail.append("대상 테이블 미지정")

        if verdict in ("스키마변경",):
            issues.append((verdict, ent, fld, tgt, tcol, "; ".join(detail)))

        # COMMENT 계열은 이관 시 말머리 [NAIMS-I] 삽입 (모든 트랙 공통). REF_COMMENT 제외.
        if (B.COMMENT_PREFIX and tcol.upper().endswith("COMMENT")
                and tcol.upper() != "REF_COMMENT"):
            detail.append("말머리 %s 삽입" % B.COMMENT_PREFIX)

        out.append(dict(kind=KIND.get(kind, kind), mapid=mapid, order=order,
                        ent=ent, pat=pat, obj=obj, fld=fld,
                        tgt=tgt or "-", tcol=tcol or "-", ttyp=ttyp or "-",
                        rule=rule or "-", status=status, verdict=verdict,
                        note="; ".join(detail) or note, review=""))

    cols = ["kind", "mapid", "order", "ent", "pat", "obj", "fld",
            "tgt", "tcol", "ttyp", "rule", "status", "verdict", "note", "review"]
    with io.open(os.path.join(HERE, "file_merged.tsv"), "w", encoding="utf-8") as f:
        f.write("\t".join(cols) + "\n")
        for r in out:
            f.write("\t".join(str(r[c]).replace("\t", " ") for c in cols) + "\n")

    vc = collections.Counter(r["verdict"] for r in out)
    kc = collections.Counter(r["kind"] for r in out)
    tc = collections.Counter(r["tgt"] for r in out)
    with io.open(os.path.join(HERE, "file_report.txt"), "w", encoding="utf-8") as f:
        f.write("총 %d행\n" % len(out))
        for k, v in kc.most_common():
            f.write("  %-12s %d\n" % (k, v))
        f.write("\n[판정]\n")
        for k, v in vc.most_common():
            f.write("  %-10s %d\n" % (k, v))
        f.write("\n[대상 테이블 %d개]\n" % len(tc))
        for k, v in sorted(tc.items()):
            mark = "" if k in tg or k == "-" else "  << 현재 NAIMS2 에 없음"
            f.write("  %-28s %d%s\n" % (k, v, mark))
        f.write("\n[정합성 이슈 %d건]\n" % len(issues))
        for v, e, fl, t, tc2, d in issues:
            f.write("  %-10s %s / %s -> %s.%s : %s\n" % (v, e, fl, t, tc2, d))
    print(io.open(os.path.join(HERE, "file_report.txt"), encoding="utf-8").read()[:5000])


if __name__ == "__main__":
    main()
