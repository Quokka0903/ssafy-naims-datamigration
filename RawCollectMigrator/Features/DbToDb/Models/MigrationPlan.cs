namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>테이블 하나가 부모 테이블을 참조하는 외래키 한 건.</summary>
    /// <param name="SourceColumn">소스 테이블의 FK 컬럼 (예: CLASS_ID)</param>
    /// <param name="ParentSourceTable">부모 소스 테이블 (예: CLASSES)</param>
    /// <param name="TargetColumn">타겟의 대응 FK 컬럼 (예: REF_STD_CLASSES_ID)</param>
    /// <param name="Optional">부모가 없어도 NULL 로 넣고 진행할지</param>
    public sealed record ForeignKeyLink(
        string SourceColumn,
        string ParentSourceTable,
        string TargetColumn,
        bool Optional = true);

    /// <summary>기존 타겟 행을 재사용할지 판단하는 정책.</summary>
    public enum ReusePolicy
    {
        /// <summary>항상 새로 INSERT.</summary>
        InsertAlways,

        /// <summary>
        /// 자연키가 일치하는 행이 타겟에 이미 있으면 그 RECORD_ID 를 재사용하고 INSERT 를 건너뛴다.
        /// (사용자 지시: "이미 있는 국가는 재사용해")
        /// </summary>
        ReuseByNaturalKey
    }

    // [NAIMS-ISSUE:DERIVED-CHILD] 부모 INSERT 직후 만들어 넣는 파생 자식행
    /// <summary>
    /// 부모 행을 넣은 직후, 그 RECORD_ID 를 FK 로 자동 생성해 넣는 자식 테이블.
    ///
    /// 소스 DB 에 대응 테이블이 없어 "이관"이 아니라 "산출"이 필요한 경우에 쓴다.
    /// 현재 용도: STD_AI_SNIPPET → STD_AI_SPECTROGRAM (레거시엔 스펙트로그램이 없어
    /// 스니펫 원음에서 생성해 넣는다. 기준식별은 이 자식 1행이 있어야 스니펫을 전시한다.)
    /// </summary>
    /// <param name="TargetTable">자식 타겟 테이블.</param>
    /// <param name="ParentFkColumn">부모 RECORD_ID 를 담을 자식 FK 컬럼.</param>
    public sealed record DerivedChildSpec(string TargetTable, string ParentFkColumn);

    /// <summary>테이블 한 건의 이관 계획.</summary>
    public sealed class TablePlan
    {
        /// <summary>부모 INSERT 직후 산출해 넣는 자식행(없으면 null).</summary>
        public DerivedChildSpec? DerivedChild { get; init; }

        public required string SourceTable { get; init; }
        public required string TargetTable { get; init; }

        /// <summary>소스의 기본키 컬럼. IdRemapper 등록에 쓴다.</summary>
        public required string SourcePk { get; init; }

        /// <summary>FK 의존 관계. 이 목록이 이관 순서를 결정한다.</summary>
        public IReadOnlyList<ForeignKeyLink> ForeignKeys { get; init; } = Array.Empty<ForeignKeyLink>();

        public ReusePolicy Reuse { get; init; } = ReusePolicy.InsertAlways;

        /// <summary>재사용 판정에 쓰는 자연키. 소스/타겟 각각의 컬럼명.</summary>
        public string SourceKeyColumn { get; init; } = "";
        public string TargetKeyColumn { get; init; } = "";

        /// <summary>
        /// 부모 단위 멱등의 기준 FK(소스 컬럼명). CLASS 서브트리 계보의 부모를 가리킨다.
        /// 이 부모가 이번 실행에서 "재사용(기존)"으로 판정되면 자식도 이미 이관된 것으로 보고 스킵한다.
        /// (공유 참조 FK — COUNTRY/WEAPON/NB_SNIPPET/ACTIVE_SONAR — 는 여기 두지 않는다.)
        /// ForeignKeys 안에 같은 SourceColumn 의 FK 가 있어야 한다.
        /// </summary>
        public string SubtreeParentColumn { get; init; } = "";

        /// <summary>
        /// 복합 자연키 dedup 에 쓰는 타겟 식별 컬럼(부모 FK 제외). 비어 있지 않으면,
        /// INSERT 전에 (부모 FK + 이 컬럼들)로 타겟을 조회해 이미 있으면 재사용한다.
        /// 키가 뚜렷한 자식(HARMONICS·TR_FRAME·TRANSIENT)에만 설정한다.
        /// </summary>
        public IReadOnlyList<string> DedupKeyColumns { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 링크(매핑) 테이블 전용 멱등. 이 테이블의 존재 의의는 "옮긴 부모들의 연관관계 보존"이므로,
        /// 부모 단위 스킵을 쓰지 않고 **모든 FK의 타겟 값(튜플)** 으로 대조한다.
        /// 같은 FK 조합이 이미 있으면 재사용(스킵), 없으면 INSERT — 부모가 기존이든 신규든
        /// 관계는 항상 채운다. (SIG_SNIPPET·FRAME_SNIPPET 등 순수 링크 테이블에 설정)
        /// </summary>
        public bool DedupByForeignKeys { get; init; }

        /// <summary>이 테이블이 복호화를 필요로 하는지 (CryptoContext 기준).</summary>
        public bool NeedsDecryption => CryptoContext.RequiresDecryption(SourceTable);

        public string Note { get; init; } = "";

        /// <summary>위상 정렬 결과 단계(1부터). BuildOrder 가 채운다.</summary>
        public int Level { get; internal set; }
    }

    /// <summary>
    /// DB → DB 이관 계획 전체.
    ///
    /// 설계 원칙
    ///  1) 타겟 RECORD_ID 는 IDENTITY 다. 부모를 먼저 넣고 새 ID 를 IdRemapper 에 기록한 뒤
    ///     자식의 REF_*_ID 를 치환한다.
    ///  2) 부모가 없으면 자식을 넣을 수 없으므로 FK 그래프를 위상 정렬해 단계를 만든다.
    ///  3) 암호화 컬럼이 있는 테이블은 CryptoContext 가 SymmetricKey 모드일 때만 실행한다.
    ///  4) 사용자 확정: RAWDATAINFO / RAWSIGNALINFO / RAWDATAIMPORTINFO / ONA_209 / ONA_214
    ///     및 PASSWORD / USER_JUMIN / DATASET / FIELD_CONV 는 이관 대상이 아니다.
    /// </summary>
    public static class MigrationPlan
    {
        /// <summary>
        /// 이관 대상에서 제외된 소스 테이블. 단일 원본(mapping_rules.json)에서 로드한다.
        /// (2026-07-23 SSOT 통합)
        /// </summary>
        public static IReadOnlySet<string> Excluded => MigrationRules.ExcludedTables;

        public static List<TablePlan> Defaults() => new()
        {
            // ── 최상위: 부모 없음 ──
            new() { SourceTable = "COUNTRY_CODES", TargetTable = "STD_COUNTRY_CODES", SourcePk = "COUNTRY_ID",
                    Reuse = ReusePolicy.ReuseByNaturalKey,
                    SourceKeyColumn = "CTRY_NAME", TargetKeyColumn = "CTRY_NAME",
                    Note = "평문 varchar. 실측 8건 중 6건이 타겟에 이미 존재 — 이름으로 대조해 재사용하고 신규 2건만 INSERT." },

            new() { SourceTable = "CLASSES", TargetTable = "STD_CLASSES", SourcePk = "CLASS_ID",
                    Reuse = ReusePolicy.ReuseByNaturalKey,
                    SourceKeyColumn = "CLASS_NAME", TargetKeyColumn = "CLASS_NAME",
                    Note = "CLASS_NAME 암호화 — 복호화 후에야 재사용 대조가 가능하다. " +
                           "소스에 국가 숫자 FK 없음(USER_COUNTRIES 문자열뿐) → REF_STD_COUNTRY_CODES_ID 는 USER_COUNTRIES 해석 필요(도메인 결정 보류)." },

            new() { SourceTable = "ACTIVE_SONAR", TargetTable = "STD_ACTV_SONAR", SourcePk = "SONAR_ID",
                    Reuse = ReusePolicy.ReuseByNaturalKey,
                    SourceKeyColumn = "SONAR_NAME", TargetKeyColumn = "SONAR_NAME",
                    Note = "SONAR_NAME 암호화. 소스에 국가 컬럼 없음 — 국가 FK 미존재." },

            new() { SourceTable = "WEAPON", TargetTable = "STD_WEAPON", SourcePk = "WEAPON_ID",
                    // [NAIMS-ISSUE:IDEMPOTENT-GAP] 부모 FK 가 없어 모델명 단독 자연키로 멱등.
                    DedupKeyColumns = new[] { "WEAPON_MODEL" },
                    Note = "평문. 이름 컬럼은 WEAPON_NAME 이 아니라 WEAPON_MODEL — 매핑 재검증 완료. " +
                           "재이관 중복 방지: WEAPON_MODEL 자연키 dedup(타겟 컬럼 실측으로 자동 검증)." },

            new() { SourceTable = "PULSE_SEGMENTS", TargetTable = "STD_PULSE_SEGMENTS", SourcePk = "SEGMENT_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("SONAR_ID", "ACTIVE_SONAR", "REF_STD_ACTV_SONAR_ID", Optional: false) },
                    // [NAIMS-ISSUE:IDEMPOTENT-GAP] (소나 + 주파수범위·변조·길이)로 세그먼트 동일성 판정.
                    //   타겟 실측 컬럼명: FREQ_START/FREQ_STOP/MODULATION/SEG_LEN_MS_MIN/SEG_LEN_MS_MAX
                    //   (전부 소스와 동일명 매핑). 2026-08-19 G2 게이트가 오기를 잡아 교정.
                    // [NAIMS-ISSUE:DEDUP-KEY-VERIFY] 제원(주파수·변조·길이)만으로는 세그먼트가 구분되지 않는다.
                    //   실측: 같은 (소나, 0/0/NULL/300/300) 에 CWFM300L_U_NULL·CWFM300H_U_SEG2·
                    //   CWFM300L_D_SEG2·CWFM300H_D_SEG2 네 건이 있다. 이름의 U/D(상승·하강)와
                    //   SEG1/SEG2 가 실제 구분자다. SEGMENT_NAME 을 빼면 37행이 30행으로 뭉개졌다.
                    DedupKeyColumns = new[] { "SEGMENT_NAME", "FREQ_START", "FREQ_STOP", "MODULATION", "SEG_LEN_MS_MIN", "SEG_LEN_MS_MAX" },
                    Note = "재이관 중복 방지: (ACTIVE_SONAR, 주파수/변조/길이) 복합키 dedup." },

            new() { SourceTable = "DB_DIST_GROUP", TargetTable = "STD_DB_DIST_GROUP", SourcePk = "GROUP_ID" },

            new() { SourceTable = "NB_SNIPPET", TargetTable = "STD_NB_SNIPPET", SourcePk = "NB_SNIPPET_ID",
                    Reuse = ReusePolicy.ReuseByNaturalKey,
                    SourceKeyColumn = "GRAM_FILENAME", TargetKeyColumn = "STD_NB_GRAM_SNIPPET_FILE_NAME",
                    Note = ".grm → R8 66B 변환 + .wav 동반. GRAM 파일명 자연키로 멱등(재이관 중복 방지)." },

            new() { SourceTable = "TR_SNIPPET", TargetTable = "STD_TR_SNIPPET", SourcePk = "TR_SNIPPET_ID",
                    // [NAIMS-ISSUE:IDEMPOTENT-GAP] NB_SNIPPET 과 같은 방식 — 오디오 파일명 자연키.
                    //   타겟 실측 컬럼명은 AUDIO_FILE_NAME (소스 AUDIO_FILENAME 에서 이름변경 매핑).
                    //   2026-08-19 G2 게이트가 오기(STD_TR_AUDIO_SNIPPET_FILE_NAME)를 잡아 교정.
                    DedupKeyColumns = new[] { "AUDIO_FILE_NAME" },
                    Note = ".wav 만. 실측 4개 전부 파일 없음 — 처리 방침 확인 필요. " +
                           "재이관 중복 방지: 오디오 파일명 dedup." },

            // ── 2단계 ──
            new() { SourceTable = "VESSEL_DATA", TargetTable = "STD_VESSEL_DATA", SourcePk = "VESSEL_ID",
                    ForeignKeys = new[]
                    {
                        new ForeignKeyLink("CLASS_ID",   "CLASSES",       "REF_STD_CLASSES_ID"),
                        new ForeignKeyLink("COUNTRY_ID", "COUNTRY_CODES", "REF_STD_COUNTRY_CODES_ID"),
                    },
                    Reuse = ReusePolicy.ReuseByNaturalKey,
                    SourceKeyColumn = "VESSEL_NAME", TargetKeyColumn = "VESSEL_NAME",
                    Note = "VESSEL_NAME 암호화." },

            new() { SourceTable = "PULSES", TargetTable = "STD_PULSES", SourcePk = "PULSE_ID",
                    ForeignKeys = new[]
                    {
                        // 소나는 NOT NULL 필수 FK → 부모(ACTIVE_SONAR) 없으면 행 스킵.
                        new ForeignKeyLink("SONAR_ID",       "ACTIVE_SONAR",   "REF_STD_ACTV_SONAR_ID", Optional: false),
                        // 세그먼트 참조 7개 — 타겟 컬럼명은 FIRST_SEG_ID..SEVENTH_SEG_ID (NOT NULL).
                        // 미사용 슬롯은 소스 0 → 0 sentinel 그대로. 사용 슬롯은 새 RECORD_ID 로 치환.
                        new ForeignKeyLink("FIRST_SEG_ID",   "PULSE_SEGMENTS", "FIRST_SEG_ID",   Optional: false),
                        new ForeignKeyLink("SECOND_SEG_ID",  "PULSE_SEGMENTS", "SECOND_SEG_ID",  Optional: false),
                        new ForeignKeyLink("THIRD_SEG_ID",   "PULSE_SEGMENTS", "THIRD_SEG_ID",   Optional: false),
                        new ForeignKeyLink("FOURTH_SEG_ID",  "PULSE_SEGMENTS", "FOURTH_SEG_ID",  Optional: false),
                        new ForeignKeyLink("FIFTH_SEG_ID",   "PULSE_SEGMENTS", "FIFTH_SEG_ID",   Optional: false),
                        new ForeignKeyLink("SIXTH_SEG_ID",   "PULSE_SEGMENTS", "SIXTH_SEG_ID",   Optional: false),
                        new ForeignKeyLink("SEVENTH_SEG_ID", "PULSE_SEGMENTS", "SEVENTH_SEG_ID", Optional: false),
                    },
                    // [NAIMS-ISSUE:IDEMPOTENT-GAP] (소나 + 펄스명)으로 재이관 중복 방지.
                    DedupKeyColumns = new[] { "PULSE_NAME" },
                    Note = "소나 필수 FK. 세그먼트 7개는 FIRST_SEG_ID..SEVENTH_SEG_ID(NOT NULL), 미사용 슬롯 0 유지. " +
                           "재이관 중복 방지: (ACTIVE_SONAR, PULSE_NAME) 복합키 dedup." },

            new() { SourceTable = "WEAPON_CARRIER", TargetTable = "STD_WEAPON_CLASSES_LINK", SourcePk = "WCARRIER_ID",
                    ForeignKeys = new[]
                    {
                        new ForeignKeyLink("WEAPON_ID", "WEAPON",  "REF_STD_WEAPON_ID",  Optional: false),
                        new ForeignKeyLink("CLASS_ID",  "CLASSES", "REF_STD_CLASSES_ID", Optional: false),
                    },
                    SubtreeParentColumn = "CLASS_ID",   // 클래스가 재사용이면 그 무장 링크도 이미 있음
                    Note = "클래스 단위 멱등." },

            new() { SourceTable = "DIST_LIST", TargetTable = "STD_DIST_LIST", SourcePk = "DIST_LIST_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("GROUP_ID", "DB_DIST_GROUP", "REF_STD_DB_DIST_GROUP_ID") } },

            // ── 3단계 ──
            new() { SourceTable = "SIGNATURES", TargetTable = "STD_SIGNATURES", SourcePk = "SIGNATURE_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("VESSEL_ID", "VESSEL_DATA", "REF_STD_VESSEL_DATA_ID", Optional: false) },
                    SubtreeParentColumn = "VESSEL_ID",   // 함정이 재사용(기존)이면 그 시그니처도 이미 있음 → 스킵
                    Note = "뚜렷한 단일 자연키 없음 — 부모(함정) 단위 멱등으로 중복 방지." },

            new() { SourceTable = "CHARACTERISTICS", TargetTable = "STD_CHARACTERISTICS", SourcePk = "CHAR_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("VESSEL_ID", "VESSEL_DATA", "REF_STD_VESSEL_DATA_ID", Optional: false) },
                    SubtreeParentColumn = "VESSEL_ID",
                    Note = "부모(함정) 단위 멱등." },

            new() { SourceTable = "TRANSIENT", TargetTable = "STD_TRANSIENT", SourcePk = "TR_ID",
                    ForeignKeys = new[]
                    {
                        new ForeignKeyLink("CLASS_ID",  "CLASSES",     "REF_STD_CLASSES_ID"),
                        new ForeignKeyLink("VESSEL_ID", "VESSEL_DATA", "REF_STD_VESSEL_DATA_ID"),
                    },
                    SubtreeParentColumn = "CLASS_ID",
                    DedupKeyColumns = new[] { "TR_NAME" },   // (REF_STD_CLASSES_ID, TR_NAME) 복합키
                    Note = "TR_NAME 암호화. 부모(클래스) 단위 멱등 + (클래스, TR_NAME) 복합키 dedup." },

            new() { SourceTable = "AI_SNIPPET", TargetTable = "STD_AI_SNIPPET", SourcePk = "AI_SNIPPET_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("PULSE_ID", "PULSES", "REF_STD_PULSES_ID") },
                    // [NAIMS-ISSUE:IDEMPOTENT-GAP] (펄스 + 그램 파일명) 복합키.
                    //   타겟 실측 컬럼명은 GRAM_FILE_NAME (소스 GRAM_FILENAME 에서 이름변경 매핑).
                    //   2026-08-19 G2 게이트가 오기(STD_AI_GRAM_SNIPPET_FILE_NAME)를 잡아 교정.
                    DedupKeyColumns = new[] { "GRAM_FILE_NAME" },
                    // [NAIMS-ISSUE:DERIVED-CHILD] 스펙트로그램은 레거시에 없어 스니펫 원음에서 산출해 넣는다.
                    DerivedChild = new DerivedChildSpec("STD_AI_SPECTROGRAM", "REF_STD_AI_SNIPPET_ID"),
                    Note = ".grm → R8 66B 변환. 실측 6개 참조 중 1개만 파일 존재. " +
                           "재이관 중복 방지: (PULSES, 그램 파일명) dedup." },

            new() { SourceTable = "DB_DIST_LOG", TargetTable = "STD_DB_DIST_LOG", SourcePk = "LOG_ID",
                    Note = "배포관리(실행 제외). 소스에 DIST_LIST 로의 숫자 FK 없음 — GROUP_NAME 문자열로만 연결." },

            new() { SourceTable = "DIST_TO", TargetTable = "STD_DIST_TO", SourcePk = "LOG_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("LOG_ID", "DB_DIST_LOG", "REF_STD_DB_DIST_LOG_ID", Optional: false) },
                    Note = "배포관리(실행 제외). 서러게이트 PK 없음 — LOG_ID(부모 FK)를 PK 로 사용. (LOG_ID, DESTINATION) 복합키." },

            // ── 4단계 ──
            new() { SourceTable = "HARMONICS", TargetTable = "STD_HARMONICS", SourcePk = "HARM_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("SIGNATURES_ID", "SIGNATURES", "REF_STD_SIGNATURES_ID", Optional: false) },
                    SubtreeParentColumn = "SIGNATURES_ID",
                    DedupKeyColumns = new[] { "HARM_NO" },   // (REF_STD_SIGNATURES_ID, HARM_NO) 복합키
                    Note = "부모(시그니처) 단위 멱등 + (시그니처, HARM_NO) 복합키 dedup." },

            new() { SourceTable = "TR_FRAME", TargetTable = "STD_TR_FRAME", SourcePk = "FRAME_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("TR_ID", "TRANSIENT", "REF_STD_TRANSIENT_ID", Optional: false) },
                    SubtreeParentColumn = "TR_ID",
                    // [NAIMS-ISSUE:DEDUP-KEY-VERIFY] MFB_NO 를 프레임 순번으로 오해해 dedup 키로 썼다가
                    //   495행이 3행으로 뭉개졌다(게이트에는 "재사용 492건" 이라는 정상적인 숫자로 나왔다).
                    //   MFB_NO 는 순번이 아니라 "유효 MFB 밴드 개수" 이고 전 행이 32 다.
                    //   정본 근거: AAS ViewCCFApplySubTR.cpp:1060 (MFB_no = iMFBCount),
                    //             NAIMS-II TRScoring.cs:220 ("0 < MFB_NO < 차수 프레임은 제외").
                    //   프레임을 구분할 컬럼이 아예 없다(나머지는 MFB_VALUE_1~32 실수 32개뿐).
                    //   잘못된 키를 쓰느니 끄는 게 맞다 — 부모(트랜지언트) 단위 멱등만 남긴다.
                    //   재실행 중복은 전량 재구축으로 막는다.
                    Note = "MFB_VALUE_1~32 (32밴드 특징벡터). MFB_NO 는 밴드 '개수'라 식별자가 아니다 — " +
                           "행 구분 키가 없어 복합키 dedup 없이 부모(트랜지언트) 단위 멱등만 적용." },

            new() { SourceTable = "SIG_SNIPPET", TargetTable = "STD_NB_SIG_SNIPPET_LINK", SourcePk = "SIG_SNP_ID",
                    ForeignKeys = new[]
                    {
                        new ForeignKeyLink("SIGNATURE_ID",  "SIGNATURES", "REF_STD_SIGNATURES_ID", Optional: false),
                        new ForeignKeyLink("NB_SNIPPET_ID", "NB_SNIPPET", "REF_STD_NB_SNIPPET_ID", Optional: false),
                    },
                    DedupByForeignKeys = true,   // 링크: (시그니처,NB) 튜플로 대조 — 관계는 항상 채운다
                    Note = "매핑 테이블 — 옮긴 특징정보↔스니펫 관계 보존. FK 튜플 단위 멱등." },

            new() { SourceTable = "IMAGE_DATA", TargetTable = "STD_REFERENCE_DATA", SourcePk = "IMAGE_ID",
                    // [NAIMS-ISSUE:REFDATA-FK] STD_REFERENCE_DATA 에는 REF_STD_VESSEL_DATA_ID 컬럼이 없다.
                    //   이 표는 여러 대상(함정·협대역·AI·능동)을 한 표에 담으려고
                    //   (REF_TARGET_TYPE, REF_TARGET_ID) 다형 참조를 쓴다. 그래서 FK 를 일반 규칙대로
                    //   REF_STD_<부모>_ID 로 선언하면 없는 컬럼에 INSERT 를 시도해 터진다
                    //   (실측: "열 이름 'REF_STD_VESSEL_DATA_ID'이(가) 유효하지 않습니다").
                    //   실제 치환은 실행기의 REF_TARGET_ID 분기가 VESSEL_DATA 매핑으로 처리한다.
                    ForeignKeys = new[] { new ForeignKeyLink("VESSEL_ID", "VESSEL_DATA", "REF_TARGET_ID") },
                    SubtreeParentColumn = "VESSEL_ID",
                    Note = "영상자료. 다형 참조 (REF_TARGET_TYPE=5(함정), REF_TARGET_ID=VESSEL 신규 RECORD_ID). " +
                           "부모(함정) 단위 멱등." },

            // ── 5단계 ──
            new() { SourceTable = "FRAME_SNIPPET", TargetTable = "STD_TR_FRAME_SNIPPET_LINK", SourcePk = "FRM_SNP_ID",
                    ForeignKeys = new[]
                    {
                        new ForeignKeyLink("FRAME_ID",      "TR_FRAME",   "REF_STD_TR_FRAME_ID",   Optional: false),
                        new ForeignKeyLink("TR_SNIPPET_ID", "TR_SNIPPET", "REF_STD_TR_SNIPPET_ID", Optional: false),
                    },
                    DedupByForeignKeys = true,   // 링크: (프레임,TR스니펫) 튜플로 대조
                    Note = "매핑 테이블 — TR_FRAME↔TR_SNIPPET 관계 보존. FK 튜플 단위 멱등." },

            // ── 이력 (부모 없음, 마지막에 몰아서) ──
            new() { SourceTable = "CD_CHANGE_LOG", TargetTable = "STD_CD_CHANGE_LOG", SourcePk = "CHNG_ID" },
            new() { SourceTable = "CD_BACKUP_LOG", TargetTable = "STD_CD_BACKUP_LOG", SourcePk = "BACKUP_ID" },
            new() { SourceTable = "CCF2CD_LOG",    TargetTable = "STD_CCF2CD_LOG",    SourcePk = "CCFCD_ID" },

            // [NAIMS-ISSUE:IDEMPOTENT-GAP] ISUS90 전용 3표 — 부모 FK + 값 컬럼으로 재이관 중복 방지.
            new() { SourceTable = "PULSE_FREQUENCY_ISUS90",  TargetTable = "STD_PULSE_FREQUENCY_ISUS90",  SourcePk = "PULSE_F_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("SONAR_ID", "ACTIVE_SONAR", "REF_STD_ACTV_SONAR_ID", Optional: false) },
                    DedupKeyColumns = new[] { "FREQUENCY" } },          // 타겟 실측명(소스 FREQUENCY 동일명)
            new() { SourceTable = "PULSE_SEG_LENGTH_ISUS90", TargetTable = "STD_PULSE_SEG_LENGTH_ISUS90", SourcePk = "SEG_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("PULSE_F_ID", "PULSE_FREQUENCY_ISUS90", "REF_STD_PULSE_FREQUENCY_ISUS90_ID", Optional: false) },
                    DedupKeyColumns = new[] { "LENGTH_MIN", "LENGTH_MAX" } },   // 타겟 실측명
            new() { SourceTable = "PULSE_SEG_SWING_ISUS90",  TargetTable = "STD_PULSE_SEG_SWING_ISUS90",  SourcePk = "SWING_ID",
                    ForeignKeys = new[] { new ForeignKeyLink("PULSE_F_ID", "PULSE_FREQUENCY_ISUS90", "REF_STD_PULSE_FREQUENCY_ISUS90_ID", Optional: false) },
                    DedupKeyColumns = new[] { "SWING", "MODULATION" } },        // 타겟 실측명
        };

        /// <summary>
        /// FK 그래프를 위상 정렬해 실행 순서를 만든다. 각 계획의 Level 을 채워 반환한다.
        /// 같은 Level 끼리는 서로 의존하지 않으므로 순서가 자유롭다.
        /// </summary>
        public static List<TablePlan> BuildOrder(IEnumerable<TablePlan> plans)
        {
            var list = plans.Where(p => !Excluded.Contains(p.SourceTable)).ToList();
            var byName = list.ToDictionary(p => p.SourceTable, StringComparer.OrdinalIgnoreCase);

            var level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int Resolve(string name)
            {
                if (level.TryGetValue(name, out var v)) return v;
                if (!byName.TryGetValue(name, out var plan)) return 0;   // 계획 밖 테이블은 depth 0 취급

                if (!visiting.Add(name))
                    throw new InvalidOperationException($"FK 순환 참조: {name}");

                int max = 0;
                foreach (var fk in plan.ForeignKeys)
                {
                    // 자기참조는 같은 단계 내 2-pass 로 처리하므로 깊이에서 제외
                    if (string.Equals(fk.ParentSourceTable, name, StringComparison.OrdinalIgnoreCase)) continue;
                    max = Math.Max(max, Resolve(fk.ParentSourceTable));
                }

                visiting.Remove(name);
                level[name] = max + 1;
                return max + 1;
            }

            foreach (var p in list) p.Level = Resolve(p.SourceTable);

            return list.OrderBy(p => p.Level)
                       .ThenBy(p => p.SourceTable, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        /// <summary>실행 전 점검 — 무엇이 막혀 있는지 사람이 읽을 수 있게 돌려준다.</summary>
        public static List<string> Validate(IEnumerable<TablePlan> ordered, CryptoContext crypto)
        {
            var problems = new List<string>();
            var known = new HashSet<string>(ordered.Select(p => p.SourceTable), StringComparer.OrdinalIgnoreCase);

            foreach (var p in ordered)
            {
                if (p.NeedsDecryption && !crypto.CanDecrypt)
                    problems.Add($"{p.SourceTable}: 암호화 컬럼 존재 — 현재 모드({crypto.Mode})로는 이관 불가. {crypto.Diagnosis}");

                if (p.Reuse == ReusePolicy.ReuseByNaturalKey &&
                    (p.SourceKeyColumn.Length == 0 || p.TargetKeyColumn.Length == 0))
                    problems.Add($"{p.SourceTable}: 재사용 정책인데 자연키 컬럼이 비어 있다.");

                foreach (var fk in p.ForeignKeys)
                {
                    if (!known.Contains(fk.ParentSourceTable))
                        problems.Add($"{p.SourceTable}.{fk.SourceColumn} → 부모 {fk.ParentSourceTable} 가 이관 계획에 없다.");
                    else if (!fk.Optional)
                    {
                        var parent = ordered.First(x =>
                            string.Equals(x.SourceTable, fk.ParentSourceTable, StringComparison.OrdinalIgnoreCase));
                        if (parent.Level >= p.Level)
                            problems.Add($"{p.SourceTable}(L{p.Level}) 가 부모 {parent.SourceTable}(L{parent.Level}) 보다 먼저/같이 실행된다.");
                    }
                }

                if (p.TargetTable.StartsWith('(') )
                    problems.Add($"{p.SourceTable}: 타겟 테이블 미확정 — {p.TargetTable}");
            }
            return problems;
        }
    }
}
