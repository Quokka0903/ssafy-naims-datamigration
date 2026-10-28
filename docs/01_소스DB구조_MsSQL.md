# 소스 DB 구조 — MsSQL (NAIMS)

| 항목 | 내용 |
|---|---|
| 문서번호 | NAIMS-MIG-260722-01 |
| 작성일 | 2026-07-22 |
| 소스 DB (MsSQL) | 192.168.111.39:1433 / NAIMS |
| 소스 DB (Oracle) | 260420_NAIMS_SQL.pdf (25p DDL 스캔본 전사) |
| 대상 DB | 192.168.200.205:1433 / NAIMS2 |
| 기준 확정본 | NAIMS_마이그레이션_정리_최종준비본.xlsx (03_마이그레이션_매핑 / 09_결정규칙) |

192.168.111.39 의 NAIMS DB를 2026-07-22 직접 조회한 결과다.

## 테이블 목록 (42개)

| 테이블 | 컬럼수 | 레코드수 | 이관 여부 |
|---|---|---|---|
| ACTIVE_SONAR | 21 | 11 | 이관대상 |
| AI_SNIPPET | 27 | 3 | 이관대상 |
| CCF2CD_LOG | 9 | 26 | 이관대상 |
| CD_BACKUP_LOG | 31 | 14 | 이관대상 |
| CD_CHANGE_LOG | 9 | 1965 | 이관대상 |
| CHARACTERISTICS | 42 | 20 | 이관대상 |
| CLASSES | 63 | 9 | 이관대상 |
| COUNTRY_CODES | 10 | 8 | 이관대상 |
| DATASET | 43 | 0 | 제외 |
| DB_DIST_GROUP | 19 | 12 | 이관대상 |
| DB_DIST_LOG | 17 | 17 | 이관대상 |
| DIST_LIST | 6 | 141 | 이관대상 |
| DIST_TO | 4 | 17 | 이관대상 |
| DS_BACKUP_LOG | 7 | 0 | 제외 |
| DS_DIST_LOG | 7 | 0 | 제외 |
| FIELD_CONV | 8 | 0 | 제외 |
| FRAME_SNIPPET | 6 | 4 | 이관대상 |
| HARMONICS | 6 | 56 | 이관대상 |
| IMAGE_DATA | 8 | 3 | 이관대상 |
| NB_SNIPPET | 25 | 22 | 이관대상 |
| ONA_209 | 25 | 30 | 제외 |
| ONA_214 | 37 | 6 | 제외 |
| OPERATOR | 6 | 2 | 제외 |
| PASSWORD | 3 | 6 | 제외 |
| PULSES | 23 | 26 | 이관대상 |
| PULSE_FREQUENCY_ISUS90 | 4 | 2 | 이관대상 |
| PULSE_SEGMENTS | 11 | 37 | 이관대상 |
| PULSE_SEG_LENGTH_ISUS90 | 5 | 2 | 이관대상 |
| PULSE_SEG_SWING_ISUS90 | 5 | 1 | 이관대상 |
| RAWDATAIMPORTINFO | 23 | 55 | 제외 |
| RAWDATAINFO | 11 | 92 | 제외 |
| RAWSIGNALINFO | 26 | 63 | 제외 |
| SIGNATURES | 39 | 45 | 이관대상 |
| SIG_SNIPPET | 6 | 22 | 이관대상 |
| TRANSIENT | 9 | 5 | 이관대상 |
| TR_FRAME | 40 | 495 | 이관대상 |
| TR_SNIPPET | 20 | 4 | 이관대상 |
| USER_JUMIN | 7 | 0 | 제외 |
| VESSEL_DATA | 19 | 19 | 이관대상 |
| WEAPON | 12 | 3 | 이관대상 |
| WEAPON_CARRIER | 6 | 2 | 이관대상 |
| sysdiagrams | 5 | 0 | 제외 |

## 테이블별 컬럼

### ACTIVE_SONAR

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SONAR_ID | int NOT NULL |
| 2 | SONAR_NAME | varbinary(300) |
| 3 | SONAR_NAME_ISUS90 | varbinary(300) |
| 4 | TRANS_TYPE | varchar(20) |
| 5 | SONAR_TYPE | varchar(20) |
| 6 | TRM_MODE | varchar(10) |
| 7 | MODULATION | varchar(12) |
| 8 | SOURCE_LEVEL_MIN | int |
| 9 | SOURCE_LEVEL_MAX | int |
| 10 | RANGE_RESOL_M_MIN | int |
| 11 | RANGE_RESOL_M_MAX | int |
| 12 | DPLR_RESOL_KTS_MIN | float |
| 13 | DPLR_RESOL_KTS_MAX | float |
| 14 | DET_RANGE_KM | float |
| 15 | MANUFACTURER | varchar(30) |
| 16 | MANUFACT_DATE | varchar(20) |
| 17 | VERSION | int |
| 18 | DATE_SET_CHNG | datetime2 |
| 19 | COMMENTS | varchar(80) |
| 20 | DELETED | smallint |
| 21 | SELF_DB | smallint |

