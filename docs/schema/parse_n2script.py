# -*- coding: utf-8 -*-
"""
N2script_260723.sql (UTF-16, SSMS 생성 스크립트) → naims2_schema.txt 형식 파싱.
CREATE TABLE 문에서 컬럼·타입·IDENTITY·NOT NULL·COMPUTED·PK·FK·DEFAULT 를 추출한다.
라이브 DB 덤프 대신 이 스크립트를 권위 기준으로 삼는다.
"""
import io, os, re, sys

SRC = r"Z:\N2script_260723.sql"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "naims2_schema.txt")


def load():
    raw = open(SRC, "rb").read()
    return raw.decode("utf-16")


def norm_type(coltype):
    """[int] / [varchar](100) / [decimal](18, 2) → int / varchar(100) / decimal(18,2)"""
    t = coltype.replace("[", "").replace("]", "").strip()
    t = re.sub(r"\(\s*", "(", t)
    t = re.sub(r"\s*,\s*", ",", t)
    t = re.sub(r"\s*\)", ")", t)
    # nvarchar/nchar 는 SQL 상 길이가 문자수. 덤프와 동일하게 문자수 유지.
    return t


def parse_columns(body):
    """CREATE TABLE 괄호 내부 → [(name, type_with_flags), ...]"""
    cols = []
    # 컬럼 라인: [NAME] [type](args) ... 제약/키 라인은 CONSTRAINT/PRIMARY/FOREIGN 로 시작
    for line in body.splitlines():
        s = line.strip().rstrip(",")
        if not s:
            continue
        if re.match(r"^(CONSTRAINT|PRIMARY|FOREIGN|UNIQUE|CHECK|\)|GO)\b", s, re.I):
            continue
        m = re.match(r"^\[(?P<name>[^\]]+)\]\s+(?P<rest>.+)$", s)
        if not m:
            continue
        name = m.group("name")
        rest = m.group("rest")

        # PK/인덱스 정의 라인: [COL] ASC / [COL] DESC — 컬럼 정의가 아니다.
        if re.match(r"^(ASC|DESC)\b", rest, re.I):
            continue
        # 타입이 아닌 것으로 시작하면(예: 대괄호 없는 ASC) 건너뜀
        if not re.match(r"^(\[|[A-Za-z])", rest):
            continue

        # 타입 추출
        tm = re.match(r"^(\[[A-Za-z0-9_ ]+\](?:\s*\([^)]*\))?|[A-Za-z0-9_]+(?:\s*\([^)]*\))?)", rest)
        typ = norm_type(tm.group(1)) if tm else "?"

        flags = ""
        if re.search(r"\bIDENTITY\b", rest, re.I):
            flags += " IDENTITY"
        if re.search(r"\bNOT\s+NULL\b", rest, re.I):
            flags += " NOT NULL"
        if re.search(r"\bAS\b", rest) and "(" in rest and "NOT NULL" not in flags.upper():
            # computed column: [COL] AS (expr)
            if re.match(r"^\s*AS\s*\(", rest, re.I):
                typ = "AS(computed)"
                flags = " COMPUTED"
        cols.append((name, (typ + flags).strip()))
    return cols


