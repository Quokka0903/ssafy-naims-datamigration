-- rig_20b (ASCII-safe) : SYSTEM 스키마 필드 재현 + DES 암호화. NAIMS12/12c 에서 system 으로 실행.
-- 한글은 파일 인코딩 문제 회피 위해 CP949 hex(HEXTORAW)로 삽입.
SET DEFINE OFF
BEGIN FOR t IN (SELECT 'CLASSES' n FROM dual UNION ALL SELECT 'VESSEL_DATA' FROM dual
                UNION ALL SELECT 'ACTIVE_SONAR' FROM dual UNION ALL SELECT 'TRANSIENT' FROM dual) LOOP
  BEGIN EXECUTE IMMEDIATE 'DROP TABLE '||t.n||' PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;
END LOOP; END;
/
CREATE TABLE classes(
  class_id int, class_name raw(200), class_name_k raw(200), class_name_isus90 raw(200),
  ship_sub varchar2(12), draught_m number(12,3), version number(5), date_set_chng date, deleted number(1),
  CONSTRAINT cls_pk PRIMARY KEY(class_id));
CREATE TABLE vessel_data(
  vessel_id int, class_id int, vessel_name raw(200), vessel_name_k raw(200), vessel_name_isus90 raw(200),
  pendant_no varchar2(10), version number(5), date_set_chng date, deleted number(1),
  CONSTRAINT ves_pk PRIMARY KEY(vessel_id));
CREATE TABLE active_sonar(
  sonar_id int, sonar_name raw(200), sonar_name_isus90 raw(200), sonar_type varchar2(20),
  version number(5), date_set_chng date, deleted number(1), CONSTRAINT AI_pk PRIMARY KEY(sonar_id));
CREATE TABLE transient(
  tr_id int, class_id int, vessel_id int, tr_name raw(200), tr_group varchar2(20),
  date_set_chng date, deleted number(1), CONSTRAINT TR_pk PRIMARY KEY(tr_id));

-- 시드(평문 -> raw). class_name_k / vessel_name_k 는 CP949 한글 hex, 나머지 ASCII.
INSERT INTO classes VALUES(1, UTL_RAW.CAST_TO_RAW('Najin-class frigate'),
   HEXTORAW('B098C1F1B1DEC8A3C0A7C7D4'), UTL_RAW.CAST_TO_RAW('NAJIN'), 'FF', 4.5, 1, SYSDATE, 0);
INSERT INTO classes VALUES(2, UTL_RAW.CAST_TO_RAW('Sejong the Great-class destroyer'),
   UTL_RAW.CAST_TO_RAW('KDX-III'), UTL_RAW.CAST_TO_RAW('KDX3'), 'DDG', 6.25, 1, SYSDATE, 0);
INSERT INTO vessel_data VALUES(101,1, UTL_RAW.CAST_TO_RAW('Najin'),
   HEXTORAW('B098C1F8'), UTL_RAW.CAST_TO_RAW('A201'), '531', 1, SYSDATE, 0);
INSERT INTO active_sonar VALUES(11, UTL_RAW.CAST_TO_RAW('MGK-335'), UTL_RAW.CAST_TO_RAW('MGK335'),
   'HULL', 1, SYSDATE, 0);
INSERT INTO transient VALUES(21,1,101, UTL_RAW.CAST_TO_RAW('hatch-slam'), 'MECH', SYSDATE, 0);
COMMIT;

-- 필드형 DES 암호화(빈값 가드). raw 저장 유지.
UPDATE classes      SET class_name=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)))                 WHERE UTL_RAW.LENGTH(class_name)>0;
UPDATE classes      SET class_name_k=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name_k)))             WHERE UTL_RAW.LENGTH(class_name_k)>0;
UPDATE classes      SET class_name_isus90=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name_isus90)))   WHERE UTL_RAW.LENGTH(class_name_isus90)>0;
UPDATE vessel_data  SET vessel_name=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name)))               WHERE UTL_RAW.LENGTH(vessel_name)>0;
UPDATE vessel_data  SET vessel_name_k=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name_k)))           WHERE UTL_RAW.LENGTH(vessel_name_k)>0;
UPDATE vessel_data  SET vessel_name_isus90=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name_isus90))) WHERE UTL_RAW.LENGTH(vessel_name_isus90)>0;
UPDATE active_sonar SET sonar_name=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(sonar_name)))                 WHERE UTL_RAW.LENGTH(sonar_name)>0;
UPDATE active_sonar SET sonar_name_isus90=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(sonar_name_isus90)))   WHERE UTL_RAW.LENGTH(sonar_name_isus90)>0;
UPDATE transient    SET tr_name=UTL_RAW.CAST_TO_RAW(CRYPTO.ENCRYPT(UTL_RAW.CAST_TO_VARCHAR2(tr_name)))                       WHERE UTL_RAW.LENGTH(tr_name)>0;
COMMIT;

-- 검증: 복호화 왕복 + 암호문 hex
SET LINESIZE 200
COLUMN name_dec FORMAT A34
COLUMN kor_dec FORMAT A20
SELECT class_id, CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(class_name)) AS name_dec, RAWTOHEX(class_name) AS cipher_hex FROM classes ORDER BY class_id;
SELECT vessel_id, RAWTOHEX(vessel_name_k) AS kor_cipher_hex, RAWTOHEX(UTL_RAW.CAST_TO_RAW(CRYPTO.DECRYPT(UTL_RAW.CAST_TO_VARCHAR2(vessel_name_k)))) AS kor_dec_hex FROM vessel_data;
EXIT
