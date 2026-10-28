-- ============================================================================
--  [12c 전용] 리허설 CRYPTO 패키지 — 레거시와 동일하게 DBMS_OBFUSCATION_TOOLKIT(DES) 사용
--  현재 접속 스키마에 생성. (21c 는 이 툴킷이 없으므로 02_crypto_pkg.sql(DBMS_CRYPTO) 사용)
-- ----------------------------------------------------------------------------
--  · 레거시(USPASK_CDMU) 원본 방식: DES, 키 'hellios' 계열. 12c 에 툴킷이 살아 있어 그대로 재현.
--  · ENCRYPT(VARCHAR2)->RAW, DECRYPT(RAW)->VARCHAR2. RAW/VARCHAR2(hex) 컬럼 모두 왕복.
--  · 우리 도구는 CRYPTO.DECRYPT(col) 로만 읽으므로 내부 구현과 무관.
-- ============================================================================

CREATE OR REPLACE PACKAGE CRYPTO AS
    FUNCTION ENCRYPT(p_text IN VARCHAR2) RETURN RAW;
    FUNCTION DECRYPT(p_data IN RAW)      RETURN VARCHAR2;
END CRYPTO;
/

CREATE OR REPLACE PACKAGE BODY CRYPTO AS
    -- DES 키는 정확히 8바이트('hellios'=7 → 8로 패딩)
    G_KEY RAW(8) := UTL_RAW.CAST_TO_RAW(RPAD('hellios', 8, '0'));

    FUNCTION ENCRYPT(p_text IN VARCHAR2) RETURN RAW IS
        l_txt VARCHAR2(4000);
        l_in  RAW(4000);
        l_out RAW(4000);
    BEGIN
        IF p_text IS NULL THEN RETURN NULL; END IF;
        l_txt := p_text;
        WHILE MOD(LENGTHB(l_txt), 8) <> 0 LOOP l_txt := l_txt || ' '; END LOOP;  -- 8바이트 블록 패딩
        l_in := UTL_RAW.CAST_TO_RAW(l_txt);
        DBMS_OBFUSCATION_TOOLKIT.DESEncrypt(input => l_in, key => G_KEY, encrypted_data => l_out);
        RETURN l_out;
    END ENCRYPT;

    FUNCTION DECRYPT(p_data IN RAW) RETURN VARCHAR2 IS
        l_out RAW(4000);
    BEGIN
        IF p_data IS NULL THEN RETURN NULL; END IF;
        DBMS_OBFUSCATION_TOOLKIT.DESDecrypt(input => p_data, key => G_KEY, decrypted_data => l_out);
        RETURN RTRIM(UTL_RAW.CAST_TO_VARCHAR2(l_out));
    END DECRYPT;
END CRYPTO;
/

SET SERVEROUTPUT ON
DECLARE
    v_in  VARCHAR2(100) := '충무공이순신급';
    v_out VARCHAR2(100);
BEGIN
    v_out := CRYPTO.DECRYPT(CRYPTO.ENCRYPT(v_in));
    DBMS_OUTPUT.PUT_LINE('CRYPTO(12c/DES) 왕복검증: ['|| v_in ||'] -> ['|| v_out ||'] '
                         || CASE WHEN v_in = v_out THEN 'OK' ELSE '실패!' END);
END;
/
