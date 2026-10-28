# -*- coding: utf-8 -*-
"""
NAIMS(구) → NAIMS2(신) 전 컬럼 1:1 매핑표 생성기.

입력 (같은 폴더):
  oracle_naims_schema.txt   Oracle 소스 (pdf_pages 25p DDL 스캔 전사본)
  mssql_naims_schema.txt    MsSQL 소스 (192.168.111.39/NAIMS 실측)
  naims2_schema.txt         타겟     (192.168.200.205/NAIMS2 실측)

출력:
  mapping.tsv               전 컬럼 매핑 (문서 생성기의 입력)
  mapping_stats.txt         집계

매칭 규칙은 RawCollectMigrator/Features/DbToDb/ColumnMapping.cs 의 ColumnMapper 와
동일하게 유지한다. 여기서 규칙을 바꾸면 C# 쪽도 함께 고쳐야 한다.
"""
import re, io, os, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))

# ── 이관 계획 (MigrationPlan.cs 와 동기화) ─────────────────────────────
# (소스테이블, 타겟테이블, 소스PK, 단계, 구분)
PLAN = [
    ("COUNTRY_CODES",           "STD_COUNTRY_CODES",           "COUNTRY_ID",        1, "기준정보"),
    ("CLASSES",                 "STD_CLASSES",                 "CLASS_ID",          2, "기준정보"),
    ("VESSEL_DATA",             "STD_VESSEL_DATA",             "VESSEL_ID",         3, "기준정보"),
    ("CHARACTERISTICS",         "STD_CHARACTERISTICS",         "CHARACTERISTICS_ID",4, "기준정보"),
    ("SIGNATURES",              "STD_SIGNATURES",              "SIGNATURES_ID",     4, "수동신호"),
    ("HARMONICS",               "STD_HARMONICS",               "HARMONICS_ID",      5, "수동신호"),
    ("ACTIVE_SONAR",            "STD_ACTV_SONAR",              "SONAR_ID",          2, "능동소나"),
    ("PULSES",                  "STD_PULSES",                  "PULSE_ID",          3, "능동소나"),
    ("PULSE_SEGMENTS",          "STD_PULSE_SEGMENTS",          "SEGMENT_ID",        1, "능동소나"),
    ("PULSE_FREQUENCY_ISUS90",  "STD_PULSE_FREQUENCY_ISUS90",  "PULSE_F_ID",        1, "능동소나"),
    ("PULSE_SEG_LENGTH_ISUS90", "STD_PULSE_SEG_LENGTH_ISUS90", "SEG_ID",            1, "능동소나"),
    ("PULSE_SEG_SWING_ISUS90",  "STD_PULSE_SEG_SWING_ISUS90",  "SWING_ID",          1, "능동소나"),
    ("TRANSIENT",               "STD_TRANSIENT",               "TR_ID",             4, "순간소음"),
    ("TR_FRAME",                "STD_TR_FRAME",                "FRAME_ID",          5, "순간소음"),
    ("NB_SNIPPET",              "STD_NB_SNIPPET",              "SNIPPET_ID",        1, "스니펫"),
    ("AI_SNIPPET",              "STD_AI_SNIPPET",              "SNIPPET_ID",        4, "스니펫"),
    ("TR_SNIPPET",              "STD_TR_SNIPPET",              "SNIPPET_ID",        1, "스니펫"),
    ("SIG_SNIPPET",             "STD_NB_SIG_SNIPPET_LINK",     "SIG_SNIPPET_ID",    5, "스니펫"),
    ("FRAME_SNIPPET",           "STD_TR_FRAME_SNIPPET_LINK",   "FRAME_SNIPPET_ID",  6, "스니펫"),
    ("IMAGE_DATA",              "STD_REFERENCE_DATA",          "IMG_ID",            4, "영상자료"),
    ("WEAPON",                  "STD_WEAPON",                  "WEAPON_ID",         1, "무장"),
    ("WEAPON_CARRIER",          "STD_WEAPON_CLASSES_LINK",     "CARRIER_ID",        3, "무장"),
    ("DIST_LIST",               "STD_DIST_LIST",               "DIST_ID",           2, "배포관리"),
    ("DIST_TO",                 "STD_DIST_TO",                 "DIST_TO_ID",        3, "배포관리"),
    ("DB_DIST_GROUP",           "STD_DB_DIST_GROUP",           "GROUP_ID",          1, "배포관리"),
    ("DB_DIST_LOG",             "STD_DB_DIST_LOG",             "LOG_ID",            3, "배포관리"),
    ("CD_CHANGE_LOG",           "STD_CD_CHANGE_LOG",           "LOG_ID",            1, "변경이력"),
    ("CD_BACKUP_LOG",           "STD_CD_BACKUP_LOG",           "LOG_ID",            1, "변경이력"),
    ("CCF2CD_LOG",              "STD_CCF2CD_LOG",              "CCFCD_ID",          1, "변경이력"),
]

