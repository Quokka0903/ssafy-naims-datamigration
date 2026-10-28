# -*- coding: utf-8 -*-
"""
검토·결재용 문서 세트 생성기.

출력 (docs/ 아래)
  00_개요.md
  01_소스DB구조_MsSQL.md
  02_소스DB구조_Oracle.md
  03_소스간_구조차이.md
  04_NAIMS2_매핑명세.md
  05_미결사항_및_결정요청.md
  NAIMS_이관_매핑_검토본_<날짜>.xlsx
"""
import io, os, re, sys, collections
import build_mapping as B

HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.abspath(os.path.join(HERE, ".."))
STAMP = "2026-07-22"
TAG = "260722"

SRC_MSSQL = "192.168.111.39:1433 / NAIMS"
SRC_ORACLE = "260420_NAIMS_SQL.pdf (25p DDL 스캔본 전사)"
TGT = "192.168.200.205:1433 / NAIMS2"
BASELINE = "NAIMS_마이그레이션_정리_최종준비본.xlsx (03_마이그레이션_매핑 / 09_결정규칙)"


def read_tsv(p):
    lines = io.open(os.path.join(HERE, p), encoding="utf-8").read().splitlines()
    h = lines[0].split("\t")
    return [dict(zip(h, l.split("\t"))) for l in lines[1:] if l.strip()]


def md_table(header, rows):
    out = ["| " + " | ".join(header) + " |",
           "|" + "|".join(["---"] * len(header)) + "|"]
    for r in rows:
        out.append("| " + " | ".join(str(c).replace("|", "\\|") for c in r) + " |")
    return "\n".join(out)


def w(name, text):
    p = os.path.join(DOCS, name)
    io.open(p, "w", encoding="utf-8").write(text)
    print("  %s (%d bytes)" % (name, len(text.encode("utf-8"))))


# ── 데이터 적재 ────────────────────────────────────────────────────────
M = read_tsv("mapping_merged.tsv")       # DB→DB (STD_*)
FM = read_tsv("file_merged.tsv")         # 파일→RAW_*/PREC_*
DEC = read_tsv("decisions.tsv")
PEND = read_tsv("pending.tsv")
ora = B.load_oracle(os.path.join(HERE, "oracle_naims_schema.txt"))
ms = B.load_flat(os.path.join(HERE, "mssql_naims_schema.txt"))
tg = B.load_flat(os.path.join(HERE, "naims2_schema.txt"))
_, _, ms_rows = B.load_meta(os.path.join(HERE, "mssql_naims_schema.txt"))
tg_pk, tg_fk, tg_rows = B.load_meta(os.path.join(HERE, "naims2_schema.txt"))

PLAN_TABLES = [s for s, _, _, _, _ in B.PLAN]
VC = collections.Counter(r["verdict"] for r in M)
FVC = collections.Counter(r["verdict"] for r in FM)
ALLVC = VC + FVC


def header(title, no):
    return ("# %s\n\n" % title +
            "| 항목 | 내용 |\n|---|---|\n" +
            "| 문서번호 | NAIMS-MIG-%s-%s |\n" % (TAG, no) +
            "| 작성일 | %s |\n" % STAMP +
            "| 소스 DB (MsSQL) | %s |\n" % SRC_MSSQL +
            "| 소스 DB (Oracle) | %s |\n" % SRC_ORACLE +
            "| 대상 DB | %s |\n" % TGT +
            "| 기준 확정본 | %s |\n\n" % BASELINE)


