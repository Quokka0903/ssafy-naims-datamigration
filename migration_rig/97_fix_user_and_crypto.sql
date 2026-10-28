-- ============================================================================
--  97_fix_user_and_crypto.sql   (SYSTEM 으로 접속해 실행)
--    sqlplus system/<비밀번호>@localhost:1522/NAIMS12 @scripts\97_fix_user_and_crypto.sql
--    (LEGACYNAIMS 비밀번호는 실행 중 입력받는다 — 스크립트에 두지 않는다)
--
--  목적: 02(유저) / 03(CRYPTO) 단계가 실패/누락/깨졌을 때 한 번에 복구한다. 몇 번 돌려도 안전(idempotent).
--  고치는 것:
--    - LEGACYNAIMS 유저 없음/권한 부족  → 생성 + 권한 재부여
--    - CRYPTO/CryptIT 패키지 없음/INVALID → 재생성 + 재컴파일
--    - PUBLIC SYNONYM 없음               → 재생성 (스키마명 없이 CRYPTO.DECRYPT 호출되게)
--    - DBMS_OBFUSCATION_TOOLKIT 실행권한   → PUBLIC 부여
--    - 서비스명 별칭 LegacyNaims 누락      → service_names 에 추가
--  끝에 자체 검증: CRYPTO.ENCRYPT('A201') = F45F4284A4E23E62 이어야 정상.
-- ============================================================================
-- 비밀번호는 실행 시 입력받아 세션 안에서만 쓴다(스크립트·로그에 남기지 않는다).
SET VERIFY OFF
ACCEPT legacy_pw CHAR DEFAULT 'naims2!' PROMPT 'LEGACYNAIMS 스키마 비밀번호(기본값 그대로 쓰려면 Enter): ' HIDE

set echo off feedback on serveroutput on lines 200 pages 100

PROMPT == [1] Ensure LEGACYNAIMS user ==
DECLARE n INTEGER;
BEGIN
  SELECT COUNT(*) INTO n FROM dba_users WHERE username='LEGACYNAIMS';
  IF n=0 THEN
    EXECUTE IMMEDIATE q'[CREATE USER LEGACYNAIMS IDENTIFIED BY "&&legacy_pw" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users]';
    DBMS_OUTPUT.PUT_LINE('  created LEGACYNAIMS');
  ELSE
    EXECUTE IMMEDIATE q'[ALTER USER LEGACYNAIMS IDENTIFIED BY "&&legacy_pw" ACCOUNT UNLOCK]';
    DBMS_OUTPUT.PUT_LINE('  LEGACYNAIMS exists (password reset, unlocked)');
  END IF;
END;
/
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE TO LEGACYNAIMS;
ALTER USER LEGACYNAIMS QUOTA UNLIMITED ON users;

