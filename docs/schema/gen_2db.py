# -*- coding: utf-8 -*-
"""
NAIMS-I(Oracle) → NAIMS-II(NAIMS2) 2-DB 컬럼 매핑 PDF.
중간의 MsSQL(NAIMS) 열을 빼고, Oracle 소스 → NAIMS2 대상만 나란히 놓는다.
STD_* 이관 대상 전용. 테이블마다 새 페이지.

출력  docs/NAIMS_2DB_OracleToNAIMS2_<날짜>.pdf
      docs/09_2DB_Oracle_NAIMS2.md
"""
import io, os, collections
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph,
                                Spacer, Table, TableStyle, PageBreak,
                                NextPageTemplate)
import build_mapping as B
import gen_docs as G
import pdf_common

pdfmetrics.registerFont(TTFont("KR", r"C:\Windows\Fonts\malgun.ttf"))
pdfmetrics.registerFont(TTFont("KRB", r"C:\Windows\Fonts\malgunbd.ttf"))

OUT = os.path.join(G.DOCS, "NAIMS_2DB_컬럼매핑_%s.pdf" % G.TAG)
NAVY = colors.HexColor("#1F3864")

S_TITLE = ParagraphStyle("t", fontName="KRB", fontSize=18, leading=24, textColor=NAVY, spaceAfter=4)
S_SUB = ParagraphStyle("s", fontName="KR", fontSize=9.5, leading=13.5, textColor=colors.HexColor("#555555"))
S_SUM_H = ParagraphStyle("sumh", fontName="KRB", fontSize=10.5, leading=14,
                         textColor=NAVY, spaceBefore=7, spaceAfter=3)
S_TBL = ParagraphStyle("tb", fontName="KRB", fontSize=11.5, leading=15, textColor=NAVY, spaceBefore=6, spaceAfter=3)
S_P = ParagraphStyle("p", fontName="KR", fontSize=8.5, leading=12, spaceAfter=3)
S_CELL = ParagraphStyle("c", fontName="KR", fontSize=7.6, leading=9.8)
S_COL = ParagraphStyle("col", fontName="KRB", fontSize=7.8, leading=10)
S_CELLB = ParagraphStyle("cb", fontName="KRB", fontSize=7.8, leading=10, textColor=colors.white, alignment=1)

VC_COLOR = {
    "확정": None, "자동생성": colors.HexColor("#EAF0FA"), "제외": colors.HexColor("#F2F2F2"),
    "미결": colors.HexColor("#FFF2CC"), "스키마변경": colors.HexColor("#FCE4D6"),
    "신규제안": colors.HexColor("#DDEBF7"),
}

CATEGORY_ORDER = ["기준정보", "수동신호", "능동소나", "순간소음", "스니펫",
                  "영상자료", "무장", "배포관리", "변경이력"]


def category_no(cat):
    return CATEGORY_ORDER.index(cat) + 1 if cat in CATEGORY_ORDER else 99


def P(t, st=S_CELL):
    return Paragraph(str(t).replace("&", "&amp;").replace("<", "&lt;"), st)


def col_cell(name, typ, absent):
    if not name or name == "-":
        return Paragraph("<font size=6.4 color='#999999'>%s</font>" % absent, S_CELL)
    if not typ or typ == "-":
        return Paragraph("<b>%s</b>" % name.replace("&", "&amp;"), S_COL)
    return Paragraph("<b>%s</b><br/><font size=6.2 color='#777777'>%s</font>"
                     % (name.replace("&", "&amp;"), typ.replace("&", "&amp;")), S_COL)


def make_deco(header_title):
    def deco(canvas, doc):
        canvas.saveState()
        pdf_common.draw_header(canvas, doc, header_title)  # 상단 러닝 헤더
        pdf_common.draw_footer_logos(canvas, doc)          # 좌 LIG / 우 syscore
        canvas.setFont("KR", 7.5)
        canvas.setFillColor(colors.HexColor("#888888"))
        canvas.drawCentredString(doc.pagesize[0] / 2, 5 * mm,
            "NAIMS-MIG-%s  ·  NAIMS-I(Oracle) → NAIMS-II 2DB 매핑  ·  - %d -" % (G.TAG, doc.page))
        canvas.setStrokeColor(colors.HexColor("#CCCCCC"))
        canvas.line(15 * mm, 13 * mm, doc.pagesize[0] - 15 * mm, 13 * mm)
        canvas.restoreState()
    return deco