# ── 00 개요 ────────────────────────────────────────────────────────────
def doc00():
    t = header("NAIMS → NAIMS2 데이터 이관 매핑 명세 — 개요", "00")
    t += """## 1. 문서 목적

구 NAIMS 체계를 신규 NAIMS2 체계로 이관하기 위한 **필드 단위 매핑 명세**이다.
담당자 검토와 결재를 거쳐 구현 기준으로 확정하는 것을 목적으로 한다.

## 2. 이관 범위 — 두 트랙

이관은 성격이 다른 두 갈래로 구성된다. 본 문서 세트는 **양쪽을 모두 포함**한다.

| 트랙 | 소스 | 대상 | 매핑 행수 |
|---|---|---|---|
| ① DB → DB | 구 NAIMS DB (MsSQL 또는 Oracle) | **`STD_*` 전용** | %d |
| ② 파일 → DB | 원음 자료 파일 (`.sd` 단주기 / `.nb` 협대역) | `RAW_*`, `PREC_*`, `EXTRACT_DATA_STORE` | %d |
| | | **합계** | **%d** |

> **DB에서 이관해 가는 대상은 기준식별 DB, 즉 `STD_*` 에 집중된다.**
> `RAW_*` · `PREC_*` · `EXT_*` 계열은 **DB→DB 이관 대상이 아니며**, 전량 트랙②(원음 파일 이관)로
> 적재된다. 두 트랙은 대상 테이블이 겹치지 않는다.

다만 트랙②를 수행할 때 구 DB의 일부 테이블을 **참조**한다. 이관은 하지 않는다.

""" % (len(M), len(FM), len(M) + len(FM))
    t += md_table(["테이블", "역할"], sorted(B.REFERENCE_ONLY.items()))
    t += """

## 3. 문서 구성

| 문서 | 내용 |
|---|---|
| 00_개요 | 본 문서. 범위·출처·요약 |
| 01_소스DB구조_MsSQL | MsSQL 소스 전 테이블·컬럼 |
| 02_소스DB구조_Oracle | Oracle 소스 전 테이블·컬럼 |
| 03_소스간_구조차이 | 두 소스 DB의 차이점 |
| 04_NAIMS2_매핑명세 | **트랙① DB→DB 전 컬럼 1:1 매핑표** |
| 05_미결사항_및_결정요청 | 담당자 결정이 필요한 항목 (양 트랙) |
| 06_파일이관_매핑명세 | **트랙② 파일→DB 매핑표** |

## 4. 소스 DB 이원화"""
    t += """

트랙①의 소스는 **MsSQL과 Oracle 두 가지 후보**가 존재한다. 양자를 대조한 결과
**논리 구조는 사실상 동일**하며, 어느 쪽을 소스로 택하더라도 매핑표가 그대로 적용된다.
차이는 03 문서를 참조한다.

## 5. 기존 확정본과의 관계

본 명세는 기존 확정본을 **대체하지 않는다.** 기존 확정본 830행 전체를 기준선으로 삼고,
여기에 다음 두 가지를 덧붙인 것이다.

1. **현재 스키마와의 정합성 검증** — 확정 시점(2026-04) 이후 대상 DB 스키마가 변경된 부분
2. **미매핑 구간 보완 제안** — 기존 확정본에 행이 없던 컬럼에 대한 규칙 기반 제안

## 6. 요약

"""
    t += md_table(["판정", "트랙① DB→DB", "트랙② 파일→DB", "합계", "의미"], [
        ["확정", VC.get("확정", 0), FVC.get("확정", 0), ALLVC.get("확정", 0),
         "기존 확정 + 현재 스키마와 일치. 그대로 구현"],
        ["제외", VC.get("제외", 0), FVC.get("제외", 0), ALLVC.get("제외", 0),
         "기존에 이관 제외로 확정"],
        ["미결", VC.get("미결", 0), FVC.get("미결", 0), ALLVC.get("미결", 0),
         "담당자 결정 필요"],
        ["스키마변경", VC.get("스키마변경", 0), FVC.get("스키마변경", 0), ALLVC.get("스키마변경", 0),
         "기존 확정이나 대상이 현재 없음 — **재확정 필요**"],
        ["신규제안", VC.get("신규제안", 0), FVC.get("신규제안", 0), ALLVC.get("신규제안", 0),
         "규칙 기반 제안 — 승인 필요"],
        ["**합계**", "**%d**" % len(M), "**%d**" % len(FM), "**%d**" % (len(M) + len(FM)), ""],
    ])
    need = (ALLVC.get("미결", 0) + ALLVC.get("스키마변경", 0) + ALLVC.get("신규제안", 0))
    t += "\n\n**결정이 필요한 항목은 총 %d건**이다. 05 문서를 참조한다.\n" % need
    t += "\n\n## 7. 트랙① 이관 대상 테이블 %d개 (실행 순서)\n\n" % len(B.PLAN)
    lv = collections.defaultdict(list)
    for s, tt, pk, l, c in B.PLAN:
        lv[l].append("%s → %s" % (s, tt))
    t += md_table(["단계", "테이블"],
                  [[k, "<br>".join(v)] for k, v in sorted(lv.items())])
    t += "\n\n단계는 외래키 의존 관계를 위상 정렬한 결과다. 같은 단계 안에서는 순서가 자유롭다.\n"
    t += "\n### 트랙① 이관 제외 테이블\n\n"
    t += md_table(["테이블", "구분", "사유"],
                  [[k, "참조용" if k in B.REFERENCE_ONLY else "제외", v]
                   for k, v in sorted(B.EXCLUDED.items())])
    t += "\n'참조용'은 이관하지 않되 트랙② 수행 시 값을 조회하는 테이블이다.\n"
    w("00_개요.md", t)