# 이관하지 않지만 원음 파일 이관(트랙②) 시 값을 참조하는 테이블.
# 2026-07-22 사용자 확정: "원음 파일을 마이그레이션할 때 RAWDATAINFO,
# RAWDATAIMPORTINFO 값을 봐야 한다."
REFERENCE_ONLY = {
    "RAWDATAINFO":       "원음자료 정보. 파일 이관 시 원음 메타 대조용으로 조회",
    "RAWDATAIMPORTINFO": "원음반입 이력. 파일 이관 시 반입 이력 대조용으로 조회",
}

# 이관 제외 (사용자 확정 2026-07-22)
EXCLUDED = {
    "RAWDATAINFO":       "미이관 — 단, 원음 파일 이관 시 참조",
    "RAWSIGNALINFO":     "원음신호 정보 — 이관 대상 아님(확정)",
    "RAWDATAIMPORTINFO": "미이관 — 단, 원음 파일 이관 시 참조",
    "ONA_209":           "용도 미상 — 이관 대상 아님(확정)",
    "ONA_214":           "용도 미상 — 이관 대상 아님(확정)",
    "PASSWORD":          "레거시 계정 — 신규 체계 인증과 무관",
    "USER_JUMIN":        "개인식별정보 — 이관 제외",
    "OPERATOR":          "레거시 운용자 계정 — 신규 체계 인증과 무관",
    "DATASET":           "0건 (빈 테이블)",
    "FIELD_CONV":        "0건 (빈 테이블)",
    "DS_BACKUP_LOG":     "0건 (빈 테이블)",
    "DS_DIST_LOG":       "0건 (빈 테이블)",
    "sysdiagrams":       "SSMS 다이어그램 메타 — DB 산출물 아님",
    "HELP":              "Oracle 전용 도움말 테이블",
    "REDO_DB":           "Oracle 시스템 테이블",
    "REDO_LOG":          "Oracle 시스템 테이블",
}

# 신규 체계에서 폐기된 소스 컬럼
DROPPED = {"SELF_DB": "신규 체계에서 사용하지 않음"}

# 고정 별칭 (소스 → 타겟)
# A_START_TIME 은 MILLISEC 이 아니라 MICROSEC 이다 — 2026-07-22 타겟 실측으로 확인.
# (시간 단위 규약이 마이크로초라는 기존 분석과 일치)
ALIASES = {
    "DATE_SET_CHNG": "CHNG_DATETIME",
    "COMMENTS": "COMMENT", "COMMENTS1": "COMMENT1", "COMMENTS2": "COMMENT2",
    "DET_DATE": "DET_DATETIME",
    "NO_FRAME": "FRAME_NUM",
    "A_START_TIME": "A_START_MICROSEC", "A_END_TIME": "A_END_MICROSEC",
    "NO_SEGMENT": "SEGMENT_NUM",
}

# 규칙으로 유도되지 않는 테이블별 수동 대응.
# 값이 None 이면 "대응 없음(폐기)" 을 명시적으로 확정한 것.
MANUAL = {
    ("CLASSES", "ACTIVE_SONAR1"):      ("ACTV_SONAR1", "수동", ""),
    ("CLASSES", "ACTIVE_SONAR2"):      ("ACTV_SONAR2", "수동", ""),
    ("CD_CHANGE_LOG", "RECORD_ID"):    ("TABLE_ID", "수동",
                                        "타겟 RECORD_ID 는 신규 PK 라 이름 충돌 — 원래 값은 TABLE_ID 로"),
    ("CD_CHANGE_LOG", "DATASET_ID"):   (None, "미결",
                                        "타겟 OPERATOR_ID 와의 대응 여부 불명 — 담당자 확인 필요"),
    ("CCF2CD_LOG", "DATASET_ID"):      ("REF_EXT_RAW_SIG_ID", "미결",
                                        "타겟이 EXT_RAW_SIG 참조로 바뀜 — 값 변환 규칙 확인 필요"),
    ("AI_SNIPPET", "DISTANCE_KM"):     ("DET_DISTANCE_NM", "단위변환",
                                        "km → 해리(NM) 단위 변환 필요 (÷1.852)"),
    ("IMAGE_DATA", "VESSEL_ID"):       ("REF_TARGET_ID", "미결",
                                        "타겟이 다형 참조(REF_TARGET_TYPE + REF_TARGET_ID) — TYPE 값 확정 필요"),
    # FK 명명 규칙(어간)으로 자동 매칭되지 않는 외래키 — 부모가 확실해 수동 지정.
    ("DIST_LIST", "GROUP_ID"):         ("REF_STD_DB_DIST_GROUP_ID", "FK수동",
                                        "배포그룹 참조 — DB_DIST_GROUP.RECORD_ID 로 치환"),
    ("FRAME_SNIPPET", "FRAME_ID"):     ("REF_STD_TR_FRAME_ID", "FK수동",
                                        "순간소음 프레임 참조 — TR_FRAME.RECORD_ID 로 치환"),
    ("FRAME_SNIPPET", "SNIPPET_ID"):   ("REF_STD_TR_SNIPPET_ID", "FK수동",
                                        "순간소음 스니펫 참조 — TR_SNIPPET.RECORD_ID 로 치환"),
    ("SIG_SNIPPET", "SIGNATURES_ID"):  ("REF_STD_SIGNATURES_ID", "FK수동",
                                        "시그니처 참조"),
    ("SIG_SNIPPET", "SNIPPET_ID"):     ("REF_STD_NB_SNIPPET_ID", "FK수동",
                                        "협대역 스니펫 참조"),
}

