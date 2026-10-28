# 현장 일회성 마이그레이션 런북 (NAIMS N1 Oracle → N2)

> 현장에서 사전 리허설 불가 → 이 절차는 **오프라인 리그(NAIMS12 12c)로 전 구간 예행 완료**된 것을 그대로 재생하는 것.
> 검증 완료(2026-08-06): classic exp→imp 왕복, DES('hellios#') 복호(.NET 바이트일치), 한글 CP949 왕복, 마이그레이터 ODBC 읽기+복호.

## 0-A. 비밀번호 취급 규칙

계정을 두 부류로 나눠 다르게 다룬다. 섞으면 도구가 못 쓰게 되거나, 반대로 실계정이 새어 나간다.

| 부류 | 계정 | 취급 |
|---|---|---|
| **로컬 리그** | `NAIMS12` SYSTEM = `Naims12#2026`<br>`LEGACYNAIMS` = `naims2!`<br>`REH`(21c XE) = `Reh2026x` | 이 저장소 스크립트가 만든 **격리 DB**. 값을 잃으면 리그를 못 쓰므로 **스크립트·문서에 기본값으로 적어 둔다.** |
| **현장·실운영** | `NAIMS2DB`(N2 적재), `NaimsMig`(구 NAIMS 읽기) | 실제 운영 자산. **기본값을 두지 않는다** — 환경변수나 입력으로만 받는다. |

현장에서 값을 갈아끼울 때는 스크립트를 고치지 말고 환경변수로 덮어쓴다(항상 환경변수가 기본값보다 우선):

```powershell
$env:NAIMS12_SYS_PW = '...'   # NAIMS12 SYSTEM        (없으면 기본값 사용)
$env:LEGACYNAIMS_PW = '...'   # LEGACYNAIMS 스키마     (없으면 기본값 사용)
$env:NAIMS_MSSQL_PW = '...'   # NaimsMig  — docs/schema/dump_mssql.ps1  (기본값 없음)
$env:NAIMS2_PW      = '...'   # NAIMS2DB  — docs/schema/dump_naims2.ps1 (기본값 없음)
```

- 해석 순서: **파라미터 → 환경변수 → 기본값(로컬 리그만) → 화면 입력** (`migration_rig/_secrets.ps1`)
- 기본값이 없는 계정은, 무인 실행(비대화형)에서 환경변수도 없으면 **명확한 오류로 멈춘다**
- SQL 스크립트는 `ACCEPT … DEFAULT … HIDE` — Enter 만 치면 리그 기본값이 쓰인다
- 마이그레이터(RawCollectMigrator)는 시작 화면에서 입력 → `비밀번호까지 저장` 체크 시
  **Windows DPAPI(현재 사용자 키)로 암호화**해 `config\connections.json` 에 보관 (평문 저장 경로 없음)
- 로그·보고서에 찍히는 접속문자열은 항상 `Password=***` 로 마스킹된다 (`Hide-Secret` / `MaskConn`)

## 0. 사전 확인 (현장 도착 시)
- 현장 N1 Oracle 버전: **12c R2(12.2.0.x) 예상** (R1도 대응 가능 — 논리 imp라 릴리스 무관).
- 접속 방식: **ODBC DSN "naims"**(system/naims), 스키마 소유자 **SYSTEM**.
- 백업 방식: 앱 백업버튼 또는 `DB관련자료\backup.bat` = **classic `EXP`** (Data Pump/RMAN 아님).

## 1. 소스 백업 확보 (필드에서)
현장 관행대로 classic exp 실행 (예: backup.bat). 산출물 = per-table `.DMP` 또는 `FULLBACKUP.DMP`.
```
EXP USERID=SYSTEM/<pw> FILE=D:\...\OracleTable_Classes.DMP TABLES=(SYSTEM.CLASSES)   ... (34개 테이블)
```
※ 만약 현장이 expdp로 바꿨으면 → 임포트를 `impdp`로 (2순위, 21c/12c 모두 지원).

## 2. 로컬(반출 장비 = 12c)로 임포트
장비에 **Oracle 12c(NAIMS12) + classic imp** 준비돼 있음. classic exp 덤프는 **반드시 `imp`**(impdp 불가).
```
# SYSTEM 스키마로 그대로 복원(권장, 필드와 동일):
imp USERID=system/Naims12#2026@localhost:1522/NAIMS12 FILE=<받은>.DMP FULL=Y
# 또는 별도 스키마로 리마스터:
imp USERID=system/... FILE=<받은>.DMP FROMUSER=SYSTEM TOUSER=NAIMS
```
- **ORA-01950(tablespace 쿼터) 나면** → 대상유저에 `GRANT UNLIMITED TABLESPACE` 후 재임포트.
- 다중 .DMP면 테이블별로 반복(레거시 recovery.bat과 동일 구조).

## 3. 복호 준비 (이름 컬럼 DES)
NAIMS12엔 이미 `SYSTEM.CRYPTO`/`SYSTEM.CRYPTIT` 패키지(DES 키 `hellios#`)가 설치돼 있음(rig_10).
- 12c엔 `DBMS_OBFUSCATION_TOOLKIT` 존재 → 패키지 그대로 동작.
- **주의**: 최종 타깃이 21c면 OBFUSCATION_TOOLKIT 제거됨 → C# CryptoContext(.NET DES 'hellios#')로 복호(바이트 일치 검증됨) 또는 DBMS_CRYPTO 재구현.
```sql
SELECT SYSTEM.CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)) FROM classes;  -- 복호 확인
```

## 4. 이관 실행 (RawCollectMigrator)
기본 연결값 이미 세팅됨(`DbProvider.cs`): 소스=ODBC `DSN=naims;Uid=system;Pwd=Naims12#2026;`, 타깃=N2 MsSQL.
1. 소스 Oracle 라디오 + ODBC 모드, 소스 연결문자열 확인(DSN naims).
2. **DryRun** — 읽기+매핑 검증(쓰기 없음).
3. **Trial** — INSERT 후 롤백(타깃 검증). ⚠️ N2가 실운영이면 시퀀스 갭 주의.
4. **Commit** — 실제 적재. ⚠️ 타깃 N2 정체(테스트/실운영) 반드시 확인 후.

## 5. 검증 & 롤백
- 행수 대조(소스 vs N2), 이름 복호 표본 확인, 한글 표시 확인.
- 롤백: Commit 전이면 무해. Commit 후 문제 시 N2측 트랜잭션/백업 기준 복구.

## 접속 요약
| 대상 | 값 |
|---|---|
| 로컬 12c | `system/Naims12#2026@localhost:1522/NAIMS12`, DSN `naims`, 소유자 SYSTEM |
| DES 키 | `hellios#` (RPAD('hellios',8,'#')), CBC, zero-IV, 8배수 공백패딩, 복호후 trim, CP949 |
| 타깃 N2 | MsSQL 192.168.200.205:1433 / NAIMS2 |

## 리그 자산
- `D:\OraInstall\create_naims12_manual_ELEVATED.ps1` — 12c DB 재생성(관리자)
- `D:\OraInstall\rig_10.sql` — CryptIT/CRYPTO 패키지 + .NET 교차검증
- `D:\OraInstall\rig_20b.sql` — SYSTEM 스키마 필드 재현(암호화)
- `D:\OraInstall\cleanup_21c_ELEVATED.ps1` — 21c 좀비 정리(관리자)
- 21c 보존 덤프: `D:\NAIMS2_Payload\dump\preserve_preswap_20260806\`