# ── 01 / 02 소스 구조 ──────────────────────────────────────────────────
def doc_struct(no, name, schema, title, rowcounts=None, note=""):
    t = header(title, no)
    if note:
        t += note + "\n\n"
    t += "## 테이블 목록 (%d개)\n\n" % len(schema)
    rows = []
    for tb, cols in sorted(schema.items()):
        mark = "이관대상" if tb in PLAN_TABLES else ("제외" if tb in B.EXCLUDED else "-")
        n = rowcounts.get(tb, "") if rowcounts else ""
        rows.append([tb, len(cols), n, mark])
    t += md_table(["테이블", "컬럼수", "레코드수", "이관 여부"], rows)
    t += "\n\n## 테이블별 컬럼\n\n"
    for tb, cols in sorted(schema.items()):
        t += "### %s\n\n" % tb
        t += md_table(["#", "컬럼", "타입"],
                      [[i + 1, c, ty] for i, (c, ty) in enumerate(cols)])
        t += "\n\n"
    w(name, t)


# ── 03 소스 간 차이 ────────────────────────────────────────────────────
def doc03():
    t = header("소스 DB 구조 차이 — MsSQL vs Oracle", "03")
    O, Ms = set(ora), set(ms) - {"sysdiagrams"}
    common = sorted(O & Ms)
    t += """## 1. 결론

두 소스는 **동일한 논리 스키마**다. 공통 테이블 %d개에서 컬럼 구성 차이는 `SELF_DB` 하나뿐이며,
이 컬럼은 신규 체계에서 폐기되므로 이관에 영향이 없다.

따라서 **소스가 어느 쪽이든 본 매핑표가 그대로 적용된다.**

## 2. 테이블 존재 차이

""" % len(common)
    t += "### Oracle에만 존재\n\n"
    t += md_table(["테이블", "컬럼수", "판단"],
                  [[x, len(ora[x]), B.EXCLUDED.get(x, "확인 필요")] for x in sorted(O - Ms)])
    t += "\n\n### MsSQL에만 존재\n\n"
    t += md_table(["테이블", "컬럼수", "판단"],
                  [[x, len(ms[x]), B.EXCLUDED.get(x, "확인 필요")] for x in sorted(Ms - O)])

    t += "\n\n## 3. 컬럼 구성 차이\n\n"
    diffs = []
    for tb in common:
        oc = [c for c, _ in ora[tb]]
        mc = [c for c, _ in ms[tb]]
        only_o = [c for c in oc if c not in mc]
        only_m = [c for c in mc if c not in oc]
        if only_o or only_m:
            diffs.append([tb, ", ".join(only_o) or "-", ", ".join(only_m) or "-"])
    t += md_table(["테이블", "Oracle에만", "MsSQL에만"], diffs)
    t += "\n\n`SELF_DB` 는 구 체계의 자체 DB 식별 플래그로, 신규 체계에서 사용하지 않아 이관 대상이 아니다.\n"

    t += "\n## 4. 암호화 범위 차이 — **주의**\n\n"
    t += """두 소스의 암호화 적용 범위가 다르다.

"""
    enc_rows = []
    for key in sorted(set(B.ENC_MSSQL) | set(B.ENC_ORACLE)):
        tb, c = key.split(".", 1)
        ot = dict(ora.get(tb, [])).get(c, "-")
        mt = dict(ms.get(tb, [])).get(c, "-")
        enc_rows.append([tb, c, ot, mt,
                         "양쪽 암호화" if key in B.ENC_MSSQL and key in B.ENC_ORACLE
                         else ("MsSQL만 암호화" if key in B.ENC_MSSQL else "Oracle만 암호화")])
    t += md_table(["테이블", "컬럼", "Oracle 타입", "MsSQL 타입", "구분"], enc_rows)
    t += """

- **MsSQL** : `ENCRYPTBYKEY` (대칭키 `symmetric_key_AASManager`, 인증서 `certificate_manager`).
  2026-07-22 복호화 검증 완료. 이관 계정에 아래 권한 부여가 전제다.

  ```sql
  GRANT VIEW DEFINITION ON SYMMETRIC KEY::symmetric_key_AASManager TO <이관계정>;
  GRANT VIEW DEFINITION ON CERTIFICATE::certificate_manager TO <이관계정>;
  GRANT CONTROL        ON CERTIFICATE::certificate_manager TO <이관계정>;
  ```

- **Oracle** : `RAW(200)`. `ENCRYPTBYKEY` 와 규격이 다르며 **복호화 방식이 확인되지 않았다.**
  소스로 Oracle을 채택할 경우 선결 과제다. (05 문서 참조)

## 5. 타입 변환

| Oracle | MsSQL | 처리 |
|---|---|---|
| `VARCHAR2(n BYTE)` | `varchar(n)` | 동일 |
| `CHAR(n BYTE)` | `char(n)` / `varchar(n)` | 동일 |
| `NUMBER(*,0)` | `int` | 동일 |
| `NUMBER(p,s)` | `float` / `decimal` | 동일 |
| `NUMBER` (무정밀도) | `int` / `float` | 값 범위 확인 후 결정 |
| `DATE`, `TIMESTAMP(6)` | `datetime2` | 동일 |
| `RAW(n)` | `varbinary(n)` | 암호화 컬럼 — 위 참조 |

### 개별 확인 필요 항목

"""
    t += md_table(["테이블", "컬럼", "Oracle", "MsSQL", "사유"], [
        ["CD_CHANGE_LOG", "DATASET_ID", "NUMBER", "varchar(20)", "숫자 ↔ 문자. 값 형태 확인 필요"],
        ["WEAPON_CARRIER", "CLASS_ID", "VARCHAR2(30)", "int", "**외래키**인데 형이 다름. 원본 설계 결함으로 판단"],
        ["CCF2CD_LOG", "CTRY_CODE", "CHAR(3)", "varchar(2)", "Oracle이 1자 넓음 — 잘림 가능"],
        ["DIST_TO", "DESTINATION", "VARCHAR2(30)", "varchar(15)", "Oracle이 15자 넓음 — 잘림 가능"],
        ["TR_SNIPPET", "SAMPLING_RATE", "NUMBER(6,0)", "float", "정수 ↔ 실수"],
    ])
    w("03_소스간_구조차이.md", t)