### AI_SNIPPET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | AI_SNIPPET_ID | int NOT NULL |
| 2 | PULSE_ID | int |
| 3 | GRAM_FILENAME | varchar(80) |
| 4 | AUDIO_FILENAME | varchar(80) |
| 5 | FREQ_MIN | float |
| 6 | FREQ_MAX | float |
| 7 | NO_FRAME | int |
| 8 | RESOLUTION | float |
| 9 | A_ORG_CFREQ | float |
| 10 | A_START_TIME | int |
| 11 | A_END_TIME | int |
| 12 | SAMPLING_RATE | float |
| 13 | DET_SONAR | varchar(10) |
| 14 | LATITUDE_DEGREE | int |
| 15 | LATITUDE_MINUTE | int |
| 16 | LATITUDE_SECOND | int |
| 17 | LONGITUDE_DEGREE | int |
| 18 | LONGITUDE_MINUTE | int |
| 19 | LONGITUDE_SECOND | int |
| 20 | DET_DATE | datetime2 |
| 21 | DET_PLATFORM | varchar(30) |
| 22 | DISTANCE_KM | float |
| 23 | COMMENTS1 | varchar(80) |
| 24 | COMMENTS2 | varchar(240) |
| 25 | DATE_SET_CHNG | datetime2 |
| 26 | DELETED | smallint |
| 27 | SELF_DB | smallint |

### CCF2CD_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | CCFCD_ID | int NOT NULL |
| 2 | DATASET_ID | int |
| 3 | SHIP_SUB | varchar(20) |
| 4 | CTRY_NAME | varchar(35) |
| 5 | CTRY_CODE | varchar(2) |
| 6 | TARGET_TYPE | varchar(6) |
| 7 | TARGET_NAME | varchar(30) |
| 8 | NUM_APPLIED | int |
| 9 | APPL_DATE | datetime2 |

### CD_BACKUP_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | BACKUP_ID | int NOT NULL |
| 2 | BACKUP_TYPE | varchar(5) |
| 3 | BACKUP_DATE | datetime2 |
| 4 | BACK_BY | varchar(8) |
| 5 | BACKUP_MEDIA | varchar(5) |
| 6 | BACKUP_MEDIA_ID | varchar(30) |
| 7 | NO_CLASSES | int |
| 8 | NO_VESSEL | int |
| 9 | NO_SONAR | int |
| 10 | NO_WEAPON | int |
| 11 | NO_TR | int |
| 12 | NO_NB_GRAM | int |
| 13 | SIZE_NB_GRAM | float |
| 14 | NO_AI_GRAM | int |
| 15 | SIZE_AI_GRAM | float |
| 16 | NO_TR_GRAM | int |
| 17 | SIZE_TR_GRAM | float |
| 18 | NO_NB_AUDIO | int |
| 19 | SIZE_NB_AUDIO | float |
| 20 | NO_AI_AUDIO | int |
| 21 | SIZE_AI_AUDIO | float |
| 22 | NO_TR_AUDIO | int |
| 23 | SIZE_TR_AUDIO | float |
| 24 | NO_VESSEL_IMAGE | int |
| 25 | SIZE_VESSEL_IMAGE | float |
| 26 | NO_FLAG_IMAGE | int |
| 27 | SIZE_FLAG_IMAGE | float |
| 28 | NO_RELEASE_LOG | int |
| 29 | SIZE_RELEASE_LOG | float |
| 30 | TOTAL_SIZE | float |
| 31 | COMMENTS | varchar(80) |

### CD_CHANGE_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | CHNG_ID | int NOT NULL |
| 2 | OPERATOR | varchar(20) |
| 3 | CHNG_DATE | datetime2 |
| 4 | TABLE_NAME | varchar(30) |
| 5 | RECORD_ID | varchar(20) |
| 6 | TYPE_CHNG | varchar(20) |
| 7 | MENU_NAME | varchar(30) |
| 8 | COMMENTS | varchar(80) |
| 9 | DATASET_ID | varchar(20) |

