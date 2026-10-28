-- ============================================================================
-- rig_20_schema_seed_encrypt.sql   (NAIMS12 / 12c 에서 SYSTEM 으로 접속하여 실행)
-- 순서: rig_10(CryptIT/CRYPTO 패키지) 실행 후 → 이 스크립트.
-- 목적: 레거시 USPASKDB.txt 권위 DDL(암호화 대상 테이블) 재현 + 시드 + 필드형 DES 암호화.
--       => backup.bat(classic exp) 로 뜨면 "진짜 필드형 .DMP"(SYSTEM 소유, 이름컬럼 DES암호문).
-- 접속 예: sqlplus system/<비밀번호>@localhost:1522/NAIMS12
-- 문자셋: DB=KO16MSWIN949 (한글 이름 CP949 재현). NLS_LANG 세션도 KO16MSWIN949 권장.
-- 주: 전체 30여 테이블은 USPASKDB.txt 그대로 확장 가능. 여기선 암호화 검증에 필요한 핵심만.
-- ============================================================================

-- 1) 암호화 대상 핵심 테이블 (레거시 DDL 원문 타입 유지: 이름=raw(200)) ----------
BEGIN FOR t IN (SELECT 'CLASSES' n FROM dual UNION ALL SELECT 'VESSEL_DATA' FROM dual
                UNION ALL SELECT 'ACTIVE_SONAR' FROM dual UNION ALL SELECT 'TRANSIENT' FROM dual
                UNION ALL SELECT 'COUNTRY_CODES' FROM dual) LOOP
  BEGIN EXECUTE IMMEDIATE 'DROP TABLE '||t.n||' PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;
END LOOP; END;
/

CREATE TABLE classes(
  class_id          int,
  class_name        raw(200),
  class_name_k      raw(200),
  ship_sub          varchar2(12),
  class_type        varchar2(5),
  draught_m         number(12,3),
  length_m          number(12,3),
  displacement      number(12,3),
  user_countries    varchar2(80),
  comments          varchar2(80),
  version           number(5),
  date_set_chng     date,
  deleted           number(1),
  class_name_isus90 raw(200),
  CONSTRAINT cls_pk PRIMARY KEY(class_id)
);

CREATE TABLE vessel_data(
  vessel_id         int,
  class_id          int,
  country_id        int,
  vessel_name       raw(200),
  vessel_name_k     raw(200),
  vessel_name_isus90 raw(200),
  pendant_no        varchar2(10),
  version           number(5),
  date_set_chng     date,
  deleted           number(1),
  CONSTRAINT ves_pk PRIMARY KEY(vessel_id)
);

CREATE TABLE active_sonar(
  sonar_id          int,
  sonar_name        raw(200),
  sonar_name_isus90 raw(200),
  sonar_type        varchar2(20),
  version           number(5),
  date_set_chng     date,
  deleted           number(1),
  CONSTRAINT AI_pk PRIMARY KEY(sonar_id)
);

CREATE TABLE transient(
  tr_id             int,
  class_id          int,
  vessel_id         int,
  tr_name           raw(200),
  tr_group          varchar2(20),
  date_set_chng     date,
  deleted           number(1),
  CONSTRAINT TR_pk PRIMARY KEY(tr_id)
);

-- 2) 시드: 평문을 우선 raw 로 저장(UTL_RAW.CAST_TO_RAW). 한글 포함(CP949 검증) ------
INSERT INTO classes(class_id,class_name,class_name_k,class_name_isus90,ship_sub,draught_m,deleted)
  VALUES(1, UTL_RAW.CAST_TO_RAW('Najin-class frigate'),
            UTL_RAW.CAST_TO_RAW('나진급 호위함'),
            UTL_RAW.CAST_TO_RAW('NAJIN'), 'FF', 4.5, 0);
INSERT INTO classes(class_id,class_name,class_name_k,class_name_isus90,ship_sub,draught_m,deleted)
  VALUES(2, UTL_RAW.CAST_TO_RAW('Sejong the Great-class destroyer'),
            UTL_RAW.CAST_TO_RAW('세종대왕급 구축함'),
            UTL_RAW.CAST_TO_RAW('KDX3'), 'DDG', 6.25, 0);
INSERT INTO vessel_data(vessel_id,class_id,vessel_name,vessel_name_k,vessel_name_isus90,pendant_no,deleted)
  VALUES(101,1, UTL_RAW.CAST_TO_RAW('A201'), UTL_RAW.CAST_TO_RAW('나진'), UTL_RAW.CAST_TO_RAW('A201'),'531',0);
INSERT INTO active_sonar(sonar_id,sonar_name,sonar_name_isus90,sonar_type,deleted)
  VALUES(11, UTL_RAW.CAST_TO_RAW('MGK-335'), UTL_RAW.CAST_TO_RAW('MGK335'),'HULL',0);
INSERT INTO transient(tr_id,class_id,vessel_id,tr_name,tr_group,deleted)
  VALUES(21,1,101, UTL_RAW.CAST_TO_RAW('hatch-slam'),'MECH',0);
COMMIT;

-- 3) 필드형 암호화: 이름컬럼을 CRYPTO.ENCRYPT 로 in-place 갱신 (오라클암호화쿼리.txt 재현) --
--    저장형이 raw 이므로 CryptIT(varchar2) 결과를 raw 로 캐스팅해 저장.
UPDATE classes      SET class_name        = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)));
UPDATE classes      SET class_name_k      = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name_k)));
UPDATE classes      SET class_name_isus90 = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name_isus90)));
UPDATE vessel_data  SET vessel_name        = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name)));
UPDATE vessel_data  SET vessel_name_k      = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name_k)));
UPDATE vessel_data  SET vessel_name_isus90 = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name_isus90)));
UPDATE active_sonar SET sonar_name         = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(sonar_name)));
UPDATE active_sonar SET sonar_name_isus90  = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(sonar_name_isus90)));
UPDATE transient    SET tr_name            = UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(tr_name)));
COMMIT;

-- 4) 확인: 복호화가 되는지 (CRYPTO.DECRYPT 왕복) --------------------------------
SET SERVEROUTPUT ON
SELECT class_id, CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)) AS name_dec,
                 RAWTOHEX(class_name) AS name_cipher_hex
  FROM classes ORDER BY class_id;
-- name_dec 가 'Najin-class frigate' 등으로 나오면 OK. name_cipher_hex 는 .NET/필드와 대조 가능.
