# 리허설 RUNBOOK — 가짜 레거시 Oracle(12c) → 우리 도구 → NAIMS2

실제 레거시 Oracle 덤프가 없어, **Oracle 12c에 가짜 레거시 소스를 세우고** 우리 이관기
(RawCollectMigrator)의 **ODBC + CRYPTO.DECRYPT 경로**를 실전과 동일하게 리허설한다.

```
[Oracle 12c = 가짜 레거시]  --(우리 도구: ODBC, DSN=naims, CRYPTO.DECRYPT)-->  [NAIMS2 MsSQL]
   SYSTEM 스키마 / 이름컬럼 암호화 / CRYPTO 패키지
```

> ⚠️ 두 지점은 사람 손이 필요하다(설치 GUI·서버 접근): **① 12c 설치**, **② NAIMS2 쓰기**.
> 나머지(스키마·CRYPTO·시드·DSN·도구 실행)는 이 문서의 명령을 그대로 실행하면 된다.

---

## 0. 준비물

- `D:\다운로드\V839963-01.zip` (Oracle DB 12.2 서버) — **이것만 설치**하면 됨
- (선택) `V980724-01.zip` (Client) — DB와 다른 PC에서 도구를 돌릴 때 ODBC 드라이버용
- 이 폴더의 스크립트: `01_schema.sql`, `02_crypto_pkg.sql`, `03_seed_demo.sql`

---

## 1. Oracle 12c 설치 (사람 손)

`V839963-01.zip` 압축 해제 → `database\setup.exe` 실행. GUI에서:

| 항목 | 값 |
|---|---|
| 옵션 | **단일 인스턴스 DB 생성 및 구성** (Create and configure) |
| 시스템 클래스 | Desktop class |
| 글로벌 DB 이름 / SID | `orcl` (예시, 아래 tnsnames 와 맞추면 됨) |
| 컨테이너 DB | 만들려면 PDB 하나(`orclpdb`) — 단순하게 non-CDB 도 가능 |
| **문자셋** | **AL32UTF8** (한글 안전) |
| 관리자 비밀번호 | **`naims`** ← 레거시가 `system/naims`로 붙으므로 SYSTEM 암호를 naims 로 |

> non-CDB 로 만들면 서비스명=`orcl`. PDB 로 만들면 서비스명=`orclpdb` (아래 tnsnames 를 그 값으로).

설치 후 확인:
```bash
sqlplus system/naims@localhost:1521/orcl
```
접속되면 성공. (PDB면 `.../orclpdb`)

---

## 2. 스키마 · CRYPTO · 시드 적재

`sqlplus` 로 SYSTEM 접속 후 순서대로 실행. (경로는 이 폴더 기준)

```bash
cd "F:/JKYi/2026/260109 DB임플란트/rehearsal"
sqlplus system/naims@localhost:1521/orcl @01_schema.sql
sqlplus system/naims@localhost:1521/orcl @02_crypto_pkg.sql
sqlplus system/naims@localhost:1521/orcl @03_seed_demo.sql
```

- `02_crypto_pkg.sql` 끝에 **왕복검증**이 찍힌다: `CRYPTO 왕복검증: [충무공이순신급] -> [충무공이순신급] OK` 가 나와야 함.
- `03_seed_demo.sql` 끝의 SELECT 에서 이름이 **평문 한글**로 나오면 시드 성공.

> ⚠️ `02` 실행 시 `DBMS_OBFUSCATION_TOOLKIT` 관련 오류가 나면 → 그 DB가 21c 이상이라 툴킷이
> 제거된 것. **반드시 12c(이하)** 여야 한다.

---

## 3. ODBC DSN `naims` 등록 (레거시와 동일한 이름)

먼저 tnsnames 별칭을 만든다. `%ORACLE_HOME%\network\admin\tnsnames.ora` 에 추가
(설치 시 대개 이미 `ORCL` 항목이 있음 — 없으면):

```
NAIMS =
  (DESCRIPTION =
    (ADDRESS = (PROTOCOL = TCP)(HOST = localhost)(PORT = 1521))
    (CONNECT_DATA = (SERVICE_NAME = orcl))   -- PDB면 orclpdb
  )
```