### CHARACTERISTICS

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | CHAR_ID | int NOT NULL |
| 2 | VESSEL_ID | int |
| 3 | CAVITATION_DEPTH_M | float |
| 4 | CAVITATION_SPEED | float |
| 5 | CAVITATION_I90 | varchar(8) |
| 6 | DIST_FEATURES | varchar(40) |
| 7 | SPECTRAL_RANGE | varchar(6) |
| 8 | BLADE_RESONANCE | varchar(1) |
| 9 | FLUTTER | varchar(1) |
| 10 | SHAFT_RUB | varchar(1) |
| 11 | WHINE | varchar(1) |
| 12 | TRANSMISSIONS | varchar(1) |
| 13 | NOISE_MAKER | varchar(1) |
| 14 | BEST_TRK_FREQ | float |
| 15 | STRONG_SRC_GT_300 | float |
| 16 | STRONG_SRC_LT_300 | float |
| 17 | DISCRETE_FREQ1_MAX | float |
| 18 | DISCRETE_FREQ1_AVG | float |
| 19 | DISCRETE_FREQ1_MIN | float |
| 20 | DISCRETE_FREQ1_TYPE | varchar(30) |
| 21 | DISCRETE_FREQ2_MAX | float |
| 22 | DISCRETE_FREQ2_AVG | float |
| 23 | DISCRETE_FREQ2_MIN | float |
| 24 | DISCRETE_FREQ2_TYPE | varchar(30) |
| 25 | DISCRETE_FREQ3_MAX | float |
| 26 | DISCRETE_FREQ3_AVG | float |
| 27 | DISCRETE_FREQ3_MIN | float |
| 28 | DISCRETE_FREQ3_TYPE | varchar(30) |
| 29 | DISCRETE_FREQ4_MAX | float |
| 30 | DISCRETE_FREQ4_AVG | float |
| 31 | DISCRETE_FREQ4_MIN | float |
| 32 | DISCRETE_FREQ4_TYPE | varchar(30) |
| 33 | RANGE1_SPD_LO | float |
| 34 | RANGE1_SPD_HI | float |
| 35 | RANGE1_TPK_RPM | float |
| 36 | RANGE2_SPD_LO | float |
| 37 | RANGE2_SPD_HI | float |
| 38 | RANGE2_TPK_RPM | float |
| 39 | RANGE3_SPD_LO | float |
| 40 | RANGE3_SPD_HI | float |
| 41 | RANGE3_TPK_RPM | float |
| 42 | SELF_DB | smallint |

### CLASSES

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | CLASS_ID | int NOT NULL |
| 2 | CLASS_NAME | varbinary(300) |
| 3 | CLASS_NAME_K | varbinary(300) |
| 4 | SHIP_SUB | varchar(12) |
| 5 | CLASS_TYPE | varchar(5) |
| 6 | ACTIVE_SONAR1 | int |
| 7 | ACTIVE_SONAR2 | int |
| 8 | DRAUGHT_M | float |
| 9 | LENGTH_M | float |
| 10 | WIDTH_M | float |
| 11 | DISPLACEMENT | float |
| 12 | DET_RANGE_KM | float |
| 13 | NUMBER_BLADES | int |
| 14 | NUMBER_SHAFTS | int |
| 15 | PROPULSION_TYPE | varchar(20) |
| 16 | ENGINE_TYPE | varchar(30) |
| 17 | NUMBER_CYLINDERS | int |
| 18 | STROKE_TYPE | int |
| 19 | NUMBER_ENGINES | int |
| 20 | MAX_CFR | float |
| 21 | AVG_CFR | float |
| 22 | MIN_CFR | float |
| 23 | MAX_CSR | float |
| 24 | AVG_CSR | float |
| 25 | MIN_CSR | float |
| 26 | MAX_EFR | float |
| 27 | AVG_EFR | float |
| 28 | MIN_EFR | float |
| 29 | PSR_CSR_RATIO | float |
| 30 | PSR_CFR_RATIO | float |
| 31 | PSR_EFR_RATIO | float |
| 32 | TURBINE_TYPE | varchar(30) |
| 33 | NUMBER_TURBINES | int |
| 34 | MAX_TSR | float |
| 35 | AVG_TSR | float |
| 36 | MIN_TSR | float |
| 37 | PSR_TSR_RATIO | float |
| 38 | TSR_GEN_RATIO | float |
| 39 | MOTOR_TYPE | varchar(30) |
| 40 | NUMBER_MOTORS | int |
| 41 | NUMBER_SLOT | int |
| 42 | MAX_MSR | float |
| 43 | AVG_MSR | float |
| 44 | MIN_MSR | float |
| 45 | PSR_MSR_RATIO | float |
| 46 | MIN_SPEED | float |
| 47 | MAX_SPEED | float |
| 48 | MAX_PSR | float |
| 49 | AVG_PSR | float |
| 50 | MIN_PSR | float |
| 51 | BR_GEN_RATIO | float |
| 52 | GEAR_RED_TYPE | varchar(25) |
| 53 | PSR_BGR_RATIO | float |
| 54 | USER_COUNTRIES | varchar(80) |
| 55 | H_MAST_M | float |
| 56 | H_RADAR_M | float |
| 57 | H_BRIDGE_M | float |
| 58 | COMMENTS | varchar(80) |
| 59 | VERSION | int |
| 60 | DATE_SET_CHNG | datetime2 |
| 61 | DELETED | smallint |
| 62 | CLASS_NAME_ISUS90 | varbinary(300) |
| 63 | SELF_DB | smallint |

### COUNTRY_CODES

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | COUNTRY_ID | int NOT NULL |
| 2 | CTRY_CODE | varchar(2) |
| 3 | CTRY_NAME | varchar(35) |
| 4 | HOSTILE_LEVEL | int |
| 5 | CTRY_NAME_ISUS90 | varchar(20) |
| 6 | FLAG_FILENAME | varchar(40) |
| 7 | COMMENTS | varchar(40) |
| 8 | DATE_SET_CHNG | datetime2 |
| 9 | DELETED | smallint |
| 10 | SELF_DB | smallint |

