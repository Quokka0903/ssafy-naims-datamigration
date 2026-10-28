# Oracle Test Dump 12c — W/S 12c 소스(LegacyNaims) 구축 & 덤프 복구 패키지

출장지 W/S에 **Oracle 12c를 처음부터 설치**하고, **LEGACYNAIMS 스키마 덤프**(실 MsSQL 데이터 + DES 암호화 이름)를 복구해, 마이그레이션 도구(RawCollectMigrator)가 **ODBC(DSN=LegacyNaims)** 로 바로 붙게 만든다.
**이 README 하나만 순서대로 따라 하면 처음부터 끝까지 완료된다.**

---

## 0. 준비물 확인 (먼저 점검)

| 항목 | 요구 |
|---|---|
| 권한 | **관리자 권한 PowerShell** (설치·DB생성 단계 필수) |
| 디스크 | **D: 드라이브** + 여유공간 **15GB↑** (설치본 압축해제 + Oracle 홈 + DB) |
| 경로 | 설치 경로에 **한글 금지**(설치기 멈춤). 이 패키지도 ASCII 경로에 두기 |
| .NET | 마이그레이터 실행용(자체포함 exe면 불필요) |

> **D: 드라이브가 없으면?** 두 파일의 경로를 존재하는 드라이브로 **똑같이** 바꾼 뒤 진행:
> 1. `scripts\db_install_naims12.rsp` 의 `ORACLE_HOME=` / `ORACLE_BASE=`
> 2. `scripts\00_install_software_ELEVATED.ps1` 의 `$OH`, `$extract`
> 3. `scripts\01_install_and_createdb_ELEVATED.ps1` 의 `$OH`, `$base`
> (세 파일의 홈 경로가 **반드시 일치**해야 함)

---

## 1. 패키지 구성

```
Oracle Test Dump 12c/
  installer/  V839963-01.zip                       ← Oracle DB 12.2.0.1 (Win x64) 설치본(2.8GB)
  dump/       legacynaims.dmp                       ← LEGACYNAIMS 스키마 Data Pump 덤프(실데이터+암호화)
  scripts/
     95_check_existing_oracle.ps1        ★ 기존 Oracle 점검→판정(설치 전 먼저 실행 권장, 읽기전용)
     00_install_software_ELEVATED.ps1   (관리자) ★소프트웨어 설치 — 이걸 먼저!
     01_install_and_createdb_ELEVATED.ps1 (관리자)  NAIMS12 DB 생성. -OracleHome 로 기존 홈 채택 가능
     db_install_naims12.rsp                        무인 설치 응답파일
     02_setup_legacynaims.sql            (system)   유저 LEGACYNAIMS + 서비스 별칭 + 권한
     03_crypto_packages.sql              (system)   CryptIT/CRYPTO + PUBLIC SYNONYM(키 hellios#)
     04_restore_dump.ps1                            impdp 로 legacynaims.dmp 복구(패키지 덤프)
     04b_restore_anydump.ps1             ★ 아무 덤프나 복구(classic exp/Data Pump 자동판별·현장 실덤프용)
     05_register_dsn.ps1                 (비관리자)  User DSN 'LegacyNaims' 등록
     05b_fix_dsn.ps1                     ★ DSN 문제 복구(드라이버 탐지·재등록·HKCU 그림자 정리)
     96_start_db_listener.ps1            (관리자) ★ "Network Adapter" 에러 해결(DB/리스너 시작·검증)
     09_make_baseline.ps1                (정상PC) 정상 홈 지문 생성 → baseline.json (98이 대조에 사용)
     10_export_oracle_home.ps1           (정상PC) 정상 홈을 통째로 내보내기 → home_image\
     11_transplant_oracle_home.ps1       (관리자) ★ 설치기 없이 홈 이식(설치 반복 실패 시 최종 수단)
     12_set_install_drive.ps1            (관리자) ★ 설치 드라이브 변경(exFAT/USB/D:없음 대응, 경로 일괄 수정)
     13_register_home.ps1                (관리자) ★ 홈 레지스트리 등록(Error 57 자동복구). 00·01·11·99가 자동 호출
     14_install_vcredist.ps1             (관리자) ★★ VC++ 2013 x64 런타임 설치(Oracle 12.2 필수). 00이 설치 전 자동 수행
  installer/  vcredist_x64.exe          VC++ 2013 x64 재배포(오라클 미디어 동봉본, 오프라인 설치용)
     baseline.json                       정상 홈 기준선(파일수·크기·핵심파일)
     97_fix_user_and_crypto.sql          (system) ★ 02/03 복구(유저·CRYPTO 재생성 + F45F 자체검증)
     98_diagnose.ps1                     ★ 진단(설치완결성·DB·CRYPTO·DSN 한 번에, 영문 출력)
     99_fix_reinstall.ps1                (관리자)  ★ 설치 실패/미완료 복구(정리 후 00 재설치)
     99_verify.ps1                                  접속 + 복호 검증
     90_developer_queries.sql            ★ SQL Developer 업무 확인 쿼리 모음
     rig_10_crypto_reference.sql                    crypto 원본 참조
     ref_*.ps1                                      데이터 재구축 참조(복구엔 불필요)
   ※ 모든 .ps1/.sql 콘솔 출력은 영문(깨짐 방지). 막히면 먼저 98_diagnose.ps1.
  docs/       OFFLINE_WORKSTATION_GUIDE.md, FIELD_RUNBOOK.md
```

