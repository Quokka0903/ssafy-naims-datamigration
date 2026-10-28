# -*- coding: utf-8 -*-
"""
docs/02_소스DB구조_Oracle.md 의 표를 파싱해 Oracle CREATE TABLE DDL(01_schema.sql)을 생성한다.
리허설용 가짜 레거시 Oracle 스키마 — 타입은 원본(RAW/VARCHAR2/NUMBER)을 그대로 보존한다.
"""
import re, io, sys, os

HERE = os.path.dirname(os.path.abspath(__file__))
SRC  = os.path.join(HERE, "..", "docs", "02_소스DB구조_Oracle.md")
OUT  = os.path.join(HERE, "01_schema.sql")

# 이관 제외(문서 '제외') + 예약문자(#) 포함 테이블은 리허설 스키마에서 뺀다.
EXCLUDE = {"DATASET", "DS_BACKUP_LOG", "DS_DIST_LOG", "REDO_DB", "REDO_LOG", "HELP"}

def norm_type(t: str) -> str:
    t = t.strip()
    t = re.sub(r"NUMBER\(\*,", "NUMBER(38,", t)      # NUMBER(*,0) → NUMBER(38,0)
    t = re.sub(r"TIMESTAMP\s*\((\d+)\)", r"TIMESTAMP(\1)", t)
    t = t.replace("NOT NULL ENABLE", "NOT NULL")
    return t.strip()

def main():
    with io.open(SRC, encoding="utf-8") as f:
        lines = f.readlines()

    tables = []          # (name, [(col, type), ...])
    cur = None
    in_cols_section = False
    for ln in lines:
        s = ln.rstrip("\n")
        if s.startswith("## 테이블별 컬럼"):
            in_cols_section = True
            continue
        if not in_cols_section:
            continue
        m = re.match(r"^###\s+([A-Z0-9_]+)\s*$", s)
        if m:
            cur = {"name": m.group(1), "cols": []}
            tables.append(cur)
            continue
        # | 1 | COL | TYPE |
        m = re.match(r"^\|\s*\d+\s*\|\s*([A-Z0-9_#]+)\s*\|\s*(.+?)\s*\|\s*$", s)
        if m and cur is not None:
            col, typ = m.group(1), norm_type(m.group(2))
            cur["cols"].append((col, typ))

    out = io.StringIO()
    out.write("-- 자동 생성: gen_schema.py (docs/02_소스DB구조_Oracle.md 기준)\n")
    out.write("-- 리허설용 가짜 레거시 Oracle 스키마. SYSTEM 스키마에 생성한다(레거시와 동일).\n")
    out.write("-- 이름 컬럼(CLASS_NAME 등 RAW, SONAR_NAME 등 VARCHAR2)은 CRYPTO 패키지로 암복호화된다.\n\n")
    made = 0
    for t in tables:
        if t["name"] in EXCLUDE or not t["cols"]:
            continue
        made += 1
        out.write("-- {}개 컬럼\n".format(len(t["cols"])))
        # 무자격 이름 → sqlplus 접속 계정(REH)의 스키마에 생성된다.
        out.write("BEGIN EXECUTE IMMEDIATE 'DROP TABLE \"{}\" CASCADE CONSTRAINTS'; "
                  "EXCEPTION WHEN OTHERS THEN NULL; END;\n/\n".format(t["name"]))
        out.write("CREATE TABLE \"{}\" (\n".format(t["name"]))
        defs = ["  {:<24} {}".format('"'+c+'"', ty) for c, ty in t["cols"]]
        out.write(",\n".join(defs))
        out.write("\n);\n\n")

    out.write("-- 총 {}개 테이블\n".format(made))
    with io.open(OUT, "w", encoding="utf-8") as f:
        f.write(out.getvalue())
    print("생성 완료: {} ({}개 테이블)".format(OUT, made))

if __name__ == "__main__":
    main()
