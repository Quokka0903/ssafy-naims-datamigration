# -*- coding: utf-8 -*-
"""
Oracle 소스 NULL 허용 컬럼 ↔ NAIMS2 대응 컬럼 대비표 PDF.

목적: 레거시 Oracle(NAIMS)은 PK 를 빼면 사실상 전 컬럼이 NULL 허용인데,
      타겟 NAIMS2 는 NOT NULL 컬럼이 많다. 그 조합이 곧 "이관 시 값을 반드시
      만들어 넣어야 하는 컬럼" 이므로 테이블별로 뽑아 결재/검토용으로 전시한다.

입력 (모두 오프라인 산출물):
  · oracle_naims_schema.txt : Oracle DDL 원본(260420_NAIMS_SQL.pdf 25p 스캔본) 전사
                              — NOT NULL 표기가 있는 컬럼만 NOT NULL, PK 는 암묵적 NOT NULL
  · naims2_schema.txt       : 타겟 NAIMS2 스키마 덤프(NOT NULL / DEFAULT 포함)
  · mapping_merged.tsv      : 컬럼 매핑 확정본(src/col → tgt/tcol, rule/verdict/note)

사용: python gen_nullable_pdf.py
"""
import os
import re
import collections

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph,
                                Spacer, Table, TableStyle, PageBreak)

import pdf_common

HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.dirname(HERE)
TAG = "260813"
OUT = os.path.join(DOCS, "NAIMS_Oracle_NULL허용_대비표_%s.pdf" % TAG)

pdfmetrics.registerFont(TTFont("KR", r"C:\Windows\Fonts\malgun.ttf"))
pdfmetrics.registerFont(TTFont("KRB", r"C:\Windows\Fonts\malgunbd.ttf"))

# 이관 제외 분류 — 이관기(DbToDbOptions.ExcludeCategories)와 같은 기준을 쓴다.
#   배포관리 : DB_DIST_GROUP / DB_DIST_LOG / DIST_LIST / DIST_TO
#   변경이력 : CCF2CD_LOG / CD_BACKUP_LOG / CD_CHANGE_LOG
# 사용자 확정(2026-08-13): 배포·이력(LOG) 계열은 이관하지 않으므로 이 문서에서도 제외한다.
EXCLUDED_CATEGORIES = {"배포관리", "변경이력"}

NAVY = colors.HexColor("#1F3864")
RED = colors.HexColor("#C00000")
AMBER = colors.HexColor("#B26B00")
GREY = colors.HexColor("#666666")

S_TITLE = ParagraphStyle("t", fontName="KRB", fontSize=19, leading=26, textColor=NAVY, spaceAfter=4)
S_SUB = ParagraphStyle("s", fontName="KR", fontSize=10.5, leading=15, textColor=colors.HexColor("#555555"))
S_H1 = ParagraphStyle("h1", fontName="KRB", fontSize=13.5, leading=19, textColor=NAVY,
                      spaceBefore=12, spaceAfter=6)
S_BODY = ParagraphStyle("b", fontName="KR", fontSize=9.5, leading=14)
S_CELL = ParagraphStyle("c", fontName="KR", fontSize=7.6, leading=10)
S_CELLB = ParagraphStyle("cb", fontName="KRB", fontSize=7.6, leading=10)
S_HEAD = ParagraphStyle("hd", fontName="KRB", fontSize=8, leading=11, textColor=colors.white)


# ────────────────────────── 스키마 파싱 ──────────────────────────

def parse_oracle(path):
    """Oracle 전사본 → {table: [(col, type, not_null)]}. PK 컬럼은 NOT NULL 로 본다."""
    tables = collections.OrderedDict()
    cur = None
    pk_of = {}
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.match(r"^### TABLE:\s+(\S+)", line)
        if m:
            name = m.group(1)
            # 스캔본이 페이지로 잘린 표는 연속 블록으로 전사돼 있고, 표 이름이 안 보이면
            # "<UNKNOWN - name not visible>" 로 적혀 있다. 이때 새 표를 만들면 컬럼이 유실되므로
            # 직전 표에 계속 이어 붙인다(예: CLASSES 62컬럼이 7컬럼으로 잘리던 문제).
            if name.startswith("<") or ("CONTINUED FROM PREVIOUS PAGE" in line and cur):
                if cur is None:
                    cur = name
                    tables.setdefault(cur, [])
                continue
            cur = name
            tables.setdefault(cur, [])
            continue
        if cur is None:
            continue
        m = re.match(r"^PK:\s*(\S+)", line)
        if m:
            pk_of[cur] = m.group(1).upper()
            continue
        # 연속 블록의 PK 안내문: (PK constraint on page 07 is "CLS_PK" PRIMARY KEY ("CLASS_ID"))
        m = re.search(r'PRIMARY KEY\s*\(\s*"?([A-Z0-9_]+)"?\s*\)', line, re.I)
        if m:
            pk_of.setdefault(cur, m.group(1).upper())
            continue
        if line.startswith("#") or line.startswith("(") or not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        col, typ = parts[0].strip(), parts[1].strip()
        if not col or " " in col:
            continue
        nn = "NOT NULL" in typ.upper()
        typ = re.sub(r"\s*NOT NULL\s*$", "", typ, flags=re.I).strip()
        tables[cur].append([col.upper(), typ, nn])

    for t, pk in pk_of.items():
        for c in tables.get(t, []):
            if c[0] == pk:
                c[2] = True
    return tables, pk_of