def parse():
    txt = load()
    # CREATE TABLE [schema].[Name]( ... )  블록 추출
    # 스키마 접두어는 dbo. 등. 테이블명만 취한다.
    pattern = re.compile(
        r"CREATE\s+TABLE\s+\[[^\]]+\]\.\[(?P<tbl>[^\]]+)\]\s*\((?P<body>.*?)\)\s*ON\s+",
        re.I | re.S)
    tables = {}
    for m in pattern.finditer(txt):
        tbl = m.group("tbl")
        cols = parse_columns(m.group("body"))
        if cols:
            tables[tbl] = cols

    # 보강: ON [PRIMARY] 없이 ) 로 끝나는 형태도 처리
    if len(tables) < 50:
        pat2 = re.compile(
            r"CREATE\s+TABLE\s+\[[^\]]+\]\.\[(?P<tbl>[^\]]+)\]\s*\((?P<body>.*?)\n\)\s*(?:ON|TEXTIMAGE_ON|WITH|GO)",
            re.I | re.S)
        for m in pat2.finditer(txt):
            tbl = m.group("tbl")
            if tbl not in tables:
                cols = parse_columns(m.group("body"))
                if cols:
                    tables[tbl] = cols

    # PK 추출: ALTER TABLE ... ADD CONSTRAINT ... PRIMARY KEY / 인라인
    pks = {}
    for m in re.finditer(
            r"ALTER\s+TABLE\s+\[[^\]]+\]\.\[(?P<tbl>[^\]]+)\]\s+ADD\s+CONSTRAINT\s+\[[^\]]+\]\s+PRIMARY\s+KEY[^(]*\((?P<cols>[^)]+)\)",
            txt, re.I | re.S):
        cols = re.findall(r"\[([^\]]+)\]", m.group("cols"))
        pks.setdefault(m.group("tbl"), []).extend(cols)
    # 인라인 PK (CREATE TABLE 내부)
    for m in pattern.finditer(txt):
        tbl = m.group("tbl")
        body = m.group("body")
        pm = re.search(r"PRIMARY\s+KEY[^(]*\(([^)]+)\)", body, re.I)
        if pm and tbl not in pks:
            pks[tbl] = re.findall(r"\[([^\]]+)\]", pm.group(1))

    # FK 추출
    fks = []
    for m in re.finditer(
            r"ALTER\s+TABLE\s+\[[^\]]+\]\.\[(?P<c>[^\]]+)\]\s+WITH[^\n]*ADD\s+CONSTRAINT\s+\[[^\]]+\]\s+FOREIGN\s+KEY\s*\((?P<cc>[^)]+)\)\s+REFERENCES\s+\[[^\]]+\]\.\[(?P<p>[^\]]+)\]\s*\((?P<pc>[^)]+)\)",
            txt, re.I | re.S):
        cc = re.findall(r"\[([^\]]+)\]", m.group("cc"))
        pc = re.findall(r"\[([^\]]+)\]", m.group("pc"))
        for a, b in zip(cc, pc):
            fks.append((m.group("c"), a, m.group("p"), b))

    # DEFAULT 제약: ALTER TABLE ... ADD DEFAULT (val) FOR [col]
    defaults = {}
    for m in re.finditer(
            r"ALTER\s+TABLE\s+\[[^\]]+\]\.\[(?P<tbl>[^\]]+)\]\s+ADD\s+(?:CONSTRAINT\s+\[[^\]]+\]\s+)?DEFAULT\s*\((?P<val>.*?)\)\s*FOR\s+\[(?P<col>[^\]]+)\]",
            txt, re.I | re.S):
        defaults[(m.group("tbl"), m.group("col"))] = m.group("val").strip()

    return tables, pks, fks, defaults


def main():
    tables, pks, fks, defaults = parse()
    out = []
    for tbl in sorted(tables):
        out.append("")
        out.append("### TABLE: %s" % tbl)
        for name, typ in tables[tbl]:
            dv = defaults.get((tbl, name))
            line = "%s\t%s" % (name, typ)
            if dv is not None:
                line += "  DEFAULT %s" % dv
            out.append(line)

    out.append("")
    out.append("## PRIMARY KEYS")
    for tbl in sorted(pks):
        for c in pks[tbl]:
            out.append("%s.%s" % (tbl, c))

    out.append("")
    out.append("## FOREIGN KEYS")
    for c, cc, p, pc in sorted(fks):
        out.append("%s.%s -> %s.%s" % (c, cc, p, pc))

    io.open(OUT, "w", encoding="utf-8").write("\n".join(out) + "\n")
    print("tables: %d" % len(tables))
    print("PKs: %d, FKs: %d, defaults: %d" % (len(pks), len(fks), len(defaults)))
    std = [t for t in tables if t.startswith("STD_")]
    print("STD_ tables: %d" % len(std))
    # 이관 대상 대조
    plan_tgt = set()
    import build_mapping as B
    for s, t, _, _, _ in B.PLAN:
        plan_tgt.add(t)
    missing = sorted(plan_tgt - set(tables))
    if missing:
        print("!! 이관 대상 대상테이블 중 스크립트에 없음: %s" % ", ".join(missing))
    else:
        print("이관 대상 대상테이블 %d개 전부 스크립트에 존재" % len(plan_tgt))


if __name__ == "__main__":
    main()