### DATASET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | DATASET_ID | int NOT NULL |
| 2 | DATASET_NAME | varchar(45) |
| 3 | DATASET_SIZE | int |
| 4 | RECEIPT_DATE | datetime2 |
| 5 | RECEIPT_PATH | varchar(10) |
| 6 | DET_DATE | datetime2 |
| 7 | LATITUDE_DEGREE | int |
| 8 | LATITUDE_MINUTE | int |
| 9 | LATITUDE_SECOND | int |
| 10 | LONGITUDE_DEGREE | int |
| 11 | LONGITUDE_MINUTE | int |
| 12 | LONGITUDE_SECOND | int |
| 13 | DET_PLATFORM | varchar(30) |
| 14 | DET_VESSEL | varbinary(300) |
| 15 | DET_SONAR | varchar(10) |
| 16 | DISTANCE_KM | float |
| 17 | QA_UNIT | varchar(4) |
| 18 | QA_OPERATOR | varchar(8) |
| 19 | QA_DATE | datetime2 |
| 20 | SIGNAL_TYPE | varchar(5) |
| 21 | NO_IMAGES | int |
| 22 | NO_IMG_REVIEWD | int |
| 23 | NO_FEAT | int |
| 24 | NO_REVIEWD | int |
| 25 | LOADED | smallint |
| 26 | NO_AUDIO | smallint |
| 27 | COMMENTS | varchar(80) |
| 28 | A1_STATUS | varchar(10) |
| 29 | A1_ST_DATE | datetime2 |
| 30 | A1_NO_FEAT | int |
| 31 | A1_NO_REVIEWD | int |
| 32 | A2_STATUS | varchar(10) |
| 33 | A2_ST_DATE | datetime2 |
| 34 | A2_NO_FEAT | int |
| 35 | A2_NO_REVIEWD | int |
| 36 | A3_STATUS | varchar(10) |
| 37 | A3_ST_DATE | datetime2 |
| 38 | A3_NO_FEAT | int |
| 39 | A3_NO_REVIEWD | int |
| 40 | C0_STATUS | varchar(10) |
| 41 | C0_ST_DATE | datetime2 |
| 42 | C0_NO_FEAT | int |
| 43 | C0_NO_REVIEWD | int |

### DB_DIST_GROUP

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | GROUP_ID | int NOT NULL |
| 2 | GROUP_NAME | varchar(30) |
| 3 | IF_SYSTEM | varchar(15) |
| 4 | IF_MEDIA | varchar(10) |
| 5 | CLASS_INFO | varchar(1) |
| 6 | NB_INFO | varchar(1) |
| 7 | AI_INFO | varchar(1) |
| 8 | TR_INFO | varchar(1) |
| 9 | WEAPON_INFO | varchar(1) |
| 10 | NB_GRAM_INFO | varchar(1) |
| 11 | NB_AUDIO_INFO | varchar(1) |
| 12 | IMAGE_FILE | varchar(1) |
| 13 | AI_GRAM_INFO | varchar(1) |
| 14 | AI_AUDIO_INFO | varchar(1) |
| 15 | TR_AUDIO_INFO | varchar(1) |
| 16 | FREQ_MIN | float |
| 17 | FREQ_MAX | float |
| 18 | FINAL_DIST_DATE | datetime2 |
| 19 | COMMENTS | varchar(80) |

### DB_DIST_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | LOG_ID | int NOT NULL |
| 2 | GROUP_NAME | varchar(30) |
| 3 | IF_SYSTEM | varchar(15) |
| 4 | DIST_PATH | varchar(10) |
| 5 | DB_FILENAME | varchar(80) |
| 6 | DB_FILESIZE_MB | float |
| 7 | DIST_DATE | datetime2 |
| 8 | DIST_OPERATOR | varchar(8) |
| 9 | DOC_NO | varchar(50) |
| 10 | NO_CLASSES | int |
| 11 | NO_VESSEL | int |
| 12 | NO_SONAR | int |
| 13 | NO_TR_EVENT | int |
| 14 | NO_WEAPON | int |
| 15 | NO_IMAGE | int |
| 16 | COMMENTS | varchar(80) |
| 17 | LOG_FILENAME | varchar(80) |

### DIST_LIST

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | DIST_LIST_ID | int NOT NULL |
| 2 | GROUP_ID | int |
| 3 | TABLE_NAME | varchar(15) |
| 4 | ID | int |
| 5 | PARENT_ID | int |
| 6 | IAUDIOINCLUDE | int |

### DIST_TO

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | LOG_ID | int NOT NULL |
| 2 | DESTINATION | varchar(15) |
| 3 | COMPLT_DATE | datetime2 |
| 4 | COMPLT_OPTR | varchar(20) |

### DS_BACKUP_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | DS_BACKUP_ID | int NOT NULL |
| 2 | DATASET_ID | int |
| 3 | MEDIA_NO | varchar(6) |
| 4 | BACKUP_MEDIA | varchar(5) |
| 5 | BACKUP_OPERATOR | varchar(8) |
| 6 | BACKUP_DATE | datetime2 |
| 7 | COMMENTS | varchar(80) |

### DS_DIST_LOG

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | DIST_LOG_ID | int NOT NULL |
| 2 | DATASET_ID | int |
| 3 | DESTINATION | varchar(50) |
| 4 | DISTRIB_DATE | datetime2 |
| 5 | OPERATOR | varchar(8) |
| 6 | DOC_NO | varchar(50) |
| 7 | COMMENTS | varchar(80) |