# 타겟에만 있는 컬럼의 채움 규칙
TARGET_ONLY = {
    "ROWGUID":       "NEWID() 기본값 (merge replication용)",
    "IS_LEGACY":     "1 로 채움 (레거시 이관 데이터 표시)",
    "DELETED":       "소스 동명 컬럼 없으면 0",
    "CHNG_DATETIME": "소스 DATE_SET_CHNG, 없으면 이관 시각",
    "RECORD_ID":     "IDENTITY — 타겟이 자동 채번 (INSERT 목록에서 제외)",
    "IS_TRAINING":   "0 으로 채움 (신규 체계 생성 필드)",
}

# 스크립트에 DEFAULT 선언이 없지만 사용자가 지정한 고정 기본값 (테이블.컬럼 → 값).
# 2026-07-23 사용자 확정.
TARGET_FIXED_DEFAULT = {
    ("STD_VESSEL_DATA", "HAS_CLUTTER"): "0",   # 항목4(1차)
}

# ── 2026-07-23 2차 피드백: 타겟 컬럼별 산출/변환 규칙 ──────────────────
# (타겟테이블, 타겟컬럼) → dict(rule, verdict, note)
# 값이 있으면 해당 타겟 컬럼 행의 규칙/판정/비고를 이 값으로 덮어쓴다.
#   verdict: "확정"(규칙 확정) / "미결"(추가 확인 필요) / "제외"(이관 안 함)
COLUMN_RULES = {
    # 항목2) STD_NB_SNIPPET
    ("STD_NB_SNIPPET", "CLASSIFI_NB_AUDIO_SNIPPET_FILE"):
        dict(rule="wav 원본 적재", verdict="확정",
             note="스니펫 실제 wav 데이터를 그대로 적재"),
    ("STD_NB_SNIPPET", "CLASSIFI_NB_GRAM_SNIPPET_FILE"):
        dict(rule="grm→R8 변환 적재", verdict="확정",
             note="레거시 .grm 을 신규 R8 66B 포맷으로 변환해 적재 (GramReader.cs)"),
    ("STD_NB_SNIPPET", "STD_NB_AUDIO_SNIPPET_FILE_NAME"):
        dict(rule="AUDIO_FILENAME", verdict="확정", note="소스 AUDIO_FILENAME(파일명)"),
    ("STD_NB_SNIPPET", "STD_NB_GRAM_SNIPPET_FILE_NAME"):
        dict(rule="GRAM_FILENAME", verdict="확정", note="소스 GRAM_FILENAME(파일명)"),
    ("STD_NB_SNIPPET", "BIN_NUM"):
        dict(rule="frame block bin 수", verdict="확정",
             note="실제 frame block 의 bin 수 (기본값 1601)"),
    ("STD_NB_SNIPPET", "SNIPPET_START_DATETIME"):
        dict(rule="wav 헤더 계산", verdict="확정", note="wav 헤더로 시작시각 계산"),
    ("STD_NB_SNIPPET", "SNIPPET_END_DATETIME"):
        dict(rule="wav 헤더 계산", verdict="확정", note="wav 헤더로 끝시각 계산"),
    ("STD_NB_SNIPPET", "GRAM_UPDATE_TIME"):
        dict(rule="마이그레이션 시각", verdict="확정", note="이관 수행 시각"),
    ("STD_NB_SNIPPET", "INTEGRAL_TIME"):
        dict(rule="고정값 -1", verdict="확정", note="-1"),
    ("STD_NB_SNIPPET", "GRAM_TYPE"):
        dict(rule=".grm 헤더 cGram→코드", verdict="확정",
             note="레거시 헤더 @0 cGram[3] 태그로 결정: L→101(LOFAR), D→201(DEMON), "
                  "V1~V4→301~304(VERNIER). (LegacyGrmReader.cs / Constants.cs 확인)"),

    # 항목3) STD_AI_SNIPPET
    ("STD_AI_SNIPPET", "GRAM_FILE"):
        dict(rule="grm→R8 변환 적재", verdict="확정",
             note="레거시 .grm 을 신규 R8 66B 포맷으로 변환해 적재"),
    ("STD_AI_SNIPPET", "AUDIO_FILE"):
        dict(rule="wav 원본 적재", verdict="확정", note="스니펫 실제 wav 데이터"),
    ("STD_AI_SNIPPET", "BIN_NUM"):
        dict(rule="그램 데이터에서 계산", verdict="확정", note="변환된 그램 데이터의 bin 수 계산"),

    # 항목3) STD_TR_SNIPPET (그램 없음 — SPECTROGRAM 사용)
    ("STD_TR_SNIPPET", "AUDIO_FILE"):
        dict(rule="wav 원본 적재", verdict="확정", note="스니펫 실제 wav 데이터"),
    ("STD_TR_SNIPPET", "BIN_NUM"):
        dict(rule="그램 데이터에서 계산", verdict="확정", note="그램 데이터의 bin 수 계산"),
    ("STD_TR_SNIPPET", "SPECTROGRAM_DATA"):
        dict(rule="빈 byte", verdict="확정", note="일단 빈 바이트 적재"),
    ("STD_TR_SNIPPET", "SPECTROGRAM_START_FREQ"):
        dict(rule="그램 내부값/-1", verdict="확정",
             note="그램 내부에 있으면 삽입, 없으면 -1"),
    ("STD_TR_SNIPPET", "SPECTROGRAM_END_FREQ"):
        dict(rule="그램 내부값/-1", verdict="확정",
             note="그램 내부에 있으면 삽입, 없으면 -1"),
    ("STD_TR_SNIPPET", "SPECTROGRAM_FRAME_PER_SEC"):
        dict(rule="직접 계산", verdict="확정", note="초당 프레임 직접 계산"),

    # 항목4) STD_NB_SIG_SNIPPET_LINK OLD_* — 삭제 예정
    ("STD_NB_SIG_SNIPPET_LINK", "OLD_NB_SNIPPET_ID"):
        dict(rule="삭제 예정", verdict="제외", note="타겟에서 삭제 예정 — 매핑 안 함"),
    ("STD_NB_SIG_SNIPPET_LINK", "OLD_SIGNATURE_ID"):
        dict(rule="삭제 예정", verdict="제외", note="타겟에서 삭제 예정 — 매핑 안 함"),

    # 항목5) IMAGE_DATA → STD_REFERENCE_DATA
    ("STD_REFERENCE_DATA", "FILE_NAME"):
        dict(rule="IMG_FILENAME", verdict="확정", note="소스 IMG_FILENAME"),
    ("STD_REFERENCE_DATA", "FILE_DATA"):
        dict(rule="IMG 파일 원본", verdict="확정", note="IMG_FILENAME 이 가리키는 실제 이미지 바이트"),
    ("STD_REFERENCE_DATA", "FILE_TYPE"):
        dict(rule="png", verdict="확정", note="이미지(png) 코드 (레거시 FileType=1=이미지)"),
    ("STD_REFERENCE_DATA", "REF_TARGET_TYPE"):
        dict(rule="고정값 5 (vessel)", verdict="확정",
             note="함정(vessel) = 5 (신규 코드). 기존: 협대역=1/AI=2/능동=4"),
    ("STD_REFERENCE_DATA", "REF_TARGET_ID"):
        dict(rule="VESSEL→RECORD_ID", verdict="확정",
             note="소스 VESSEL_ID 를 VESSEL_DATA 신규 RECORD_ID 로 치환"),
    # SOURCE 는 소스 IMAGE_DATA.SOURCE(varchar12) 를 동일명칭으로 직접 매핑 (규칙 덮어쓰지 않음)
}