def parse_naims2(path):
    """NAIMS2 덤프 → {table: {col: (type, not_null, has_default)}}."""
    tables = {}
    cur = None
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.match(r"^### TABLE:\s+(\S+)", line)
        if m:
            cur = m.group(1)
            tables.setdefault(cur, {})
            continue
        if cur is None or not line.strip() or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        col, rest = parts[0].strip(), parts[1].strip()
        if not col or " " in col:
            continue
        nn = "NOT NULL" in rest.upper()
        dflt = "DEFAULT" in rest.upper()
        tables[cur][col.upper()] = (rest, nn, dflt)
    return tables


def parse_mapping(path):
    """mapping_merged.tsv → {(src_table, src_col): row}, {src_table: tgt_table}, {src_table: category}."""
    rows = {}
    tgt_of = {}
    cat_of = {}
    with open(path, encoding="utf-8") as f:
        head = f.readline().rstrip("\n").split("\t")
        idx = {h: i for i, h in enumerate(head)}
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) < len(head):
                p += [""] * (len(head) - len(p))
            src = p[idx["src"]].strip().upper()
            col = p[idx["col"]].strip().upper()
            if not src or not col:
                continue
            rows[(src, col)] = {k: p[idx[k]].strip() for k in idx}
            t = p[idx["tgt"]].strip()
            if t and src not in tgt_of:
                tgt_of[src] = t
            c = p[idx["cat"]].strip()
            if c and src not in cat_of:
                cat_of[src] = c
    return rows, tgt_of, cat_of


# ────────────────────────── 문서 ──────────────────────────

def cell(text, style=S_CELL, color=None):
    st = style
    if color is not None:
        st = ParagraphStyle("x", parent=style, textColor=color)
    return Paragraph(str(text or "").replace("&", "&amp;").replace("<", "&lt;"), st)