# ── 04 매핑 명세 ───────────────────────────────────────────────────────
def doc04():
    t = header("NAIMS → NAIMS2 전 컬럼 매핑 명세", "04")
    t += """## 1. 판정 구분

| 판정 | 의미 | 조치 |
|---|---|---|
| 확정 | 기존 확정본 + 현재 스키마 일치 | 그대로 구현 |
| 제외 | 이관하지 않음으로 확정 | 조치 없음 |
| 미결 | 대응 규칙 미확정 | **담당자 결정 필요** |
| 스키마변경 | 확정된 대상 컬럼이 현재 DB에 없음 | **재확정 필요** |
| 신규제안 | 규칙 기반 제안 | **승인 필요** |

## 2. 공통 규칙

| 규칙 | 내용 |
|---|---|
| 공통1 | `IS_TRAINING` 은 신규 체계 생성 필드로 모두 `0` |
| 공통2 | `DELETED` 는 신규 체계 생성 필드로 모두 `0` |
| 공통3 | 레거시 PK는 `RECORD_ID` 로 승계하지 않고 신규 DB에서 IDENTITY 자동 생성 |
| PK | `<TABLE>_ID` → `RECORD_ID` (IDENTITY, INSERT 목록에서 제외) |
| FK | `<X>_ID` → `REF_STD_<대상테이블>_ID` — 부모 이관 후 신규 RECORD_ID로 치환 |
| 파일 | `*_FILENAME`(경로/이름) → `*_FILE_NAME`(varchar) + `*_FILE`(varbinary, 파일 바이트 적재) |
| 폐기 | `SELF_DB` |

## 3. 테이블별 매핑

"""
    by = collections.OrderedDict()
    for r in M:
        by.setdefault((r["src"], r["tgt"]), []).append(r)

    order = {s: i for i, (s, _, _, _, _) in enumerate(B.PLAN)}
    for (s, tt), rows in sorted(by.items(), key=lambda kv: order.get(kv[0][0], 999)):
        vc = collections.Counter(r["verdict"] for r in rows)
        badge = " / ".join("%s %d" % (k, v) for k, v in vc.most_common())
        t += "### %s → %s\n\n" % (s, tt)
        t += "소스 레코드 %s건 · 매핑 %d행 (%s)\n\n" % (
            format(ms_rows.get(s, 0), ","), len(rows), badge)
        t += md_table(
            ["소스 컬럼", "MsSQL 타입", "Oracle 타입", "대상 컬럼", "대상 타입",
             "변환 규칙", "판정", "비고"],
            [[r["col"], r["mtyp"], r["otyp"], r["tcol"], r["ttyp"],
              r["rule"] or "-", r["verdict"], r["note"] or "-"] for r in rows])
        t += "\n\n"
    w("04_NAIMS2_매핑명세.md", t)


