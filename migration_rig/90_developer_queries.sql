-- ============================================================================
--  90_developer_queries.sql
--  Oracle SQL Developer(또는 sqlplus)에서 우리 업무를 직접 확인하는 쿼리 모음.
--  각 섹션 머리에 [필요 권한] 을 적어 두었다. 필요한 줄만 골라 실행(F5/F9)하면 된다.
--
--  접속 예)  아이디 system  /  비번 <$env:NAIMS12_SYS_PW>  /  호스트 localhost  /  포트 1522  /  서비스명 NAIMS12
--  (일반 계정 예: LEGACYNAIMS / <$env:LEGACYNAIMS_PW>  — 소스 데이터·복호 검증용, 권한 낮음)
-- ============================================================================


-- ════════════════════════════════════════════════════════════════════════
--  A. 나는 누구인가 / 어디에 붙었나                       [필요 권한: 없음]
-- ════════════════════════════════════════════════════════════════════════
SELECT USER AS 내계정 FROM dual;

SELECT SYS_CONTEXT('USERENV','SESSION_USER')   AS 세션유저,
       SYS_CONTEXT('USERENV','CURRENT_SCHEMA') AS 현재스키마,
       SYS_CONTEXT('USERENV','DB_NAME')        AS DB이름,
       SYS_CONTEXT('USERENV','SERVICE_NAME')   AS 서비스명,
       SYS_CONTEXT('USERENV','ISDBA')          AS DBA접속여부
  FROM dual;


-- ════════════════════════════════════════════════════════════════════════
--  B. 내 권한 확인  +  권한 승격                     [확인:없음 / 승격:SYSTEM]
-- ════════════════════════════════════════════════════════════════════════
-- (B-1) 내게 부여된 역할
SELECT granted_role, admin_option, default_role
  FROM user_role_privs ORDER BY granted_role;

-- (B-2) 지금 세션에 활성화된 역할  (여기 SELECT_CATALOG_ROLE 있으면 소스/본문 열람 가능)
SELECT role FROM session_roles ORDER BY role;

-- (B-3) 내게 직접 부여된 시스템 권한
SELECT privilege FROM user_sys_privs ORDER BY privilege;

-- (B-4) [승격] ↓↓↓  아래 두 줄은 반드시 SYSTEM(또는 SYS)으로 접속해서 실행.
--       <계정> 에 위 A 의 '내계정' 값(예: LEGACYNAIMS)을 넣는다. 실행 후 그 계정으로 "재접속"해야 적용됨.
-- GRANT SELECT_CATALOG_ROLE TO <계정>;          -- 데이터딕셔너리·소스 열람(권장, 좁음)
-- GRANT SELECT ANY DICTIONARY TO <계정>;         -- 더 넓게 필요할 때만
-- (되돌리기) REVOKE SELECT_CATALOG_ROLE FROM <계정>;


-- ════════════════════════════════════════════════════════════════════════
--  C. 인스턴스 / DB / 문자셋 상태                        [필요 권한: 없음~낮음]
-- ════════════════════════════════════════════════════════════════════════
SELECT instance_name, status, version, host_name FROM v$instance;       -- OPEN 이어야 정상
SELECT name, open_mode, log_mode FROM v$database;                       -- READ WRITE
SELECT parameter, value FROM nls_database_parameters
 WHERE parameter IN ('NLS_CHARACTERSET','NLS_NCHAR_CHARACTERSET');       -- KO16MSWIN949 기대
SELECT value FROM v$parameter WHERE name='local_listener';              -- (PORT=1522) 기대


-- ════════════════════════════════════════════════════════════════════════
--  D. 스키마 / 테이블 / 행수  (덤프 복구 검증)     [필요 권한: SYSTEM 또는 소유자]
-- ════════════════════════════════════════════════════════════════════════
-- (D-1) LEGACYNAIMS 테이블 개수  (복구 성공 시 42 기대)
SELECT COUNT(*) AS 테이블수 FROM dba_tables WHERE owner='LEGACYNAIMS';