---

## 2. 설치·복구 순서 (W/S에서 그대로 실행)

> 아래 명령은 이 패키지 폴더 안에서 실행한다고 가정한다. 먼저 이 폴더로 이동:
> `cd "C:\...\Oracle Test Dump 12c"` (실제 복사한 위치로)

### 0-1단계 — **기존 Oracle 점검** (설치 전에 먼저, 읽기전용)
```
powershell -ExecutionPolicy Bypass -File scripts\95_check_existing_oracle.ps1
```
W/S에 **이미 Oracle이 깔려 있을 수 있으므로** 먼저 점검한다. 이 스크립트가 모든 Oracle 홈·버전·서비스·리스너를 조사해 **어떻게 할지(판정)** 를 콕 집어 출력한다:
- 우리 홈이 정상 + NAIMS12 열림 → 바로 `99_verify`/마이그레이터
- 우리 홈 정상 + DB만 정지 → **서비스 시작**(재생성 아님)
- 우리 홈 불완전 → `99_fix_reinstall`
- **다른 경로에 기존 12c/19c** → 채택: `01 -OracleHome "<경로>"` / 또는 무시하고 우리 것 설치(공존)
- **21c만 있음** → 경고(암호화 툴킷 없음) → 우리 12c 설치 권장
- 아무것도 없음 → `00`부터

### 0단계 — Oracle 12c **소프트웨어 설치** (★ 이게 빠지면 01이 실패)
**관리자 권한 PowerShell**에서:
```
powershell -ExecutionPolicy Bypass -File scripts\00_install_software_ELEVATED.ps1
```
- 설치본 압축 해제(`D:\OraInstall\db12201`) → 무인 설치 → **10~20분** 소요. 창이 닫히고 **`[SUCCESS] 설치 완료`** 가 뜰 때까지 기다린다.
- 스크립트가 끝에 **`sqlplus -V`로 실제 실행까지 확인**한다(파일만 있고 안 도는 "미완료 설치"도 잡아냄). 실패하면 원인·조치(98/99)를 출력한다.
- **디스크 공간 15GB+** 필요. 부족하면 설치가 중간에 끊겨 `Error 57`이 난다(가장 흔한 실패).

> **왜 필요한가:** `01` 스크립트는 `sqlplus.exe`·`oradim.exe`·`lsnrctl.exe`를 쓰는데, 이건 0단계 설치로 생긴다.
> 0단계 없이 01을 돌리면 `sqlplus.exe 인식 안 됨` 오류가, 0단계가 **덜** 끝나면 `Unable to load Oracle dll's` / `Error 57` 오류가 난다.
> `01` 은 시작 시 **[0/8] PREFLIGHT** 로 이를 먼저 검사해, 설치가 불완전하면 한 줄로 알려주고 멈춘다(에러 폭포 방지).

### 1단계 — NAIMS12 **DB 생성** (관리자 권한 PowerShell)
```
powershell -ExecutionPolicy Bypass -File scripts\01_install_and_createdb_ELEVATED.ps1
```
→ DB `NAIMS12`(non-CDB, KO16MSWIN949, 포트 **1522**), sys/system 비번 `Naims12#2026`, 리스너 LISTENER_N12(1522), System DSN `naims` 자동 등록.
→ 전체 로그: `D:\OraInstall\create_naims12_full.log`