### FIELD_CONV

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | FIELD_CONV_ID | int NOT NULL |
| 2 | TARGET_SYSTEM | varchar(15) |
| 3 | TABLE_NAME | varchar(15) |
| 4 | FIELD_NAME | varchar(15) |
| 5 | FIELD_TYPE | varchar(15) |
| 6 | ORIGINAL | varchar(40) |
| 7 | TARGET | varchar(40) |
| 8 | COMMENTS | varchar(80) |

### FRAME_SNIPPET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | FRM_SNP_ID | int NOT NULL |
| 2 | FRAME_ID | int |
| 3 | TR_SNIPPET_ID | int |
| 4 | DATE_SET_CHNG | datetime2 |
| 5 | DELETED | smallint |
| 6 | SELF_DB | smallint |

### HARMONICS

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | HARM_ID | int NOT NULL |
| 2 | SIGNATURES_ID | int |
| 3 | HARM_NO | int |
| 4 | FREQ | float |
| 5 | PREDOMINANCE | smallint |
| 6 | SELF_DB | smallint |

### IMAGE_DATA

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | IMAGE_ID | int NOT NULL |
| 2 | VESSEL_ID | int |
| 3 | IMG_FILENAME | varchar(80) |
| 4 | SOURCE | varchar(12) |
| 5 | DATE_SET_CHNG | datetime2 |
| 6 | COMMENTS | varchar(80) |
| 7 | DELETED | smallint |
| 8 | SELF_DB | smallint |

### NB_SNIPPET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | NB_SNIPPET_ID | int NOT NULL |
| 2 | GRAM_FILENAME | varchar(80) |
| 3 | AUDIO_FILENAME | varchar(80) |
| 4 | FREQ_MIN | float |
| 5 | FREQ_MAX | float |
| 6 | NO_FRAME | int |
| 7 | RESOLUTION | float |
| 8 | A_START_TIME | int |
| 9 | A_END_TIME | int |
| 10 | SAMPLING_RATE | float |
| 11 | DET_SONAR | varchar(10) |
| 12 | LATITUDE_DEGREE | int |
| 13 | LATITUDE_MINUTE | int |
| 14 | LATITUDE_SECOND | int |
| 15 | LONGITUDE_DEGREE | int |
| 16 | LONGITUDE_MINUTE | int |
| 17 | LONGITUDE_SECOND | int |
| 18 | DET_DATE | datetime2 |
| 19 | DET_PLATFORM | varchar(30) |
| 20 | DISTANCE_KM | float |
| 21 | COMMENTS1 | varchar(80) |
| 22 | COMMENTS2 | varchar(240) |
| 23 | DATE_SET_CHNG | datetime2 |
| 24 | DELETED | smallint |
| 25 | SELF_DB | smallint |

### ONA_209

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | ID | int NOT NULL |
| 2 | SPEED_TYPE | int |
| 3 | SENSOR_01 | float |
| 4 | SENSOR_02 | float |
| 5 | SENSOR_03 | float |
| 6 | SENSOR_04 | float |
| 7 | SENSOR_05 | float |
| 8 | SENSOR_06 | float |
| 9 | SENSOR_07 | float |
| 10 | SENSOR_08 | float |
| 11 | SENSOR_09 | float |
| 12 | SENSOR_10 | float |
| 13 | SENSOR_11 | float |
| 14 | SENSOR_12 | float |
| 15 | SENSOR_13 | float |
| 16 | SENSOR_14 | float |
| 17 | SENSOR_15 | float |
| 18 | SENSOR_16 | float |
| 19 | SENSOR_17 | float |
| 20 | SENSOR_18 | float |
| 21 | SENSOR_19 | float |
| 22 | SENSOR_20 | float |
| 23 | MEASUREMENT_DATE | datetime2 |
| 24 | VESSEL_NAME | varbinary(300) |
| 25 | MEASURING_PLACE | int |

### ONA_214

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | ID | int NOT NULL |
| 2 | SPEED_TYPE | int |
| 3 | SENSOR_01 | float |
| 4 | SENSOR_02 | float |
| 5 | SENSOR_03 | float |
| 6 | SENSOR_04 | float |
| 7 | SENSOR_05 | float |
| 8 | SENSOR_06 | float |
| 9 | SENSOR_07 | float |
| 10 | SENSOR_08 | float |
| 11 | SENSOR_09 | float |
| 12 | SENSOR_10 | float |
| 13 | SENSOR_11 | float |
| 14 | SENSOR_12 | float |
| 15 | SENSOR_13 | float |
| 16 | SENSOR_14 | float |
| 17 | SENSOR_15 | float |
| 18 | SENSOR_16 | float |
| 19 | SENSOR_17 | float |
| 20 | SENSOR_18 | float |
| 21 | SENSOR_19 | float |
| 22 | SENSOR_20 | float |
| 23 | SENSOR_21 | float |
| 24 | SENSOR_22 | float |
| 25 | SENSOR_23 | float |
| 26 | SENSOR_24 | float |
| 27 | SENSOR_25 | float |
| 28 | SENSOR_26 | float |
| 29 | SENSOR_27 | float |
| 30 | SENSOR_28 | float |
| 31 | SENSOR_29 | float |
| 32 | SENSOR_30 | float |
| 33 | SENSOR_31 | float |
| 34 | SENSOR_32 | float |
| 35 | MEASUREMENT_DATE | datetime2 |
| 36 | VESSEL_NAME | varbinary(300) |
| 37 | MEASURING_PLACE | int |

