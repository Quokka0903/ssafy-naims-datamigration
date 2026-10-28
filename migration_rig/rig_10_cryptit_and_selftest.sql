-- ============================================================================
-- rig_10_cryptit_and_selftest.sql
-- 목적: 레거시(USPASK_CDMU) 이름컬럼 DES 암호화를 12c 리허설 리그에서 그대로 재현.
--       DBMS_OBFUSCATION_TOOLKIT 은 12c 에 존재(21c 에서는 제거됨) → 12c 에서만 실행.
-- 출처: \\192.168.111.165\...\USPASK_CDMU_170306\DB관련자료\오라클암호화.txt (CryptIT 본문)
--        + 오라클암호화쿼리.txt (실제 적용: cryptIt.encrypt(col,'hellios'))
-- 실행: SYSTEM 스키마로 접속하여 실행 (레거시 소유자=SYSTEM 재현).
-- 검증: .NET DES(키 'hellios#', CBC, zero-IV, no-pad, 8배수 공백패딩, 복호후 trim)와
--        바이트 일치. (사전 검증: 'A201    ' -> F45F4284A4E23E62)
-- ============================================================================

-- 0) 전제: DBMS_OBFUSCATION_TOOLKIT 사용 준비 (필요 시 SYS 로 1회)
--   @?/rdbms/admin/dbmsobtk.sql
--   @?/rdbms/admin/prvtobtk.plb
--   GRANT EXECUTE ON dbms_obfuscation_toolkit TO PUBLIC;

-- 1) 레거시 CryptIT 패키지 (오라클암호화.txt 원문 그대로) -----------------------
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

-- 2) 앱이 인라인 SQL 로 호출하던 CRYPTO.ENCRYPT/DECRYPT 래퍼 (키 'hellios' 고정) --
--    출처: DBTableManager_DataSet.cpp 의 CRYPTO.ENCRYPT(?)/CRYPTO.DECRYPT(col)
CREATE OR REPLACE PACKAGE CRYPTO AS
   FUNCTION ENCRYPT( Str VARCHAR2 ) RETURN VARCHAR2;
   FUNCTION DECRYPT( xCrypt VARCHAR2 ) RETURN VARCHAR2;
END CRYPTO;
/
CREATE OR REPLACE PACKAGE BODY CRYPTO AS
   k CONSTANT VARCHAR2(8) := 'hellios';   -- CryptIT 내부에서 RPAD(...,8,'#') => 'hellios#'
   FUNCTION ENCRYPT( Str VARCHAR2 ) RETURN VARCHAR2 IS BEGIN RETURN CryptIT.encrypt(Str, k); END;
   FUNCTION DECRYPT( xCrypt VARCHAR2 ) RETURN VARCHAR2 IS BEGIN RETURN CryptIT.decrypt(xCrypt, k); END;
END CRYPTO;
/

-- 3) 자체 왕복 검증 + .NET 교차검증 기준값 출력 --------------------------------
SET SERVEROUTPUT ON
DECLARE
   PROCEDURE chk(p_plain VARCHAR2) IS
      v_ct   VARCHAR2(2000);
      v_back VARCHAR2(2000);
   BEGIN
      v_ct   := CRYPTO.ENCRYPT(p_plain);
      v_back := CRYPTO.DECRYPT(v_ct);
      dbms_output.put_line(RPAD('['||p_plain||']',36)
         || ' CT(hex)=' || RAWTOHEX(UTL_RAW.CAST_TO_RAW(v_ct))
         || ' | roundtrip=' || CASE WHEN v_back=p_plain THEN 'OK' ELSE 'FAIL('||v_back||')' END);
   END;
BEGIN
   chk('A201');                              -- 기대 CT(hex)=F45F4284A4E23E62  (.NET 과 일치해야 함)
   chk('Najin-class frigate');               -- 기대 CT(hex)=8512AC6216385691EF61B5E016BFA226F8D24C7C3BEB142D
   chk('Sejong the Great-class destroyer');  -- 기대 CT(hex)=8CC2133E5D4E624DA33499F409FB5B9599B3853B0BDE5D0E7AF761A77F4612FC
END;
/
-- 위 CT(hex) 가 주석의 .NET 기대값과 일치하면 => 필드 암호문을 우리 .NET 복호기가 그대로 푼다는 증명.
-- (한글 이름은 CP949 바이트로 저장/복호되므로 세션 NLS_LANG=...KO16MSWIN949 확인 후 별도 검증)
