-- ============================================================================
--  기존 21c XE 에 격리된 리허설 스키마 REH 를 만든다. SYSDBA 로 실행.
--    sqlplus / as sysdba @00_setup_user_xe.sql
--  · 기존 NAIMS 픽스처(CLASSES 등)와 충돌하지 않도록 별도 계정에 세운다.
--  · 21c 엔 DBMS_OBFUSCATION_TOOLKIT 이 없으므로 CRYPTO 패키지는 DBMS_CRYPTO 를 쓴다
--    → REH 에 EXECUTE 권한 부여.
-- ============================================================================
ALTER SESSION SET CONTAINER=XEPDB1;

-- 재실행 안전: 기존 REH 만 제거(다른 스키마는 건드리지 않음)
BEGIN EXECUTE IMMEDIATE 'DROP USER REH CASCADE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/

-- 비밀번호는 실행 시 입력받는다(스크립트에 두지 않는다).
SET VERIFY OFF
ACCEPT reh_pw CHAR DEFAULT 'Reh2026x' PROMPT 'REH 스키마 비밀번호(기본값 그대로 쓰려면 Enter): ' HIDE
CREATE USER REH IDENTIFIED BY "&reh_pw" DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS;
GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE TO REH;
GRANT EXECUTE ON SYS.DBMS_CRYPTO TO REH;

SELECT 'REH 생성 완료' AS status FROM DUAL;
EXIT
