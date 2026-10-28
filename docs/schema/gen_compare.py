# -*- coding: utf-8 -*-
"""
Oracle · NAIMS(MsSQL) · NAIMS2 세 DB 테이블별 병렬 비교표.
분류 / 기능 / 세 DB의 테이블·컬럼수·행수 / 매핑 상태를 한 행에 나란히 놓는다.

출력  docs/NAIMS_3DB_테이블비교_<날짜>.pdf
      docs/07_3DB_테이블비교.md
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

OUT = os.path.join(G.DOCS, "NAIMS_3DB_테이블비교_%s.pdf" % G.TAG)
NAVY = colors.HexColor("#1F3864")

# 테이블별 기능 설명 (분류는 build_mapping.PLAN 의 category 사용)
FUNC = {
    "COUNTRY_CODES": "국가 코드·적성도. 국기 이미지 동반",
    "CLASSES": "함급 제원(추진/엔진/터빈 축비 등 음향 특성 다수)",
    "VESSEL_DATA": "함정 개체. 함급·국가 참조",
    "CHARACTERISTICS": "함정별 음향 특성(캐비테이션·이산주파수)",
    "SIGNATURES": "소음원 시그니처. 함정 참조",
    "HARMONICS": "시그니처별 하모닉 성분",
    "ACTIVE_SONAR": "능동 소나 제원",
    "PULSES": "펄스 정의. 최대 7개 세그먼트 참조",
    "PULSE_SEGMENTS": "펄스 세그먼트(주파수·변조·길이)",
    "PULSE_FREQUENCY_ISUS90": "ISUS90 전용 펄스 주파수표",
    "PULSE_SEG_LENGTH_ISUS90": "ISUS90 전용 세그먼트 길이표",
    "PULSE_SEG_SWING_ISUS90": "ISUS90 전용 세그먼트 스윙표",
    "TRANSIENT": "순간소음 정의. 함급·함정 참조",
    "TR_FRAME": "순간소음 프레임(32밴드 특징벡터)",
    "NB_SNIPPET": "협대역 스니펫(.grm+.wav)",
    "AI_SNIPPET": "능동식별 스니펫(.grm+.wav)",
    "TR_SNIPPET": "순간소음 스니펫(.wav)",
    "SIG_SNIPPET": "시그니처↔협대역 스니펫 연결",
    "FRAME_SNIPPET": "순간소음 프레임↔스니펫 연결",
    "IMAGE_DATA": "함정 영상자료",
    "WEAPON": "무장 제원",
    "WEAPON_CARRIER": "무장↔함급 연결",
    "DIST_LIST": "배포 목록",
    "DIST_TO": "배포 대상",
    "DB_DIST_GROUP": "배포 그룹",
    "DB_DIST_LOG": "배포 이력",
    "CD_CHANGE_LOG": "기준식별 변경 이력",
    "CD_BACKUP_LOG": "기준식별 백업 이력",
    "CCF2CD_LOG": "CCF→CD 변환 이력",
    # 제외/참조
    "RAWDATAINFO": "원음자료 정보 (파일 이관 시 참조)",
    "RAWDATAIMPORTINFO": "원음반입 이력 (파일 이관 시 참조)",
    "RAWSIGNALINFO": "원음신호 정보",
    "ONA_209": "용도 미상",
    "ONA_214": "용도 미상",
    "OPERATOR": "레거시 운용자 계정",
    "PASSWORD": "레거시 계정 정보",
    "USER_JUMIN": "개인식별정보",
    "DATASET": "데이터셋(빈 테이블)",
    "FIELD_CONV": "필드 변환(빈 테이블)",
    "DS_BACKUP_LOG": "데이터셋 백업 이력(빈 테이블)",
    "DS_DIST_LOG": "데이터셋 배포 이력(빈 테이블)",
    "HELP": "Oracle 도움말",
    "REDO_DB": "Oracle 시스템",
    "REDO_LOG": "Oracle 시스템",
    "sysdiagrams": "SSMS 다이어그램 메타",
}

S_TITLE = ParagraphStyle("t", fontName="KRB", fontSize=18, leading=24, textColor=NAVY, spaceAfter=4)
S_SUB = ParagraphStyle("s", fontName="KR", fontSize=10, leading=14, textColor=colors.HexColor("#555555"))
S_SUM_H = ParagraphStyle("sumh", fontName="KRB", fontSize=10.5, leading=14,
                         textColor=NAVY, spaceBefore=7, spaceAfter=3)
S_H1 = ParagraphStyle("h1", fontName="KRB", fontSize=13, leading=18, textColor=NAVY, spaceBefore=11, spaceAfter=5)
S_P = ParagraphStyle("p", fontName="KR", fontSize=9, leading=13.5, spaceAfter=4)
S_CELL = ParagraphStyle("c", fontName="KR", fontSize=7.4, leading=9.6)
S_CELLB = ParagraphStyle("cb", fontName="KRB", fontSize=7.4, leading=9.6, textColor=colors.white, alignment=1)
S_CAT = ParagraphStyle("cat", fontName="KRB", fontSize=7.6, leading=9.6, textColor=NAVY)

CAT_FILL = {
    "기준정보": "#E9EEF7", "수동신호": "#E2EFDA", "능동소나": "#FFF2CC",
    "순간소음": "#FCE4D6", "스니펫": "#DDEBF7", "영상자료": "#EDEDED",
    "무장": "#F2E9F5", "배포관리": "#E2F0F0", "변경이력": "#F5F0E2",
}


def P(t, st=S_CELL):
    return Paragraph(str(t).replace("&", "&amp;").replace("<", "&lt;"), st)


def make_deco(header_title, header_subtitle, footer_title):
    def deco(canvas, doc):
        canvas.saveState()
        pdf_common.draw_header(canvas, doc, header_title, header_subtitle)
        pdf_common.draw_footer_logos(canvas, doc)   # 좌 LIG / 우 syscore
        canvas.setFont("KR", 7.5)
        canvas.setFillColor(colors.HexColor("#888888"))
        canvas.drawCentredString(doc.pagesize[0] / 2, 5 * mm,
            "NAIMS-MIG-%s  ·  %s  ·  - %d -" % (G.TAG, footer_title, doc.page))
        canvas.setStrokeColor(colors.HexColor("#CCCCCC"))
        canvas.line(15 * mm, 13 * mm, doc.pagesize[0] - 15 * mm, 13 * mm)
        canvas.restoreState()
    return deco


def deco(canvas, doc):
    canvas.saveState()
    pdf_common.draw_header(canvas, doc, "3DB 테이블 비교")  # 상단 러닝 헤더
    pdf_common.draw_footer_logos(canvas, doc)   # 좌 LIG / 우 syscore
    canvas.setFont("KR", 7.5)
    canvas.setFillColor(colors.HexColor("#888888"))
    canvas.drawCentredString(doc.pagesize[0] / 2, 5 * mm,
        "NAIMS-MIG-%s  ·  3DB 테이블 병렬 비교  ·  - %d -" % (G.TAG, doc.page))
    canvas.setStrokeColor(colors.HexColor("#CCCCCC"))
    canvas.line(15 * mm, 13 * mm, doc.pagesize[0] - 15 * mm, 13 * mm)
    canvas.restoreState()


deco_intro = make_deco("NAIMS DB Migration Mapping Tables",
                       "Oracle · NAIMS · NAIMS2 Table Comparison",
                       "NAIMS DB Migration Mapping Tables")
deco_body = make_deco("3DB 테이블 비교", "NAIMS DB Migration Mapping Tables", "3DB 테이블 병렬 비교")


def cell3(schema, name, rows=None):
    """테이블 존재 여부 + 컬럼수(+행수) 표기."""
    if name not in schema:
        return "—"
    n = len(schema[name])
    if rows is not None and name in rows:
        return "%s\n%d열 · %s행" % (name, n, format(rows[name], ","))
    return "%s\n%d열" % (name, n)


def build_rows():
    """(분류, 소스테이블, 기능, oracle셀, mssql셀, naims2셀, 상태) 리스트."""
    plan_by_src = {s: (t, c) for s, t, _, _, c in B.PLAN}
    order_cat = ["기준정보", "수동신호", "능동소나", "순간소음", "스니펫",
                 "영상자료", "무장", "배포관리", "변경이력"]
    cat_rank = {c: i for i, c in enumerate(order_cat)}

    verdict_by_src = collections.defaultdict(collections.Counter)
    for r in G.M:
         verdict_by_src[r["src"]][r["verdict"]] += 1

    rows = []
    for s, t, pk, lvl, cat in sorted(
            B.PLAN, key=lambda x: (cat_rank.get(x[4], 99), x[3], x[0])):
        vc = verdict_by_src[s]
        need = vc.get("미결", 0) + vc.get("스키마변경", 0) + vc.get("신규제안", 0)
        status = "확정" if need == 0 else "검토 %d건" % need
        rows.append(dict(
            cat=cat, func=FUNC.get(s, ""),
            ora=cell3(G.ora, s),
            ms=cell3(G.ms, s, G.ms_rows),
            n2=cell3(G.tg, t),
            status=status, kind="이관"))
    return rows


def excluded_rows():
    rows = []
    for name in sorted(B.EXCLUDED):
        kind = "참조" if name in B.REFERENCE_ONLY else "제외"
        rows.append(dict(
            cat=kind, func=FUNC.get(name, B.EXCLUDED[name]),
            ora=cell3(G.ora, name),
            ms=cell3(G.ms, name, G.ms_rows),
            n2="—", status=B.EXCLUDED[name], kind=kind))
    return rows


def make_table(data_rows, AW):
    header = ["분류", "기능", "① Oracle\n(SYSTEM)", "② NAIMS\n(MsSQL)",
              "③ NAIMS2\n(대상 STD_*)", "매핑 상태"]
    data = [[P(h, S_CELLB) for h in header]]
    fills = []
    for r in data_rows:
        data.append([
            P(r["cat"], S_CAT), P(r["func"]),
            P(r["ora"]), P(r["ms"]), P(r["n2"]), P(r["status"])])
        fills.append(CAT_FILL.get(r["cat"]))
    widths = [17 * mm, 52 * mm, 42 * mm, 44 * mm, 46 * mm, AW - 201 * mm]
    t = Table(data, colWidths=widths, repeatRows=1)
    st = [("BACKGROUND", (0, 0), (-1, 0), NAVY),
          ("GRID", (0, 0), (-1, -1), 0.3, colors.HexColor("#BFBFBF")),
          ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
          ("TOPPADDING", (0, 0), (-1, -1), 2.5),
          ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
          ("LEFTPADDING", (0, 0), (-1, -1), 3),
          ("RIGHTPADDING", (0, 0), (-1, -1), 3)]
    for i, f in enumerate(fills, 1):
        if f:
            st.append(("BACKGROUND", (0, i), (0, i), colors.HexColor(f)))
    t.setStyle(TableStyle(st))
    return t


def simple_table(header, rows, widths, rowcolors=None):
    data = [[P(h, S_CELLB) for h in header]] + [[P(c) for c in r] for r in rows]
    t = Table(data, colWidths=widths, repeatRows=1)
    st = [("BACKGROUND", (0, 0), (-1, 0), NAVY),
          ("GRID", (0, 0), (-1, -1), 0.3, colors.HexColor("#BFBFBF")),
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


def intro_summary(ins, exc, AW):
    O, Ms = set(G.ora), set(G.ms) - {"sysdiagrams"}
    review = sum(1 for r in ins if r["status"] != "확정")
    cat_counts = collections.Counter(r["cat"] for r in ins)

    els = []
    els.append(Paragraph("문서 요약", S_SUM_H))
    els.append(simple_table(["항목", "내용"], [
        ["문서 성격", "Oracle · NAIMS(MsSQL) · NAIMS2 테이블 구조를 한 행에서 병렬 비교한 표"],
        ["비교 범위", "이관 대상 %d개, 제외/참조 %d개 테이블" % (len(ins), len(exc))],
        ["표기 기준", "각 칸은 테이블 존재 여부, 컬럼수, MsSQL 행수를 함께 표시"],
        ["활용 목적", "STD_* 이관 대상과 제외/참조 테이블을 빠르게 구분하고 검토 필요 테이블을 식별"],
    ], [30 * mm, AW - 30 * mm]))

    els.append(Paragraph("DB 구조 요약", S_SUM_H))
    els.append(simple_table(["구분", "값", "비고"], [
        ["Oracle (SYSTEM)", "%d개 테이블" % len(G.ora), "260420_NAIMS_SQL.pdf 전사"],
        ["NAIMS (MsSQL)", "%d개 테이블" % len(Ms), "192.168.111.39 실측"],
        ["NAIMS2 (대상)", "%d개 테이블 (STD_* %d)" % (
            len(G.tg), sum(1 for x in G.tg if x.startswith("STD_"))), "192.168.200.205 실측"],
        ["매핑 상태", "확정 %d개 · 검토 %d개" % (len(ins) - review, review), "검토는 미결/스키마변경/신규제안 포함"],
    ], [42 * mm, 62 * mm, AW - 104 * mm]))

    els.append(Paragraph("분류별 이관 대상", S_SUM_H))
    rows = []
    order = ["기준정보", "수동신호", "능동소나", "순간소음", "스니펫",
             "영상자료", "무장", "배포관리", "변경이력"]
    for i, cat in enumerate(order, 1):
        if cat_counts.get(cat):
            examples = [r["ora"].split("\n")[0] for r in ins if r["cat"] == cat][:3]
            rows.append(["%d. %s" % (i, cat), cat_counts[cat], ", ".join(examples)])
    els.append(simple_table(["분류", "테이블", "대표 테이블"], rows,
                            [30 * mm, 20 * mm, AW - 50 * mm]))
    return els


def build():
    W, H = landscape(A4)
    doc = BaseDocTemplate(OUT, pagesize=(W, H),
                          leftMargin=13 * mm, rightMargin=13 * mm,
                          topMargin=19 * mm, bottomMargin=16 * mm,
                          title="NAIMS 3DB 테이블 병렬 비교")
    def make_frame():
        return Frame(13 * mm, 16 * mm, W - 26 * mm, H - 30 * mm)

    doc.addPageTemplates([
        PageTemplate(id="intro", frames=[make_frame()], onPage=deco_intro),
        PageTemplate(id="body", frames=[make_frame()], onPage=deco_body),
    ])
    AW = W - 26 * mm
    F = []
    ins = build_rows()
    exc = excluded_rows()
    F.append(Paragraph("NAIMS DB Migration Mapping Tables", S_TITLE))
    F.append(Paragraph("Oracle · NAIMS · NAIMS2 Table Comparison", S_SUB))
    F.append(Paragraph(
        "구 기준식별 DB의 Oracle/MsSQL 두 후보와 신규 NAIMS2 대상을 테이블 단위로 나란히 비교한다.", S_P))
    F.append(Spacer(1, 1 * mm))
    F.extend(intro_summary(ins, exc, AW))

    F.append(NextPageTemplate("body"))
    F.append(PageBreak())
    F.append(Paragraph("1. 이관 대상 테이블 (DB → DB, 대상 STD_* 전용)", S_H1))
    F.append(Paragraph(
        "이관 대상 %d개. 분류 색상으로 그룹을 구분한다." % len(ins), S_P))
    F.append(make_table(ins, AW))

    F.append(PageBreak())
    F.append(Paragraph("2. 이관 제외 · 참조 테이블", S_H1))
    F.append(Paragraph(
        "'참조'는 이관하지 않되 원음 파일 이관 시 값을 조회하는 테이블이다. "
        "③ NAIMS2 칸의 '—'는 대응 대상이 없음을 뜻한다.", S_P))
    F.append(make_table(exc, AW))

    F.append(Spacer(1, 4 * mm))
    F.append(Paragraph("3. 요약", S_H1))
    O, Ms = set(G.ora), set(G.ms) - {"sysdiagrams"}
    summ = [
        ["Oracle (SYSTEM 스키마)", "%d개 테이블" % len(G.ora), "260420_NAIMS_SQL.pdf 전사"],
        ["NAIMS (MsSQL)", "%d개 테이블" % len(Ms), "192.168.111.39 실측"],
        ["NAIMS2 (대상)", "%d개 테이블 (STD_* %d)" % (
            len(G.tg), sum(1 for x in G.tg if x.startswith("STD_"))), "192.168.200.205 실측"],
        ["이관 대상", "%d개 (전부 STD_* 로)" % len(ins), "6단계 위상 정렬"],
        ["Oracle/MsSQL 논리 구조", "동일 (차이는 SELF_DB 1컬럼)", "어느 소스든 매핑 동일"],
    ]
    data = [[P(a, S_CELLB) for a in ["항목", "값", "비고"]]] + [[P(x) for x in r] for r in summ]
    tb = Table(data, colWidths=[55 * mm, 90 * mm, AW - 145 * mm], repeatRows=1)
    tb.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("GRID", (0, 0), (-1, -1), 0.3, colors.HexColor("#BFBFBF")),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3)]))
    F.append(tb)

    doc.build(F)
    print("saved: %s" % OUT)


def build_md():
    t = "# Oracle · NAIMS · NAIMS2 테이블 병렬 비교\n\n"
    t += "| 항목 | 내용 |\n|---|---|\n"
    t += "| 문서번호 | NAIMS-MIG-%s-07 |\n| 작성일 | %s |\n\n" % (G.TAG, G.STAMP)
    t += ("각 칸은 테이블 존재 여부·컬럼수를 나타낸다. `—` 는 해당 DB에 없음.\n\n")

    def block(title, rows):
        s = "## %s\n\n" % title
        s += "| 분류 | 기능 | ① Oracle | ② NAIMS(MsSQL) | ③ NAIMS2 | 매핑 상태 |\n"
        s += "|---|---|---|---|---|---|\n"
        for r in rows:
            s += "| %s | %s | %s | %s | %s | %s |\n" % (
                r["cat"], r["func"],
                r["ora"].replace("\n", " "), r["ms"].replace("\n", " "),
                r["n2"].replace("\n", " "), r["status"])
        return s + "\n"

    t += block("1. 이관 대상 (DB→DB, 대상 STD_* 전용)", build_rows())
    t += block("2. 이관 제외 · 참조", excluded_rows())
    io.open(os.path.join(G.DOCS, "07_3DB_테이블비교.md"), "w", encoding="utf-8").write(t)
    print("07_3DB_테이블비교.md")


if __name__ == "__main__":
    build_md()
    build()
