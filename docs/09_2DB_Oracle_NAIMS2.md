# NAIMS-I (Oracle) → NAIMS-II (NAIMS2) 컬럼 매핑

| 항목 | 내용 |
|---|---|
| 문서번호 | NAIMS-MIG-260722-09 |
| 작성일 | 2026-07-22 |

구 NAIMS-I(Oracle) → 신규 NAIMS-II(NAIMS2) 이관 컬럼 대응. STD_* 전용. `—`=대응 없음, '(신규)'=NAIMS2 신규 컬럼.

## 1.1. COUNTRY_CODES → STD_COUNTRY_CODES

9개 컬럼 · 확정 8 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| COUNTRY_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| CTRY_CODE | VARCHAR2(2 BYTE) | CTRY_CODE | varchar(2) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CTRY_NAME | VARCHAR2(35 BYTE) | CTRY_NAME | varchar(35) NOT NULL | Direct | 확정 | 완전하게 대응 |
| HOSTILE_LEVEL | NUMBER | HOSTILE_LEVEL | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| CTRY_NAME_ISUS90 | VARCHAR2(20 BYTE) | CTRY_NAME_ISUS90 | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| FLAG_FILENAME | VARCHAR2(40 BYTE) | FLAG_FILE | varbinary(max) | 경로의 파일을 읽어 파일 자체(byte[])를 저장 | 확정 | MsSQL: 타입 계열 상이 (STR→BIN) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→BIN) 변환 규칙 확정 필요 |
| COMMENTS | VARCHAR2(40 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 1.2. CLASSES → STD_CLASSES

70개 컬럼 · 확정 69 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| CLASS_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| CLASS_NAME | RAW(200) | CLASS_NAME | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| CLASS_NAME_K | RAW(200) | CLASS_NAME_K | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| SHIP_SUB | VARCHAR2(12 BYTE) | SHIP_SUB | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CLASS_TYPE | VARCHAR2(5 BYTE) | CLASS_TYPE | varchar(5) NOT NULL | Direct | 확정 | 완전하게 대응 |
| ACTIVE_SONAR1 | NUMBER(*,0) | ACTV_SONAR1 | int NOT NULL | Rename | 확정 | - |
| ACTIVE_SONAR2 | NUMBER(*,0) | ACTV_SONAR2 | int NOT NULL | Rename | 확정 | - |
| DRAUGHT_M | NUMBER(12,3) | DRAUGHT_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| LENGTH_M | NUMBER(12,3) | LENGTH_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| WIDTH_M | NUMBER(12,3) | WIDTH_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISPLACEMENT | NUMBER(12,3) | DISPLACEMENT | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_RANGE_KM | NUMBER(12,3) | DET_RANGE_KM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_BLADES | NUMBER(2,0) | NUMBER_BLADES | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_SHAFTS | NUMBER(1,0) | NUMBER_SHAFTS | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| PROPULSION_TYPE | VARCHAR2(20 BYTE) | PROPULSION_TYPE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| ENGINE_TYPE | VARCHAR2(30 BYTE) | ENGINE_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_CYLINDERS | NUMBER(2,0) | NUMBER_CYLINDERS | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| STROKE_TYPE | NUMBER(1,0) | STROKE_TYPE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_ENGINES | NUMBER(1,0) | NUMBER_ENGINES | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_CFR | NUMBER(12,3) | MAX_CFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_CFR | NUMBER(12,3) | AVG_CFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_CFR | NUMBER(12,3) | MIN_CFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_CSR | NUMBER(12,3) | MAX_CSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_CSR | NUMBER(12,3) | AVG_CSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_CSR | NUMBER(12,3) | MIN_CSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_EFR | NUMBER(12,3) | MAX_EFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_EFR | NUMBER(12,3) | AVG_EFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_EFR | NUMBER(12,3) | MIN_EFR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_CSR_RATIO | NUMBER(12,3) | PSR_CSR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_CFR_RATIO | NUMBER(12,3) | PSR_CFR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_EFR_RATIO | NUMBER(12,3) | PSR_EFR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| TURBINE_TYPE | VARCHAR2(30 BYTE) | TURBINE_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_TURBINES | NUMBER(1,0) | NUMBER_TURBINES | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_TSR | NUMBER(12,3) | MAX_TSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_TSR | NUMBER(12,3) | AVG_TSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_TSR | NUMBER(12,3) | MIN_TSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_TSR_RATIO | NUMBER(12,3) | PSR_TSR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| TSR_GEN_RATIO | NUMBER(12,3) | TSR_GEN_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MOTOR_TYPE | VARCHAR2(30 BYTE) | MOTOR_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_MOTORS | NUMBER(1,0) | NUMBER_MOTORS | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_SLOT | NUMBER(2,0) | NUMBER_SLOT | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_MSR | NUMBER(12,3) | MAX_MSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_MSR | NUMBER(12,3) | AVG_MSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_MSR | NUMBER(12,3) | MIN_MSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_MSR_RATIO | NUMBER(12,3) | PSR_MSR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_SPEED | NUMBER(12,3) | MIN_SPEED | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_SPEED | NUMBER(12,3) | MAX_SPEED | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MAX_PSR | NUMBER(12,3) | MAX_PSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| AVG_PSR | NUMBER(12,3) | AVG_PSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MIN_PSR | NUMBER(12,3) | MIN_PSR | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| BR_GEN_RATIO | NUMBER(12,3) | BR_GEN_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| GEAR_RED_TYPE | VARCHAR2(25 BYTE) | GEAR_RED_TYPE | varchar(25) NOT NULL | Direct | 확정 | 완전하게 대응 |
| PSR_BGR_RATIO | NUMBER(12,3) | PSR_BGR_RATIO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| USER_COUNTRIES | VARCHAR2(80 BYTE) | USER_COUNTRIES | varchar(1000) | Direct | 확정 | 완전하게 대응 |
| H_MAST_M | NUMBER(12,3) | H_MAST_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| H_RADAR_M | NUMBER(12,3) | H_RADAR_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| H_BRIDGE_M | NUMBER(12,3) | H_BRIDGE_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| VERSION | NUMBER(5,0) | VERSION | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| CLASS_NAME_ISUS90 | RAW(200) | CLASS_NAME_ISUS90 | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| (신규) | - | PASSIVE_SONAR | int NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MIN_SRPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MAX_SRPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MIN_ERPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MAX_ERPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MIN_TRPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | MAX_TRPM | real NOT NULL | 임시 기본값 -1 | 확정 | 소스 대응 없음 — 임시로 -1 (추후 확정) |
| (신규) | - | REF_COMMENT | varchar(500) | 고정값 | 확정 | NAIMS-I 이관 데이터 |

## 1.3. VESSEL_DATA → STD_VESSEL_DATA

19개 컬럼 · 확정 18 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| VESSEL_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| CLASS_ID | NUMBER(*,0) | REF_STD_CLASSES_ID | int NOT NULL | FKRename | 확정 | - |
| COUNTRY_ID | NUMBER(*,0) | REF_STD_COUNTRY_CODES_ID | int NOT NULL | FKRename | 확정 | - |
| VESSEL_NAME | RAW(200) | VESSEL_NAME | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| VESSEL_NAME_K | RAW(200) | VESSEL_NAME_K | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| VESSEL_NAME_ISUS90 | RAW(200) | VESSEL_NAME_ISUS90 | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 복호화 필요 (암호문→평문); 양쪽 암호화 — 복호화 후 적재 |
| CTRY_USER_RMKS | VARCHAR2(35 BYTE) | CTRY_USER_RMKS | varchar(35) NOT NULL | Direct | 확정 | 완전하게 대응 |
| PENDANT_NO | VARCHAR2(10 BYTE) | PENDANT_NO | varchar(10) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CALL_SIGN | VARCHAR2(20 BYTE) | CALL_SIGN | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CREW | NUMBER(5,0) | CREW | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| LAID_DOWN | VARCHAR2(20 BYTE) | LAID_DOWN | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMISSIONED | VARCHAR2(20 BYTE) | COMMISSIONED | varchar(20) | Direct | 확정 | 완전하게 대응 |
| RETIRED | VARCHAR2(20 BYTE) | RETIRED | varchar(20) | Direct | 확정 | 완전하게 대응 |
| MANUFACTURER | VARCHAR2(30 BYTE) | MANUFACTURER | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| VERSION | NUMBER(5,0) | VERSION | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| (신규) | - | HAS_CLUTTER | smallint NOT NULL | 신규 | 확정 | 타겟 기본값 0 로 채움 |

## 1.4. CHARACTERISTICS → STD_CHARACTERISTICS

42개 컬럼 · 확정 41 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| CHAR_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| VESSEL_ID | NUMBER(*,0) | REF_STD_VESSEL_DATA_ID | int NOT NULL | FKRename | 확정 | - |
| CAVITATION_DEPTH_M | NUMBER(12,3) | CAVITATION_DEPTH_M | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| CAVITATION_SPEED | NUMBER(12,3) | CAVITATION_SPEED | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| CAVITATION_I90 | VARCHAR2(8 BYTE) | CAVITATION_I90 | varchar(8) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DIST_FEATURES | VARCHAR2(40 BYTE) | DIST_FEATURES | varchar(100) NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPECTRAL_RANGE | VARCHAR2(6 BYTE) | SPECTRAL_RANGE | varchar(6) NOT NULL | Direct | 확정 | 완전하게 대응 |
| BLADE_RESONANCE | CHAR(1 BYTE) | BLADE_RESONANCE | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| FLUTTER | CHAR(1 BYTE) | FLUTTER | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| SHAFT_RUB | CHAR(1 BYTE) | SHAFT_RUB | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| WHINE | CHAR(1 BYTE) | WHINE | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| TRANSMISSIONS | CHAR(1 BYTE) | TRANSMISSIONS | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NOISE_MAKER | CHAR(1 BYTE) | NOISE_MAKER | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| BEST_TRK_FREQ | NUMBER(12,3) | BEST_TRK_FREQ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| STRONG_SRC_GT_300 | NUMBER(12,3) | STRONG_SRC_GT_300 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| STRONG_SRC_LT_300 | NUMBER(12,3) | STRONG_SRC_LT_300 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ1_MAX | NUMBER(12,3) | DISCRETE_FREQ1_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ1_AVG | NUMBER(12,3) | DISCRETE_FREQ1_AVG | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ1_MIN | NUMBER(12,3) | DISCRETE_FREQ1_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ1_TYPE | VARCHAR2(30 BYTE) | DISCRETE_FREQ1_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ2_MAX | NUMBER(12,3) | DISCRETE_FREQ2_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ2_AVG | NUMBER(12,3) | DISCRETE_FREQ2_AVG | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ2_MIN | NUMBER(12,3) | DISCRETE_FREQ2_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ2_TYPE | VARCHAR2(30 BYTE) | DISCRETE_FREQ2_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ3_MAX | NUMBER(12,3) | DISCRETE_FREQ3_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ3_AVG | NUMBER(12,3) | DISCRETE_FREQ3_AVG | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ3_MIN | NUMBER(12,3) | DISCRETE_FREQ3_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ3_TYPE | VARCHAR2(30 BYTE) | DISCRETE_FREQ3_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ4_MAX | NUMBER(12,3) | DISCRETE_FREQ4_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ4_AVG | NUMBER(12,3) | DISCRETE_FREQ4_AVG | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ4_MIN | NUMBER(12,3) | DISCRETE_FREQ4_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISCRETE_FREQ4_TYPE | VARCHAR2(30 BYTE) | DISCRETE_FREQ4_TYPE | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE1_SPD_LO | NUMBER(5,2) | RANGE1_SPD_LO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE1_SPD_HI | NUMBER(5,2) | RANGE1_SPD_HI | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE1_TPK_RPM | NUMBER(7,2) | RANGE1_TPK_RPM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE2_SPD_LO | NUMBER(5,2) | RANGE2_SPD_LO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE2_SPD_HI | NUMBER(5,2) | RANGE2_SPD_HI | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE2_TPK_RPM | NUMBER(7,2) | RANGE2_TPK_RPM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE3_SPD_LO | NUMBER(5,2) | RANGE3_SPD_LO | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE3_SPD_HI | NUMBER(5,2) | RANGE3_SPD_HI | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE3_TPK_RPM | NUMBER(7,2) | RANGE3_TPK_RPM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| (신규) | - | CHNG_DATETIME | datetime2(7) NOT NULL | 신규 | 확정 | 타겟 기본값 sysdatetime() 로 채움 |

## 2.1. SIGNATURES → STD_SIGNATURES

39개 컬럼 · 확정 37 · 자동생성 1 · 신규제안 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SIGNATURE_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| VESSEL_ID | NUMBER(*,0) | REF_STD_VESSEL_DATA_ID | int NOT NULL | FKRename | 확정 | - |
| TYPE_NOISE | VARCHAR2(15 BYTE) | TYPE_NOISE | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CATEGORY_NOISE | VARCHAR2(15 BYTE) | CATEGORY_NOISE | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| ASPECT_TGT | VARCHAR2(30 BYTE) | ASPECT_TGT | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NOISE_SOURCE | VARCHAR2(25 BYTE) | NOISE_SOURCE | varchar(25) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NOISE_SOURCE_L2 | VARCHAR2(20 BYTE) | NOISE_SOURCE_L2 | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| PROPULSION_SOURCE | VARCHAR2(12 BYTE) | PROPULSION_SOURCE | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| OPERATING_MODE | VARCHAR2(20 BYTE) | OPERATING_MODE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPEED_ACOUS_SIGN_LO_KN | NUMBER(12,3) | SPEED_ACOUS_SIGN_LO_KN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPEED_ACOUS_SIGN_HI_KN | NUMBER(12,3) | SPEED_ACOUS_SIGN_HI_KN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_NOISE_LO_HZ | NUMBER(12,3) | FREQ_NOISE_LO_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_NOISE_HI_HZ | NUMBER(12,3) | FREQ_NOISE_HI_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_NOISE_AVG_HZ | NUMBER(12,3) | FREQ_NOISE_AVG_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| NOISE_SOURCE_DEPTH_M | NUMBER(5,0) | NOISE_SOURCE_DEPTH_M | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPL_LO_DB | NUMBER(12,3) | SPL_LO_DB | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPL_HI_DB | NUMBER(12,3) | SPL_HI_DB | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPL_AVG_DB | NUMBER(12,3) | SPL_AVG_DB | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| BNDWTH_NOISE_LO_HZ | NUMBER(12,3) | BNDWTH_NOISE_LO_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| BNDWTH_NOISE_HI_HZ | NUMBER(12,3) | BNDWTH_NOISE_HI_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| BNDWTH_NOISE_AVG_HZ | NUMBER(12,3) | BNDWTH_NOISE_AVG_HZ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| TIME_NOISE_LO_SEC | NUMBER(12,3) | TIME_NOISE_LO_SEC | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| TIME_NOISE_HI_SEC | NUMBER(12,3) | TIME_NOISE_HI_SEC | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUMBER_HARMONICS | NUMBER(4,0) | NUMBER_HARMONICS | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| HARM_PREDOMINANCE | NUMBER(4,0) | HARM_PREDOMINANCE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| MULTIPLETS | NUMBER(1,0) | MULTIPLETS | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DESYNCHRONIZATION | NUMBER(12,3) | DESYNCHRONIZATION | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| STABILITY | VARCHAR2(10 BYTE) | STABILITY | varchar(10) NOT NULL | Direct | 확정 | 완전하게 대응 |
| REDUCTION_RATIO_MIN | NUMBER(12,3) | REDUCTION_RATIO_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| REDUCTION_RATIO_MAX | NUMBER(12,3) | REDUCTION_RATIO_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| RED_RATION_RMKS | VARCHAR2(80 BYTE) | RED_RATION_RMKS | varchar(100) | Direct | 확정 | 완전하게 대응 |
| DET_COUNT | NUMBER | DET_COUNT | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| GEN_RMKS | VARCHAR2(240 BYTE) | GEN_RMKS | varchar(1000) | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | int NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| HARMONIC_LIST | VARCHAR2(240 BYTE) | HARMONIC_LIST | varchar(2000) NOT NULL | Direct | 확정 | 완전하게 대응 |
| PREDOMINANCE_LIST | VARCHAR2(80 BYTE) | PREDOMINANCE_LIST | varchar(200) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CPA | CHAR(1 BYTE) | CPA | varchar(1) NOT NULL | Direct | 확정 | 완전하게 대응 |
| (신규) | - | REF_STD_NB_SNIPPET_ID | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |

## 2.2. HARMONICS → STD_HARMONICS

5개 컬럼 · 확정 4 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| HARM_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| SIGNATURES_ID | NUMBER(*,0) | REF_STD_SIGNATURES_ID | int NOT NULL | FKRename | 확정 | - |
| HARM_NO | NUMBER(3,0) | HARM_NO | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ | NUMBER(12,3) | FREQ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PREDOMINANCE | NUMBER(1,0) | PREDOMINANCE | smallint NOT NULL | Direct | 확정 | 완전하게 대응 |

## 3.1. ACTIVE_SONAR → STD_ACTV_SONAR

20개 컬럼 · 확정 19 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SONAR_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| SONAR_NAME | VARCHAR2(100 BYTE) | SONAR_NAME | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); MsSQL만 암호화 / Oracle 평문 |
| SONAR_NAME_ISUS90 | VARCHAR2(100 BYTE) | SONAR_NAME_ISUS90 | varchar(100) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); MsSQL만 암호화 / Oracle 평문 |
| TRANS_TYPE | VARCHAR2(20 BYTE) | TRANS_TYPE | varchar(20) | Direct | 확정 | 완전하게 대응 |
| SONAR_TYPE | VARCHAR2(20 BYTE) | SONAR_TYPE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| TRM_MODE | VARCHAR2(10 BYTE) | TRM_MODE | varchar(10) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MODULATION | VARCHAR2(12 BYTE) | MODULATION | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| SOURCE_LEVEL_MIN | NUMBER(4,0) | SOURCE_LEVEL_MIN | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SOURCE_LEVEL_MAX | NUMBER(4,0) | SOURCE_LEVEL_MAX | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE_RESOL_M_MIN | NUMBER(4,0) | RANGE_RESOL_M_MIN | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| RANGE_RESOL_M_MAX | NUMBER(4,0) | RANGE_RESOL_M_MAX | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DPLR_RESOL_KTS_MIN | NUMBER(6,2) | DPLR_RESOL_KTS_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DPLR_RESOL_KTS_MAX | NUMBER(6,2) | DPLR_RESOL_KTS_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_RANGE_KM | NUMBER(12,3) | DET_RANGE_KM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MANUFACTURER | VARCHAR2(30 BYTE) | MANUFACTURER | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MANUFACT_DATE | VARCHAR2(20 BYTE) | MANUFACT_DATE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| VERSION | NUMBER(5,0) | VERSION | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | DATE_SET_CHNG -> CHNG_DATETIME |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | COMMENTS -> COMMENT; 말머리 [NAIMS-I] 삽입 |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 3.2. PULSES → STD_PULSES

