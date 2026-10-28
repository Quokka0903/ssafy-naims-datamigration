# -*- coding: utf-8 -*-
"""PDF 공용 요소: 결재 문서 헤더(제목)와 푸터(CI 로고) 배치."""
import os
from reportlab.lib import colors
from reportlab.lib.units import mm
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "assets")

# 모든 결재 문서 공통 러닝 헤더 (2026-07-24 사용자 지정, 간결 정제).
HEADER_TITLE = "NAIMS Migration Mapping"
HEADER_SUBTITLE = "NAIMS DB Migration Mapping Tables"
_HEADER_NAVY = colors.HexColor("#1F3864")
_HEADER_BLUE = colors.HexColor("#2E75B6")   # 강조 블록 밝은 파랑
_HEADER_MARGIN = 15   # mm, 좌우 여백 (헤더 라인 폭 기준)


def _font(name, fallback):
    return name if name in pdfmetrics.getRegisteredFontNames() else fallback


def draw_header(canvas, doc, title=None, subtitle=None):
    """
    사내 양식: 좌측 강조 블록 + 좌측 메인 제목 + 우측 보조 제목 + 하단 구분선.
    """
    page_w, page_h = doc.pagesize
    left = _HEADER_MARGIN * mm
    top = page_h - 6 * mm            # 헤더 상단 기준
    blk = 5.2 * mm                   # 강조 블록 한 변
    main_title = title or getattr(doc, "header_title", HEADER_TITLE) or HEADER_TITLE
    sub_title = subtitle or getattr(doc, "header_subtitle", HEADER_SUBTITLE) or HEADER_SUBTITLE

    canvas.saveState()

    # 좌측 강조 블록 (네이비 정사각 + 우상단 밝은 파랑 삼각)
    bx, by = left, top - blk
    canvas.setFillColor(_HEADER_NAVY)
    canvas.rect(bx, by, blk, blk, fill=1, stroke=0)
    p = canvas.beginPath()
    p.moveTo(bx + blk, by)
    p.lineTo(bx + blk, by + blk)
    p.lineTo(bx, by + blk)
    p.close()
    canvas.setFillColor(_HEADER_BLUE)
    canvas.drawPath(p, fill=1, stroke=0)

    # 제목: 좌측 메인, 우측 보조
    canvas.setFont(_font("KRB", "Helvetica-Bold"), 13)
    canvas.setFillColor(colors.HexColor("#222222"))
    canvas.drawString(bx + blk + 3.5 * mm, by + 1.1 * mm, main_title)

    canvas.setFont(_font("KR", "Helvetica"), 8.2)
    canvas.setFillColor(colors.HexColor("#6F7782"))
    canvas.drawRightString(page_w - _HEADER_MARGIN * mm, by + 1.45 * mm, sub_title)

    # 하단 구분선 (네이비, 폭 전체)
    ly = by - 2.6 * mm
    canvas.setStrokeColor(_HEADER_NAVY)
    canvas.setLineWidth(1.3)
    canvas.line(left, ly, page_w - _HEADER_MARGIN * mm, ly)

    canvas.restoreState()

# 왼쪽 끝 LIG, 오른쪽 끝 syscore (사용자 지정 2026-07-24)
LIG_PNG = os.path.join(ASSETS, "lig.png")
SYSCORE_PNG = os.path.join(ASSETS, "syscore.png")

_LOGO_H = 6.5 * mm       # 로고 높이 (폭은 종횡비 유지)
_MAXW = 42 * mm          # 로고 최대 폭
_MARGIN = 15 * mm        # 좌우 여백
_Y = 3.5 * mm            # 바닥에서의 높이


def _draw(canvas, path, x_anchor, page_w, align):
    """path 로고를 그린다. align='left' 면 x_anchor 가 좌측 끝, 'right' 면 우측 끝."""
    if not os.path.exists(path):
        return False
    try:
        img = ImageReader(path)
        iw, ih = img.getSize()
        w = _LOGO_H * iw / ih
        if w > _MAXW:
            w = _MAXW
            h = _MAXW * ih / iw
        else:
            h = _LOGO_H
        x = x_anchor if align == "left" else (x_anchor - w)
        canvas.drawImage(img, x, _Y, width=w, height=h,
                         preserveAspectRatio=True, mask="auto")
        return True
    except Exception:
        return False


def draw_footer_logos(canvas, doc):
    """푸터 좌측 끝 LIG, 우측 끝 syscore."""
    page_w = doc.pagesize[0]
    _draw(canvas, LIG_PNG, _MARGIN, page_w, "left")
    _draw(canvas, SYSCORE_PNG, page_w - _MARGIN, page_w, "right")


def logos_present():
    return os.path.exists(LIG_PNG) and os.path.exists(SYSCORE_PNG)
