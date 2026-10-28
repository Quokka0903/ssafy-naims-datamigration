# rehearsal/ — 가짜 레거시 Oracle 리허설 키트

실제 레거시 Oracle 덤프 없이, 가짜 레거시 소스를 세워 우리 이관기(RawCollectMigrator)의
**ODBC + CRYPTO.DECRYPT** 경로를 실전과 동일하게 검증한다.

## ✅ 실행 완료 — 기존 21c XE 에 기동됨 (2026-08-06)

이 PC엔 이미 **Oracle 21c XE**가 있고 **SYSDBA OS 인증**이 열려 있어, **12c 설치 없이**
기존 XE의 **격리 스키마 `REH`** 에 리허설 소스를 세웠다(기존 NAIMS 픽스처 무손상).
- 21c엔 OBFUSCATION_TOOLKIT이 없어 `CRYPTO` 패키지는 **DBMS_CRYPTO(AES-128)** 로 구현.
- 검증됨: 왕복 OK, RAW/VARCHAR2 이름 모두 `CRYPTO.DECRYPT` 로 평문 한글 복원,
  **ODBC(DSN-less + DSN `naims`) 접속 + 복호화** 실측 성공.

**지금 도구로 리허설하려면** (RawCollectMigrator → DB→DB 탭):
- 소스 DB `Oracle`, 접속 `ODBC`, 접속문자열:
  `DSN=naims;Uid=REH;Pwd=Reh2026x;`  (또는 DSN-less
  `Driver={Oracle in OraDB21Home1};DBQ=localhost:1521/XEPDB1;Uid=REH;Pwd=Reh2026x;`)
- `🔐 Oracle 암호화 추출` → `CRYPTO.DECRYPT` 채택 확인 → `시험 실행(Trial)`.

**재기동/정리**:
```
sqlplus / as sysdba @00_setup_user_xe.sql      -- REH 재생성(기존 REH만 DROP)
sqlplus REH/Reh2026x@localhost:1521/XEPDB1 @01_schema.sql
sqlplus REH/Reh2026x@localhost:1521/XEPDB1 @02_crypto_pkg.sql
sqlplus REH/Reh2026x@localhost:1521/XEPDB1 @03_seed_demo.sql
-- 완전 삭제: sqlplus / as sysdba 로 접속 → ALTER SESSION SET CONTAINER=XEPDB1; DROP USER REH CASCADE;
```

> `RUNBOOK.md` 의 12c 설치 절차는 **다른 PC(21c XE·SYSDBA 없는 환경)** 용 대안으로 유지.

## ▶ 실제 진행 방향 — 12c 단독 (사용자 결정)

21c 는 툴킷(DBMS_OBFUSCATION_TOOLKIT) 이 없어 임시 대체였을 뿐, 실 타깃은 **12c**.
결정: **이 PC의 21c XE 제거 → 12c 단독 설치(포트 1521)**. 설치는 사용자가 관리자로 실행.
→ **[install_12c/INSTALL_12C.md](install_12c/INSTALL_12C.md)** 참조. CRYPTO 는 `02_crypto_pkg_12c.sql`(DES 툴킷) 사용.

---

## 파일

| 파일 | 용도 |
|---|---|
| `gen_schema.py` | `docs/02_소스DB구조_Oracle.md` → `01_schema.sql` 생성기 (재생성용) |
| `00_setup_user_xe.sql` | (XE 경로) SYSDBA 로 격리 스키마 `REH` 생성 + DBMS_CRYPTO 권한 |
| `01_schema.sql` | Oracle DDL 29개 테이블(이관대상). 이름컬럼 RAW/VARCHAR2 원본 타입 보존 |
| `02_crypto_pkg.sql` | `CRYPTO` 패키지 (ENCRYPT/DECRYPT, **DBMS_CRYPTO AES-128**) + 왕복검증 |
| `03_seed_demo.sql` | 암호화 이름 + FK 로 연결된 데모 데이터 (즉시 리허설용) |
| `RUNBOOK.md` | (대안) 12c 신규설치 → 적재 → DSN → 도구 Trial 단계별 |

## 왜 12c인가

레거시 이름은 `CRYPTO.ENCRYPT/DECRYPT`(내부 DES, `DBMS_OBFUSCATION_TOOLKIT`)로 암복호화된다.
이 툴킷은 **12c엔 있고 21c엔 없다.** 그래서 리허설 소스는 반드시 12c(이하).

## 빠른 시작

`RUNBOOK.md` 참조. 요약:
1. `V839963-01.zip` 로 12c 설치(SYSTEM 암호 `naims`, AL32UTF8).
2. `sqlplus system/naims@localhost:1521/orcl @01_schema.sql` → `@02_crypto_pkg.sql` → `@03_seed_demo.sql`
3. 64비트 시스템 DSN `naims` 등록(Oracle ODBC 드라이버, tnsnames 별칭 NAIMS).
4. 도구: 소스=Oracle·ODBC·`DSN=naims;Uid=system;Pwd=naims;` → `🔐 Oracle 암호화 추출` → `시험 실행(Trial)`.

## 범위 / 한계

- **데모 시드**로 즉시 전 구간 리허설 가능(암호화 4개 테이블 + FK 재매핑 포함).
- 실 MsSQL 데이터로 채우려면 이름 평문 확보(대칭키 GRANT)가 필요 — 없으면 데모로 충분.
- 스키마는 이관대상 29테이블만(제외 6개: DATASET/DS_*/REDO_*/HELP 는 생략).
- FK/제약은 생략(읽기 리허설엔 불필요). 필요 시 추가.

관련 메모리: 레거시 연결(ODBC+DSN), 이름 암호화(CRYPTO 패키지), Oracle=12c 확정.