# 항목1) STD_CLASSES 의 소스 대응 없는 신규 컬럼은 임시 기본값 -1.
#   (아래 집합에 없는 target-only 컬럼 전체에 적용. 아래는 -1 을 넣지 않는 예외)
CLASSES_FILL_EXCEPT = {
    "RECORD_ID", "CHNG_DATETIME", "DELETED", "ROWGUID", "IS_LEGACY", "COMMENT", "REF_COMMENT",
}

# ── 구조화 상수 (레거시 → NAIMS2 코드 매핑) ──────────────────────────
# 레거시 .grm 헤더 @0 cGram[3] 태그의 첫 글자 → NAIMS2 GRAM_TYPE 코드.
# (LegacyGrmReader.cs 헤더 스펙 + 260128_svn Constants.cs 코드 상수)
GRAM_TYPE_MAP = {
    "L": 101,   # LOFAR_BASIC
    "D": 201,   # DEMON_01_BASIC
    "V1": 301,  # VERNIER_01_BASIC
    "V2": 302,
    "V3": 303,
    "V4": 304,
}

# STD_REFERENCE_DATA.REF_TARGET_TYPE 코드 (260128_svn DistRepository.cs 확인).
REF_TARGET_TYPE_CODES = {
    "NARROWBAND": 1,
    "AI": 2,
    "ACTIVE": 4,
    "VESSEL": 5,   # 함정 — 2026-07-23 신규 확정
}