### OPERATOR

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | OPERATORID | varbinary(300) NOT NULL |
| 2 | OPERATORNAME | varbinary(300) |
| 3 | OPERATORPASSWORD | varbinary(300) |
| 4 | OPERATORAUTHORITY | int |
| 5 | ASSIGNDATE | datetime2 |
| 6 | EXPIREDATE | datetime2 |

### PASSWORD

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | PASSWORDID | int NOT NULL |
| 2 | OPERATORID | varchar(20) |
| 3 | OPERATORPASSWORD | varbinary(300) |

### PULSES

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | PULSE_ID | int NOT NULL |
| 2 | SONAR_ID | int |
| 3 | PULSE_NAME | varchar(30) |
| 4 | NO_SEGMENT | int |
| 5 | SPL | float |
| 6 | PLS_LEN_MS_MIN | int |
| 7 | PLS_LEN_MS_MAX | int |
| 8 | REP_TIME_MS_MIN | float |
| 9 | REP_TIME_MS_MAX | float |
| 10 | TRM_MODE | varchar(10) |
| 11 | MODULATION | varchar(12) |
| 12 | FIRST_SEG_ID | int |
| 13 | SECOND_SEG_ID | int |
| 14 | THIRD_SEG_ID | int |
| 15 | FOURTH_SEG_ID | int |
| 16 | FIFTH_SEG_ID | int |
| 17 | SIXTH_SEG_ID | int |
| 18 | SEVENTH_SEG_ID | int |
| 19 | DET_COUNT | int |
| 20 | COMMENTS | varchar(80) |
| 21 | DATE_SET_CHNG | datetime2 |
| 22 | DELETED | smallint |
| 23 | SELF_DB | smallint |

### PULSE_FREQUENCY_ISUS90

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | PULSE_F_ID | int NOT NULL |
| 2 | FREQUENCY | float |
| 3 | SONAR_ID | int |
| 4 | SELF_DB | smallint |

### PULSE_SEGMENTS

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SEGMENT_ID | int NOT NULL |
| 2 | SONAR_ID | int |
| 3 | FREQ_START | float |
| 4 | FREQ_STOP | float |
| 5 | MODULATION | varchar(12) |
| 6 | SEG_LEN_MS_MIN | int |
| 7 | SEG_LEN_MS_MAX | int |
| 8 | DATE_SET_CHNG | datetime2 |
| 9 | DELETED | smallint |
| 10 | SEGMENT_NAME | varchar(16) |
| 11 | SELF_DB | smallint |

### PULSE_SEG_LENGTH_ISUS90

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SEG_ID | int NOT NULL |
| 2 | LENGTH_MIN | int |
| 3 | LENGTH_MAX | int |
| 4 | PULSE_F_ID | int |
| 5 | SELF_DB | smallint |

### PULSE_SEG_SWING_ISUS90

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SWING_ID | int NOT NULL |
| 2 | SWING | float |
| 3 | MODULATION | varchar(5) |
| 4 | PULSE_F_ID | int |
| 5 | SELF_DB | smallint |

### RAWDATAIMPORTINFO

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | IMPORTID | int NOT NULL |
| 2 | IMPORTNAME | varchar(50) |
| 3 | RECORDINGDATE | datetime2 |
| 4 | VALIDTRACKCNT | int |
| 5 | EXTRACTSIGNALCNT | int |
| 6 | PROCESSINGSTATE | int |
| 7 | RECORDINGSYSTEM | int |
| 8 | RECORDINGVESSEL | varchar(45) |
| 9 | COMMENT | varchar(256) |
| 10 | IMPORTOPERATOR | varchar(20) |
| 11 | RECORDINGCLASS | varchar(20) |
| 12 | IMPORTDATE | datetime2 |
| 13 | RECORDINGLATITUDE | varchar(20) |
| 14 | RECORDINGLONGITUDE | varchar(20) |
| 15 | RECORDINGDISTANCE | int |
| 16 | RECORDINGMISSION | varchar(20) |
| 17 | RAWDATACNT | int |
| 18 | FEATURECNT | int |
| 19 | CCFINCLUDED | int |
| 20 | IMGINCLUDED | int |
| 21 | RECORDLOCKING | int |
| 22 | DELETED | int NOT NULL |
| 23 | OWNER | int NOT NULL |

### RAWDATAINFO

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | RAWDATAID | int NOT NULL |
| 2 | IMPORTID | int |
| 3 | RAWDATANAME | varchar(45) |
| 4 | EXTRACTSIGNALCNT | int |
| 5 | EXTRACTSIGNALTYPE | int |
| 6 | PROCESSINGSTATE | int |
| 7 | SAMPLINGRATE | int |
| 8 | EVENTCNT | int |
| 9 | RAWDATALENGTH | int |
| 10 | DETECTSONAR | varchar(45) |
| 11 | IMPORTNAME | varchar(50) |

