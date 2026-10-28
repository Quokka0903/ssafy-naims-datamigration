# 12c 단독 설치 → 리허설 (21c XE 제거 후)

결정: **이 PC의 21c XE 제거 → 12c 단독(포트 1521)**, 설치 `setup.exe` 는 **사용자가 관리자로 실행**.
12c 를 쓰는 이유: 레거시 암호화가 `DBMS_OBFUSCATION_TOOLKIT`(DES) 인데 **12c엔 있고 21c엔 없음**.

---

## STEP 1 — (관리자, 사용자) 기존 21c XE 제거

> ⚠️ 제거 시 기존 XE 의 NAIMS 평문 픽스처와 앞서 만든 REH 리허설이 사라집니다(수용 결정됨).

1. 서비스 중지: 관리자 PowerShell
   ```powershell
   Get-Service *XE*,*OraDB21* | Stop-Service -Force -ErrorAction SilentlyContinue
   ```
2. 설정 → 앱 → **Oracle Database 21c Express Edition** → 제거. (또는 제어판 프로그램 제거)
3. 제거 후 잔여 폴더 정리(선택): `D:\app\oracle\product\21c` 삭제. 리스너/서비스가 남아있지
   않은지 확인:
   ```powershell
   Get-Service *oracle*,*XE* -ErrorAction SilentlyContinue
   ```

## STEP 2 — (완료, 나) 설치본 압축 해제

`D:\oracle12c_setup\database\setup.exe` 로 이미 풀어둠. (없으면
`tar -xf "D:\다운로드\V839963-01.zip" -C D:\oracle12c_setup`)

## STEP 3 — (관리자, 사용자) 12c 무인 설치

관리자 **명령 프롬프트(cmd)** 에서:
```bat
cd /d D:\oracle12c_setup\database
setup.exe -silent -responseFile "F:\JKYi\2026\260109 DB임플란트\rehearsal\install_12c\db_install.rsp" -ignorePrereqFailure -waitForCompletion
```
- 20~40분 소요. 소프트웨어 설치 + DBCA 로 DB `orcl`(non-CDB, AL32UTF8) 생성까지 진행됨.
- SYS/SYSTEM 암호 = `Naims2026#` (응답파일 값).
- 끝에 `Successfully Setup Software` / DBCA 완료가 보이면 성공.
- Win11 미인증이라 `-ignorePrereqFailure` 로 사전검사 경고를 넘긴다.

설치 확인:
```bat
sqlplus system/Naims2026#@localhost:1521/orcl
```
접속되면 STEP 4 로. **여기서 저(어시스턴트)에게 알려주시면 이후는 제가 처리합니다.**

## STEP 4 — (나) 리허설 소스 적재 + 검증

설치 확인되면 제가 실행:
```bat
set NLS_LANG=KOREAN_KOREA.AL32UTF8
sqlplus system/Naims2026#@localhost:1521/orcl @rehearsal\00_setup_user_12c.sql
sqlplus NAIMS/Naims2026@localhost:1521/orcl   @rehearsal\01_schema.sql
sqlplus NAIMS/Naims2026@localhost:1521/orcl   @rehearsal\02_crypto_pkg_12c.sql   -- DES 툴킷
sqlplus NAIMS/Naims2026@localhost:1521/orcl   @rehearsal\03_seed_demo.sql
```
- `02_crypto_pkg_12c.sql` 왕복검증 `OK` 확인.
- User DSN `naims` 재등록(12c 드라이버 "Oracle in OraDB12Home1", DBQ=localhost:1521/orcl).
- ODBC + `CRYPTO.DECRYPT` 로 평문 한글 복원 실측(21c에서 이미 통과한 검증을 12c에서 재확인).

## STEP 5 — (사용자) 도구 리허설

RawCollectMigrator → DB→DB: 소스 `Oracle`/`ODBC`,
`DSN=naims;Uid=NAIMS;Pwd=Naims2026;` → `🔐 Oracle 암호화 추출`(CRYPTO.DECRYPT 채택) → `시험 실행(Trial)`.

---

### 참고: 문제 시
- 사전검사 실패로 멈추면 `-ignorePrereqFailure` 유지 + 로그(`D:\app\oracle\...\install\`) 확인.
- 무인 설치가 Win11에서 계속 막히면 **GUI 설치**(`setup.exe` 더블클릭)로 전환:
  DB 생성/구성, Desktop, SID=`orcl`, non-CDB, 문자셋 **AL32UTF8**, 관리자암호 `Naims2026#`.