# 암호화 컬럼
ENC_MSSQL = {
    "CLASSES.CLASS_NAME", "CLASSES.CLASS_NAME_K", "CLASSES.CLASS_NAME_ISUS90",
    "VESSEL_DATA.VESSEL_NAME", "VESSEL_DATA.VESSEL_NAME_K", "VESSEL_DATA.VESSEL_NAME_ISUS90",
    "ACTIVE_SONAR.SONAR_NAME", "ACTIVE_SONAR.SONAR_NAME_ISUS90",
    "TRANSIENT.TR_NAME",
}
ENC_ORACLE = {
    "CLASSES.CLASS_NAME", "CLASSES.CLASS_NAME_K", "CLASSES.CLASS_NAME_ISUS90",
    "VESSEL_DATA.VESSEL_NAME", "VESSEL_DATA.VESSEL_NAME_K", "VESSEL_DATA.VESSEL_NAME_ISUS90",
}

# ══════════════════════════════════════════════════════════════════════
#  단일 규칙 원본(SSOT): mapping_rules.json
#  위 Python 리터럴은 최초 부트스트랩 시드일 뿐이고, 아래에서 JSON 이 있으면
#  그 값으로 전부 덮어쓴다. 즉 런타임 기준은 JSON 하나다.
#  규칙 수정은 mapping_rules.json 에서 하고 문서 파이프라인을 다시 돌리면 된다.
#  C# MigrationRules.cs 도 같은 JSON 을 읽는다.
# ══════════════════════════════════════════════════════════════════════
RULES_JSON = os.path.abspath(os.path.join(
    HERE, "..", "..", "RawCollectMigrator", "Features", "DbToDb", "mapping_rules.json"))


def _load_rules_from_json(path=RULES_JSON):
    global PLAN, EXCLUDED, REFERENCE_ONLY, DROPPED, ALIASES, TARGET_ONLY
    global MANUAL, COLUMN_RULES, TARGET_FIXED_DEFAULT, CLASSES_FILL_EXCEPT
    global ENC_MSSQL, ENC_ORACLE, GRAM_TYPE_MAP, REF_TARGET_TYPE_CODES
    import json
    if not os.path.exists(path):
        return False
    with io.open(path, encoding="utf-8") as f:
        d = json.load(f)

    PLAN = [(p["source"], p["target"], p["pk"], p["level"], p["category"])
            for p in d["plan"]]
    EXCLUDED = dict(d["excluded"])
    REFERENCE_ONLY = dict(d["referenceOnly"])
    DROPPED = dict(d["dropped"])
    ALIASES = dict(d["aliases"])
    TARGET_ONLY = dict(d["targetOnlyNotes"])
    MANUAL = {(m["source"], m["column"]): (m["target"], m["rule"], m["note"])
              for m in d["manual"]}
    COLUMN_RULES = {(c["target"], c["column"]):
                    dict(rule=c["rule"], verdict=c["verdict"], note=c["note"])
                    for c in d["columnRules"]}
    TARGET_FIXED_DEFAULT = {(x["target"], x["column"]): x["value"]
                            for x in d["targetFixedDefault"]}
    CLASSES_FILL_EXCEPT = set(d["classesFillDefault"]["except"])
    ENC_MSSQL = set(d["encrypted"]["mssql"])
    ENC_ORACLE = set(d["encrypted"]["oracle"])
    GRAM_TYPE_MAP = dict(d["gramTypeMap"])
    REF_TARGET_TYPE_CODES = dict(d["refTargetTypeCodes"])
    global COMMENT_PREFIX, HIDDEN_TARGET, CORRECTIONS
    COMMENT_PREFIX = d.get("commentPrefix", "")
    HIDDEN_TARGET = set(d.get("hiddenTargetColumns", []))
    CORRECTIONS = {(c["source"], c["column"]):
                   dict(target=c["target"], rule=c["rule"], verdict=c["verdict"], note=c["note"])
                   for c in d.get("corrections", [])}
    return True


