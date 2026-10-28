# -*- coding: utf-8 -*-
"""결재 상신용 PDF 생성. 전 컬럼 매핑표는 별첨(xlsx)으로 분리하고 여기서는 요약과 결정요청을 담는다."""
import os, collections
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph,
                                Spacer, Table, TableStyle, PageBreak)
import build_mapping as B
import gen_docs as G
import pdf_common

pdfmetrics.registerFont(TTFont("KR", r"C:\Windows\Fonts\malgun.ttf"))
pdfmetrics.registerFont(TTFont("KRB", r"C:\Windows\Fonts\malgunbd.ttf"))

OUT = os.path.join(G.DOCS, "NAIMS_이관_매핑_검토본_%s.pdf" % G.TAG)
NAVY = colors.HexColor("#1F3864")

S_TITLE = ParagraphStyle("t", fontName="KRB", fontSize=19, leading=26, textColor=NAVY, spaceAfter=4)
S_SUB = ParagraphStyle("s", fontName="KR", fontSize=10.5, leading=15, textColor=colors.HexColor("#555555"))
S_H1 = ParagraphStyle("h1", fontName="KRB", fontSize=13.5, leading=19, textColor=NAVY,
                      spaceBefore=13, spaceAfter=6)
S_H2 = ParagraphStyle("h2", fontName="KRB", fontSize=11, leading=16, spaceBefore=9, spaceAfter=4)
S_P = ParagraphStyle("p", fontName="KR", fontSize=9.5, leading=14.5, spaceAfter=4)
S_CELL = ParagraphStyle("c", fontName="KR", fontSize=7.6, leading=10.2)
S_CELLB = ParagraphStyle("cb", fontName="KRB", fontSize=7.6, leading=10.2,
                         textColor=colors.white, alignment=1)

VC_COLOR = {
    "확정": colors.HexColor("#E2EFDA"), "자동생성": colors.HexColor("#D9E1F2"), "제외": colors.HexColor("#F2F2F2"),
    "미결": colors.HexColor("#FFF2CC"), "스키마변경": colors.HexColor("#FCE4D6"),
    "신규제안": colors.HexColor("#DDEBF7"),
}


def P(t, st=S_CELL):
    return Paragraph(str(t).replace("&", "&amp;").replace("<", "&lt;"), st)


def table(header, rows, widths, rowcolors=None):
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
        for i, c in enumerate(rowcolors, 1):
            if c:
                st.append(("BACKGROUND", (0, i), (-1, i), c))
    t.setStyle(TableStyle(st))
    return t


def deco(canvas, doc):
    canvas.saveState()
    if doc.page == 1:
        header_title = "NAIMS DB Migration Mapping Tables"
        header_subtitle = "Migration Mapping Review"
        footer_title = "NAIMS DB Migration Mapping Tables"
    else:
        header_title = "이관 매핑 검토본"
        header_subtitle = "NAIMS DB Migration Mapping Tables"
        footer_title = G.STAMP
    pdf_common.draw_header(canvas, doc, header_title, header_subtitle)  # 상단 러닝 헤더
    pdf_common.draw_footer_logos(canvas, doc)   # 좌 LIG / 우 syscore
    canvas.setFont("KR", 7.5)
    canvas.setFillColor(colors.HexColor("#888888"))
    canvas.drawCentredString(doc.pagesize[0] / 2, 5 * mm,
        "NAIMS-MIG-%s  ·  %s  ·  - %d -" % (G.TAG, footer_title, doc.page))
    canvas.setStrokeColor(colors.HexColor("#CCCCCC"))
    canvas.line(15 * mm, 13 * mm, doc.pagesize[0] - 15 * mm, 13 * mm)
    canvas.restoreState()