def build():
    ora, pk_of = parse_oracle(os.path.join(HERE, "oracle_naims_schema.txt"))
    n2 = parse_naims2(os.path.join(HERE, "naims2_schema.txt"))
    maps, tgt_of, cat_of = parse_mapping(os.path.join(HERE, "mapping_merged.tsv"))

    doc = BaseDocTemplate(
        OUT, pagesize=landscape(A4),
        leftMargin=15 * mm, rightMargin=15 * mm,
        topMargin=22 * mm, bottomMargin=16 * mm,
        title="NAIMS Oracle NULL 허용 컬럼 ↔ NAIMS2 대응표", author="NAIMS Migration")
    doc.header_title = "NAIMS Migration Mapping"
    doc.header_subtitle = "Oracle NULL 허용 컬럼 ↔ NAIMS2 대응표"
    frame = Frame(doc.leftMargin, doc.bottomMargin,
                  doc.width, doc.height, id="f")
    doc.addPageTemplates([PageTemplate(
        id="p", frames=[frame],
        onPage=lambda c, d: (pdf_common.draw_header(c, d), pdf_common.draw_footer_logos(c, d)))])

    story = []
    story.append(Paragraph("Oracle NULL 허용 컬럼 ↔ NAIMS2 대응표", S_TITLE))
    story.append(Paragraph(
        "레거시 Oracle(NAIMS) 소스의 컬럼별 NULL 허용 여부와, 이관 대상 NAIMS2 의 대응 컬럼·제약을 "
        "테이블별로 대비한 표다. 근거: Oracle DDL 원본(260420_NAIMS_SQL.pdf 25p) 전사본, "
        "NAIMS2 스키마 덤프, 컬럼 매핑 확정본(mapping_merged.tsv).", S_SUB))
    story.append(Spacer(1, 4))
    story.append(Paragraph(
        "※ 배포관리(DIST 계열)와 변경이력(LOG 계열)은 이관 대상이 아니므로 이 문서에서 제외했다.", S_SUB))
    story.append(Spacer(1, 8))

    # ── 요약: 위험 컬럼 집계 ──
    risk_rows = []
    per_table = collections.OrderedDict()
    excluded_seen = []
    for t, cols in ora.items():
        if t not in tgt_of:
            continue
        if cat_of.get(t, "") in EXCLUDED_CATEGORIES:
            excluded_seen.append((t, cat_of.get(t, "")))
            continue
        risky = []
        for col, typ, nn in cols:
            m = maps.get((t, col))
            if not m:
                continue
            tt, tc = m.get("tgt", ""), m.get("tcol", "")
            if not tt or not tc:
                continue
            info = n2.get(tt, {}).get(tc.upper())
            if not info:
                continue
            ttyp, tnn, tdef = info
            if (not nn) and tnn and not tdef and "IDENTITY" not in ttyp.upper():
                risky.append((col, typ, tt, tc, ttyp, m.get("rule", ""), m.get("note", "")))
        per_table[t] = (cols, risky)
        if risky:
            risk_rows.append((t, tgt_of[t], len(cols), len(risky)))

    story.append(Paragraph("1. 요약 — 값 생성이 필요한 컬럼", S_H1))
    story.append(Paragraph(
        "Oracle 에서는 NULL 이 허용되지만 NAIMS2 에서는 NOT NULL 이고 DEFAULT 도 없는 컬럼이다. "
        "소스가 비어 있으면 이관이 실패하므로, 이관기가 값을 만들어 넣어야 한다.", S_BODY))
    story.append(Spacer(1, 5))

    data = [[cell("소스 테이블", S_HEAD), cell("타겟 테이블", S_HEAD),
             cell("소스 컬럼수", S_HEAD), cell("값 생성 필요", S_HEAD)]]
    for t, tt, nall, nrisk in sorted(risk_rows, key=lambda x: -x[3]):
        data.append([cell(t, S_CELLB), cell(tt), cell(nall), cell(nrisk, S_CELLB, RED)])
    if len(data) == 1:
        data.append([cell("해당 없음"), cell(""), cell(""), cell("")])
    tbl = Table(data, colWidths=[60 * mm, 75 * mm, 30 * mm, 30 * mm], repeatRows=1)
    tbl.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#BBBBBB")),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F4F6FA")]),
        ("LEFTPADDING", (0, 0), (-1, -1), 4), ("RIGHTPADDING", (0, 0), (-1, -1), 4),
    ]))
    story.append(tbl)

    # ── 테이블별 상세 ──
    story.append(PageBreak())
    story.append(Paragraph("2. 테이블별 상세", S_H1))
    story.append(Paragraph(
        "NULL: Oracle 에서 NULL 허용(Y) / NOT NULL(N).  "
        "타겟 제약이 <b>NOT NULL</b> 이고 DEFAULT 도 없는데 소스가 Y 면 위험 칸에 <b>값 필요</b> 로 표시하고 "
        "행 배경을 연분홍으로 칠했다.", S_BODY))

    widths = [34 * mm, 30 * mm, 11 * mm, 44 * mm, 34 * mm, 16 * mm, 43 * mm, 55 * mm]
    for t, (cols, risky) in per_table.items():
        story.append(Spacer(1, 8))
        story.append(Paragraph("%s  →  %s" % (t, tgt_of.get(t, "(미확정)")), S_H1))

        data = [[cell(x, S_HEAD) for x in
                 ("Oracle 컬럼", "Oracle 타입", "NULL", "→ NAIMS2 컬럼", "NAIMS2 타입",
                  "이관 시", "변환 규칙", "비고")]]
        styles = []
        for i, (col, typ, nn) in enumerate(cols, start=1):
            m = maps.get((t, col), {})
            tt, tc = m.get("tgt", ""), m.get("tcol", "")
            info = n2.get(tt, {}).get(tc.upper()) if tt and tc else None
            ttyp = info[0] if info else ("(미확정)" if not tc else tc and "(스키마 없음)")
            tnn = info[1] if info else False
            tdef = info[2] if info else False
            danger = (not nn) and tnn and not tdef and info and "IDENTITY" not in info[0].upper()

            data.append([
                cell(col, S_CELLB),
                cell(typ),
                cell("Y" if not nn else "N", S_CELL, AMBER if not nn else GREY),
                cell(tc or "(대응 없음)"),
                cell(ttyp or ""),
                cell("값 필요" if danger else "", S_CELLB, RED),
                cell(m.get("rule", "")),
                cell(m.get("note", "")),
            ])
            if danger:
                styles.append(("BACKGROUND", (0, i), (-1, i), colors.HexColor("#FDF2F2")))

        tbl = Table(data, colWidths=widths, repeatRows=1)
        tbl.setStyle(TableStyle([
            ("BACKGROUND", (0, 0), (-1, 0), NAVY),
            ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#CCCCCC")),
            ("VALIGN", (0, 0), (-1, -1), "TOP"),
            ("LEFTPADDING", (0, 0), (-1, -1), 3), ("RIGHTPADDING", (0, 0), (-1, -1), 3),
            ("TOPPADDING", (0, 0), (-1, -1), 2), ("BOTTOMPADDING", (0, 0), (-1, -1), 2),
        ] + styles))
        story.append(tbl)

    doc.build(story)
    return OUT, per_table, risk_rows


if __name__ == "__main__":
    out, per_table, risk = build()
    total_risk = sum(r[3] for r in risk)
    print("생성:", out)
    print("테이블 %d개 / 값 생성 필요 컬럼 %d개" % (len(per_table), total_risk))
