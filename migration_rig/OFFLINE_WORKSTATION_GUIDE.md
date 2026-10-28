# 오프라인 워크스테이션 구축 & 필드 덤프 복구 가이드

목표: 반출용 오프라인 w/s에 **Oracle 12c**를 세우고, 소스 DB를 **`LegacyNaims`(서비스) / 유저 `LEGACYNAIMS`(비번 `naims2!`)** 로 만든 뒤, 현장에서 받은 **classic exp `.DMP`를 복구**해 RawCollectMigrator가 바로 읽게 한다.

> ⚠️ **명명 제약**: Oracle `DB_NAME`은 **최대 8자** → "LegacyNaims"(11자)는 DB명 불가. **서비스명(service_names)** 으로만 지정한다. DB명은 짧게(예: `NAIMS12`), 서비스 별칭 `LegacyNaims` 추가.

## 준비물 (반입)
- `V839963-01.zip` (Oracle Database 12.2.0.1 for Windows x64)
- 리그 스크립트 폴더 `D:\OraInstall\` (create_naims12_manual_ELEVATED.ps1 등)
- (현장에서) 받은 `.DMP` 파일

## 1. Oracle 12c 소프트웨어 설치 (관리자)
```
# ZIP을 ASCII 경로로 풀고(한글경로 금지), 관리자 PowerShell에서:
powershell -ExecutionPolicy Bypass -File D:\OraInstall\rig_00_install_ELEVATED.ps1
```
- `setup.exe -silent -responseFile db_install_naims12.rsp -ignorePrereqFailure` (SWONLY).
- Win11 등 미인증 OS면 `-ignorePrereqFailure` 필수. 응답파일 계정=내장(IsBuiltInAccount=true, IsVirtualAccount=false).

## 2. DB 생성 (RMAN-클론 dbca는 hang → 수동 CREATE DATABASE)
```
powershell -ExecutionPolicy Bypass -File D:\OraInstall\create_naims12_manual_ELEVATED.ps1
```
- non-CDB, **문자셋 KO16MSWIN949**(필드 CP949 재현), 포트 1522, sys/system=`Naims12#2026`.
- 교훈: `MAXINSTANCES 1 MAXLOGFILES 16`, FRA 하위폴더(`fast_recovery_area\<SID>`) 선생성 필수.

## 3. LegacyNaims 서비스 + LEGACYNAIMS 유저 (naims2!)
```sql
-- sqlplus system/Naims12#2026@localhost:1522/NAIMS12
ALTER SYSTEM SET service_names='NAIMS12,LegacyNaims' SCOPE=MEMORY;   -- pfile 이면 MEMORY
ALTER SYSTEM REGISTER;
CREATE USER legacynaims IDENTIFIED BY "naims2!" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users;
GRANT CREATE SESSION, CREATE TABLE, UNLIMITED TABLESPACE TO legacynaims;
GRANT EXECUTE ON SYSTEM.CRYPTO TO legacynaims;      -- 복호 패키지(키 hellios#)
GRANT EXECUTE ON SYSTEM.CRYPTIT TO legacynaims;
-- ★ 필수: PUBLIC SYNONYM (없으면 LEGACYNAIMS 접속 시 비수식 CRYPTO.DECRYPT 가 ORA-00904 → 암호화추출 실패)
CREATE OR REPLACE PUBLIC SYNONYM CRYPTO  FOR SYSTEM.CRYPTO;
CREATE OR REPLACE PUBLIC SYNONYM CRYPTIT FOR SYSTEM.CRYPTIT;
GRANT EXECUTE ON SYSTEM.CRYPTO  TO PUBLIC;
GRANT EXECUTE ON SYSTEM.CRYPTIT TO PUBLIC;
```
> 이유: 마이그레이터가 소스에 **LEGACYNAIMS** 로 접속하는데 CRYPTO 패키지는 SYSTEM 소유 → 비수식 호출은 **PUBLIC SYNONYM** 이 있어야 풀린다(레거시는 system 접속이라 불필요했음).
- 영구화: pfile(`init<SID>.ora`)에 `service_names='NAIMS12,LegacyNaims'` 추가.
- 복호 패키지 없으면: `D:\OraInstall\rig_10.sql`(CryptIT/CRYPTO) 를 system 으로 실행.

## 4. 필드 덤프 복구 (classic imp → LEGACYNAIMS 스키마)
```
imp system/Naims12#2026@localhost:1522/NAIMS12 FILE=<받은>.DMP FROMUSER=SYSTEM TOUSER=LEGACYNAIMS LOG=imp.log
```
- 다중 .DMP면 파일별 반복(레거시 per-table 구조).
- **ORA-01950(쿼터)** 나면: `GRANT UNLIMITED TABLESPACE TO legacynaims;` 후 재실행.
- classic exp 덤프는 **반드시 `imp`** (impdp 불가). 현장이 expdp면 impdp 사용.

## 5. ODBC DSN 'LegacyNaims' 등록
- System DSN(관리자) 또는 User DSN(HKCU, 비관리자):
```
HKCU\SOFTWARE\ODBC\ODBC.INI\LegacyNaims : Driver=<...\OraDB12Home1\BIN\SQORA32.DLL>, DBQ=localhost:1522/LegacyNaims
HKCU\SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources : LegacyNaims = "Oracle in OraDB12Home1"
```
- ⚠️ **함정**: 삭제된 다른 Oracle 홈을 가리키는 잔여 DSN이 있으면 로드 실패(IM003). HKCU/HKLM 양쪽 확인.

## 6. 검증
```
sqlplus legacynaims/naims2!@localhost:1522/LegacyNaims
SQL> SELECT SYSTEM.CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)) FROM classes;  -- 복호 확인
```
- RawCollectMigrator 기본값이 이미 `DSN=LegacyNaims;Uid=LEGACYNAIMS;Pwd=naims2!;` 이므로, 앱에서 [소스 연결 시험] → OK.

## (선택) 복구 기능을 프로그램에 내장
- RawCollectMigrator 에 "덤프 복구" 버튼 추가안: `imp.exe` 를 ShellExecute 로 호출(레거시 recovery.bat 패턴 동일) + FROMUSER/TOUSER 파라미터 UI. 현재는 본 가이드의 CLI 절차로 대체.
