-- 03_crypto_packages.sql  (connect as system to NAIMS12 and run)
-- CryptIT/CRYPTO packages for legacy name-column DES decrypt (key hellios#) + PUBLIC SYNONYM.
-- 12c has DBMS_OBFUSCATION_TOOLKIT -> works as-is. (21c removed it -> use C#/DBMS_CRYPTO instead)
SET ECHO ON
-- prereq (once, as SYS if needed): @?/rdbms/admin/dbmsobtk.sql ; @?/rdbms/admin/prvtobtk.plb
GRANT EXECUTE ON dbms_obfuscation_toolkit TO PUBLIC;

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
         input_string => RPAD( Str, pieces_of_eight ),
         key_string   => RPAD(hash,8,'#'),
         encrypted_string => crypted_string );
      RETURN crypted_string;
   END;
   FUNCTION decrypt( xCrypt VARCHAR2, hash VARCHAR2 ) RETURN VARCHAR2 AS
   BEGIN
      dbms_obfuscation_toolkit.DESDecrypt(
         input_string => xCrypt,
         key_string   => RPAD(hash,8,'#'),
         decrypted_string => crypted_string );
      RETURN trim(crypted_string);
   END;
END CryptIT;
/
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
-- PUBLIC SYNONYM is required so LEGACYNAIMS (non-SYSTEM) can call unqualified CRYPTO.DECRYPT.
CREATE OR REPLACE PUBLIC SYNONYM CRYPTO  FOR SYSTEM.CRYPTO;
CREATE OR REPLACE PUBLIC SYNONYM CRYPTIT FOR SYSTEM.CRYPTIT;
GRANT EXECUTE ON SYSTEM.CRYPTO  TO PUBLIC;
GRANT EXECUTE ON SYSTEM.CRYPTIT TO PUBLIC;
PROMPT === 03 DONE: CRYPTO/CRYPTIT + PUBLIC SYNONYM ===
EXIT