### RAWSIGNALINFO

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | RAWSIGNALID | int NOT NULL |
| 2 | RAWDATAID | int |
| 3 | SIGNALNAME | varchar(50) |
| 4 | SIGNALTYPE | int |
| 5 | ANALYSISSTATE | int |
| 6 | SIZE | int |
| 7 | EXTRACTLENGTH | int |
| 8 | RECORDINGSYSTEM | int |
| 9 | RECORDINGVESSEL | varchar(45) |
| 10 | DETECTSONAR | varchar(45) |
| 11 | LOGFILE | int |
| 12 | DETECTCCF | int |
| 13 | IMPORTCCF | int |
| 14 | SAMPLERATE | int |
| 15 | IMAGECNT | int |
| 16 | FEATURECNT | int |
| 17 | IMPORTNAME | varchar(50) |
| 18 | IMPORTDATE | datetime2 |
| 19 | COMMENT | varchar(256) |
| 20 | RECORDINGDATE | datetime2 |
| 21 | RECORDINGLATITUDE | varchar(20) |
| 22 | RECORDINGLONGITUDE | varchar(20) |
| 23 | EXTRACTDATE | datetime2 |
| 24 | RECORDLOCKING | int |
| 25 | DELETED | int NOT NULL |
| 26 | OWNER | int NOT NULL |

### SIGNATURES

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SIGNATURE_ID | int NOT NULL |
| 2 | VESSEL_ID | int |
| 3 | TYPE_NOISE | varchar(15) |
| 4 | CATEGORY_NOISE | varchar(15) |
| 5 | ASPECT_TGT | varchar(30) |
| 6 | NOISE_SOURCE | varchar(25) |
| 7 | NOISE_SOURCE_L2 | varchar(20) |
| 8 | PROPULSION_SOURCE | varchar(12) |
| 9 | OPERATING_MODE | varchar(20) |
| 10 | SPEED_ACOUS_SIGN_LO_KN | float |
| 11 | SPEED_ACOUS_SIGN_HI_KN | float |
| 12 | FREQ_NOISE_LO_HZ | float |
| 13 | FREQ_NOISE_HI_HZ | float |
| 14 | FREQ_NOISE_AVG_HZ | float |
| 15 | NOISE_SOURCE_DEPTH_M | int |
| 16 | SPL_LO_DB | float |
| 17 | SPL_HI_DB | float |
| 18 | SPL_AVG_DB | float |
| 19 | BNDWTH_NOISE_LO_HZ | float |
| 20 | BNDWTH_NOISE_HI_HZ | float |
| 21 | BNDWTH_NOISE_AVG_HZ | float |
| 22 | TIME_NOISE_LO_SEC | float |
| 23 | TIME_NOISE_HI_SEC | float |
| 24 | NUMBER_HARMONICS | int |
| 25 | HARM_PREDOMINANCE | int |
| 26 | MULTIPLETS | int |
| 27 | DESYNCHRONIZATION | float |
| 28 | STABILITY | varchar(10) |
| 29 | REDUCTION_RATIO_MIN | float |
| 30 | REDUCTION_RATIO_MAX | float |
| 31 | RED_RATION_RMKS | varchar(80) |
| 32 | DET_COUNT | int |
| 33 | GEN_RMKS | varchar(240) |
| 34 | DATE_SET_CHNG | datetime2 |
| 35 | DELETED | int |
| 36 | HARMONIC_LIST | varchar(240) |
| 37 | PREDOMINANCE_LIST | varchar(80) |
| 38 | CPA | varchar(1) |
| 39 | SELF_DB | smallint |

### SIG_SNIPPET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | SIG_SNP_ID | int NOT NULL |
| 2 | NB_SNIPPET_ID | int |
| 3 | SIGNATURE_ID | int |
| 4 | DATE_SET_CHNG | datetime2 |
| 5 | DELETED | smallint |
| 6 | SELF_DB | smallint |

### TRANSIENT

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | TR_ID | int NOT NULL |
| 2 | CLASS_ID | int |
| 3 | VESSEL_ID | int |
| 4 | TR_NAME | varbinary(300) |
| 5 | TR_GROUP | varchar(20) |
| 6 | COMMENTS | varchar(80) |
| 7 | DATE_SET_CHNG | datetime2 |
| 8 | DELETED | smallint |
| 9 | SELF_DB | smallint |