### 2단계 — 유저 LEGACYNAIMS + CRYPTO 설치 (system 접속)
```
set ORACLE_HOME=D:\app\oracle\product\12.2.0\dbhome_1
%ORACLE_HOME%\bin\sqlplus system/Naims12#2026@localhost:1522/NAIMS12 @scripts\02_setup_legacynaims.sql
%ORACLE_HOME%\bin\sqlplus system/Naims12#2026@localhost:1522/NAIMS12 @scripts\03_crypto_packages.sql
```
→ 유저 `LEGACYNAIMS`/`naims2!`, 서비스 별칭 `LegacyNaims`, CRYPTO/CryptIT 패키지 + PUBLIC SYNONYM(DES 키 `hellios#`).

### 3단계 — 덤프 복구 (impdp)
```
powershell -ExecutionPolicy Bypass -File scripts\04_restore_dump.ps1
```
→ `legacynaims.dmp` 를 LEGACYNAIMS 로 복구(42테이블). **종료코드 5는 정상**(예: 스키마 이미 존재 ORA-31684 — 무해).

### 4단계 — ODBC DSN 등록 + 검증
```
powershell -ExecutionPolicy Bypass -File scripts\05_register_dsn.ps1
powershell -ExecutionPolicy Bypass -File scripts\99_verify.ps1
```
→ 소스 접속 문자열 = `DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=naims2!;`

### 5단계 — SQL Developer로 눈으로 확인 (선택, 권장)
`scripts\90_developer_queries.sql` 를 SQL Developer에서 열어 섹션별 실행 → 복구·CRYPTO·권한을 직접 확인(6절 참고).

---

## 3. 마이그레이션 도구(RawCollectMigrator) 사용

RawCollectMigrator 기본값이 소스=`DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=naims2!` 로 세팅돼 있다.
앱 → **DB → DB 탭**:

1. (필요 시) **💾 덤프 복구** — 현장에서 받은 `.dmp`를 프로그램이 직접 복구(imp/impdp 자동 판별). 위 3단계를 앱에서 대신 할 수 있음.
2. **🔌 연결 시험** — 소스/타깃 접속 확인.
3. **🔐 Oracle 암호화 추출** — CRYPTO.DECRYPT 복호식을 찾아 적용(이름을 평문으로 이관).
4. **🔓 복호화 불러오기(체크박스)** — 켬(기본)=현장 암호문을 복호해 이관 / **끔=이미 평문으로 dump된 소스는 원문 그대로** 이관(CRYPTO 불필요).
5. **🔍 미리보기(DryRun) → 🧪 시험 실행(Trial, 롤백) → ⚠ 실제 이관(Commit)**.

> ⚠ 타깃 N2(실서버)에는 **RECORD_ID=0 "참조없음" 센티넬 행**이 필요(`ref_create_sentinels_N2.ps1` 참조 — STD_ACTV_SONAR/STD_VESSEL_DATA/STD_CLASSES/STD_COUNTRY_CODES).

---

## 4. 문제해결 (자주 나는 것)

