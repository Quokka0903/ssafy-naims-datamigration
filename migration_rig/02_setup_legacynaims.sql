-- 02_setup_legacynaims.sql  (connect as system to NAIMS12 and run)
-- LEGACYNAIMS user + service alias 'LegacyNaims' + impdp privileges.
SET ECHO ON
-- service alias (connect via .../LegacyNaims). On a pfile instance use SCOPE=MEMORY.
ALTER SYSTEM SET service_names='NAIMS12,LegacyNaims' SCOPE=MEMORY;
ALTER SYSTEM REGISTER;
-- to persist: add  service_names='NAIMS12,LegacyNaims'  to init<SID>.ora (survives restart).

BEGIN EXECUTE IMMEDIATE 'DROP USER legacynaims CASCADE'; EXCEPTION WHEN OTHERS THEN NULL; END;
/
-- 비밀번호는 스크립트에 두지 않는다. 실행 시 물어보고(&&legacy_pw) 세션 안에서만 쓴다.
--   무인 실행: sqlplus ... @02_setup_legacynaims.sql <비밀번호>
SET VERIFY OFF
ACCEPT legacy_pw CHAR DEFAULT 'naims2!' PROMPT 'LEGACYNAIMS 스키마 비밀번호(기본값 그대로 쓰려면 Enter): ' HIDE
CREATE USER legacynaims IDENTIFIED BY "&legacy_pw"
  DEFAULT TABLESPACE users TEMPORARY TABLESPACE temp QUOTA UNLIMITED ON users;
GRANT CREATE SESSION, CREATE TABLE, UNLIMITED TABLESPACE TO legacynaims;
GRANT DATAPUMP_IMP_FULL_DATABASE TO legacynaims;
GRANT READ, WRITE ON DIRECTORY DATA_PUMP_DIR TO legacynaims;
-- CRYPTO execute privilege is granted to PUBLIC in 03.
PROMPT === 02 DONE: LEGACYNAIMS user + LegacyNaims service ready ===
EXIT