### TR_FRAME

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | FRAME_ID | int NOT NULL |
| 2 | TR_ID | int |
| 3 | MFB_NO | int |
| 4 | MFB_VALUE_1 | float |
| 5 | MFB_VALUE_2 | float |
| 6 | MFB_VALUE_3 | float |
| 7 | MFB_VALUE_4 | float |
| 8 | MFB_VALUE_5 | float |
| 9 | MFB_VALUE_6 | float |
| 10 | MFB_VALUE_7 | float |
| 11 | MFB_VALUE_8 | float |
| 12 | MFB_VALUE_9 | float |
| 13 | MFB_VALUE_10 | float |
| 14 | MFB_VALUE_11 | float |
| 15 | MFB_VALUE_12 | float |
| 16 | MFB_VALUE_13 | float |
| 17 | MFB_VALUE_14 | float |
| 18 | MFB_VALUE_15 | float |
| 19 | MFB_VALUE_16 | float |
| 20 | MFB_VALUE_17 | float |
| 21 | MFB_VALUE_18 | float |
| 22 | MFB_VALUE_19 | float |
| 23 | MFB_VALUE_20 | float |
| 24 | MFB_VALUE_21 | float |
| 25 | MFB_VALUE_22 | float |
| 26 | MFB_VALUE_23 | float |
| 27 | MFB_VALUE_24 | float |
| 28 | MFB_VALUE_25 | float |
| 29 | MFB_VALUE_26 | float |
| 30 | MFB_VALUE_27 | float |
| 31 | MFB_VALUE_28 | float |
| 32 | MFB_VALUE_29 | float |
| 33 | MFB_VALUE_30 | float |
| 34 | MFB_VALUE_31 | float |
| 35 | MFB_VALUE_32 | float |
| 36 | DET_COUNT | int |
| 37 | COMMENTS | varchar(80) |
| 38 | DATE_SET_CHNG | datetime2 |
| 39 | DELETED | smallint |
| 40 | SELF_DB | smallint |

### TR_SNIPPET

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | TR_SNIPPET_ID | int NOT NULL |
| 2 | AUDIO_FILENAME | varchar(80) |
| 3 | NO_FRAME | int |
| 4 | A_START_TIME | int |
| 5 | A_END_TIME | int |
| 6 | SAMPLING_RATE | float |
| 7 | DET_SONAR | varchar(10) |
| 8 | LATITUDE_DEGREE | int |
| 9 | LATITUDE_MINUTE | int |
| 10 | LATITUDE_SECOND | int |
| 11 | LONGITUDE_DEGREE | int |
| 12 | LONGITUDE_MINUTE | int |
| 13 | LONGITUDE_SECOND | int |
| 14 | DET_DATE | datetime2 |
| 15 | DET_PLATFORM | varchar(30) |
| 16 | DISTANCE_KM | float |
| 17 | COMMENTS | varchar(240) |
| 18 | DATE_SET_CHNG | datetime2 |
| 19 | DELETED | smallint |
| 20 | SELF_DB | smallint |

### USER_JUMIN

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | UID | int NOT NULL |
| 2 | USER_ID | int NOT NULL |
| 3 | USER_NAME | varchar(20) NOT NULL |
| 4 | PASSWORD | varchar(20) NOT NULL |
| 5 | JUMIN_NO | varchar(18) NOT NULL |
| 6 | N_PASSWORD | varchar(100) NOT NULL |
| 7 | N_JUMIN_NO | varchar(100) NOT NULL |

### VESSEL_DATA

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | VESSEL_ID | int NOT NULL |
| 2 | CLASS_ID | int |
| 3 | COUNTRY_ID | int |
| 4 | VESSEL_NAME | varbinary(300) |
| 5 | VESSEL_NAME_K | varbinary(300) |
| 6 | VESSEL_NAME_ISUS90 | varbinary(300) |
| 7 | CTRY_USER_RMKS | varchar(35) |
| 8 | PENDANT_NO | varchar(10) |
| 9 | CALL_SIGN | varchar(20) |
| 10 | CREW | int |
| 11 | LAID_DOWN | varchar(20) |
| 12 | COMMISSIONED | varchar(20) |
| 13 | RETIRED | varchar(20) |
| 14 | MANUFACTURER | varchar(30) |
| 15 | VERSION | int |
| 16 | DATE_SET_CHNG | datetime2 |
| 17 | COMMENTS | varchar(80) |
| 18 | DELETED | smallint |
| 19 | SELF_DB | smallint |

### WEAPON

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | WEAPON_ID | int NOT NULL |
| 2 | WEAPON_MODEL | varchar(30) |
| 3 | WEAPON_TYPE | varchar(20) |
| 4 | ATTACK_RANGE_KM | float |
| 5 | GUIDE_METHOD | varchar(20) |
| 6 | MANUFACTURER | varchar(30) |
| 7 | MANUFACT_DATE | varchar(20) |
| 8 | VERSION | int |
| 9 | DATE_SET_CHNG | datetime2 |
| 10 | COMMENTS | varchar(80) |
| 11 | DELETED | smallint |
| 12 | SELF_DB | smallint |

### WEAPON_CARRIER

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | WCARRIER_ID | int NOT NULL |
| 2 | WEAPON_ID | int |
| 3 | CLASS_ID | int |
| 4 | QUANTITY | int |
| 5 | DATE_SET_CHNG | datetime2 |
| 6 | SELF_DB | smallint |

### sysdiagrams

| # | 컬럼 | 타입 |
|---|---|---|
| 1 | name | sysname NOT NULL |
| 2 | principal_id | int NOT NULL |
| 3 | diagram_id | int NOT NULL IDENTITY |
| 4 | version | int |
| 5 | definition | varbinary(max) |

