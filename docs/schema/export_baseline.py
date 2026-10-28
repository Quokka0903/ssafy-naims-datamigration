# -*- coding: utf-8 -*-
"""
기존 확정 매핑(권위본)에서 DB→DB(STD_*) 구간을 추출한다.

원본: NAIMS_마이그레이션_정리_최종준비본.xlsx
  03_마이그레이션_매핑  구분='STD' 행  → std_baseline.tsv
  09_결정규칙                          → decisions.tsv
  10_최종확정필요목록  구분='STD' 행    → pending.tsv

이 워크북이 우선한다. 신규 문서는 이것을 덮어쓰지 않고 '현재 스키마와의 정합성'만 덧붙인다.
"""
import io, os, openpyxl

SRC = r"F:\JKYi\2026\260109 DB임플란트\NAIMS_마이그레이션_정리_최종준비본.xlsx"
HERE = os.path.dirname(os.path.abspath(__file__))


def write_tsv(path, header, rows):
    with io.open(path, "w", encoding="utf-8") as f:
        f.write("\t".join(header) + "\n")
        for r in rows:
            f.write("\t".join((c or "").replace("\t", " ").replace("\n", " ") for c in r) + "\n")
    print("%-22s %d rows" % (os.path.basename(path), len(rows)))


def cells(ws):
    for row in ws.iter_rows(values_only=True):
        yield ["" if v is None else str(v).strip() for v in row]


def main():
    wb = openpyxl.load_workbook(SRC, data_only=True, read_only=True)

    # ── 03_마이그레이션_매핑 : STD 구간 ──
    ws = wb["03_마이그레이션_매핑"]
    it = cells(ws)
    hdr = next(it)
    allrows = [r for r in it if any(r)]
    write_tsv(os.path.join(HERE, "std_baseline.tsv"), hdr,
              [r for r in allrows if r[0] == "STD"])
    # SD(단주기 .sd) / NB(협대역 .nb) — 파일 기반 이관 구간
    write_tsv(os.path.join(HERE, "file_baseline.tsv"), hdr,
              [r for r in allrows if r[0] in ("SD", "NB")])
    other = sorted({r[0] for r in allrows} - {"STD", "SD", "NB"})
    if other:
        print("  !! 분류되지 않은 구분: %s" % ", ".join(other))

    # ── 09_결정규칙 ──
    ws = wb["09_결정규칙"]
    it = cells(ws)
    hdr = next(it)
    rows = [r for r in it if any(r)]
    write_tsv(os.path.join(HERE, "decisions.tsv"), hdr, rows)

    # ── 10_최종확정필요목록 : STD 구간 ──
    ws = wb["10_최종확정필요목록"]
    it = cells(ws)
    hdr = next(it)
    rows = [r for r in it if len(r) > 1 and r[1] == "STD"]
    write_tsv(os.path.join(HERE, "pending.tsv"), hdr, rows)

    wb.close()


if __name__ == "__main__":
    main()