def deco(canvas, doc):
    canvas.saveState()
    pdf_common.draw_header(canvas, doc, "NAIMS DB Migration Mapping Tables",
                           "NAIMS-I(Oracle) → NAIMS-II(NAIMS2)")
    pdf_common.draw_footer_logos(canvas, doc)   # 좌 LIG / 우 syscore
    canvas.setFont("KR", 7.5)
    canvas.setFillColor(colors.HexColor("#888888"))
    canvas.drawCentredString(doc.pagesize[0] / 2, 5 * mm,
        "NAIMS-MIG-%s  ·  NAIMS DB Migration Mapping Tables  ·  - %d -" % (G.TAG, doc.page))
    canvas.setStrokeColor(colors.HexColor("#CCCCCC"))
    canvas.line(15 * mm, 13 * mm, doc.pagesize[0] - 15 * mm, 13 * mm)
    canvas.restoreState()


def is_mssql_only(r):
    """소스 컬럼이 MsSQL 에만 있고 Oracle 에는 없는 행 (예: SELF_DB).
    NAIMS-I(Oracle)→NAIMS-II 2DB 문서에서는 표시하지 않는다. (항목4, 2026-07-23)"""
    return r["col"] not in ("", "-") and r["otyp"] == "-"


def rows_by_table():
    by = collections.OrderedDict()
    order = {s: i for i, (s, _, _, _, _) in enumerate(B.PLAN)}
    for r in G.M:
        if is_mssql_only(r):
            continue
        by.setdefault((r["src"], r["tgt"]), []).append(r)
    return sorted(by.items(), key=lambda kv: order.get(kv[0][0], 999))


def simple_table(header, rows, widths, rowcolors=None):
    data = [[P(h, S_CELLB) for h in header]] + [[P(c) for c in r] for r in rows]
    t = Table(data, colWidths=widths, repeatRows=1)
    st = [("BACKGROUND", (0, 0), (-1, 0), NAVY),
          ("GRID", (0, 0), (-1, -1), 0.3, colors.HexColor("#CFCFCF")),
          ("VALIGN", (0, 0), (-1, -1), "TOP"),
          ("TOPPADDING", (0, 0), (-1, -1), 2.5),
          ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
          ("LEFTPADDING", (0, 0), (-1, -1), 3),
          ("RIGHTPADDING", (0, 0), (-1, -1), 3)]
    if rowcolors:
        for i, color in enumerate(rowcolors, 1):
            if color:
                st.append(("BACKGROUND", (0, i), (-1, i), color))
    t.setStyle(TableStyle(st))
    return t