def build():
    W, H = landscape(A4)
    doc = BaseDocTemplate(OUT, pagesize=(W, H),
                          leftMargin=15 * mm, rightMargin=15 * mm,
                          topMargin=19 * mm, bottomMargin=16 * mm,
                          title="NAIMS 이관 매핑 검토본", author="NAIMS 이관")
    doc.addPageTemplates([PageTemplate(id="n",
                          frames=[Frame(15 * mm, 16 * mm, W - 30 * mm, H - 30 * mm)],
                          onPage=deco)])
    AW = W - 30 * mm
    F = []

    # ── 표지 ──
    F.append(Paragraph("NAIMS DB Migration Mapping Tables", S_TITLE))
    F.append(Paragraph("NAIMS → NAIMS2 데이터 이관 매핑 검토본", S_SUB))
    F.append(Spacer(1, 4 * mm))
    F.append(table(["항목", "내용"], [
        ["문서번호", "NAIMS-MIG-%s" % G.TAG],
        ["작성일", G.STAMP],
        ["소스 DB (MsSQL)", G.SRC_MSSQL],
        ["소스 DB (Oracle)", G.SRC_ORACLE],
        ["대상 DB", G.TGT],
        ["기준 확정본", G.BASELINE],
        ["별첨", "NAIMS_이관_매핑_검토본_%s.xlsx (매핑 %d행)" % (G.TAG, len(G.M) + len(G.FM))],
    ], [45 * mm, AW - 45 * mm]))

    need = (G.ALLVC.get("미결", 0) + G.ALLVC.get("스키마변경", 0) + G.ALLVC.get("신규제안", 0))
    F.append(Paragraph("문서 요약", S_H2))
    F.append(table(["항목", "내용"], [
        ["문서 성격", "구 기준식별 DB를 신규 NAIMS2로 이관하기 위한 테이블/컬럼 단위 매핑 검토본"],
        ["이관 범위", "트랙① DB→DB %d행 + 트랙② 파일→DB %d행 = 총 %d행" % (
            len(G.M), len(G.FM), len(G.M) + len(G.FM))],
        ["검토 상태", "확정 %d행, 자동생성 %d행, 제외 %d행, 검토 필요 %d행" % (
            G.ALLVC.get("확정", 0), G.ALLVC.get("자동생성", 0), G.ALLVC.get("제외", 0), need)],
        ["주요 확인", "소스 DB 확정, 운영환경 복호화 권한, 스키마변경/미결/신규제안 항목 승인"],
        ["별첨 활용", "상세 필드 단위 판단은 Excel의 결정요청 시트에 기록"],
    ], [36 * mm, AW - 36 * mm]))
    F.append(PageBreak())

    F.append(Paragraph("1. 이관 범위 — 두 트랙", S_H1))
    F.append(Paragraph(
        "이관은 성격이 다른 두 갈래로 구성되며, 본 문서는 양쪽을 모두 포함한다.", S_P))
    F.append(table(["트랙", "소스", "대상", "매핑 행수"], [
        ["① DB → DB", "구 NAIMS DB (MsSQL 또는 Oracle)", "STD_* 전용", "%d" % len(G.M)],
        ["② 파일 → DB", "원음 자료 파일 (.sd 단주기 / .nb 협대역)",
         "RAW_*, PREC_*, EXTRACT_DATA_STORE", "%d" % len(G.FM)],
        ["합계", "", "", "%d" % (len(G.M) + len(G.FM))],
    ], [26 * mm, 78 * mm, 70 * mm, AW - 174 * mm]))
    F.append(Spacer(1, 2 * mm))
    F.append(Paragraph(
        "DB에서 이관해 가는 대상은 기준식별 DB, 즉 <b>STD_* 에 집중된다.</b> "
        "RAW_* · PREC_* · EXT_* 계열은 DB→DB 이관 대상이 아니며 전량 트랙②로 적재된다. "
        "다만 트랙② 수행 시 구 DB의 아래 테이블을 <b>조회</b>한다(이관은 하지 않음).", S_P))
    F.append(table(["참조 테이블", "레코드수", "역할"],
                   [[k, G.ms_rows.get(k, "-"), v] for k, v in sorted(B.REFERENCE_ONLY.items())],
                   [40 * mm, 22 * mm, AW - 62 * mm]))

    F.append(Paragraph("2. 검토 요약", S_H1))
    F.append(Paragraph(
        "기존 확정본 830행을 기준선으로 삼고, 현재 소스·대상 DB를 직접 조회해 정합성을 "
        "검증한 결과다. 총 %d행 중 %d행은 그대로 구현 가능하며, "
        "<b>%d행이 담당자 결정을 필요로 한다.</b>" % (
            len(G.M) + len(G.FM), G.ALLVC.get("확정", 0), need), S_P))
    F.append(Spacer(1, 2 * mm))
    order = ["확정", "자동생성", "제외", "미결", "스키마변경", "신규제안"]
    rows = [[k, G.VC.get(k, 0), G.FVC.get(k, 0), G.ALLVC.get(k, 0), {
        "확정": "기존 확정 + 현재 스키마 일치 — 그대로 구현",
        "자동생성": "PK→RECORD_ID 등 타겟 IDENTITY 자동 채번",
        "제외": "이관하지 않음으로 확정된 항목",
        "미결": "대응 규칙 미확정 — 결정 필요",
        "스키마변경": "확정된 대상이 현재 DB에 없음 — 재확정 필요",
        "신규제안": "규칙 기반 자동 제안 — 승인 필요",
    }[k]] for k in order]
    F.append(table(["판정", "트랙①", "트랙②", "합계", "의미"], rows,
                   [26 * mm, 18 * mm, 18 * mm, 18 * mm, AW - 80 * mm],
                   [VC_COLOR[k] for k in order]))

    # ── 이관 순서 ──
    F.append(Paragraph("3. 트랙① 이관 대상 및 실행 순서", S_H1))
    F.append(Paragraph(
        "이관 대상 %d개 테이블. 단계는 외래키 의존 관계를 위상 정렬한 결과이며, "
        "같은 단계 안에서는 순서가 자유롭다. 대상 테이블의 RECORD_ID 는 IDENTITY 이므로 "
        "부모를 먼저 적재하고 새로 채번된 값으로 자식의 외래키를 치환한다." % len(B.PLAN), S_P))
    lv = collections.defaultdict(list)
    for s, t, pk, l, c in B.PLAN:
        lv[l].append("%s → %s" % (s, t))
    F.append(table(["단계", "테이블 (소스 → 대상)"],
                   [[k, "   ·   ".join(v)] for k, v in sorted(lv.items())],
                   [16 * mm, AW - 16 * mm]))
    F.append(Spacer(1, 3 * mm))
    F.append(Paragraph("트랙① 이관 제외 테이블", S_H2))
    F.append(table(["테이블", "구분", "사유"],
                   [[k, "참조용" if k in B.REFERENCE_ONLY else "제외", v]
                    for k, v in sorted(B.EXCLUDED.items())],
                   [45 * mm, 18 * mm, AW - 63 * mm]))

    # ── 소스 차이 ──
    F.append(PageBreak())
    F.append(Paragraph("4. 소스 DB 이원화 (MsSQL / Oracle) — 트랙①", S_H1))
    O, Ms = set(G.ora), set(G.ms) - {"sysdiagrams"}
    F.append(Paragraph(
        "이관 소스는 MsSQL과 Oracle 두 후보가 있다. 공통 테이블 %d개를 대조한 결과 "
        "<b>논리 구조는 사실상 동일</b>하며, 컬럼 구성 차이는 신규 체계에서 폐기되는 "
        "SELF_DB 하나뿐이다. 따라서 어느 쪽을 택하더라도 본 매핑표가 그대로 적용된다."
        % len(O & Ms), S_P))
    F.append(table(["구분", "내용"], [
        ["Oracle에만 존재", ", ".join(sorted(O - Ms)) + "  (Oracle 시스템/도움말 테이블 — 이관 대상 아님)"],
        ["MsSQL에만 존재", ", ".join(sorted(Ms - O)) + "  (전부 이관 제외 확정 항목과 일치)"],
        ["컬럼 차이", "SELF_DB (MsSQL에만). 신규 체계 미사용 — 폐기"],
    ], [38 * mm, AW - 38 * mm]))

    F.append(Paragraph("4-1. 암호화 범위 차이 — 주의", S_H2))
    F.append(Paragraph(
        "두 소스의 암호화 적용 범위가 다르다. MsSQL은 복호화 경로가 확보되었으나 "
        "<b>Oracle의 RAW(200) 복호화 방식은 확인되지 않았다.</b>", S_P))
    enc = []
    for key in sorted(set(B.ENC_MSSQL) | set(B.ENC_ORACLE)):
        tb, c = key.split(".", 1)
        enc.append([tb, c,
                    dict(G.ora.get(tb, [])).get(c, "-"),
                    dict(G.ms.get(tb, [])).get(c, "-"),
                    "양쪽 암호화" if key in B.ENC_MSSQL and key in B.ENC_ORACLE
                    else ("MsSQL만 암호화" if key in B.ENC_MSSQL else "Oracle만 암호화")])
    F.append(table(["테이블", "컬럼", "Oracle 타입", "MsSQL 타입", "구분"],
                   enc, [34 * mm, 46 * mm, 34 * mm, 34 * mm, AW - 148 * mm]))

    F.append(Paragraph("4-2. 개별 확인이 필요한 타입 차이", S_H2))
    F.append(table(["테이블", "컬럼", "Oracle", "MsSQL", "사유"], [
        ["CD_CHANGE_LOG", "DATASET_ID", "NUMBER", "varchar(20)", "숫자 ↔ 문자. 값 형태 확인 필요"],
        ["WEAPON_CARRIER", "CLASS_ID", "VARCHAR2(30)", "int", "외래키인데 형이 다름 — 원본 설계 결함으로 판단"],
        ["CCF2CD_LOG", "CTRY_CODE", "CHAR(3)", "varchar(2)", "Oracle이 1자 넓음 — 잘림 가능"],
        ["DIST_TO", "DESTINATION", "VARCHAR2(30)", "varchar(15)", "Oracle이 15자 넓음 — 잘림 가능"],
        ["TR_SNIPPET", "SAMPLING_RATE", "NUMBER(6,0)", "float", "정수 ↔ 실수"],
    ], [34 * mm, 34 * mm, 30 * mm, 26 * mm, AW - 124 * mm]))

    # ── 결정 요청 ──
    F.append(PageBreak())
    F.append(Paragraph("5. 결정 요청 사항", S_H1))

    F.append(Paragraph("5-1. 선결 과제 (공통)", S_H2))
    F.append(table(["항목", "내용", "요청"], [
        ["소스 DB 확정",
         "MsSQL은 복호화 검증 완료(2026-07-22). Oracle은 RAW(200) 복호화 방식 미확인.",
         "소스를 어느 쪽으로 확정할지 결정"],
        ["운영환경 권한",
         "복호화에 대칭키 VIEW DEFINITION + 인증서 CONTROL 권한이 필요. 시험환경은 부여 완료.",
         "운영환경 권한 부여 가능 여부 확인"],
    ], [32 * mm, AW - 92 * mm, 60 * mm]))

    chg = [r for r in G.M if r["verdict"] == "스키마변경"]
    F.append(Paragraph("5-2. [트랙①] 대상 스키마 변경으로 재확정이 필요한 항목 (%d건)" % len(chg), S_H2))
    F.append(Paragraph(
        "2026-04 확정 이후 NAIMS2 스키마가 변경되어, 확정되었던 대상 컬럼이 현재 존재하지 않는다. "
        "(a) 신규 대상 컬럼으로 재지정 또는 (b) 이관 제외 중 결정이 필요하다.", S_P))
    bytab = collections.defaultdict(list)
    for r in chg:
        bytab[(r["src"], r["tgt"])].append(r)
    F.append(table(["소스 테이블", "대상 테이블", "건수", "해당 소스 컬럼"],
                   [[s, t, len(v), ", ".join(x["col"] for x in v)]
                    for (s, t), v in sorted(bytab.items())],
                   [34 * mm, 42 * mm, 14 * mm, AW - 90 * mm],
                   [VC_COLOR["스키마변경"]] * len(bytab)))

    und = [r for r in G.M if r["verdict"] == "미결"]
    F.append(PageBreak())
    F.append(Paragraph("5-3. [트랙①] 대응 규칙 미확정 항목 (%d건)" % len(und), S_H2))
    F.append(table(["소스 테이블", "소스 컬럼", "대상 테이블", "대상 컬럼", "사유"],
                   [[r["src"], r["col"], r["tgt"], r["tcol"], r["note"] or "-"] for r in und],
                   [34 * mm, 40 * mm, 44 * mm, 40 * mm, AW - 158 * mm],
                   [VC_COLOR["미결"]] * len(und)))

    prop = [r for r in G.M if r["verdict"] == "신규제안"]
    F.append(PageBreak())
    F.append(Paragraph("5-4. [트랙①] 승인 요청 — 규칙 기반 신규 제안 (%d건)" % len(prop), S_H2))
    F.append(Paragraph(
        "기존 확정본에 해당 행이 없어, 명명 규칙을 적용해 자동 도출한 대응이다. 승인이 필요하다.", S_P))
    F.append(table(["소스 테이블", "소스 컬럼", "대상 컬럼", "적용 규칙", "비고"],
                   [[r["src"], r["col"], r["tcol"], r["rule"], r["note"] or "-"] for r in prop],
                   [34 * mm, 40 * mm, 44 * mm, 28 * mm, AW - 146 * mm],
                   [VC_COLOR["신규제안"]] * len(prop)))

    # ── 트랙② 결정 요청 ──
    fchg = [r for r in G.FM if r["verdict"] == "스키마변경"]
    fund = [r for r in G.FM if r["verdict"] == "미결"]
    F.append(PageBreak())
    F.append(Paragraph("5-5. [트랙②] 대상 스키마 변경으로 재확정이 필요한 항목 (%d건)" % len(fchg), S_H2))
    F.append(Paragraph(
        "파일 이관 구간에서도 확정 이후 대상 스키마가 변경되었다. 특히 "
        "<b>PREC_COLLECT_SUPPLEMENT 테이블은 현재 존재하지 않으며</b>, "
        "PREC_ANALYSIS_COLLECT_SUPPLEMENT / PREC_SRC_COLLECT_SUPPLEMENT 로 분리된 것으로 보인다.", S_P))
    F.append(table(["소스 파일", "소스 항목", "확정되었던 대상", "현황"],
                   [[r["ent"], r["fld"], "%s.%s" % (r["tgt"], r["tcol"]), r["note"]]
                    for r in fchg],
                   [40 * mm, 46 * mm, 66 * mm, AW - 152 * mm],
                   [VC_COLOR["스키마변경"]] * len(fchg)))

    F.append(Paragraph("5-6. [트랙②] 대응 규칙 미확정 항목 (%d건)" % len(fund), S_H2))
    F.append(table(["소스 파일", "파싱 객체", "소스 항목", "대상 테이블", "대상 컬럼", "사유"],
                   [[r["ent"], r["obj"], r["fld"], r["tgt"], r["tcol"],
                     r["note"] or r["rule"] or "-"] for r in fund],
                   [34 * mm, 32 * mm, 40 * mm, 40 * mm, 34 * mm, AW - 180 * mm],
                   [VC_COLOR["미결"]] * len(fund)))

    F.append(PageBreak())
    F.append(Paragraph("6. 트랙② 소스 파일별 대상 테이블", S_H1))
    ent_tgt = collections.OrderedDict()
    for r in G.FM:
        ent_tgt.setdefault((r["kind"], r["ent"]), collections.Counter())[r["tgt"]] += 1
    F.append(table(["구분", "소스 파일", "대상 테이블 (행수)"],
                   [[k[0], k[1], ", ".join("%s(%d)" % (a, b) for a, b in v.most_common())]
                    for k, v in ent_tgt.items()],
                   [24 * mm, 44 * mm, AW - 68 * mm]))

    F.append(Paragraph("7. 적용 공통 규칙", S_H1))
    F.append(table(["구분", "내용"],
                   [[d.get("구분", ""), d.get("내용", "")] for d in G.DEC],
                   [24 * mm, AW - 24 * mm]))

    F.append(Paragraph("8. 별첨", S_H1))
    F.append(Paragraph(
        "필드 단위 매핑표 %d행(트랙① %d + 트랙② %d)은 분량 관계로 별첨 Excel 파일에 수록하였다. "
        "「결정요청」 시트의 [결정] 열에 판단을 기입해 주시기 바란다." % (
            len(G.M) + len(G.FM), len(G.M), len(G.FM)), S_P))
    F.append(table(["파일", "내용"], [
        ["NAIMS_이관_매핑_검토본_%s.xlsx" % G.TAG,
         "표지 / 이관순서 / 이관제외 / 전컬럼매핑(트랙①) / 파일이관매핑(트랙②) / "
         "결정요청 / 소스구조비교 / 소스구조상세 / 대상구조 / 기존결정규칙"],
        ["docs/00~06_*.md", "동일 내용의 Markdown 문서 세트 (형상관리용)"],
    ], [70 * mm, AW - 70 * mm]))

    doc.build(F)
    print("saved: %s" % OUT)


if __name__ == "__main__":
    build()