# COMMENT 계열 컬럼 이관 시 붙일 말머리 (JSON commentPrefix). 항목3(2026-07-23).
COMMENT_PREFIX = "[NAIMS-I]"
# 문서에서 아예 제외할 타겟 컬럼 "table.column" (삭제 예정 컬럼). 항목(2026-07-24).
HIDDEN_TARGET = set()
# 잘못된 baseline 매핑을 덮어쓰는 교정 {(소스,컬럼): {target,rule,verdict,note}}. (2026-07-24)
CORRECTIONS = {}


_RULES_FROM_JSON = _load_rules_from_json()


# ── 스키마 로더 ────────────────────────────────────────────────────────
def load_flat(path, stop_at=("## PRIMARY KEYS", "## FOREIGN KEYS", "## ROW COUNTS")):
    """### TABLE: 헤더 + '컬럼<TAB>타입' 형식 파일을 읽는다."""
    tabs, cur = collections.OrderedDict(), None
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        if any(line.startswith(s) for s in stop_at):
            break
        m = re.match(r"^### TABLE:\s*(.+)$", line)
        if m:
            cur = m.group(1).strip()
            tabs.setdefault(cur, [])
            continue
        if "\t" in line and cur:
            col, typ = line.split("\t", 1)
            tabs[cur].append((col.strip(), typ.strip()))
    return tabs


def load_oracle(path):
    """전사본은 페이지 분할·연속 표기가 섞여 있어 별도 처리."""
    tabs, cur = collections.OrderedDict(), None
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        m = re.match(r"^### TABLE:\s*(.+?)\s*(\[.*)?$", line)
        if m:
            name = m.group(1).strip()
            if name.startswith("<") or name.upper().startswith("(UNKNOWN"):
                continue          # 이름이 앞 페이지에 있는 연속 조각 — 직전 테이블에 이어붙인다
            cur = name
            tabs.setdefault(cur, [])
            continue
        if not line.strip() or line.startswith(("##", "PK:", "(", "NOTE", "-")):
            continue
        if "\t" in line and cur:
            col, typ = line.split("\t", 1)
            col = col.strip().strip('"')
            if re.match(r"^[A-Z_][A-Z0-9_#$]*$", col):
                tabs[cur].append((col, typ.strip()))
    return tabs


def load_meta(path):
    """PK / FK / ROW COUNTS 섹션을 읽는다."""
    pk, fk, rows = collections.defaultdict(list), [], {}
    sec = None
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n").strip()
        if line.startswith("## PRIMARY KEYS"): sec = "pk"; continue
        if line.startswith("## FOREIGN KEYS"): sec = "fk"; continue
        if line.startswith("## ROW COUNTS"):   sec = "rc"; continue
        if not line or line.startswith("###"): continue
        if sec == "pk" and "." in line:
            t, c = line.split(".", 1); pk[t].append(c)
        elif sec == "fk" and "->" in line:
            a, b = line.split("->"); fk.append((a.strip(), b.strip()))
        elif sec == "rc" and "\t" in line:
            t, n = line.split("\t", 1)
            try: rows[t] = int(n)
            except ValueError: pass
    return pk, fk, rows


# ── 매칭 규칙 (ColumnMapper.cs 와 동일) ────────────────────────────────
def normalize(name):
    s = name.replace("_", "").upper()
    if s.endswith("IES"): s = s[:-3] + "Y"
    elif s.endswith("ES") and len(s) > 4: s = s[:-2]
    elif s.endswith("S") and len(s) > 3: s = s[:-1]
    return s


def ref_matches(ref_col, stem):
    core = re.sub(r"^REF_", "", ref_col, flags=re.I)
    core = re.sub(r"_ID$", "", core, flags=re.I)
    core = re.sub(r"^STD_", "", core, flags=re.I)
    core = normalize(core)
    return core == stem or core.startswith(stem) or stem.startswith(core)