def intro_summary(row_groups, AW):
    rows = [r for _, table_rows in row_groups for r in table_rows]
    vc = collections.Counter(r["verdict"] for r in rows)
    review = vc.get("미결", 0) + vc.get("스키마변경", 0) + vc.get("신규제안", 0)

    by_cat = collections.OrderedDict()
    for (src, tgt), table_rows in row_groups:
        cat = next((c for s, _, _, _, c in B.PLAN if s == src), "")
        by_cat.setdefault(cat, dict(tables=0, rows=0, examples=[]))
        by_cat[cat]["tables"] += 1
        by_cat[cat]["rows"] += len(table_rows)
        if len(by_cat[cat]["examples"]) < 3:
            by_cat[cat]["examples"].append(src)

    els = []
    els.append(Paragraph("문서 요약", S_SUM_H))
    els.append(simple_table(["항목", "내용"], [
        ["문서 성격", "NAIMS-I(Oracle) 소스 컬럼을 NAIMS-II(NAIMS2) 대상 컬럼과 1:1로 대조한 2DB 매핑표"],
        ["이관 범위", "DB→DB 이관 대상 STD_* %d개 테이블, %d개 컬럼 매핑" % (len(row_groups), len(rows))],
        ["표기 기준", "MsSQL에만 존재하는 소스 컬럼은 제외하고 Oracle 기준 대응만 표시"],
        ["읽는 방법", "헤더는 대분류(예: 1. 기준정보), 본문 제목은 하위 항목(예: 1.2. CLASSES)으로 번호화"],
    ], [30 * mm, AW - 30 * mm]))

    els.append(Paragraph("판정 요약", S_SUM_H))
    verdict_order = ["확정", "자동생성", "제외", "미결", "스키마변경", "신규제안"]
    els.append(simple_table(["판정", "건수", "검토 의미"], [
        [k, vc.get(k, 0), {
            "확정": "현 스키마 기준 그대로 구현 가능",
            "자동생성": "대상 IDENTITY/기본값 등 타겟에서 생성",
            "제외": "이관하지 않음",
            "미결": "대응 규칙 확정 필요",
            "스키마변경": "확정 대상이 현재 스키마와 불일치",
            "신규제안": "규칙 기반 제안으로 승인 필요",
        }[k]] for k in verdict_order
    ] + [["검토 필요 합계", review, "미결 + 스키마변경 + 신규제안"]],
        [28 * mm, 18 * mm, AW - 46 * mm],
        [VC_COLOR.get(k) for k in verdict_order] + [colors.HexColor("#FFF2CC")]))

    els.append(Paragraph("분류별 구성", S_SUM_H))
    cat_rows = []
    for cat, meta in by_cat.items():
        no = category_no(cat)
        cat_rows.append(["%d. %s" % (no, cat), meta["tables"], meta["rows"],
                         ", ".join(meta["examples"])])
    els.append(simple_table(["분류", "테이블", "컬럼", "대표 테이블"], cat_rows,
                            [28 * mm, 18 * mm, 18 * mm, AW - 64 * mm]))
    return els


def table_block(src, tgt, rows, AW, sec_no, item_no):
    cat = next((c for s, _, _, _, c in B.PLAN if s == src), "")
    vc = collections.Counter(r["verdict"] for r in rows)
    badge = " · ".join("%s %d" % (k, v) for k, v in vc.most_common())
    els = [Paragraph("%d.%d. NAIMS-I  %s   →   NAIMS-II  %s" % (sec_no, item_no, src, tgt), S_TBL),
           Paragraph("%d개 컬럼 · %s" % (len(rows), badge), S_P)]

    header = ["① NAIMS-I (Oracle) 컬럼", "② NAIMS-II (NAIMS2) 대상 컬럼",
              "변환 규칙", "판정", "비고"]
    data = [[P(h, S_CELLB) for h in header]]
    fills = []
    for r in rows:
        ocell = col_cell(r["col"] if r["otyp"] != "-" else "", r["otyp"],
                         "(신규 — 소스 없음)" if r["col"] == "-" else "Oracle 없음(MsSQL 전용)")
        tcell = col_cell(r["tcol"], r["ttyp"], "— (대응 없음)")
        data.append([ocell, tcell, P(r["rule"] or "-"), P(r["verdict"]), P(r["note"] or "-")])
        fills.append(VC_COLOR.get(r["verdict"]))

    widths = [58 * mm, 62 * mm, 34 * mm, 16 * mm, AW - 170 * mm]
    t = Table(data, colWidths=widths, repeatRows=1)
    st = [("BACKGROUND", (0, 0), (-1, 0), NAVY),
          ("GRID", (0, 0), (-1, -1), 0.3, colors.HexColor("#CFCFCF")),
          ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
          ("TOPPADDING", (0, 0), (-1, -1), 2), ("BOTTOMPADDING", (0, 0), (-1, -1), 2),
          ("LEFTPADDING", (0, 0), (-1, -1), 3), ("RIGHTPADDING", (0, 0), (-1, -1), 3)]
    for i, f in enumerate(fills, 1):
        if f:
            st.append(("BACKGROUND", (0, i), (-1, i), f))
    t.setStyle(TableStyle(st))
    els.append(t)
    return els