# ── 05 미결사항 ────────────────────────────────────────────────────────
def doc05():
    t = header("미결사항 및 결정 요청", "05")
    need_a = VC.get("미결", 0) + VC.get("스키마변경", 0) + VC.get("신규제안", 0)
    need_b = FVC.get("미결", 0) + FVC.get("스키마변경", 0) + FVC.get("신규제안", 0)
    t += "결정이 필요한 항목은 총 **%d건**이다. (트랙① %d건 / 트랙② %d건)\n\n" % (
        need_a + need_b, need_a, need_b)
    t += "---\n\n# 공통 선결 과제\n\n"
    t += "## 1. 선결 과제\n\n"
    t += """### 1-1. 소스 DB 확정 (MsSQL / Oracle)

두 소스의 논리 구조는 동일하나, **Oracle을 채택하면 `RAW(200)` 복호화 방식 확인이 선결 과제**가 된다.
MsSQL은 복호화 경로가 확보되어 있다(2026-07-22 검증 완료).

| 항목 | MsSQL | Oracle |
|---|---|---|
| 암호화 컬럼 | 9개 | 6개 |
| 복호화 방식 | `ENCRYPTBYKEY` — **확인 완료** | `RAW(200)` — **미확인** |
| 즉시 이관 가능 | 가능 | 불가 |

**요청**: 소스 DB를 어느 쪽으로 확정할지 결정 바랍니다.

### 1-2. 운영 환경 권한

복호화를 위해 이관 계정에 대칭키·인증서 권한이 필요합니다. 시험 환경에서는 부여했으나,
**운영 환경에서 동일한 권한 부여가 가능한지 확인이 필요**합니다.

권한 부여가 불가할 경우 대안:
1. 운영 DBA가 복호화된 평문을 별도 추출 → 이관 도구에 CSV로 주입 (구현 완료)
2. 인증서 백업(`.cer` + `.pvk`) 수령 후 작업 DB에 복원

"""
    t += "---\n\n# 트랙① DB → DB 구간\n\n"
    t += "## 2. 대상 스키마 변경으로 재확정이 필요한 항목 (%d건)\n\n" % VC.get("스키마변경", 0)
    t += ("2026-04 확정 시점 이후 NAIMS2 스키마가 변경되어, 확정된 대상 컬럼이 현재 존재하지 않습니다.\n\n")
    chg = [r for r in M if r["verdict"] == "스키마변경"]
    bytab = collections.defaultdict(list)
    for r in chg:
        bytab[r["src"]].append(r)
    for s, rows in sorted(bytab.items()):
        t += "### %s → %s (%d건)\n\n" % (s, rows[0]["tgt"], len(rows))
        cur = [c for c, _ in tg.get(rows[0]["tgt"], [])]
        t += "현재 대상 테이블 컬럼: `%s`\n\n" % "`, `".join(cur)
        t += md_table(["소스 컬럼", "MsSQL 타입", "확정되었던 대상 컬럼", "현황"],
                      [[r["col"], r["mtyp"], r["tcol"], "현재 스키마에 없음"] for r in rows])
        t += "\n**요청**: 위 컬럼들을 (a) 신규 대상 컬럼으로 재지정할지 (b) 이관 제외로 확정할지 결정 바랍니다.\n\n"

    t += "## 3. 대응 규칙 미확정 항목 (%d건)\n\n" % VC.get("미결", 0)
    und = [r for r in M if r["verdict"] == "미결"]
    t += md_table(["소스 테이블", "소스 컬럼", "대상 테이블", "대상 컬럼", "사유"],
                  [[r["src"], r["col"], r["tgt"], r["tcol"], r["note"] or "-"] for r in und])

    t += "\n\n## 4. 승인 요청 — 규칙 기반 신규 제안 (%d건)\n\n" % VC.get("신규제안", 0)
    prop = [r for r in M if r["verdict"] == "신규제안"]
    t += md_table(["소스 테이블", "소스 컬럼", "대상 컬럼", "적용 규칙", "비고"],
                  [[r["src"], r["col"], r["tcol"], r["rule"], r["note"] or "-"] for r in prop])

    t += "\n\n## 5. 기존 확정본에 기록된 미결 항목 (DB→DB 구간)\n\n"
    if PEND:
        t += md_table(["우선순위", "소스 테이블", "소스 컬럼", "대상 테이블", "보류 사유"],
                      [[p.get("Priority", ""), p.get("SourcePatternOrTable", ""),
                        p.get("SourceField", ""), p.get("TargetTable", ""),
                        p.get("PendingReason", "")] for p in PEND])

    # ── 트랙② ──
    t += "\n\n---\n\n# 트랙② 파일 → DB 구간\n\n"

    fchg = [r for r in FM if r["verdict"] == "스키마변경"]
    t += "## 6. 대상 스키마 변경으로 재확정이 필요한 항목 (%d건)\n\n" % len(fchg)
    t += ("확정 시점 이후 NAIMS2 스키마가 변경되어, 확정되었던 대상 테이블/컬럼이 "
          "현재 존재하지 않습니다.\n\n")
    t += md_table(["파일", "소스 항목", "확정되었던 대상", "현황"],
                  [[r["ent"], r["fld"], "%s.%s" % (r["tgt"], r["tcol"]), r["note"]]
                   for r in fchg])
    t += "\n### 참고 — 현재 NAIMS2 의 해당 테이블\n\n"
    hint = []
    for name in sorted({r["tgt"] for r in fchg}):
        if name in tg:
            hint.append([name, "존재", ", ".join(c for c, _ in tg[name])])
        else:
            cand = [x for x in tg if name.split("_")[-1] in x or x.endswith(name.split("_", 1)[-1])]
            hint.append([name, "**없음**",
                         ("유사 테이블: " + ", ".join(sorted(cand)[:4])) if cand else "유사 테이블 없음"])
    t += md_table(["확정되었던 대상 테이블", "현황", "현재 컬럼 / 유사 테이블"], hint)
    t += "\n**요청**: 위 항목들을 (a) 현재 스키마의 어느 테이블·컬럼으로 재지정할지, 또는 (b) 이관 제외로 확정할지 결정 바랍니다.\n\n"

    fund = [r for r in FM if r["verdict"] == "미결"]
    t += "## 7. 대응 규칙 미확정 항목 (%d건)\n\n" % len(fund)
    t += md_table(["파일", "파싱 객체", "소스 항목", "대상 테이블", "대상 컬럼", "사유"],
                  [[r["ent"], r["obj"], r["fld"], r["tgt"], r["tcol"],
                    r["note"] or r["rule"] or "-"] for r in fund])

    t += "\n\n## 8. 참고 — 기존 확정 결정 규칙\n\n"
    t += md_table(["구분", "내용"],
                  [[d.get("구분", ""), d.get("내용", "")] for d in DEC])
    w("05_미결사항_및_결정요청.md", t)


