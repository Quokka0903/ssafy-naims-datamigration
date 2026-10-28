# 매핑 문서 재생성 방법

`docs/` 아래 문서는 전부 이 폴더의 스크립트로 생성된다. **문서를 직접 고치지 말고
입력 데이터나 규칙을 고친 뒤 재생성한다.**

## 실행 순서

```bash
cd docs/schema
python parse_n2script.py      # N2script_260723.sql(UTF-16) → naims2_schema.txt (대상 스키마 권위)
python export_baseline.py     # 확정 워크북 830행 → std / file / decisions / pending .tsv
python merge_mapping.py       # 트랙① 기준선 × 현재 스키마 → mapping_merged.tsv
python merge_file_mapping.py  # 트랙② 기준선 × 현재 스키마 → file_merged.tsv
python audit_coverage.py      # 누락 감사 — 반드시 "누락 없음" 이어야 함
python gen_docs.py            # → docs/00~06_*.md
python gen_xlsx.py            # → docs/NAIMS_이관_매핑_검토본_*.xlsx
python gen_pdf.py             # → docs/NAIMS_이관_매핑_검토본_*.pdf
python gen_compare.py         # → docs/07_3DB_테이블비교.md + NAIMS_3DB_테이블비교_*.pdf
python gen_colcompare.py      # → docs/08_3DB_컬럼매핑.md + NAIMS_3DB_컬럼매핑_*.pdf
```

**대상 스키마 출처**: `naims2_schema.txt` 는 라이브 DB 덤프가 아니라
`Z:\N2script_260723.sql`(SSMS 생성 스크립트, UTF-16)을 `parse_n2script.py` 로
파싱한 것이 기준이다. 스크립트가 갱신되면 `parse_n2script.py` 를 다시 돌린다.

**2026-07-23 사용자 확정 교정** (merge_mapping.py / build_mapping.py):
- 소스 PK → 타겟 `RECORD_ID` 는 같은 행에 두되 판정 '자동생성', 규칙 '제외 (신규 RECORD_ID 자동생성)'.
  실제 PK 는 소스 스키마의 PRIMARY KEYS 를 기준으로 판정한다(PLAN 의 PK명은 부정확할 수 있음).
- `DELETED` 는 상수 0 이 아니라 레거시 값을 형 변환(int→smallint 등)해 승계. 0=정상, 1=임시삭제.
- `STD_VESSEL_DATA.HAS_CLUTTER` 는 기본값 0 (`build_mapping.TARGET_FIXED_DEFAULT`).

`gen_xlsx.py` / `gen_pdf.py` 는 `gen_docs.py` 를 import 하므로 단독 실행해도 된다.

**`audit_coverage.py` 가 실패하면 문서를 배포하지 않는다.** 검사 항목:

| 코드 | 검사 |
|---|---|
| A | 소스 테이블이 전부 이관대상/제외로 분류되었는가 |
| B | 이관대상 테이블의 소스 컬럼이 전부 매핑표에 있는가 (MsSQL·Oracle 양쪽) |
| C | 대상 테이블의 컬럼이 전부 매핑표에 있는가 |
| D | 매핑표가 존재하지 않는 컬럼/테이블을 가리키지 않는가 |
| E | 제외 테이블에 사유가 기재되어 있는가 |
| F | 기준 워크북 830행이 전량 반영되었는가 |
| G | 파일 구간 대상 테이블 부재가 이슈로 표기되었는가 |

## 이관 범위 — 두 트랙

| 트랙 | 소스 | 대상 | 기준선 | 문서 |
|---|---|---|---|---|
| ① DB → DB | 구 NAIMS DB (MsSQL/Oracle) | `STD_*` | 482행 | 04 |
| ② 파일 → DB | `.sd` 단주기 / `.nb` 협대역 | `RAW_*`, `PREC_*`, `EXTRACT_DATA_STORE` | 348행 | 06 |

## 파일 구성

### 입력 (실측 데이터)

| 파일 | 출처 | 갱신 방법 |
|---|---|---|
| `mssql_naims_schema.txt` | 192.168.111.39 / NAIMS | `dump_mssql.ps1` |
| `naims2_schema.txt` | 192.168.200.205 / NAIMS2 | `dump_naims2.ps1` |
| `oracle_naims_schema.txt` | `260420_NAIMS_SQL.pdf` 25p 스캔본 전사 | 수작업 전사 |

MsSQL/NAIMS2 스키마는 서버가 바뀌면 PowerShell 스크립트를 다시 돌린다.
Oracle 은 스캔본 전사라 자동 갱신이 안 된다 — 원본 DDL 을 확보하면 교체할 것.

### 기준선 (권위본)

`NAIMS_마이그레이션_정리_최종준비본.xlsx` 의 `03_마이그레이션_매핑` 중 `구분='STD'` 482행.
**이 워크북이 우선한다.** 생성 스크립트는 이것을 덮어쓰지 않고 정합성 검증만 덧붙인다.

### 규칙

| 파일 | 역할 |
|---|---|
| `build_mapping.py` | 이관 계획(PLAN)·제외목록·별칭·수동대응(MANUAL)·타입 계열 판정 |
| `merge_mapping.py` | 기준선과 현재 스키마 대조, 공백 구간 규칙 기반 보완 |

## 규칙 단일 원본 (SSOT) — 2026-07-23

매핑 규칙은 **`RawCollectMigrator/Features/DbToDb/mapping_rules.json` 하나**가 원본이다.
Python 문서 파이프라인과 C# 실행기가 **같은 JSON** 을 읽으므로 더 이상 수동 동기화가 필요 없다.

```
mapping_rules.json  ← 규칙 수정은 여기서
   ├─ build_mapping.py  (_load_rules_from_json 으로 로드 → 문서 생성)
   └─ MigrationRules.cs (System.Text.Json 으로 로드 → 실행기)
```

- `build_mapping.py` 상단의 Python 리터럴은 **최초 부트스트랩 시드**일 뿐, import 시 JSON 값으로 덮어써진다.
- JSON 을 새로 만들려면(시드 → JSON): `python dump_rules_json.py`
- 규칙을 바꾸면: `mapping_rules.json` 편집 → 문서 파이프라인 재실행 + C# 재빌드.
- JSON 항목: `plan / excluded / referenceOnly / dropped / aliases / targetOnlyNotes /
  manual / columnRules / targetFixedDefault / classesFillDefault / encrypted /
  gramTypeMap / refTargetTypeCodes`.
- FK 위상 정렬은 `MigrationPlan.cs` 가 담당(안정적이라 코드 유지).

### 검증 도구

| 파일 | 역할 |
|---|---|
| `compare_oracle_mssql.py` | 두 소스 스키마의 테이블/컬럼/타입 차이 대조 |
| `merge_report.txt` | 정합성 이슈 목록 (재생성 시 갱신) |

## 담당자 피드백 반영

1. 배포한 xlsx 의 `결정요청` 시트에서 [결정] 열을 회수한다.
2. 확정된 내용을 기준 워크북 `03_마이그레이션_매핑` / `09_결정규칙` 에 반영한다.
   (규칙으로 일반화 가능한 것은 `build_mapping.py` 의 `ALIASES` / `MANUAL` 에 반영)
3. 위 실행 순서를 다시 돌려 문서를 재생성한다.