-- (D-2) 주요 테이블 행수 (exp 로그와 대조)  기대: CLASSES 9 / VESSEL_DATA 19 / SIGNATURES 45 /
--       HARMONICS 56 / CHARACTERISTICS 20 / PULSES 26 / ACTIVE_SONAR 11 / TRANSIENT 5 / COUNTRY_CODES 8
SELECT 'CLASSES'         AS 테이블, COUNT(*) AS 행수 FROM legacynaims.classes        UNION ALL
SELECT 'VESSEL_DATA',        COUNT(*) FROM legacynaims.vessel_data     UNION ALL
SELECT 'SIGNATURES',         COUNT(*) FROM legacynaims.signatures      UNION ALL
SELECT 'HARMONICS',          COUNT(*) FROM legacynaims.harmonics       UNION ALL
SELECT 'CHARACTERISTICS',    COUNT(*) FROM legacynaims.characteristics UNION ALL
SELECT 'PULSES',             COUNT(*) FROM legacynaims.pulses          UNION ALL
SELECT 'ACTIVE_SONAR',       COUNT(*) FROM legacynaims.active_sonar    UNION ALL
SELECT 'TRANSIENT',          COUNT(*) FROM legacynaims.transient       UNION ALL
SELECT 'COUNTRY_CODES',      COUNT(*) FROM legacynaims.country_codes
ORDER BY 테이블;

-- (D-3) 전체 테이블별 행수 자동 조회(딕셔너리 통계 기준, 근사)  [SYSTEM]
SELECT table_name, num_rows
  FROM dba_tables WHERE owner='LEGACYNAIMS' ORDER BY table_name;

-- (D-4) 무효(INVALID) 객체 점검 — 복구/컴파일 문제 탐지  [SYSTEM]
SELECT owner, object_name, object_type, status
  FROM dba_objects WHERE owner IN ('LEGACYNAIMS','SYSTEM') AND status<>'VALID'
 ORDER BY owner, object_type, object_name;


-- ════════════════════════════════════════════════════════════════════════
--  E. CRYPTO 존재 / 동의어 / 규칙(본문)      [E1~E2:낮음 / E3 본문:SYSTEM]
-- ════════════════════════════════════════════════════════════════════════
-- (E-1) 패키지 존재·상태
SELECT owner, object_name, object_type, status
  FROM all_objects WHERE object_name IN ('CRYPTO','CRYPTIT')
 ORDER BY owner, object_name, object_type;

-- (E-2) 공용 동의어(스키마명 없이 CRYPTO.DECRYPT 호출 가능 여부)
SELECT owner, synonym_name, table_owner, table_name
  FROM all_synonyms WHERE synonym_name IN ('CRYPTO','CRYPTIT');

-- (E-3) 규칙(키·알고리즘) 본문 전체.  ★ SYSTEM 접속 또는 SELECT_CATALOG_ROLE 필요.
--       스펙(PACKAGE)만 보이고 본문(PACKAGE BODY)이 안 나오면 = 권한 부족(B-4 참고).
SELECT name, type, line, text
  FROM dba_source WHERE owner='SYSTEM' AND name IN ('CRYPTO','CRYPTIT')
 ORDER BY name, type, line;
--   기대 요지:  CRYPTO 는 CryptIT.encrypt/decrypt 에 키 'hellios' 전달,
--              CryptIT 는 DES(DBMS_OBFUSCATION_TOOLKIT), 키 RPAD('hellios',8,'#')='hellios#', 8배수 공백패딩.


-- ════════════════════════════════════════════════════════════════════════
--  F. CRYPTO 동작 검증  (권한 없이도 됨 — EXECUTE 만)        [필요 권한: EXECUTE]
-- ════════════════════════════════════════════════════════════════════════
-- (F-1) 고정 테스트 벡터.  결과가 F45F4284A4E23E62 이면 키(hellios#)·알고리즘 정상.
SELECT RAWTOHEX(UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT('A201'))) AS ct_hex FROM dual;

-- (F-2) 왕복(암호화→복호화)  결과가 'A201' 이면 OK.
SELECT CRYPTO.DECRYPT(CRYPTO.ENCRYPT('A201')) AS roundtrip FROM dual;