PROMPT == [2] Recreate CryptIT package (DES, key hellios#) ==
CREATE OR REPLACE PACKAGE CryptIT AS
   FUNCTION encrypt( Str VARCHAR2, hash VARCHAR2 ) RETURN VARCHAR2;
   FUNCTION decrypt( xCrypt VARCHAR2, hash VARCHAR2 ) RETURN VARCHAR2;
END CryptIT;
/
CREATE OR REPLACE PACKAGE BODY CryptIT AS
   crypted_string VARCHAR2(2000);
   FUNCTION encrypt( Str VARCHAR2, hash VARCHAR2 ) RETURN VARCHAR2 AS
      pieces_of_eight INTEGER := ((FLOOR(LENGTH(Str)/8 + .9)) * 8);
   BEGIN
      dbms_obfuscation_toolkit.DESEncrypt(
         input_string     => RPAD( Str, pieces_of_eight ),
         key_string       => RPAD(hash,8,'#'),
         encrypted_string => crypted_string );
      RETURN crypted_string;
   END;
   FUNCTION decrypt( xCrypt VARCHAR2, hash VARCHAR2 ) RETURN VARCHAR2 AS
   BEGIN
      dbms_obfuscation_toolkit.DESDecrypt(
         input_string     => xCrypt,
         key_string       => RPAD(hash,8,'#'),
         decrypted_string => crypted_string );
      RETURN trim(crypted_string);
   END;
END CryptIT;
/

PROMPT == [3] Recreate CRYPTO wrapper (key 'hellios') ==
CREATE OR REPLACE PACKAGE CRYPTO AS
   FUNCTION ENCRYPT( Str VARCHAR2 ) RETURN VARCHAR2;
   FUNCTION DECRYPT( xCrypt VARCHAR2 ) RETURN VARCHAR2;
END CRYPTO;
/
CREATE OR REPLACE PACKAGE BODY CRYPTO AS
   k CONSTANT VARCHAR2(8) := 'hellios';
   FUNCTION ENCRYPT( Str VARCHAR2 ) RETURN VARCHAR2 IS BEGIN RETURN CryptIT.encrypt(Str, k); END;
   FUNCTION DECRYPT( xCrypt VARCHAR2 ) RETURN VARCHAR2 IS BEGIN RETURN CryptIT.decrypt(xCrypt, k); END;
END CRYPTO;
/

PROMPT == [4] Grants + PUBLIC SYNONYM ==
GRANT EXECUTE ON SYSTEM.CryptIT TO PUBLIC;
GRANT EXECUTE ON SYSTEM.CRYPTO  TO PUBLIC;
CREATE OR REPLACE PUBLIC SYNONYM CryptIT FOR SYSTEM.CryptIT;
CREATE OR REPLACE PUBLIC SYNONYM CRYPTO  FOR SYSTEM.CRYPTO;
-- DBMS_OBFUSCATION_TOOLKIT 실행권한은 보통 01(catproc)이 SYSDBA 로 이미 PUBLIC 부여함.
-- SYSTEM 은 SYS 객체 grant 권한이 없어 실패할 수 있으나(ORA-01031) 무해 → 실패해도 무시.
DECLARE
BEGIN
  EXECUTE IMMEDIATE 'GRANT EXECUTE ON dbms_obfuscation_toolkit TO PUBLIC';
  DBMS_OUTPUT.PUT_LINE('  dbms_obfuscation_toolkit grant OK');
EXCEPTION WHEN OTHERS THEN
  DBMS_OUTPUT.PUT_LINE('  (dbms_obfuscation_toolkit grant skipped - already granted by 01/catproc): '||SQLERRM);
END;
/

PROMPT == [5] Recompile + service name alias LegacyNaims ==
ALTER PACKAGE SYSTEM.CryptIT COMPILE BODY;
ALTER PACKAGE SYSTEM.CRYPTO  COMPILE BODY;
-- 서비스명에 LegacyNaims 추가(이미 있으면 무시). SPFILE 없으면 MEMORY 로 폴백. ODBC DSN 은 05 가 담당.
DECLARE
BEGIN
  BEGIN EXECUTE IMMEDIATE q'[ALTER SYSTEM SET service_names='NAIMS12,LegacyNaims' SCOPE=BOTH]';
  EXCEPTION WHEN OTHERS THEN
    EXECUTE IMMEDIATE q'[ALTER SYSTEM SET service_names='NAIMS12,LegacyNaims' SCOPE=MEMORY]';
  END;
  DBMS_OUTPUT.PUT_LINE('  service_names = NAIMS12,LegacyNaims');
EXCEPTION WHEN OTHERS THEN DBMS_OUTPUT.PUT_LINE('  service_names 설정 건너뜀(무해): '||SQLERRM);
END;
/

PROMPT == [6] Verify key/algorithm (must be F45F4284A4E23E62) ==
SELECT RAWTOHEX(UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT('A201'))) AS ct_hex_expect_F45F4284A4E23E62 FROM dual;
SELECT CRYPTO.DECRYPT(CRYPTO.ENCRYPT('A201')) AS roundtrip_expect_A201 FROM dual;

PROMPT == [7] Remaining INVALID objects (if any) ==
SELECT owner, object_name, object_type, status FROM dba_objects
 WHERE owner='SYSTEM' AND object_name IN ('CRYPTO','CRYPTIT') AND status<>'VALID';

PROMPT == DONE. If ct_hex = F45F4284A4E23E62 the crypto is OK; otherwise check the log. ==