# ── 06 파일 이관 매핑 ──────────────────────────────────────────────────
def doc06():
    t = header("파일 → NAIMS2 이관 매핑 명세 (트랙②)", "06")
    t += """## 1. 범위

원음 자료 파일을 파싱해 NAIMS2 에 적재하는 구간이다. 소스가 DB 테이블이 아니라
파일이므로, 소스측은 **파일 패턴 · 파싱 객체 · 필드** 로 표기한다.

`RAW_*` · `PREC_*` · `EXTRACT_DATA_STORE` 는 **전량 이 트랙으로 적재**되며,
DB→DB 이관(트랙①) 대상이 아니다.

| 구분 | 소스 | 행수 |
|---|---|---|
| 단주기(.sd) | 단주기 원음 자료 패키지 | %d |
| 협대역(.nb) | 협대역 원음 자료 패키지 | %d |

## 2. 구 DB 참조 테이블

본 트랙 수행 시 구 NAIMS DB 의 아래 테이블을 **조회**한다. 이관 대상은 아니다.

""" % (sum(1 for r in FM if r["kind"].startswith("단주기")),
       sum(1 for r in FM if r["kind"].startswith("협대역")))
    t += md_table(["테이블", "MsSQL 레코드수", "역할"],
                  [[k, ms_rows.get(k, "-"), v] for k, v in sorted(B.REFERENCE_ONLY.items())])
    t += """

**요청**: 위 테이블을 어느 시점에 어떤 키로 대조하는지(파일명 / 임무명 / 반입일자 등)
구체적인 조회 규칙 확정이 필요합니다.

## 3. 소스 파일별 대상 테이블

"""

    ent_tgt = collections.OrderedDict()
    for r in FM:
        ent_tgt.setdefault((r["kind"], r["ent"]), collections.Counter())[r["tgt"]] += 1
    t += md_table(["구분", "소스 파일", "대상 테이블 (행수)"],
                  [[k[0], k[1], ", ".join("%s(%d)" % (a, b) for a, b in v.most_common())]
                   for k, v in ent_tgt.items()])

    t += "\n\n## 3. 대상 테이블별 집계\n\n"
    tc = collections.Counter(r["tgt"] for r in FM)
    t += md_table(["대상 테이블", "행수", "현황"],
                  [[k, v, "정상" if k in tg else ("대상 미지정" if k == "-" else "**현재 NAIMS2 에 없음**")]
                   for k, v in sorted(tc.items())])

    t += "\n\n## 4. 매핑 상세\n\n"
    by = collections.OrderedDict()
    for r in FM:
        by.setdefault((r["kind"], r["ent"]), []).append(r)
    for (kind, ent), rows in by.items():
        vc = collections.Counter(r["verdict"] for r in rows)
        t += "### [%s] %s\n\n" % (kind, ent)
        t += "경로 패턴: `%s` · %d행 (%s)\n\n" % (
            rows[0]["pat"], len(rows),
            " / ".join("%s %d" % (k, v) for k, v in vc.most_common()))
        t += md_table(["파싱 객체", "소스 항목", "대상 테이블", "대상 컬럼", "대상 타입",
                       "변환 규칙", "판정", "비고"],
                      [[r["obj"], r["fld"], r["tgt"], r["tcol"], r["ttyp"],
                        r["rule"], r["verdict"], r["note"] or "-"] for r in rows])
        t += "\n\n"
    w("06_파일이관_매핑명세.md", t)


if __name__ == "__main__":
    print("generating markdown...")
    doc00()
    doc_struct("01", "01_소스DB구조_MsSQL.md", ms, "소스 DB 구조 — MsSQL (NAIMS)", ms_rows,
               "192.168.111.39 의 NAIMS DB를 2026-07-22 직접 조회한 결과다.")
    doc_struct("02", "02_소스DB구조_Oracle.md", ora, "소스 DB 구조 — Oracle (NAIMS)", None,
               "제공된 Oracle DDL 스캔본(25페이지)을 전사한 것이다. "
               "저장 옵션(TABLESPACE/STORAGE 등)은 이관과 무관하여 생략했다.")
    doc03()
    doc04()
    doc05()
    doc06()
    print("done")