| 증상 | 원인 | 해결 |
|---|---|---|
| `...\bin\sqlplus.exe 용어가 인식되지 않습니다` | **0단계(소프트웨어 설치) 안 함** | `00_install_software_ELEVATED.ps1` 먼저 실행 |
| `initNAIMS12.ora 경로 일부를 찾을 수 없습니다` | 홈이 없음(=위와 동일) | 0단계부터 |
| **`Unable to load Oracle dll's` / `Error 57 initializing SQL*Plus` / `Error loading message shared library`** | 설치가 **파일 복사를 완주하지 못함**(공간·볼륨특성·백신 등). 공간 부족은 원인 중 하나일 뿐 | ① **`98_diagnose.ps1`** — baseline.json과 **파일수/크기 대조**로 무엇이 빠졌는지 확정 → ② 볼륨이 부적합하면 **`12_set_install_drive.ps1`** → ③ **`99_fix_reinstall.ps1`**(인벤토리·레지스트리까지 청소 후 재설치) → ④ 그래도 실패하면 **`11_transplant_oracle_home.ps1`**(설치기 건너뛰고 정상 홈 이식) |
| 설치 대상 볼륨이 **exFAT/FAT32 · USB(이동식) · NTFS압축** | Oracle 바이너리는 NTFS·고정디스크 필요 | **`12_set_install_drive.ps1`** 실행 → 적합 드라이브 자동 선택 + rsp·00·01 등 **모든 경로 일괄 변경**(.bak 백업). 압축이면 98이 `compact.exe /U ...` 명령 안내 |
| **`Error 57` — 설치는 "성공"인데 sqlplus가 아예 안 뜸 (가장 흔한 진짜 원인)** | **VC++ 2013 x64 런타임 없음.** `sqlplus.exe`는 `MSVCR120.dll`을 필요로 함.<br>※ 오라클 미디어엔 `stage\ext\vcredist_x64.exe`가 동봉돼 있고 설치기가 자동 실행하도록 돼 있음(`MSVCREDIST_INSTALL=true`, 정상 설치 로그로 확인). **다만 일부 PC에서 이 단계가 먹지 않아**(임시폴더 추출 차단·백신·조용한 실패) 실행 불가한 홈이 "성공"으로 남는다 | **`14_install_vcredist.ps1`**(관리자) 실행 — 동봉 `vcredist_x64.exe`로 **오프라인 설치** 후 sqlplus 재검증. 이후 `00`이 설치 전 이 런타임을 **명시적으로 확인·설치**함<br>확인법: W/S 설치로그에서 `vcredist_x64.exe` 줄을 찾아 실행 여부 대조 |
| **파일은 다 있는데 `Error 57`** — `where sqlplus`도 우리 홈을 가리키고, cmd에서 `sqlplus`만 쳐도 57 | **홈이 레지스트리에 등록 안 됨**. `ORACLE_HOME`이 없으면 sqlplus는 `HKLM\SOFTWARE\ORACLE\KEY_*`로 홈을 찾는데, 하드 클린(99)이 이 키를 지운 뒤 재설치가 복원 못 하면 발생 | **자동 복구됨** — `00`·`01`·`11`·`99`가 실패를 감지하면 **`13_register_home.ps1`을 자동 호출**해 등록 후 재시도. 수동 실행도 가능 |
| 재설치해도 계속 같은 실패 | 그 W/S에서 설치기가 정상 동작하지 않음 | **`11_transplant_oracle_home.ps1`** — `home_image\dbhome_1.zip`(정상 홈 2.89GB, 35,633파일)을 **같은 경로**로 풀고 레지스트리 임포트 → 설치 과정 자체를 우회 |
| 콘솔 글자가 깨져 못 읽음 | 옛 버전 코드페이지 문제 | **이 패키지의 모든 .ps1/.sql 콘솔 출력은 영문**이라 안 깨짐(2026-08-07 전면 영문화) |
| 설치가 D: 없음으로 실패 | D: 드라이브 없음 | 0절 안내대로 rsp·00·01 경로를 존재 드라이브로 통일 |
| **02/03 실패** — 유저 없음·CRYPTO 없음/INVALID·synonym 없음·컴파일 오류 | 유저/암호화 패키지 손상·누락 | **`97_fix_user_and_crypto.sql`**(system) 한 번 실행 → 유저·CRYPTO 재생성 + `A201→F45F` 자체검증 |
| **04 실패** — `ORA-39001/39000 valid dump file 아님` | 현장 덤프가 **classic exp**인데 impdp로 복구 시도 | **`04b_restore_anydump.ps1`** 사용(자동 판별해 imp로 복구). 원본 스키마 다르면 `-SrcSchema SYSTEM` |
| impdp/imp 종료코드 5 | 무해 경고(스키마 이미 존재 등) | **정상**. 데이터 적재됨. 로그만 확인 |
| **05 실패** — 소스 연결 안 됨·DSN 없음·엉뚱한 드라이버 | ODBC DSN 문제(HKCU 그림자 포함) | **`05b_fix_dsn.ps1`** 실행(드라이버 탐지·`LegacyNaims` 재등록·그림자 정리) |
| **SQL Developer `The Network Adapter could not establish the connection`** (ORA-17002/12541) | 그 host:port에 **리스너 없음** — 대개 **리스너/DB 정지** 또는 접속 **포트가 1521**(우리는 1522) | **`96_start_db_listener.ps1`**(관리자)로 DB·리스너 시작. 접속값 **포트 1522·Service `NAIMS12`** 확인. **재설치 불필요** |
| SQL Developer `ORA-12514` | 리스너는 떴는데 서비스명 모름 | 30초 후 재시도, Service `NAIMS12`(또는 `LegacyNaims`) 확인 |
| SQL Developer `ORA-01017` | 비번 틀림(리스너·DB는 정상) | `system`/`Naims12#2026`, 소스 `LEGACYNAIMS`/`naims2!` |
| SQL Developer에서 CRYPTO **본문**이 안 보임 | 권한 부족(EXECUTE만 있음) | `90_developer_queries.sql` B-4로 SELECT_CATALOG_ROLE 부여 or SYSTEM 접속 |
| 한글 이름이 깨짐(데이터) | 세션 문자셋 | DB는 KO16MSWIN949로 생성됨. 도구/클라 NLS 확인 |