def find_target(col, src_cols, tgt_cols, tgt_by_name, used, is_first):
    name = col
    if name in tgt_by_name and tgt_by_name[name] not in used:
        return tgt_by_name[name], "동일"
    if name in ALIASES:
        a = ALIASES[name]
        if a in tgt_by_name and tgt_by_name[a] not in used:
            return tgt_by_name[a], "별칭"
    if is_first and name.endswith("_ID") and "RECORD_ID" in tgt_by_name and "RECORD_ID" not in used:
        return "RECORD_ID", "PK→RECORD_ID"
    if name.endswith("_ID"):
        stem = normalize(name[:-3])
        for t in tgt_cols:
            if t in used: continue
            if t.upper().startswith("REF_") and t.upper().endswith("_ID") and ref_matches(t, stem):
                return t, "FK→REF_*_ID"
    if name.endswith("FILENAME"):
        kind = name.replace("_FILENAME", "").replace("FILENAME", "")
        for t in tgt_cols:
            if t in used: continue
            if t.upper().endswith("FILE_NAME") and (not kind or kind in t.upper()):
                return t, "파일명"
    nm = normalize(name)
    for t in tgt_cols:
        if t not in used and normalize(t) == nm:
            return t, "정규화"
    return None, ""


# ── 타입 계열 판정 ─────────────────────────────────────────────────────
def fam_ora(t):
    t = t.upper().replace("BYTE", "").replace(" ", "")
    t = re.sub(r"DEFAULT.*$", "", t)
    if re.match(r"^NUMBER\((\*|\d+),0\)$", t): return ("INT", None)
    if t.startswith("NUMBER"): return ("NUM", None)
    m = re.match(r"^VARCHAR2\((\d+)\)$", t) or re.match(r"^CHAR\((\d+)\)$", t)
    if m: return ("STR", int(m.group(1)))
    m = re.match(r"^RAW\((\d+)\)$", t)
    if m: return ("BIN", int(m.group(1)))
    if t.startswith("TIMESTAMP") or t == "DATE": return ("DT", None)
    if t in ("BLOB", "CLOB", "LONGRAW"): return ("LOB", None)
    return (t, None)


def fam_ms(t):
    t = t.upper()
    for s in (" NOT NULL", " IDENTITY", " COMPUTED"): t = t.replace(s, "")
    t = t.replace(" ", "")
    if t in ("INT", "SMALLINT", "TINYINT", "BIGINT", "BIT"): return ("INT", None)
    if t in ("FLOAT", "REAL") or t.startswith(("DECIMAL", "NUMERIC")): return ("NUM", None)
    m = re.match(r"^N?(?:VAR)?CHAR\((\d+|MAX)\)$", t)
    if m: return ("STR", -1 if m.group(1) == "MAX" else int(m.group(1)))
    m = re.match(r"^(?:VAR)?BINARY\((\d+|MAX)\)$", t)
    if m: return ("BIN", -1 if m.group(1) == "MAX" else int(m.group(1)))
    if "DATE" in t or "TIME" in t: return ("DT", None)
    if t in ("IMAGE", "TEXT", "NTEXT"): return ("LOB", None)
    return (t, None)


def convert_note(src_typ, tgt_typ, src_is_oracle):
    """소스→타겟 타입 변환 시 주의사항."""
    if not src_typ or not tgt_typ: return ""
    fs = fam_ora(src_typ) if src_is_oracle else fam_ms(src_typ)
    ft = fam_ms(tgt_typ)
    notes = []
    if fs[0] != ft[0]:
        if fs[0] == "BIN" and ft[0] == "STR":
            notes.append("복호화 필요 (암호문→평문)")
        elif {fs[0], ft[0]} == {"INT", "NUM"}:
            pass                                     # 정수/실수 호환 — 무해
        else:
            notes.append("타입 계열 상이 (%s→%s) 변환 규칙 확정 필요" % (fs[0], ft[0]))
    elif fs[0] in ("STR", "BIN") and fs[1] and ft[1] and ft[1] >= 0 and fs[1] > ft[1]:
        notes.append("길이 축소 %d→%d — 잘림 주의" % (fs[1], ft[1]))
    return "; ".join(notes)