22개 컬럼 · 확정 21 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| PULSE_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| SONAR_ID | NUMBER(*,0) | REF_STD_ACTV_SONAR_ID | int NOT NULL | FKRename | 확정 | - |
| PULSE_NAME | VARCHAR2(30 BYTE) | PULSE_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NO_SEGMENT | NUMBER(2,0) | NO_SEGMENT | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SPL | NUMBER(12,3) | SPL | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| PLS_LEN_MS_MIN | NUMBER(10,0) | PLS_LEN_MS_MIN | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| PLS_LEN_MS_MAX | NUMBER(10,0) | PLS_LEN_MS_MAX | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| REP_TIME_MS_MIN | NUMBER(12,3) | REP_TIME_MS_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| REP_TIME_MS_MAX | NUMBER(12,3) | REP_TIME_MS_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| TRM_MODE | VARCHAR2(10 BYTE) | TRM_MODE | varchar(10) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MODULATION | VARCHAR2(12 BYTE) | MODULATION | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| FIRST_SEG_ID | NUMBER(*,0) | FIRST_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SECOND_SEG_ID | NUMBER(*,0) | SECOND_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| THIRD_SEG_ID | NUMBER(*,0) | THIRD_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| FOURTH_SEG_ID | NUMBER(*,0) | FOURTH_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| FIFTH_SEG_ID | NUMBER(*,0) | FIFTH_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SIXTH_SEG_ID | NUMBER(*,0) | SIXTH_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SEVENTH_SEG_ID | NUMBER(*,0) | SEVENTH_SEG_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_COUNT | NUMBER(*,0) | DET_COUNT | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 3.3. PULSE_SEGMENTS → STD_PULSE_SEGMENTS