> **막히면 먼저 이것부터:** `powershell -ExecutionPolicy Bypass -File scripts\98_diagnose.ps1`
> — 설치완결성·디스크·DB open·CRYPTO·DSN·OUI로그를 한 번에 영문으로 점검하고, 다음에 뭘 할지 알려준다.
> 설치가 깨진 것으로 나오면 `scripts\99_fix_reinstall.ps1`(관리자) 로 정리·재설치한다.

---

## 5. 핵심 값

| 항목 | 값 |
|---|---|
| 12c DB / 포트 | NAIMS12 / 1522 (서비스 별칭 LegacyNaims) |
| ORACLE_HOME | D:\app\oracle\product\12.2.0\dbhome_1 |
| sys·system 비번 | Naims12#2026 |
| 소스 유저 | LEGACYNAIMS / naims2! |
| CRYPTO 위치 | SYSTEM 스키마 패키지 + PUBLIC SYNONYM |
| DES 키 | **hellios#** (RPAD('hellios',8,'#'), CBC/zero-IV, 8배수 공백패딩, 복호후 trim, CP949) |
| 키 검증 벡터 | `CRYPTO.ENCRYPT('A201')` → `F45F4284A4E23E62` |
| ODBC 드라이버 | Oracle in OraDB12Home1 |
| 소스 접속(ODBC) | `DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=naims2!;` |

---

## 6. SQL Developer 업무 확인 쿼리 (`scripts\90_developer_queries.sql`)

우리가 하는 모든 업무를 Developer에서 직접 확인하는 쿼리 모음. 섹션만 골라 실행.

| 섹션 | 내용 | 필요 권한 |
|---|---|---|
| A | 나는 누구/어디에 붙었나 | 없음 |
| B | 내 권한 확인 + **권한 승격(GRANT)** | 확인 없음 / 승격 SYSTEM |
| C | 인스턴스·DB·문자셋 상태 | 낮음 |
| D | 스키마·테이블·행수 (**덤프 복구 검증**) | SYSTEM |
| E | CRYPTO 존재·동의어·**규칙(본문)** | 본문은 SYSTEM |
| F | CRYPTO **동작 검증**(A201→F45F…, 실데이터 복호) | EXECUTE |
| G | **암복호화 탐색 재현**(후보 함수·RAW컬럼·복호식) | 낮음 |
| H | 접속·리스너·서비스·DSN 확인 | 낮음 |
| I | 문제해결/정리(권한·계정·재컴파일) | SYSTEM |

**가장 자주 쓰는 3개:**
```sql
-- 복구 확인: 42 기대
SELECT COUNT(*) FROM dba_tables WHERE owner='LEGACYNAIMS';
-- 키 검증: F45F4284A4E23E62 기대
SELECT RAWTOHEX(UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT('A201'))) FROM dual;
-- 실데이터 복호(한글 이름):
SELECT CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(CLASS_NAME)) FROM legacynaims.classes WHERE ROWNUM<=10;
```

---

## 7. 참고: 스니펫/이미지 파일
DB의 일부 스니펫 행은 NAS(`\\192.168.111.165\...MSU`)에 없는 파일명을 참조 → 그 행만 스킵(코드 결함 아님, 실환경 실파일이면 로드). **데이터 테이블은 전부 이관됨.**
