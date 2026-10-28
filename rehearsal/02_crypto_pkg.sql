-- ============================================================================
--  리허설용 CRYPTO 패키지 — 레거시(USPASK_CDMU)의 CRYPTO.ENCRYPT/DECRYPT 재현
--  현재 접속 스키마(REH)에 생성. 우리 도구는 CRYPTO.DECRYPT(col) 로만 읽는다.
-- ----------------------------------------------------------------------------
--  · 레거시 원본은 DBMS_OBFUSCATION_TOOLKIT DES(키 'hellios')였으나, 21c 에는 그 툴킷이
--    없다. 리허설 목적(우리 도구 복호화 경로 검증)엔 ENCRYPT/DECRYPT 가 서로 역함수이기만
--    하면 되므로, 21c 에 있는 DBMS_CRYPTO(AES-128 CBC)로 동등하게 구현한다.
--  · ENCRYPT(VARCHAR2)->RAW, DECRYPT(RAW)->VARCHAR2.
--    RAW 컬럼(CLASS_NAME 등)엔 RAW 그대로, VARCHAR2 컬럼(SONAR_NAME 등)엔
--    암시적 RAW->VARCHAR2(=RAWTOHEX) 로 hex 문자열 저장. 읽을 때 CRYPTO.DECRYPT(col) 은
--    VARCHAR2(hex)->RAW(암시적 HEXTORAW) 로 되돌아가므로 왕복 성립.
-- ============================================================================

CREATE OR REPLACE PACKAGE CRYPTO AS
    FUNCTION ENCRYPT(p_text IN VARCHAR2) RETURN RAW;
    FUNCTION DECRYPT(p_data IN RAW)      RETURN VARCHAR2;
END CRYPTO;
/

CREATE OR REPLACE PACKAGE BODY CRYPTO AS
    -- AES-128 CBC PKCS5. 키 16바이트 / IV 16바이트 고정(리허설용).
    G_TYP PLS_INTEGER := DBMS_CRYPTO.ENCRYPT_AES128 + DBMS_CRYPTO.CHAIN_CBC + DBMS_CRYPTO.PAD_PKCS5;
    G_KEY RAW(16) := UTL_I18N.STRING_TO_RAW('hellios_rehkey16', 'AL32UTF8');  -- 16 bytes
    G_IV  RAW(16) := UTL_I18N.STRING_TO_RAW('naims_rehearsal1', 'AL32UTF8');  -- 16 bytes

    FUNCTION ENCRYPT(p_text IN VARCHAR2) RETURN RAW IS
    BEGIN
        IF p_text IS NULL THEN RETURN NULL; END IF;
        RETURN DBMS_CRYPTO.ENCRYPT(
                   src => UTL_I18N.STRING_TO_RAW(p_text, 'AL32UTF8'),
                   typ => G_TYP, key => G_KEY, iv => G_IV);
    END ENCRYPT;

    FUNCTION DECRYPT(p_data IN RAW) RETURN VARCHAR2 IS
    BEGIN
        IF p_data IS NULL THEN RETURN NULL; END IF;
        RETURN UTL_I18N.RAW_TO_CHAR(
                   DBMS_CRYPTO.DECRYPT(src => p_data, typ => G_TYP, key => G_KEY, iv => G_IV),
                   'AL32UTF8');
    END DECRYPT;
END CRYPTO;
/

-- 왕복 자가검증
SET SERVEROUTPUT ON
DECLARE
    v_in  VARCHAR2(100) := '충무공이순신급';
    v_out VARCHAR2(100);
BEGIN
    v_out := CRYPTO.DECRYPT(CRYPTO.ENCRYPT(v_in));
    DBMS_OUTPUT.PUT_LINE('CRYPTO 왕복검증: ['|| v_in ||'] -> ['|| v_out ||'] '
                         || CASE WHEN v_in = v_out THEN 'OK' ELSE '실패!' END);
END;
/