def build():
    W, H = landscape(A4)
    def make_frame():
        return Frame(13 * mm, 16 * mm, W - 26 * mm, H - 30 * mm)

    doc = BaseDocTemplate(OUT, pagesize=(W, H),
                          leftMargin=13 * mm, rightMargin=13 * mm,
                          topMargin=19 * mm, bottomMargin=16 * mm,
                          title="NAIMS-I(Oracle) → NAIMS-II 2DB 매핑")
    row_groups = rows_by_table()
    templates = [PageTemplate(id="intro", frames=[make_frame()], onPage=deco)]
    cat_counts = collections.Counter()
    section_meta = []
    for idx, ((src, tgt), rows) in enumerate(row_groups, 1):
        cat = next((c for s, _, _, _, c in B.PLAN if s == src), "")
        cat_counts[cat] += 1
        sec_no = category_no(cat)
        section_meta.append((idx, sec_no, cat, cat_counts[cat], src, tgt, rows))
        templates.append(PageTemplate(id="sec%d" % idx, frames=[make_frame()],
                                      onPage=make_deco("%d. %s" % (sec_no, cat))))
    doc.addPageTemplates(templates)
    AW = W - 26 * mm
    F = [Paragraph("NAIMS DB Migration Mapping Tables", S_TITLE),
         Paragraph(
             "NAIMS-I (Oracle) → NAIMS-II (NAIMS2)", S_SUB),
         Paragraph(
             "구 NAIMS-I 의 Oracle DB 를 신규 NAIMS-II(NAIMS2) 로 이관할 때의 컬럼 대응이다. "
             "DB→DB 이관 대상 STD_* 전용이며, 각 테이블은 새 페이지에서 시작한다.", S_P),
         Spacer(1, 1 * mm)]
    F.extend(intro_summary(row_groups, AW))

    for idx, sec_no, cat, item_no, src, tgt, rows in section_meta:
        F.append(NextPageTemplate("sec%d" % idx))
        F.append(PageBreak())
        F.extend(table_block(src, tgt, rows, AW, sec_no, item_no))

    doc.build(F)
    print("saved: %s" % OUT)


def build_md():
    t = "# NAIMS-I (Oracle) → NAIMS-II (NAIMS2) 컬럼 매핑\n\n"
    t += "| 항목 | 내용 |\n|---|---|\n"
    t += "| 문서번호 | NAIMS-MIG-%s-09 |\n| 작성일 | %s |\n\n" % (G.TAG, G.STAMP)
    t += ("구 NAIMS-I(Oracle) → 신규 NAIMS-II(NAIMS2) 이관 컬럼 대응. STD_* 전용. "
          "`—`=대응 없음, '(신규)'=NAIMS2 신규 컬럼.\n\n")
    cat_counts = collections.Counter()
    for (src, tgt), rows in rows_by_table():
        cat = next((c for s, _, _, _, c in B.PLAN if s == src), "")
        cat_counts[cat] += 1
        sec_no = category_no(cat)
        vc = collections.Counter(r["verdict"] for r in rows)
        t += "## %d.%d. %s → %s\n\n" % (sec_no, cat_counts[cat], src, tgt)
        t += "%d개 컬럼 · %s\n\n" % (len(rows), " · ".join("%s %d" % (k, v) for k, v in vc.most_common()))
        t += "| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |\n"
        t += "|---|---|---|---|---|---|---|\n"
        for r in rows:
            ocol = r["col"] if r["otyp"] != "-" else ("(신규)" if r["col"] == "-" else "— (MsSQL전용)")
            t += "| %s | %s | %s | %s | %s | %s | %s |\n" % (
                ocol, r["otyp"], r["tcol"], r["ttyp"], r["rule"] or "-",
                r["verdict"], (r["note"] or "-").replace("|", "\\|"))
        t += "\n"
    io.open(os.path.join(G.DOCS, "09_2DB_Oracle_NAIMS2.md"), "w", encoding="utf-8").write(t)
    print("09_2DB_Oracle_NAIMS2.md")


if __name__ == "__main__":
    build_md()
    build()