그다음 64비트 시스템 DSN 등록 (PowerShell, 관리자):
```powershell
# 설치된 Oracle ODBC 드라이버 이름 확인
Get-OdbcDriver | Where-Object Name -like 'Oracle*' | Select Name

# DSN 등록 (DriverName 은 위에서 확인한 실제 이름으로)
Add-OdbcDsn -Name "naims" -DriverName "Oracle in OraDB12Home1" `
  -DsnType System -Platform "64-bit" `
  -SetPropertyValue @("SERVER=NAIMS", "DBQ=NAIMS")
```
> `SERVER`/`DBQ` 값은 tnsnames 별칭(`NAIMS`)이다. 드라이버마다 속성명이 다를 수 있으니
> 안 되면 `odbcad32.exe`(64비트) → 시스템 DSN → Oracle 드라이버로 GUI 등록해도 된다.
> TNS Service Name = `NAIMS`, User ID = `system`.

검증:
```bash
# 우리 도구의 ODBC 목록 버튼으로도 보이고, 아래로도 확인
powershell -Command "Get-OdbcDsn -Name naims"
```

---

## 4. 우리 도구로 리허설 (RawCollectMigrator)

1. `RawCollectMigrator` 실행 → **DB→DB** 탭.
2. **소스 DB**: `Oracle`, **접속 방식**: `ODBC (기본)`.
3. 소스 접속문자열: `DSN=naims;Uid=system;Pwd=naims;` (기본 템플릿 그대로)
   - `🔌 연결 시험` → 성공 확인. (안 되면 `🔎 ODBC 목록`에서 DSN `naims` 확인)
4. **`🔐 Oracle 암호화 추출`** 클릭 → 로그에 `CRYPTO.DECRYPT` 채택(점수≈1.00)이 뜨면
   복호화 식이 세션에 적용된 것.
5. 타겟: NAIMS2 (또는 **리허설용 스크래치 MsSQL**). 되도록 실 NAIMS2 대신 빈 테스트 DB 권장.
6. **`시험 실행(Trial)`** 먼저 — 실제 INSERT 를 트랜잭션 안에서 하고 롤백한다(영구반영 X).
   - 로그에서 CLASSES/VESSEL/ACTIVE_SONAR/TRANSIENT 가 차단 없이 적재되는지,
     이름이 평문으로 들어가는지(미리보기 전/후) 확인.
7. 이상 없으면 `실제 이관(Commit)`.

**성공 판정**: 소나명·천이명·함정명이 NAIMS2 에 **평문 한글**로 안착 → ODBC + CRYPTO.DECRYPT
전 구간(암호화 대상 4개 테이블 전량)이 검증된 것.

---

## 5. (선택) 데모 대신 실데이터로 시드

MsSQL NAIMS(192.168.111.39) 의 실데이터로 12c 를 채우려면:
- MsSQL 이름은 `ENCRYPTBYKEY` 암호화라 **평문 확보에 대칭키 GRANT 가 필요**(메모리 기록).
  - 복호화 가능하면 → 실명으로 시드.
  - 불가하면 → 데모(합성 이름) 리허설로 충분(파이프라인 검증엔 지장 없음).
- 시드 생성기(MsSQL 읽기 → `INSERT ... CRYPTO.ENCRYPT(name)` Oracle 스크립트 출력)는
  요청 시 별도 작성. (지금은 데모 시드로 즉시 리허설 가능)

---

## 6. 트러블슈팅

| 증상 | 원인 / 조치 |
|---|---|
| `02` 에서 툴킷 오류 | DB가 21c+ → 12c 로 재설치 |
| DSN 목록에 naims 없음 | 32/64 비트 불일치 — 64비트 odbcad32/Add-OdbcDsn 사용 |
| 이름이 깨져 이관됨 | `🔐 Oracle 암호화 추출` 을 안 돌렸거나 CRYPTO 패키지 미생성 |
| VARCHAR2 이름 INSERT 초과 | 암호문 hex 가 길이 2배 — 이름을 짧게(≤20자) |
| 연결 실패 | tnsnames 별칭·SERVICE_NAME·SYSTEM 암호(naims) 확인 |