10개 컬럼 · 확정 9 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SEGMENT_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| SONAR_ID | NUMBER(*,0) | REF_STD_ACTV_SONAR_ID | int NOT NULL | FKRename | 확정 | - |
| FREQ_START | NUMBER(12,3) | FREQ_START | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_STOP | NUMBER(12,3) | FREQ_STOP | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MODULATION | VARCHAR2(12 BYTE) | MODULATION | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| SEG_LEN_MS_MIN | NUMBER(6,0) | SEG_LEN_MS_MIN | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| SEG_LEN_MS_MAX | NUMBER(6,0) | SEG_LEN_MS_MAX | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| SEGMENT_NAME | VARCHAR2(16 BYTE) | SEGMENT_NAME | varchar(16) NOT NULL | Direct | 확정 | 완전하게 대응 |

## 3.4. PULSE_FREQUENCY_ISUS90 → STD_PULSE_FREQUENCY_ISUS90

3개 컬럼 · 확정 2 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| PULSE_F_ID | NUMBER | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| FREQUENCY | NUMBER(12,3) | FREQUENCY | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| SONAR_ID | NUMBER(*,0) | REF_STD_ACTV_SONAR_ID | int NOT NULL | FKRename | 확정 | - |

## 3.5. PULSE_SEG_LENGTH_ISUS90 → STD_PULSE_SEG_LENGTH_ISUS90