# ── 매핑 산출 ──────────────────────────────────────────────────────────
def main():
    ora = load_oracle(os.path.join(HERE, "oracle_naims_schema.txt"))
    ms  = load_flat(os.path.join(HERE, "mssql_naims_schema.txt"))
    tg  = load_flat(os.path.join(HERE, "naims2_schema.txt"))
    tg_pk, tg_fk, tg_rows = load_meta(os.path.join(HERE, "naims2_schema.txt"))

    rows = []
    for src, tgt, srcpk, level, cat in PLAN:
        mcols = ms.get(src, [])
        ocols = ora.get(src, [])
        tcols = tg.get(tgt, [])
        odict = dict(ocols)
        mdict = dict(mcols)
        tgt_names = [c for c, _ in tcols]
        tgt_by_name = {c: c for c in tgt_names}
        used = set()

        # 소스 컬럼 기준 (MsSQL 순서를 기준선으로 삼고, Oracle 전용 컬럼은 뒤에 붙인다)
        base = [c for c, _ in mcols] + [c for c, _ in ocols if c not in mdict]

        for i, col in enumerate(base):
            mtyp = mdict.get(col, "")
            otyp = odict.get(col, "")
            key = "%s.%s" % (src, col)

            if col in DROPPED:
                rows.append(dict(cat=cat, level=level, src=src, tgt=tgt, col=col,
                                 mtyp=mtyp, otyp=otyp or "(없음)", tcol="", ttyp="",
                                 rule="폐기", status="소스에만", note=DROPPED[col], todo=""))
                continue

            is_first = (i == 0 and col == (mcols[0][0] if mcols else None))
            tcol, rule = find_target(col, base, tgt_names, tgt_by_name, used, is_first)

            if tcol is None:
                rows.append(dict(cat=cat, level=level, src=src, tgt=tgt, col=col,
                                 mtyp=mtyp, otyp=otyp or "(없음)", tcol="", ttyp="",
                                 rule="-", status="소스에만",
                                 note="대응 컬럼 미발견", todo="확인 필요"))
                continue

            used.add(tcol)
            ttyp = dict(tcols)[tcol]
            status = "일치" if tcol == col else "이름변경"

            notes, todo = [], ""
            if key in ENC_MSSQL and key in ENC_ORACLE:
                notes.append("양쪽 모두 암호화 — 복호화 후 적재")
            elif key in ENC_MSSQL:
                notes.append("MsSQL만 암호화(varbinary) / Oracle은 평문")
            elif key in ENC_ORACLE:
                notes.append("Oracle만 암호화(RAW) — 복호화 방식 미확인")
                todo = "Oracle RAW 복호화 방식 확인"

            cn_m = convert_note(mtyp, ttyp, False) if mtyp else ""
            cn_o = convert_note(otyp, ttyp, True) if otyp else ""
            if cn_m: notes.append("MsSQL: " + cn_m)
            if cn_o and cn_o != cn_m: notes.append("Oracle: " + cn_o)
            if not otyp: notes.append("Oracle 소스에 없음")
            if not mtyp: notes.append("MsSQL 소스에 없음")
            if "확정 필요" in (cn_m + cn_o): todo = todo or "변환 규칙 확정"

            rows.append(dict(cat=cat, level=level, src=src, tgt=tgt, col=col,
                             mtyp=mtyp or "(없음)", otyp=otyp or "(없음)",
                             tcol=tcol, ttyp=ttyp, rule=rule, status=status,
                             note="; ".join(notes), todo=todo))

        # 타겟에만 있는 컬럼
        for tcol, ttyp in tcols:
            if tcol in used: continue
            note = TARGET_ONLY.get(tcol, "소스 대응 없음 — 기본값/NULL 또는 산출값")
            todo = "" if tcol in TARGET_ONLY else ("채움 규칙 확정" if "NOT NULL" in ttyp else "")
            rows.append(dict(cat=cat, level=level, src=src, tgt=tgt, col="",
                             mtyp="", otyp="", tcol=tcol, ttyp=ttyp,
                             rule="신규", status="타겟에만", note=note, todo=todo))

    # 출력
    cols = ["cat", "level", "src", "tgt", "col", "mtyp", "otyp", "tcol", "ttyp",
            "rule", "status", "note", "todo"]
    with io.open(os.path.join(HERE, "mapping.tsv"), "w", encoding="utf-8") as f:
        f.write("\t".join(cols) + "\n")
        for r in rows:
            f.write("\t".join(str(r[c]).replace("\t", " ") for c in cols) + "\n")

    st = collections.Counter(r["status"] for r in rows)
    todos = [r for r in rows if r["todo"]]
    with io.open(os.path.join(HERE, "mapping_stats.txt"), "w", encoding="utf-8") as f:
        f.write("이관 대상 테이블: %d\n" % len(PLAN))
        f.write("총 매핑 행     : %d\n" % len(rows))
        for k, v in st.most_common():
            f.write("  %-10s %d\n" % (k, v))
        f.write("미결 항목      : %d\n" % len(todos))
        seen = collections.Counter(r["todo"] for r in todos)
        for k, v in seen.most_common():
            f.write("  %-28s %d\n" % (k, v))
    print(io.open(os.path.join(HERE, "mapping_stats.txt"), encoding="utf-8").read())


if __name__ == "__main__":
    main()
