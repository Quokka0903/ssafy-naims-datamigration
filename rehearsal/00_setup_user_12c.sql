-- ============================================================================
--  [12c 전용] 리허설 스키마 NAIMS 생성. SYSTEM 으로 실행.
--    sqlplus system/Naims2026#@localhost:1521/orcl @00_setup_user_12c.sql
--  · non-CDB 기준(설치 응답파일이 non-CDB). CDB 로 만들었다면 먼저
--    ALTER SESSION SET CONTAINER=<PDB>; 를 앞에 추가.
--  · 12c 는 DBMS_OBFUSCATION_TOOLKIT 이 살아 있어 레거시와 동일한 DES 로 CRYPTO 를 만든다.
-- ============================================================================
BEGIN EXECUTE IMMEDIATE 'DROP USER NAIMS CASCADE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/
CREATE USER NAIMS IDENTIFIED BY "Naims2026" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS;
GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE TO NAIMS;
GRANT EXECUTE ON SYS.DBMS_OBFUSCATION_TOOLKIT TO NAIMS;
SELECT 'NAIMS 생성 완료' AS status FROM DUAL;
EXIT