4개 컬럼 · 확정 3 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SEG_ID | NUMBER | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| LENGTH_MIN | NUMBER | LENGTH_MIN | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| LENGTH_MAX | NUMBER | LENGTH_MAX | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| PULSE_F_ID | NUMBER | REF_STD_PULSE_FREQUENCY_ISUS90_ID | int NOT NULL | FKRename | 확정 | - |

## 3.6. PULSE_SEG_SWING_ISUS90 → STD_PULSE_SEG_SWING_ISUS90

4개 컬럼 · 확정 3 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SWING_ID | NUMBER | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| SWING | NUMBER(12,3) | SWING | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MODULATION | VARCHAR2(5 BYTE) | MODULATION | varchar(12) NOT NULL | Direct | 확정 | 완전하게 대응 |
| PULSE_F_ID | NUMBER | REF_STD_PULSE_FREQUENCY_ISUS90_ID | int NOT NULL | FKRename | 확정 | - |

## 4.1. TRANSIENT → STD_TRANSIENT

8개 컬럼 · 확정 7 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| TR_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| CLASS_ID | NUMBER(*,0) | REF_STD_CLASSES_ID | int | FKRename | 확정 | - |
| VESSEL_ID | NUMBER(*,0) | REF_STD_VESSEL_DATA_ID | int | FKRename | 확정 | - |
| TR_NAME | VARCHAR2(200 BYTE) | TR_NAME | varchar(50) NOT NULL | Direct | 확정 | MsSQL: 복호화 필요 (암호문→평문); Oracle: 길이 축소 200→50 — 잘림 주의; MsSQL만 암호화 / Oracle 평문 |
| TR_GROUP | VARCHAR2(20 BYTE) | TR_GROUP | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 4.2. TR_FRAME → STD_TR_FRAME

39개 컬럼 · 확정 38 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| FRAME_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| TR_ID | NUMBER(*,0) | REF_STD_TRANSIENT_ID | int NOT NULL | FKRename | 확정 | - |
| MFB_NO | NUMBER(3,0) | MFB_NO | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_1 | NUMBER(9,4) | MFB_VALUE_1 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_2 | NUMBER(9,4) | MFB_VALUE_2 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_3 | NUMBER(9,4) | MFB_VALUE_3 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_4 | NUMBER(9,4) | MFB_VALUE_4 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_5 | NUMBER(9,4) | MFB_VALUE_5 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_6 | NUMBER(9,4) | MFB_VALUE_6 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_7 | NUMBER(9,4) | MFB_VALUE_7 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_8 | NUMBER(9,4) | MFB_VALUE_8 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_9 | NUMBER(9,4) | MFB_VALUE_9 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_10 | NUMBER(9,4) | MFB_VALUE_10 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_11 | NUMBER(9,4) | MFB_VALUE_11 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_12 | NUMBER(9,4) | MFB_VALUE_12 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_13 | NUMBER(9,4) | MFB_VALUE_13 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_14 | NUMBER(9,4) | MFB_VALUE_14 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_15 | NUMBER(9,4) | MFB_VALUE_15 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_16 | NUMBER(9,4) | MFB_VALUE_16 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_17 | NUMBER(9,4) | MFB_VALUE_17 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_18 | NUMBER(9,4) | MFB_VALUE_18 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_19 | NUMBER(9,4) | MFB_VALUE_19 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_20 | NUMBER(9,4) | MFB_VALUE_20 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_21 | NUMBER(9,4) | MFB_VALUE_21 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_22 | NUMBER(9,4) | MFB_VALUE_22 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_23 | NUMBER(9,4) | MFB_VALUE_23 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_24 | NUMBER(9,4) | MFB_VALUE_24 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_25 | NUMBER(9,4) | MFB_VALUE_25 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_26 | NUMBER(9,4) | MFB_VALUE_26 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_27 | NUMBER(9,4) | MFB_VALUE_27 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_28 | NUMBER(9,4) | MFB_VALUE_28 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_29 | NUMBER(9,4) | MFB_VALUE_29 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_30 | NUMBER(9,4) | MFB_VALUE_30 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_31 | NUMBER(9,4) | MFB_VALUE_31 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| MFB_VALUE_32 | NUMBER(9,4) | MFB_VALUE_32 | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_COUNT | NUMBER(5,0) | DET_COUNT | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 5.1. NB_SNIPPET → STD_NB_SNIPPET