-- (F-3) 실데이터 복호 — 함정급 이름이 평문(한글)으로 나오는지.
--       (이름 컬럼이 RAW 저장이면 UTL_RAW.CAST_TO_VARCHAR2 로 감싼다)
SELECT CLASS_ID,
       CLASS_NAME                                             AS 암호문_raw,
       CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(CLASS_NAME))   AS 평문
  FROM legacynaims.classes
 WHERE ROWNUM <= 10;
--   ※ 한글이 깨지면 키 문제가 아니라 세션 문자셋 문제. SQL Developer 는 보통 정상 표시.


-- ════════════════════════════════════════════════════════════════════════
--  G. 암복호화 "탐색" 재현 (마이그레이터 [Oracle 암호화 추출] 이 하는 일)   [낮음]
-- ════════════════════════════════════════════════════════════════════════
-- (G-1) 복호화 후보 함수/패키지 나열 (이름에 DECRYPT/CRYPT/DES 포함)
SELECT owner, object_name, object_type
  FROM all_objects
 WHERE object_type IN ('FUNCTION','PACKAGE')
   AND (UPPER(object_name) LIKE '%DECRYPT%' OR UPPER(object_name) LIKE '%CRYPT%'
        OR UPPER(object_name) LIKE '%DES%')
 ORDER BY owner, object_name;

-- (G-2) CLASSES 의 RAW(암호화 후보) 컬럼 확인 — 이름 컬럼이 RAW 인지 VARCHAR2 인지
SELECT column_name, data_type, data_length
  FROM all_tab_columns WHERE table_name='CLASSES' AND owner='LEGACYNAIMS'
 ORDER BY column_id;

-- (G-3) 채택되는 복호식 예시 검증 (도구가 이 형태를 표본에 적용해 한글이 나오면 채택)
--       RAW 컬럼: CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(col))
SELECT CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(CLASS_NAME)) AS 복호결과
  FROM legacynaims.classes WHERE CLASS_NAME IS NOT NULL AND ROWNUM <= 3;

-- (G-4) 암호화 대상 4개 테이블의 이름 컬럼도 같은 식으로 확인 가능
--       (CLASSES / VESSEL_DATA / ACTIVE_SONAR / TRANSIENT)
SELECT CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(VESSEL_NAME)) AS 함정명
  FROM legacynaims.vessel_data WHERE ROWNUM <= 5;


-- ════════════════════════════════════════════════════════════════════════
--  H. 접속·리스너·DSN 확인 (참고)                          [필요 권한: 낮음]
-- ════════════════════════════════════════════════════════════════════════
-- (H-1) 현재 세션 접속 정보
SELECT SYS_CONTEXT('USERENV','IP_ADDRESS') AS 클라IP,
       SYS_CONTEXT('USERENV','HOST')       AS 클라호스트,
       SYS_CONTEXT('USERENV','SERVER_HOST')AS 서버호스트 FROM dual;

-- (H-2) 서비스 등록 확인 (리스너/서비스명)  [SYSTEM]
SELECT name FROM v$active_services ORDER BY name;   -- NAIMS12 보여야 함
--   ※ 리스너 자체는 SQL 이 아니라 OS 에서:  lsnrctl status
--   ※ ODBC DSN 확인도 OS 에서:  odbcad32.exe (또는 05_register_dsn.ps1)


-- ════════════════════════════════════════════════════════════════════════
--  I. 문제해결 / 정리                                      [필요 권한: SYSTEM]
-- ════════════════════════════════════════════════════════════════════════
-- (I-1) 특정 계정에 무슨 권한이 있는지 (승격 후 확인 등)
SELECT granted_role FROM dba_role_privs WHERE grantee='LEGACYNAIMS' ORDER BY 1;

-- (I-2) 잠긴/만료 계정 상태
SELECT username, account_status, default_tablespace
  FROM dba_users WHERE username IN ('SYSTEM','LEGACYNAIMS') ORDER BY username;

-- (I-3) 패키지 재컴파일(INVALID 일 때)
-- ALTER PACKAGE SYSTEM.CRYPTIT COMPILE;
-- ALTER PACKAGE SYSTEM.CRYPTIT COMPILE BODY;
-- ALTER PACKAGE SYSTEM.CRYPTO  COMPILE;
-- ALTER PACKAGE SYSTEM.CRYPTO  COMPILE BODY;