37개 컬럼 · 확정 35 · 자동생성 1 · 신규제안 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| NB_SNIPPET_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| GRAM_FILENAME | VARCHAR2(80 BYTE) | STD_NB_GRAM_SNIPPET_FILE_NAME | varchar(150) | GRAM_FILENAME | 확정 | 소스 GRAM_FILENAME(파일명) |
| AUDIO_FILENAME | VARCHAR2(80 BYTE) | STD_NB_AUDIO_SNIPPET_FILE_NAME | varchar(150) | AUDIO_FILENAME | 확정 | 소스 AUDIO_FILENAME(파일명) |
| FREQ_MIN | NUMBER(12,3) | FREQ_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_MAX | NUMBER(12,3) | FREQ_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| NO_FRAME | NUMBER(4,0) | FRAME_NUM | int NOT NULL | Rename | 확정 | - |
| RESOLUTION | NUMBER(12,3) | RESOLUTION | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| A_START_TIME | NUMBER | A_START_MILLISEC | bigint NOT NULL | Rename | 확정 | 단위 확인 필요 |
| A_END_TIME | NUMBER | A_END_MILLISEC | bigint NOT NULL | Rename | 확정 | 단위 확인 필요 |
| SAMPLING_RATE | NUMBER(12,3) | SAMPLING_RATE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_SONAR | VARCHAR2(10 BYTE) | DET_SONAR | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| LATITUDE_DEGREE | NUMBER | LATITUDE_DEGREE | int | Direct | 확정 | 완전하게 대응 |
| LATITUDE_MINUTE | NUMBER | LATITUDE_MINUTE | int | Direct | 확정 | 완전하게 대응 |
| LATITUDE_SECOND | NUMBER | LATITUDE_SECOND | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_DEGREE | NUMBER | LONGITUDE_DEGREE | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_MINUTE | NUMBER | LONGITUDE_MINUTE | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_SECOND | NUMBER | LONGITUDE_SECOND | int | Direct | 확정 | 완전하게 대응 |
| DET_DATE | DATE | DET_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | datetime 변환 |
| DET_PLATFORM | VARCHAR2(30 BYTE) | DET_PLATFORM | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISTANCE_KM | NUMBER(12,3) | DISTANCE_KM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS1 | VARCHAR2(80 BYTE) | COMMENT1 | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| COMMENTS2 | VARCHAR2(240 BYTE) | COMMENT2 | varchar(3000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| (신규) | - | CLASSIFI_NB_GRAM_SNIPPET_FILE | varbinary(max) NOT NULL | grm→R8 변환 적재 | 확정 | 레거시 .grm 을 신규 R8 66B 포맷으로 변환해 적재 (GramReader.cs) |
| (신규) | - | CLASSIFI_NB_AUDIO_SNIPPET_FILE | varbinary(max) NOT NULL | wav 원본 적재 | 확정 | 스니펫 실제 wav 데이터를 그대로 적재 |
| (신규) | - | BIN_NUM | int NOT NULL | frame block bin 수 | 확정 | 실제 frame block 의 bin 수 (기본값 1601) |
| (신규) | - | ROWGUID | uniqueidentifier NOT NULL | 신규 | 확정 | 타겟 기본값 newid() 로 채움 |
| (신규) | - | GRAM_TYPE | int NOT NULL | .grm 헤더 cGram→코드 | 확정 | 레거시 헤더 @0 cGram[3] 태그로 결정: L→101(LOFAR), D→201(DEMON), V1~V4→301~304(VERNIER). |
| (신규) | - | SNIPPET_START_DATETIME | datetime2(7) NOT NULL | wav 헤더 계산 | 확정 | wav 헤더로 시작시각 계산 |
| (신규) | - | SNIPPET_END_DATETIME | datetime2(7) NOT NULL | wav 헤더 계산 | 확정 | wav 헤더로 끝시각 계산 |
| (신규) | - | INTEGRAL_TIME | real NOT NULL | 고정값 -1 | 확정 | -1 |
| (신규) | - | GRAM_UPDATE_TIME | real NOT NULL | 마이그레이션 시각 | 확정 | 이관 수행 시각 |
| (신규) | - | NB_ALI_SNIPPET_DATA | varbinary(max) | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | IS_LEGACY | bit NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | A_START_MILLISEC_FOR_NAIMS1 | bigint NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | A_END_MILLISEC_FOR_NAIMS1 | bigint NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |

## 5.2. AI_SNIPPET → STD_AI_SNIPPET

30개 컬럼 · 확정 29 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| AI_SNIPPET_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| PULSE_ID | NUMBER(*,0) | REF_STD_PULSES_ID | int NOT NULL | FKRename | 확정 | legacy pulse -> target std pulses |
| GRAM_FILENAME | VARCHAR2(80 BYTE) | GRAM_FILE_NAME | varchar(150) NOT NULL | Rename | 확정 | 파일명만 직접 매핑 |
| AUDIO_FILENAME | VARCHAR2(80 BYTE) | AUDIO_FILE_NAME | varchar(150) NOT NULL | Rename | 확정 | 파일명만 직접 매핑 |
| FREQ_MIN | NUMBER(12,3) | FREQ_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_MAX | NUMBER(12,3) | FREQ_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| NO_FRAME | NUMBER(4,0) | FRAME_NUM | int NOT NULL | Rename | 확정 | - |
| RESOLUTION | NUMBER(12,3) | RESOLUTION | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| A_ORG_CFREQ | NUMBER(12,3) | A_ORG_CFREQ | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| A_START_TIME | NUMBER | A_START_MICROSEC | bigint NOT NULL | 초→µs (×1000000) | 확정 | 소스 A_START_TIME 은 초. AAS 확인: a_start_time = factchunk.start_time(초, ViewCCFApplySubAI.cpp:3180). 타겟 µs 이므로 ×1,000,000 |
| A_END_TIME | NUMBER | A_END_MICROSEC | bigint NOT NULL | 초→µs (×1000000) | 확정 | 소스 A_END_TIME 은 초. 타겟 µs 이므로 ×1,000,000 |
| SAMPLING_RATE | NUMBER(12,3) | SAMPLING_RATE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_SONAR | VARCHAR2(10 BYTE) | DET_SONAR | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| LATITUDE_DEGREE | NUMBER | LATITUDE_DEGREE | int | Direct | 확정 | 타겟 기본값 (0) |
| LATITUDE_MINUTE | NUMBER | LATITUDE_MINUTE | int | Direct | 확정 | 타겟 기본값 (0) |
| LATITUDE_SECOND | NUMBER | LATITUDE_SECOND | int | Direct | 확정 | 타겟 기본값 (0) |
| LONGITUDE_DEGREE | NUMBER | LONGITUDE_DEGREE | int | Direct | 확정 | 타겟 기본값 (0) |
| LONGITUDE_MINUTE | NUMBER | LONGITUDE_MINUTE | int | Direct | 확정 | 타겟 기본값 (0) |
| LONGITUDE_SECOND | NUMBER | LONGITUDE_SECOND | int | Direct | 확정 | 타겟 기본값 (0) |
| DET_DATE | DATE | DET_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | datetime 변환 |
| DET_PLATFORM | VARCHAR2(30 BYTE) | DET_PLATFORM | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DISTANCE_KM | NUMBER(12,3) | DET_DISTANCE_NM | real NOT NULL | 우선 nm로 가정하여 값 그대로 저장하고, 프로그램 내 단위 동기화/변환 가능하도록 처리 | 확정 | 현재는 nm 가정, 후속 구현에서 단위 sync 지원 |
| COMMENTS1 | VARCHAR2(80 BYTE) | COMMENT1 | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| COMMENTS2 | VARCHAR2(240 BYTE) | COMMENT2 | varchar(3000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| (신규) | - | GRAM_FILE | varbinary(max) NOT NULL | grm→R8 변환 적재 | 확정 | 레거시 .grm 을 신규 R8 66B 포맷으로 변환해 적재 |
| (신규) | - | AUDIO_FILE | varbinary(max) NOT NULL | wav 원본 적재 | 확정 | 스니펫 실제 wav 데이터 |
| (신규) | - | BIN_NUM | int NOT NULL | 그램 데이터에서 계산 | 확정 | 변환된 그램 데이터의 bin 수 계산 |
| (신규) | - | ROWGUID | uniqueidentifier NOT NULL | 신규 | 확정 | 타겟 기본값 newid() 로 채움 |

## 5.3. TR_SNIPPET → STD_TR_SNIPPET

26개 컬럼 · 확정 25 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| TR_SNIPPET_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| AUDIO_FILENAME | VARCHAR2(80 BYTE) | AUDIO_FILE_NAME | varchar(150) | Rename | 확정 | 파일명만 매핑 |
| NO_FRAME | NUMBER(4,0) | FRAME_NUM | int NOT NULL | Rename | 확정 | - |
| A_START_TIME | NUMBER | A_START_MICROSEC | bigint NOT NULL | 초→µs (×1000000) | 확정 | 소스 A_START_TIME 은 초(seconds). AAS 확인: a_start_time = 오디오헤더 factchunk.start_time(= µs/1000000 = 초, ViewCCFApplySubTR.cpp:1993). 타겟 µs 이므로 ×1,000,000 |
| A_END_TIME | NUMBER | A_END_MICROSEC | bigint NOT NULL | 초→µs (×1000000) | 확정 | 소스 A_END_TIME 은 초. 타겟 µs 이므로 ×1,000,000 |
| SAMPLING_RATE | NUMBER(6,0) | SAMPLING_RATE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DET_SONAR | VARCHAR2(10 BYTE) | DET_SONAR | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| LATITUDE_DEGREE | NUMBER | LATITUDE_DEGREE | int | Direct | 확정 | 완전하게 대응 |
| LATITUDE_MINUTE | NUMBER | LATITUDE_MINUTE | int | Direct | 확정 | 완전하게 대응 |
| LATITUDE_SECOND | NUMBER | LATITUDE_SECOND | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_DEGREE | NUMBER | LONGITUDE_DEGREE | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_MINUTE | NUMBER | LONGITUDE_MINUTE | int | Direct | 확정 | 완전하게 대응 |
| LONGITUDE_SECOND | NUMBER | LONGITUDE_SECOND | int | Direct | 확정 | 완전하게 대응 |
| DET_DATE | DATE | DET_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | datetime 변환 |
| DET_PLATFORM | VARCHAR2(30 BYTE) | DET_PLATFORM | varchar(30) | Direct | 확정 | 완전하게 대응 |
| DISTANCE_KM | NUMBER(12,3) | DISTANCE_KM | real | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(240 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | COMMENTS -> COMMENT; 말머리 [NAIMS-I] 삽입 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |
| (신규) | - | AUDIO_FILE | varbinary(max) | wav 원본 적재 | 확정 | 스니펫 실제 wav 데이터 |
| (신규) | - | BIN_NUM | int NOT NULL | 그램 데이터에서 계산 | 확정 | 그램 데이터의 bin 수 계산 |
| (신규) | - | SPECTROGRAM_DATA | varbinary(max) NOT NULL | 빈 byte | 확정 | 일단 빈 바이트 적재 |
| (신규) | - | SPECTROGRAM_START_FREQ | real NOT NULL | 그램 내부값/-1 | 확정 | 그램 내부에 있으면 삽입, 없으면 -1 |
| (신규) | - | SPECTROGRAM_END_FREQ | real NOT NULL | 그램 내부값/-1 | 확정 | 그램 내부에 있으면 삽입, 없으면 -1 |
| (신규) | - | SPECTROGRAM_FRAME_PER_SEC | real NOT NULL | 직접 계산 | 확정 | 초당 프레임 직접 계산 |
| (신규) | - | ROWGUID | uniqueidentifier NOT NULL | 신규 | 확정 | 타겟 기본값 newid() 로 채움 |

## 5.4. SIG_SNIPPET → STD_NB_SIG_SNIPPET_LINK

5개 컬럼 · 신규제안 2 · 확정 2 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| SIG_SNP_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| NB_SNIPPET_ID | NUMBER(*,0) | REF_STD_NB_SNIPPET_ID | int NOT NULL | FK→REF_*_ID | 신규제안 | - |
| SIGNATURE_ID | NUMBER(*,0) | REF_STD_SIGNATURES_ID | int NOT NULL | FK→REF_*_ID | 신규제안 | - |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 동일 | 확정 | 완전하게 대응 |

## 5.5. FRAME_SNIPPET → STD_TR_FRAME_SNIPPET_LINK

5개 컬럼 · 신규제안 2 · 확정 2 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| FRM_SNP_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| FRAME_ID | NUMBER(*,0) | REF_STD_TR_FRAME_ID | int NOT NULL | FK수동 | 신규제안 | 순간소음 프레임 참조 — TR_FRAME.RECORD_ID 로 치환 |
| TR_SNIPPET_ID | NUMBER(*,0) | REF_STD_TR_SNIPPET_ID | int NOT NULL | FK→REF_*_ID | 신규제안 | - |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | - |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 동일 | 확정 | 완전하게 대응 |

## 6.1. IMAGE_DATA → STD_REFERENCE_DATA

11개 컬럼 · 확정 10 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| IMAGE_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| VESSEL_ID | NUMBER(*,0) | REF_TARGET_ID | int NOT NULL | VESSEL→RECORD_ID | 확정 | 소스 VESSEL_ID 를 VESSEL_DATA 신규 RECORD_ID 로 치환 |
| IMG_FILENAME | VARCHAR2(80 BYTE) | FILE_NAME | varchar(150) NOT NULL | IMG_FILENAME | 확정 | 소스 IMG_FILENAME |
| SOURCE | VARCHAR2(12 BYTE) | SOURCE | varchar(20) NOT NULL | 동일 | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | - |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | 별칭 | 확정 | 말머리 [NAIMS-I] 삽입 |
| DELETED | NUMBER(1,0) | DELETED | smallint | 동일 | 확정 | 완전하게 대응 |
| (신규) | - | REF_TARGET_TYPE | int NOT NULL | 고정값 5 (vessel) | 확정 | 함정(vessel) = 5 (신규 코드). 기존: 협대역=1/AI=2/능동=4 |
| (신규) | - | FILE_TYPE | int NOT NULL | png | 확정 | 이미지(png) 코드 (레거시 FileType=1=이미지) |
| (신규) | - | ROWGUID | uniqueidentifier NOT NULL | 신규 | 확정 | 타겟 기본값 newid() 로 채움 |
| (신규) | - | FILE_DATA | varbinary(max) NOT NULL | IMG 파일 원본 | 확정 | IMG_FILENAME 이 가리키는 실제 이미지 바이트 |

## 7.1. WEAPON → STD_WEAPON

11개 컬럼 · 확정 10 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| WEAPON_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| WEAPON_MODEL | VARCHAR2(30 BYTE) | WEAPON_MODEL | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| WEAPON_TYPE | VARCHAR2(20 BYTE) | WEAPON_TYPE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| ATTACK_RANGE_KM | NUMBER(12,3) | ATTACK_RANGE_KM | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| GUIDE_METHOD | VARCHAR2(20 BYTE) | GUIDE_METHOD | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MANUFACTURER | VARCHAR2(30 BYTE) | MANUFACTURER | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MANUFACT_DATE | VARCHAR2(20 BYTE) | MANUFACT_DATE | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| VERSION | NUMBER | VERSION | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | 말머리 [NAIMS-I] 삽입 |
| DELETED | NUMBER(1,0) | DELETED | smallint NOT NULL | 형 변환 | 확정 | 0=정상, 1=임시삭제 — 레거시 값 보존 (형 변환) |

## 7.2. WEAPON_CARRIER → STD_WEAPON_CLASSES_LINK

5개 컬럼 · 확정 4 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| WCARRIER_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| WEAPON_ID | NUMBER(*,0) | REF_STD_WEAPON_ID | int NOT NULL | FKRename | 확정 | - |
| CLASS_ID | VARCHAR2(30 BYTE) | REF_STD_CLASSES_ID | int NOT NULL | FKRename | 확정 | Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| QUANTITY | NUMBER(5,0) | QUANTITY | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| DATE_SET_CHNG | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | Rename | 확정 | - |

## 8.1. DIST_LIST → STD_DIST_LIST

6개 컬럼 · 확정 4 · 자동생성 1 · 신규제안 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| DIST_LIST_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| TABLE_NAME | VARCHAR2(15 BYTE) | TABLE_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| ID | NUMBER(*,0) | ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| PARENT_ID | NUMBER(*,0) | PARENT_ID | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| IAUDIOINCLUDE | NUMBER(*,0) DEFAULT 1 | IAUDIOINCLUDE | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| GROUP_ID | NUMBER(*,0) | REF_STD_DB_DIST_GROUP_ID | int NOT NULL | FK수동 | 신규제안 | 배포그룹 참조 — DB_DIST_GROUP.RECORD_ID 로 치환 |

## 8.2. DIST_TO → STD_DIST_TO

5개 컬럼 · 확정 2 · 자동생성 1 · 미결 1 · 신규제안 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| LOG_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| DESTINATION | VARCHAR2(30 BYTE) | DESTINATION | varchar(31) NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMPLT_DATE | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | COMPLT_DATE → CHNG_DATETIME |
| COMPLT_OPTR | VARCHAR2(20 BYTE) | - | - | TBD | 미결 | STD_DIST_TO 에 대응 컬럼 없음 — 미매핑 기존 필드 |
| (신규) | - | REF_STD_DB_DIST_LOG_ID | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |

## 8.3. DB_DIST_GROUP → STD_DB_DIST_GROUP

25개 컬럼 · 확정 20 · 미결 4 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| GROUP_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| GROUP_NAME | VARCHAR2(30 BYTE) | GROUP_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| IF_SYSTEM | VARCHAR2(15 BYTE) | IF_SYSTEM | varchar(100) NOT NULL | Direct | 확정 | 완전하게 대응 |
| IF_MEDIA | VARCHAR2(10 BYTE) | IF_MEDIA | varchar(10) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CLASS_INFO | CHAR(1 BYTE) | CLASS_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| NB_INFO | CHAR(1 BYTE) | NB_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| AI_INFO | CHAR(1 BYTE) | AI_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| TR_INFO | CHAR(1 BYTE) | TR_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| WEAPON_INFO | CHAR(1 BYTE) | WEAPON_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| NB_GRAM_INFO | CHAR(1 BYTE) | NB_GRAM_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| NB_AUDIO_INFO | CHAR(1 BYTE) | NB_AUDIO_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| IMAGE_FILE | CHAR(1 BYTE) | IMAGE_FILE | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| AI_GRAM_INFO | CHAR(1 BYTE) | AI_GRAM_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| AI_AUDIO_INFO | CHAR(1 BYTE) | AI_AUDIO_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| TR_AUDIO_INFO | CHAR(1 BYTE) | TR_AUDIO_INFO | int NOT NULL | Direct | 확정 | MsSQL: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요; Oracle: 타입 계열 상이 (STR→INT) 변환 규칙 확정 필요 |
| FREQ_MIN | NUMBER(12,3) | FREQ_MIN | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FREQ_MAX | NUMBER(12,3) | FREQ_MAX | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| FINAL_DIST_DATE | DATE | FINAL_DIST_DATETIME | datetime2(7) | FINAL_DIST_DATETIME 필드에 매핑 | 확정 | 사용자 지정 매핑 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | COMMENTS -> COMMENT; 말머리 [NAIMS-I] 삽입 |
| (신규) | - | ACTV_INFO | int NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | DIST_PATH | varchar(50) NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | DIST_SOURCE | varchar(50) NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | PROTECTION_PERIOD_DATETIME | datetime2(7) NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | CHNG_DATETIME | datetime2(7) NOT NULL | 신규 | 확정 | 타겟 기본값 sysdatetime() 로 채움 |
| (신규) | - | DELETED | smallint NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |

## 8.4. DB_DIST_LOG → STD_DB_DIST_LOG

25개 컬럼 · 확정 8 · 신규제안 8 · 스키마변경 7 · 자동생성 1 · 미결 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| LOG_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| GROUP_NAME | VARCHAR2(30 BYTE) | GROUP_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| IF_SYSTEM | VARCHAR2(15 BYTE) | IF_SYSTEM | varchar(15) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DB_FILENAME | VARCHAR2(80 BYTE) | DB_FILENAME | varchar(260) | 동일 | 확정 | 배포 DB ZIP 파일명(CD_*.zip). 배포완료 응답 매칭 키. 직접 대응 (구 워크북의 파일 내부 파싱 라우팅은 오류) |
| DB_FILESIZE_MB | NUMBER(7,2) | DB_FILESIZE_MB | real NOT NULL | Direct | 확정 | 완전하게 대응 |
| DIST_DATE | DATE | DIST_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | DIST_DATE → DIST_DATETIME |
| DIST_OPERATOR | VARCHAR2(8 BYTE) | DIST_OPERATOR | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| DOC_NO | VARCHAR2(50 BYTE) | DOC_NO | varchar(150) NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | COMMENTS -> COMMENT; 말머리 [NAIMS-I] 삽입 |
| DIST_PATH | VARCHAR2(10 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_CLASSES | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_VESSEL | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_SONAR | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_TR_EVENT | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_WEAPON | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_IMAGE | NUMBER(5,0) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| LOG_FILENAME | VARCHAR2(80 BYTE) | - | - | TBD | 미결 | 배포이력 HWP 보고서 파일명(DB_FILENAME 과 별개). STD_DB_DIST_LOG 에 대응 컬럼 없음 — 미매핑 |
| (신규) | - | META_TRANSFER_TYPE | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | META_DATA_TYPE | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | OUTPUT_SYSTEM | varchar(200) | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | TARGET_COUNT | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | PROTECTION_PERIOD | datetime2(7) | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | LINKED_MEDIA | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | MISSING_REASON | varchar(1000) | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | REF_STD_DIST_TO_ID | int | 신규 | 신규제안 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |

## 9.1. CD_CHANGE_LOG → STD_CD_CHANGE_LOG

10개 컬럼 · 확정 7 · 미결 2 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| CHNG_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| OPERATOR | VARCHAR2(20 BYTE) | OPERATOR | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CHNG_DATE | DATE | CHNG_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | CHNG_DATE → CHNG_DATETIME |
| TABLE_NAME | VARCHAR2(30 BYTE) | TABLE_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| RECORD_ID | VARCHAR2(20 BYTE) | TABLE_ID | int NOT NULL | 수동 | 확정 | 소스 RECORD_ID(변경된 레코드 id) → TABLE_ID. 타겟 RECORD_ID 는 신규 IDENTITY PK 라 충돌 방지 (varchar→int 변환) |
| TYPE_CHNG | VARCHAR2(20 BYTE) | TYPE_CHNG | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| MENU_NAME | VARCHAR2(30 BYTE) | MENU_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| COMMENTS | VARCHAR2(80 BYTE) | COMMENT | varchar(1000) | Rename | 확정 | COMMENTS -> COMMENT; 말머리 [NAIMS-I] 삽입 |
| DATASET_ID | NUMBER | - | - | TBD | 미결 | 파싱만 우선 확정, 저장/미반영 정책 보류 |
| (신규) | - | OPERATOR_ID | varchar(30) NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |

## 9.2. CD_BACKUP_LOG → STD_CD_BACKUP_LOG

37개 컬럼 · 스키마변경 22 · 확정 11 · 미결 3 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| BACKUP_ID | NUMBER(*,0) | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| NO_CLASSES | NUMBER | NO_CLASSES | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| NO_VESSEL | NUMBER | NO_VESSEL | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| NO_SONAR | NUMBER | NO_SONAR | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| NO_WEAPON | NUMBER | NO_WEAPON | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| NO_TR | NUMBER | NO_TR | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| NO_FLAG_IMAGE | NUMBER | NO_FLAG_IMAGE | int NOT NULL | Direct | 확정 | 타겟 기본값 (0) |
| BACKUP_TYPE | VARCHAR2(5 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| BACKUP_DATE | DATE | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| BACK_BY | VARCHAR2(8 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| BACKUP_MEDIA | VARCHAR2(5 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| BACKUP_MEDIA_ID | VARCHAR2(30 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_NB_GRAM | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_NB_GRAM | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_AI_GRAM | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_AI_GRAM | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_TR_GRAM | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_TR_GRAM | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_NB_AUDIO | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_NB_AUDIO | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_AI_AUDIO | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_AI_AUDIO | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_TR_AUDIO | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_TR_AUDIO | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_VESSEL_IMAGE | NUMBER | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_VESSEL_IMAGE | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| SIZE_FLAG_IMAGE | NUMBER(12,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| NO_RELEASE_LOG | NUMBER | - | - | TBD | 미결 | 파싱만 우선 확정, 저장/미반영 정책 보류 |
| SIZE_RELEASE_LOG | NUMBER(12,3) | - | - | TBD | 미결 | 파싱만 우선 확정, 저장/미반영 정책 보류 |
| TOTAL_SIZE | NUMBER(15,3) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| COMMENTS | VARCHAR2(80 BYTE) | (대응 없음) | - | TBD | 스키마변경 | 대응 컬럼 지정/생성 필요 |
| (신규) | - | REF_DB_BACKUP_LOG_ID | int NOT NULL | TBD | 미결 | 소스 대응 없음 — 기본값/NULL 또는 산출값 |
| (신규) | - | NO_ACTV_SIG | int NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | NO_NB_SNP | int NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | NO_AI_SNP | int NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | NO_TR_SNP | int NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |
| (신규) | - | NO_REFERENCE | int NOT NULL | 신규 | 확정 | 타겟 기본값 (0) 로 채움 |

## 9.3. CCF2CD_LOG → STD_CCF2CD_LOG

9개 컬럼 · 확정 8 · 자동생성 1

| ① NAIMS-I(Oracle) 컬럼 | 타입 | ② NAIMS-II 대상 컬럼 | 타입 | 변환 규칙 | 판정 | 비고 |
|---|---|---|---|---|---|---|
| CCFCD_ID | NUMBER | RECORD_ID | int IDENTITY NOT NULL | 제외 (신규 RECORD_ID 자동생성) | 자동생성 | 레거시 PK 값은 승계하지 않고 타겟 IDENTITY 자동 채번 |
| DATASET_ID | NUMBER | REF_EXT_RAW_SIG_ID | int NOT NULL | 수동 | 확정 | DATASET_ID → REF_EXT_RAW_SIG_ID (구 워크북의 APPL_DATETIME 매핑은 오류) |
| SHIP_SUB | CHAR(12 BYTE) | SHIP_SUB | varchar(20) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CTRY_NAME | CHAR(35 BYTE) | CTRY_NAME | varchar(35) NOT NULL | Direct | 확정 | 완전하게 대응 |
| CTRY_CODE | CHAR(3 BYTE) | CTRY_CODE | varchar(2) NOT NULL | Direct | 확정 | Oracle: 길이 축소 3→2 — 잘림 주의 |
| TARGET_TYPE | CHAR(6 BYTE) | TARGET_TYPE | varchar(6) NOT NULL | Direct | 확정 | 완전하게 대응 |
| TARGET_NAME | CHAR(30 BYTE) | TARGET_NAME | varchar(30) NOT NULL | Direct | 확정 | 완전하게 대응 |
| NUM_APPLIED | NUMBER(3,0) | NUM_APPLIED | int NOT NULL | Direct | 확정 | 완전하게 대응 |
| APPL_DATE | TIMESTAMP (6) | APPL_DATETIME | datetime2(7) NOT NULL | 별칭 | 확정 | APPL_DATE → APPL_DATETIME |

