using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:이관흐름] [가이드:테이블매핑] [GUIDE:MIGRATION-FLOW] [GUIDE:TABLE-MAPPING] [찾기:이관실행] [찾기:INSERT]
    //  ▣ 이 파일 = DB→DB 이관 엔진. 이관이 "어떻게 도는지"는 RunAsync() 부터 읽으면 된다.
    //    실행 큰 흐름(RunAsync):
    //     1) 타겟(NAIMS2) 연결·트랜잭션 열기 + TargetSchema.LoadAsync (타겟 스키마 파악)
    //     2) CryptoContext.ProbeAsync — 소스 이름컬럼 복호 능력 판정  ([가이드:복호화] 참조)
    //     3) 매핑규칙(mapping_rules.json → MigrationRules) 을 FK 위상정렬 순서로 순회
    //     4) 테이블별 MigrateTableAsync():
    //          ReadSourceRowsAsync (SELECT + 복호식) → 행별: 형변환·기본값·COMMENT말머리·단위변환·FK치환(IdRemapper)
    //          → ExecInsertAsync ("INSERT INTO [tgt](...) OUTPUT INSERTED.RECORD_ID VALUES(...)") → 새 RECORD_ID 를 IdRemapper 에 기록
    //          (스니펫 blob 은 파일 로드+그램 변환으로 채움)
    //     5) 모드(RunMode): DryRun=계획만(미실행) / Trial=INSERT후 롤백 / Commit=실제 커밋
    //    · 매핑 규칙 원본 : Features/DbToDb/mapping_rules.json   ([찾기:매핑규칙] — 어느 소스컬럼→어느 타겟컬럼)
    //    · PK→RECORD_ID  : IdRemapper (부모 새 PK 를 자식 FK 로 치환). [찾기:IdRemapper]
    //    · FK=0 "참조없음": 부모에 RECORD_ID=0 센티넬 행이 있으면 0 유지, 없으면 스킵. [찾기:센티넬]
    //    · 멱등(재실행)  : 자연키 재사용/부모단위 스킵/복합키 dedup (같은 소스 재이관 시 중복 방지)
    // ════════════════════════════════════════════════════════════════════════════
    // [NAIMS-ISSUE:RUNMODE] DryRun(미실행)·Trial(롤백)·Commit(커밋) 차이
    /// <summary>이관 실행 방식.</summary>
    public enum RunMode
    {
        /// <summary>INSERT 를 실행하지 않고 계획만 세운다 (SQL 미실행).</summary>
        DryRun,
        /// <summary>실제 INSERT 를 트랜잭션 안에서 실행하고 마지막에 롤백 — 실 경로 안전 검증.</summary>
        Trial,
        /// <summary>실제 INSERT 를 실행하고 커밋 — 진짜 이관.</summary>
        Commit,
    }

    /// <summary>DB → DB 이관 실행 옵션.</summary>
    public sealed class DbToDbOptions
    {
        public DbProviderKind SourceKind { get; init; } = DbProviderKind.MsSql;

        // [NAIMS-ISSUE:CONN-MODE] 소스 접속 방식 — 폐쇄망 현행 ODBC 가 기본. 타겟(NAIMS2)은 항상 SqlClient 다이렉트.
        /// <summary>소스 접속 방식(ODBC 기본 / 다이렉트). 타겟은 항상 다이렉트다.</summary>
        public DbConnectionMode SourceMode { get; init; } = DbConnectionMode.Odbc;

        public string SourceConnStr { get; init; } = "";
        public string TargetConnStr { get; init; } = DbProvider.DefaultTarget;

        /// <summary>실행 방식. 기본은 계획만 세우는 DryRun.</summary>
        public RunMode Mode { get; init; } = RunMode.DryRun;

        public bool IsDryRun => Mode == RunMode.DryRun;
        public bool Writes => Mode != RunMode.DryRun;   // Trial·Commit 은 실제 INSERT

        // [NAIMS-ISSUE:EXCLUDE-CATEGORY] 배포관리·변경이력 제외
        /// <summary>이관에서 제외할 분류. 기본: 배포관리·변경이력 (사용자 지시).</summary>
        public IReadOnlySet<string> ExcludeCategories { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "배포관리", "변경이력" };

        /// <summary>0 이면 전체. 시험용으로 테이블당 행 수 제한.</summary>
        public int MaxRowsPerTable { get; init; }

        // [NAIMS-ISSUE:USE-DECRYPTION] 복호화 불러오기 ON/OFF [찾기:복호토글]
        /// <summary>
        /// 이름 컬럼 복호화 사용 여부. 기본 true(현장 암호문 → CRYPTO 로 복호).
        /// false 로 두면 복호화를 아예 시도하지 않고 이름 컬럼을 <b>원문 그대로</b> 이관한다.
        /// (이미 복호화된 상태로 dump 된 소스처럼, CRYPTO 를 찾을 수 없거나 찾으면 안 되는 경우)
        /// </summary>
        public bool UseDecryption { get; init; } = true;

        // [NAIMS-ISSUE:CLASS-FILTER] 함정급 서브트리 부분 이관
        /// <summary>
        /// CLASS(함정급) 범위 필터. 비어 있으면 전체 이관.
        /// 설정 시: 선택 CLASS 서브트리(VESSEL·SIGNATURES·… ) + 참조 공유데이터(국가·무장·소나)만 이관.
        /// </summary>
        public IReadOnlyCollection<long> ClassFilter { get; init; } = Array.Empty<long>();

        // [NAIMS-ISSUE:FORCE-REIMPORT] 멱등 우회 강제 재삽입(UI 미노출)
        /// <summary>
        /// 강제 재삽입 대상 소스 테이블. 여기 든 테이블은 멱등(자연키 재사용·부모단위 스킵·복합키 dedup)을
        /// 무시하고 항상 INSERT 한다. 특정 서브트리(예: 협대역 시그니처·NB·링크)를 삭제 후 재구축할 때 쓴다.
        /// 부모(CLASSES/VESSEL_DATA)는 여기 두지 않아야 기존 부모를 재사용해 그 아래로 재삽입된다.
        /// </summary>
        public IReadOnlySet<string> ForceReimportTables { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // [NAIMS-ISSUE:REBUILD-VS-KEY] 전량 재구축 — 멱등을 '키 대조'가 아니라 '지우고 다시'로 푼다.
        //
        //   왜 필요한가. 재실행 중복 방지를 자연키 대조로만 풀면, 키가 없는 테이블(SIGNATURES 등)에서
        //   막힌다. 부모를 재사용 처리해도 그 타겟 RECORD_ID 를 되찾을 수 없어 자식이 통째로 빠진다
        //   (실측: HARMONICS 56 + SIG_SNIPPET 22 유실). 없는 키를 만들어 낼 수는 없다.
        //
        //   그런데 이 도구의 실제 쓰임은 "신규 구축 1회 전량 이관"이다. 그러면 답은 간단하다 —
        //   대상 테이블을 비우고 처음부터 넣으면 부모 INSERT 로 RECORD_ID 가 생기고 자식이 그대로 받는다.
        //   키가 아예 필요 없고, 결과가 항상 결정적이다. 부분 보충이 필요한 경우에만 멱등 모드를 쓴다.
        //
        //   지우는 범위는 <b>이관 계획에 있는 STD_* 타겟 테이블뿐</b>이다. 그 외는 건드리지 않는다.
        /// <summary>true 면 대상 타겟 테이블을 FK 역순으로 비운 뒤 전량 INSERT 한다(멱등 대조 없음).</summary>
        public bool RebuildTargets { get; init; }

        // [NAIMS-ISSUE:EMPTY-ON-MISSING] 참조 파일이 없어도 행을 넣을지(사용자 결정).
        //   켜면 blob 은 빈 byte[], 그 파일에서 파생되던 NOT NULL 컬럼은 0/기본값으로 적재한다.
        //   행이 사라지는 것보다 "행은 있고 자료만 빈" 상태가 관계 보존에 유리하다는 판단이다
        //   (자식·링크가 부모를 잃고 연쇄로 빠지는 것을 막는다).
        /// <summary>참조 파일 부재 시 그 행을 스킵하지 않고 빈 blob 으로 적재한다.</summary>
        public bool EmptyOnMissingFile { get; init; } = true;

        // [NAIMS-ISSUE:CHAR-PADDING] 이관 직후 자체 검증 — 정규화 누락 필드가 다시 생기는 것을 막는 장치.
        //   규칙을 코드에 적어 두는 것만으로는 부족하다. 소스 읽기 경로가 하나 늘거나(새 리더),
        //   문자열을 만들어 내는 규칙이 하나 늘면 정규화를 지나치지 않는 값이 생길 수 있고,
        //   그때 증상은 <b>화면 정렬이 미묘하게 틀어지는 것</b>뿐이라 몇 달 뒤에나 발견된다.
        //   그래서 이관이 끝나는 자리에서 기계가 직접 확인하고, 1건이라도 걸리면 커밋하지 않는다.
        /// <summary>이관 종료 직전 문자열 패딩 오염을 자체 검증한다(기본 켬). 1건이라도 걸리면 실패·롤백.</summary>
        public bool VerifyStringPadding { get; init; } = true;

        // [NAIMS-ISSUE:MANUAL-FK] 레거시에는 부모를 못 찾는 행이 있다 —
        //   FK 가 0("참조 없음" 센티넬)이거나, 부모가 지워졌는데 자식만 남은 경우다.
        //   실측: CHARACTERISTICS 8행이 VESSEL_ID=0 인데 42개 컬럼이 전부 채워져 있었다.
        //   버리면 실제 음향 특성 자료가 사라지고, "미상" 부모 행을 지어내면 타겟에 없던 데이터가 생긴다.
        //   둘 다 나쁘므로 세 번째 길을 둔다 — <b>사람이 이관 전에 붙일 부모를 직접 고른다.</b>
        //   기계가 알 수 없는 것을 아는 사람이 정하고, 그 결정을 파일로 남겨 재실행에도 이어지게 한다.
        /// <summary>수동 FK 지정: "소스테이블|소스PK|FK소스컬럼" → 타겟 부모 RECORD_ID (0 이면 '스킵' 확정).</summary>
        public IReadOnlyDictionary<string, long> ManualFkMap { get; init; } =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>수동 FK 지정표의 키를 만든다.</summary>
        // [찾기:충돌해결] [NAIMS-ISSUE:IDEMPOTENT-CONFLICT]
        /// <summary>
        /// 멱등 재실행에서 <b>타겟에 이미 부합하는 행이 있을 때</b> 무엇을 할지.
        ///
        /// 지금까지는 무조건 '유지'(재사용) 였다. 그건 안전하지만 <b>소스가 고쳐진 경우를 영영 못 반영</b>한다 —
        /// 레거시에서 값을 바로잡아도 재이관하면 옛 값이 그대로 남는다. 반대로 무조건 덮어쓰면
        /// 타겟에서 손본 내용이 조용히 지워진다. 어느 쪽도 도구가 정할 일이 아니다.
        /// 그래서 리허설에서 충돌을 <b>미리 드러내고</b>, 실제 이관 전에 사람이 정하게 한다.
        /// </summary>
        public sealed record ConflictDecision(string Action, IReadOnlyDictionary<string, string>? Values = null)
        {
            /// <summary>타겟을 그대로 둔다 — 기본. 지금까지의 동작과 같다.</summary>
            public const string Keep = "keep";
            /// <summary>소스 값으로 타겟을 갱신한다.</summary>
            public const string Overwrite = "overwrite";
            /// <summary>사람이 적어 넣은 값으로 갱신한다.</summary>
            public const string NewValue = "value";
        }

        /// <summary>"소스표|소스PK" → 결정. 비어 있으면 전부 '유지'(종전 동작).</summary>
        public IReadOnlyDictionary<string, ConflictDecision> ConflictDecisions { get; init; } =
            new Dictionary<string, ConflictDecision>(StringComparer.OrdinalIgnoreCase);

        public static string ConflictKey(string sourceTable, long sourcePk) => $"{sourceTable}|{sourcePk}";

        // [찾기:파일부재해결] [NAIMS-ISSUE:MISSING-FILE-DECISION]
        /// <summary>
        /// 참조 파일이 없을 때 <b>그 파일에 대해 사람이 내린 결정</b>. 키는 파일명(대소문자 무시).
        ///
        /// 왜 도구가 정하지 않나 — "파일이 없다" 의 뜻은 자료마다 다르다.
        /// 아직 안 받아온 파일이면 빈 값으로 싣고 나중에 채우는 게 맞고,
        /// 애초에 폐기된 자료면 그 행은 버리는 게 맞다. 둘을 도구가 구별할 방법은 없다.
        /// 그래서 <b>리허설 때 사람이 정하게</b> 하고, 정하지 않으면 종전대로 빈 값으로 싣는다.
        ///
        /// 왜 행이 아니라 파일 단위인가 — 사람은 "이 파일이 없다" 로 판단하지 "PK 137번 행" 으로
        /// 판단하지 않는다. 한 파일을 여러 행이 참조하면 결정도 함께 가는 것이 자연스럽다.
        /// </summary>
        public sealed record MissingFileDecision(string Action, string? SubstitutePath = null)
        {
            /// <summary>빈 값으로 적재 — 기본. 지정이 없을 때와 같다.</summary>
            public const string Empty = "empty";
            /// <summary>이 파일을 참조하는 행을 버린다.</summary>
            public const string Discard = "discard";
            /// <summary>사람이 지정한 다른 파일로 대신 싣는다.</summary>
            public const string Substitute = "file";
        }

        /// <summary>파일명 → 결정. 비어 있으면 종전과 똑같이 동작한다.</summary>
        public IReadOnlyDictionary<string, MissingFileDecision> MissingFileActions { get; init; } =
            new Dictionary<string, MissingFileDecision>(StringComparer.OrdinalIgnoreCase);

        public static string ManualFkKey(string sourceTable, long sourcePk, string fkSourceColumn)
            => $"{sourceTable}|{sourcePk}|{fkSourceColumn}";

        // [NAIMS-ISSUE:REBUILD-GUARD] 전량 재구축은 "지우고 넣는" 작업이라 대상을 잘못 고르면 끝이다.
        //   실제로 이 도구의 기본 타겟(NAIMS2)은 신규 솔루션 팀이 함께 쓰는 시험 DB 였고,
        //   재구축을 그대로 실행했으면 남의 시험 데이터를 통째로 날릴 뻔했다(2026-08-19).
        //   그래서 모드를 켜는 것만으로는 부족하고, 지울 DB 이름을 <b>직접 타이핑</b>해서
        //   실제 타겟과 글자 그대로 일치할 때만 삭제가 일어나게 한다.
        //   체크박스는 습관적으로 눌리지만, 이름을 타이핑하려면 대상을 한 번은 읽어야 한다.
        /// <summary>재구축을 허용할 데이터베이스 이름(사용자가 직접 입력). 실제 타겟과 다르면 삭제하지 않는다.</summary>
        public string RebuildConfirmDatabase { get; init; } = "";

        // 시스템 DB 는 어떤 경우에도 거부한다.
        /// <summary>재구축 금지 데이터베이스(대소문자 무시).</summary>
        public static readonly IReadOnlySet<string> ProtectedDatabases =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "master", "msdb", "model", "tempdb" };

        // [NAIMS-ISSUE:REBUILD-GUARD] 위험을 가르는 축은 'DB 이름' 이 아니라 '어느 서버인가' 다.
        //   내 PC 의 시험 DB 를 비우는 것과 현장/공용 서버를 비우는 것은 사고의 크기가 다르다.
        //   이름으로 막으면(예: NAIMS2 금지) 정작 로컬 시험까지 막히면서, 이름만 다른 원격 DB 는
        //   그대로 통과한다 — 보호하려던 것은 못 지키고 쓸 수 있는 것만 막는 셈이다.
        //   그래서 로컬 서버가 아니면 재구축 자체를 거부한다. 현장 이관은 다른 절차로 해야 한다.
        /// <summary>이 서버가 이 PC 자신인지(재구축 허용 조건).</summary>
        public static bool IsLocalServer(string conn)
        {
            var server = "";
            foreach (var part in (conn ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                if (k is "server" or "data source" or "addr" or "address" or "network address")
                { server = kv[1].Trim(); break; }
            }
            if (server.Length == 0) return false;
            // 인스턴스·포트 표기 제거: "localhost\SQLEXPRESS", "127.0.0.1,1433"
            var host = server.Split('\\')[0].Split(',')[0].Trim();
            if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return host is "." or "(local)" or "(localdb)"
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1" || host == "::1"
                || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>접속 문자열에서 데이터베이스 이름을 뽑는다. 못 찾으면 빈 문자열.</summary>
        public static string DatabaseOf(string conn)
        {
            foreach (var part in (conn ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                if (k is "database" or "initial catalog") return kv[1].Trim();
            }
            return "";
        }

        /// <summary>접속 문자열의 서버 표기(로그·안내 문구용).</summary>
        public static string ServerNameOf(string conn)
        {
            foreach (var part in (conn ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                if (k is "server" or "data source") return kv[1].Trim();
            }
            return "(미지정)";
        }

        /// <summary>재구축을 실행해도 되는지 판정한다. 막을 이유가 있으면 그 사유를 돌려준다(없으면 null).</summary>
        public string? RebuildBlockReason()
        {
            if (!RebuildTargets) return null;
            var db = DatabaseOf(TargetConnStr);
            if (db.Length == 0)
                return "타겟 접속 문자열에서 데이터베이스 이름을 읽을 수 없어 재구축을 거부합니다.";
            if (ProtectedDatabases.Contains(db))
                return $"[{db}] 는 시스템 데이터베이스라 전량 재구축을 거부합니다.";
            if (!IsLocalServer(TargetConnStr))
                return $"[{ServerNameOf(TargetConnStr)}] 는 이 PC 가 아닙니다 — 원격·공용 서버에서는 " +
                       "전량 재구축을 거부합니다. 로컬 이관 시험 DB 를 타겟으로 지정해 검증한 뒤, " +
                       "현장 적용은 멱등 보충 모드로 진행하세요.";
            if (!string.Equals(RebuildConfirmDatabase.Trim(), db, StringComparison.Ordinal))
                return $"재구축 확인 불일치 — 지울 DB 이름을 정확히 입력해야 합니다. " +
                       $"타겟=[{db}] / 입력=[{RebuildConfirmDatabase}]";
            return null;
        }

        // [NAIMS-ISSUE:SNIPPET-ROOTS] 스니펫·영상 파일 루트 = CLASSIFICATIONDB 단일 지정
        //   예전에는 1~3순위(CLASSIFICATIONDB → 단주기 → 협대역)로 폴백 탐색했으나,
        //   "어느 폴더에서 온 파일인지"가 흐려지고 원음 폴더를 오탐할 위험이 있어 단일 루트로 고정했다.
        //   종류별 하위 폴더(SNIPPET / IMAGE / CTRYIMAGE) 분기는 그대로 유지된다.
        /// <summary>스니펫·영상 파일 참조 루트(CLASSIFICATIONDB 한 곳).</summary>
        public string ClassificationRoot { get; init; } =
            @"\\192.168.111.165\naims_share\MSU\CLASSIFICATIONDB";

        /// <summary>파일 제공자·점검이 쓰는 루트 목록(단일 원소). 기존 호출부 형태를 유지하는 어댑터.</summary>
        public IReadOnlyList<string> SnippetRoots => new[] { ClassificationRoot };

        // [찾기:세션] 한 세션 안에서 DryRun→Trial→Commit 을 같은 옵션으로 돌리기 위한 복제.
        /// <summary>실행 방식만 바꾼 복제본을 만든다(나머지 옵션은 그대로).</summary>
        public DbToDbOptions CloneWith(RunMode mode) => new()
        {
            SourceKind = SourceKind,
            SourceMode = SourceMode,
            SourceConnStr = SourceConnStr,
            TargetConnStr = TargetConnStr,
            Mode = mode,
            ExcludeCategories = ExcludeCategories,
            MaxRowsPerTable = MaxRowsPerTable,
            UseDecryption = UseDecryption,
            ClassFilter = ClassFilter,
            ForceReimportTables = ForceReimportTables,
            RebuildTargets = RebuildTargets,
            RebuildConfirmDatabase = RebuildConfirmDatabase,
            EmptyOnMissingFile = EmptyOnMissingFile,
            ManualFkMap = ManualFkMap,
            MissingFileActions = MissingFileActions,
            ConflictDecisions = ConflictDecisions,
            ClassificationRoot = ClassificationRoot,
        };
    }

    /// <summary>테이블 한 건의 이관 결과.</summary>
    // [찾기:충돌해결]
    /// <summary>멱등 충돌 한 건. 어느 행이, 어느 컬럼이, 무엇에서 무엇으로 달라지는지.</summary>
    public sealed class ConflictRef
    {
        public string SourceTable { get; init; } = "";
        public string TargetTable { get; init; } = "";
        public long SourcePk { get; init; }
        public long TargetRecordId { get; init; }
        /// <summary>다른 컬럼들: (타겟 컬럼, 지금 타겟 값, 소스에서 오는 값).</summary>
        public List<(string Column, string TargetValue, string SourceValue)> Diffs { get; } = new();
    }

    public sealed class TableResult
    {
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";
        public int SourceRows { get; set; }
        public int Inserted { get; set; }
        public int Reused { get; set; }
        public int Skipped { get; set; }
        public bool Blocked { get; set; }
        public List<string> Errors { get; } = new();

        /// <summary>소스 PK 컬럼명 — 결과 창의 레코드 대조 조회에 쓴다.</summary>
        public string SourcePk { get; set; } = "";

        /// <summary>부모 INSERT 뒤 산출해 넣은 자식행 수(AI 스펙트로그램 등).</summary>
        public int DerivedChildren { get; set; }

        /// <summary>
        /// 이번 실행에서 타겟 행이 확정된 (소스 PK → 타겟 RECORD_ID) 짝. 이관 처리 순서 그대로다.
        /// 부모단위 스킵·링크 재사용처럼 타겟 RECORD_ID 를 모르는 행은 들어가지 않는다.
        /// </summary>
        public List<(long Src, long Tgt)> IdPairs { get; } = new();

        // [NAIMS-ISSUE:GATE-METRICS] 게이트 자동 채점을 위한 계측
        //   예전에는 스킵 사유가 문자열 메시지로만 남아, "무엇 때문에 몇 건이 빠졌는지"를
        //   기계가 판정할 수 없었다. 원버튼 파이프라인이 Commit 진행 여부를 스스로 판단하려면
        //   사유별 건수가 수치로 있어야 한다.
        /// <summary>스킵 사유 분류별 건수(FK-부모미이관 / 타입변환 / NOTNULL-값없음 …).</summary>
        public Dictionary<string, int> SkipReasons { get; } = new(StringComparer.Ordinal);

        /// <summary>이번 실행에서 "기존 행 재사용"으로 판정된 소스 PK 집합(신규/재사용 구분용).</summary>
        public HashSet<long> ReusedSourcePks { get; } = new();

        // [찾기:충돌해결]
        /// <summary>
        /// 멱등 충돌 — 타겟에 부합하는 행이 이미 있고 <b>값이 다른</b> 건.
        /// 값까지 같으면 정할 것이 없으므로 담지 않는다(정할 것만 사람에게 보인다).
        /// </summary>
        public List<ConflictRef> Conflicts { get; } = new();

        /// <summary>덮어쓰기·새 값으로 <b>실제 갱신한</b> 행 수.</summary>
        public int Overwritten { get; set; }

        /// <summary>부모단위 스킵에서 "타겟 자식이 소스보다 적음"(부분 누락 의심) 경고 건수.</summary>
        public int PartialSkipWarnings { get; set; }

        /// <summary>타겟에 없어서 비활성화된 dedup 키 컬럼 수(재실행 중복 위험 지표).</summary>
        public int DedupDisabledColumns { get; set; }

        /// <summary>키 값이 비어 dedup 대조를 못한 컬럼 경고 건수(재실행 중복 위험 지표).</summary>
        public int DedupUnusableWarnings { get; set; }

        /// <summary>시각 단위 변환 결과가 "하루 범위"를 벗어난 건수(소스 단위 오해 의심).</summary>
        public int TimeOfDayRangeWarnings { get; set; }

        /// <summary>참조 파일이 없어 빈 blob 으로 적재한 값의 수(행이 아니라 파일 단위).</summary>
        public int EmptyFileRows { get; set; }

        // [NAIMS-ISSUE:EMPTY-ON-MISSING] "빈 값으로 넣었다" 는 총계만으로는 나중에 못 찾는다.
        //   파일을 확보해 다시 채울 때 어느 행이 대상인지 특정할 수 있어야 하므로 소스 PK 까지 남긴다.
        //   이게 없으면 DATALENGTH(blob)=0 으로 DB 를 뒤지는 수밖에 없다.
        /// <summary>참조 파일이 없어 빈 blob 으로 적재한 행 목록(소스 PK, 종류, 파일명).</summary>
        public List<(long SourcePk, string Kind, string FileName)> MissingFileRefs { get; } = new();

        // [NAIMS-ISSUE:MANUAL-FK] 사람이 부모를 골라 줘야 하는 행 목록.
        /// <summary>부모를 못 찾아 스킵된 행(소스 PK, FK 컬럼, 부모 소스테이블, 소스 FK 값, 사유).</summary>
        public List<(long SourcePk, string FkColumn, string ParentTable, long RawFk, string Reason)> UnresolvedFks { get; } = new();

        /// <summary>스킵 사유 1건을 분류에 누적한다.</summary>
        public void AddSkipReason(string category)
        {
            var k = string.IsNullOrEmpty(category) ? "기타" : category;
            SkipReasons[k] = SkipReasons.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        /// <summary>사유 분류를 "분류=건수" 형태로 이어 붙인다(로그·보고서용).</summary>
        public string SkipBreakdown =>
            SkipReasons.Count == 0 ? "" :
            string.Join(", ", SkipReasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));

        public string Summary =>
            Blocked ? $"{Source} → {Target}: 차단됨"
                    : $"{Source} → {Target}: 소스 {SourceRows} / 적재 {Inserted} / 재사용 {Reused} / 건너뜀 {Skipped}"
                      + (DerivedChildren > 0 ? $" / 산출자식 {DerivedChildren}" : "")
                      + (Errors.Count > 0 ? $" / 오류 {Errors.Count}" : "")
                      + (SkipReasons.Count > 0 ? $"   [{SkipBreakdown}]" : "");
    }

    public sealed class MigrationReport
    {
        public List<TableResult> Tables { get; } = new();
        public bool DryRun { get; init; }
        public int TotalInserted => Tables.Sum(t => t.Inserted);
        public int TotalReused => Tables.Sum(t => t.Reused);
        public int TotalSkipped => Tables.Sum(t => t.Skipped);
        public int TotalErrors => Tables.Sum(t => t.Errors.Count);
    }

    /// <summary>
    /// 구 NAIMS(MsSQL/Oracle) → 신규 NAIMS2(MsSQL) DB→DB 이관 실행기.
    ///
    /// 규칙은 전부 MigrationRules(mapping_rules.json, SSOT) 에서 온다.
    ///   · PK → RECORD_ID 는 IDENTITY 자동 채번(INSERT 목록에서 제외), 새 값을 IdRemapper 에 기록
    ///   · FK 는 IdRemapper 로 부모의 새 RECORD_ID 로 치환
    ///   · DELETED 형 변환, COMMENT 말머리, 고정 기본값, REF_TARGET_TYPE, 단위 변환, 복호화
    ///   · 스니펫 blob 은 파일 로드 + 그램 변환으로 채운다
    /// 배포관리·변경이력 분류는 제외한다(옵션).
    /// </summary>
    public sealed class DbToDbExecutor
    {
        private readonly DbToDbOptions _opt;
        private readonly Action<string> _log;
        private readonly CryptoContext _crypto = new();
        private readonly IdRemapper _ids = new();
        private TargetSchema _tgt = new();
        private SnippetContentProvider? _snips;
        private Dictionary<string, HashSet<long>>? _classScope;   // CLASS 범위 필터(테이블→허용 소스PK). null=전체
        private SqlConnection? _tcn;          // 타겟 연결 (실행 전체에서 공유)
        private SqlTransaction? _tx;          // Trial·Commit 트랜잭션

        /// <summary>실행기 생성. <paramref name="log"/> 는 진행 로그 콜백(없으면 무시).</summary>
        /// <param name="opt">소스/타겟 연결, 실행 방식, 제외 분류, CLASS 필터 등 이관 옵션.</param>
        /// <param name="log">진행 상황 로그 싱크. null 이면 로그를 버린다.</param>
        public DbToDbExecutor(DbToDbOptions opt, Action<string>? log = null)
        {
            _opt = opt;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// 이관 전체를 실행한다(비동기). 대상 테이블 선정 → 복호화 능력 확인 → 타겟 스키마 로드 →
        /// 스니펫 제공자 준비 → 타겟 연결/트랜잭션 개시 → FK 위상 순서로 테이블별 이관 → 종료 처리.
        /// 부작용: Trial·Commit 은 실제 INSERT 를 수행하며, 하나의 트랜잭션으로 묶어 자식 FK 가
        /// 부모의 신규 RECORD_ID 를 이어받게 한다. Commit 은 커밋, Trial 은 롤백(경로만 검증), DryRun 은 미실행.
        /// IdRemapper(_ids) 에 소스PK→신규 RECORD_ID 매핑을 누적하며, 예외 시 안전하게 롤백한다.
        /// </summary>
        /// <param name="ct">취소 토큰. 테이블·행 단위로 취소를 확인한다.</param>
        /// <returns>테이블별 적재/재사용/건너뜀/오류 집계를 담은 이관 리포트.</returns>
        public async Task<MigrationReport> RunAsync(CancellationToken ct = default)
        {
            var report = new MigrationReport { DryRun = _opt.IsDryRun };
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string modeLabel = _opt.Mode switch
            {
                RunMode.DryRun => "DryRun(계획만)",
                RunMode.Trial => "Trial(트랜잭션 후 롤백)",
                _ => "Commit(실제 적재)",
            };
            _log($"실행 방식: {modeLabel}");
            // [NAIMS-ISSUE:DRYRUN-IDEMPOTENCY] DryRun 은 타겟 대조가 꺼져 있어 집계가 실전과 다르다 — 침묵하지 않는다.
            if (_opt.IsDryRun)
                _log("  ! DryRun 은 멱등 대조(부모단위 스킵·복합키/링크 dedup)가 꺼져 있어 " +
                     "적재/재사용/건너뜀 집계가 Trial·Commit 실전과 다를 수 있습니다.");
            _log($"소스: {DbProvider.DisplayName(_opt.SourceKind)} — {MaskConn(_opt.SourceConnStr)}");
            _log($"타겟: MsSQL/NAIMS2 — {MaskConn(_opt.TargetConnStr)}");

            // 1) 대상 테이블 선정: 분류 제외 + 이관제외 반영 + FK 위상 정렬
            var plans = SelectPlans();
            _log($"이관 대상 {plans.Count}개 테이블 (제외 분류: {string.Join(", ", _opt.ExcludeCategories)})");

            // CLASS(함정급) 범위 필터: 선택 CLASS 서브트리 + 참조 공유데이터만
            if (_opt.ClassFilter.Count > 0)
            {
                _classScope = await new ClassScopeResolver(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr)
                    .ComputeAsync(_opt.ClassFilter, ct);
                _log($"CLASS 범위 필터: {_opt.ClassFilter.Count}개 CLASS 선택 → 대상 {_classScope.Count}개 테이블(서브트리+참조), 그 외 제외");
            }

            // 2) 복호화 능력 확인 (복호화 불러오기 OFF 면 시도조차 안 하고 원문 그대로 이관) [찾기:복호토글]
            _crypto.DecryptionEnabled = _opt.UseDecryption;
            var mode = await _crypto.ProbeAsync(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, ct);
            _log(_opt.UseDecryption
                ? $"복호화: {mode} — {_crypto.Diagnosis}"
                : "복호화: 비활성(불러오기 OFF) — 이름 컬럼을 원문 그대로 이관합니다.");

            // 3) 타겟 스키마 로드
            // 산출 자식표(STD_AI_SPECTROGRAM 등)의 스키마도 함께 읽는다.
            // [NAIMS-ISSUE:DERIVED-CHILD-SCHEMA] 계획 테이블만 읽으면 자식표 컬럼을 몰라
            //   "타겟 스키마 없음" 으로 자식행을 통째로 건너뛴다 — 표는 DB 에 멀쩡히 있는데도.
            //   실측 2026-08-19: STD_AI_SPECTROGRAM 이 존재하는데 AI 스펙트로그램이 하나도 안 생겼다.
            //   메시지가 "없다" 고 말해서 DB 를 의심하게 만들었다 — 원인은 <읽지 않은 것> 이었다.
            var schemaTables = plans.Select(p => p.TargetTable)
                .Concat(plans.Where(p => p.DerivedChild != null).Select(p => p.DerivedChild!.TargetTable))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            _tgt = await TargetSchema.LoadAsync(_opt.TargetConnStr, schemaTables, ct);

            // 4) 스니펫 파일 제공자
            _snips = new SnippetContentProvider(_opt.SnippetRoots, _log, _opt.EmptyOnMissingFile,
                                                manual: _opt.MissingFileActions);

            // 5) 타겟 연결·트랜잭션 (Trial·Commit 은 단일 트랜잭션으로 묶어 FK 신규 ID 가 이어지게 함)
            _tcn = new SqlConnection(_opt.TargetConnStr);
            await _tcn.OpenAsync(ct);
            if (_opt.Writes)
                _tx = (SqlTransaction)await _tcn.BeginTransactionAsync(ct);

            bool committed = false;
            try
            {
                // 5-B) 전량 재구축이면 대상 타겟을 FK 역순(자식 → 부모)으로 비운다.
                //      같은 트랜잭션 안이라 Trial 이면 롤백되고, DryRun 이면 애초에 실행되지 않는다.
                if (_opt.RebuildTargets && _opt.Writes)
                    await ClearTargetsAsync(plans, ct);

                // 6) 테이블별 이관
                foreach (var plan in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    var res = new TableResult { Source = plan.SourceTable, Target = plan.TargetTable };

                    // CLASS 범위 필터: 스코프에 없는 테이블은 이관 대상에서 제외
                    if (_classScope != null && !_classScope.ContainsKey(plan.SourceTable))
                    {
                        _log($"  · {plan.SourceTable}: CLASS 범위 밖 — 건너뜀");
                        continue;
                    }

                    try
                    {
                        if (NeedsDecryption(plan) && !_crypto.CanDecrypt)
                        {
                            res.Blocked = true;
                            res.Errors.Add($"암호화 컬럼 존재 — 복호화 불가({mode}). 스킵.");
                            _log("  ! " + res.Summary);
                            report.Tables.Add(res);
                            continue;
                        }
                        await MigrateTableAsync(plan, res, ct);
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add(ExtractRaw.ExceptionText.Describe(ex));
                    }
                    _log("  " + res.Summary);
                    LogErrorDetail(res);
                    report.Tables.Add(res);
                }

                // 6-B) 자체 검증 — 문자열 패딩 오염. 커밋 <b>전에</b>, 같은 트랜잭션 안에서 본다.
                //      커밋한 뒤에 확인하면 이미 오염이 남은 것이고, 그때는 백필 말고 되돌릴 방법이 없다.
                if (_opt.Writes && _opt.VerifyStringPadding)
                    await VerifyNoStringPaddingAsync(plans, ct);

                // 7) 종료 처리
                if (_opt.Mode == RunMode.Commit)
                {
                    await _tx!.CommitAsync(ct);
                    committed = true;
                    _log("★ 커밋 완료 — 실제 적재됨");
                }
                else if (_opt.Mode == RunMode.Trial)
                {
                    await _tx!.RollbackAsync(ct);
                    _log("↩ 롤백 — 실 INSERT 경로 검증 완료 (영구 반영 없음)");
                }
            }
            catch
            {
                if (_tx != null && !committed)
                    try { await _tx.RollbackAsync(ct); } catch { }
                throw;
            }
            finally
            {
                _tx?.Dispose(); _tx = null;
                if (_tcn != null) { await _tcn.DisposeAsync(); _tcn = null; }
            }

            _log($"완료 — 적재 {report.TotalInserted} / 재사용 {report.TotalReused} / 건너뜀 {report.TotalSkipped} / 오류 {report.TotalErrors}  [{modeLabel}]");

            // 종합 보고서 파일 (접속정보·테이블별 집계·소스PK→타겟RECORD_ID 매핑·오류)
            try { var p = WriteReportFile(report, modeLabel, stamp); _log($"[보고서] {p}"); }
            catch (Exception ex) { _log("[보고서] 기록 실패 — " + ex.Message); }

            return report;
        }

        /// <summary>커넥션 문자열에서 비밀번호만 가린다(보고서·로그 노출 방지).</summary>
        private static string MaskConn(string conn)
        {
            if (string.IsNullOrEmpty(conn)) return "";
            return System.Text.RegularExpressions.Regex.Replace(
                conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
        }

        /// <summary>
        /// 이관 종합 보고서를 파일로 남긴다. exe 옆 logs\dbtodb_report_&lt;stamp&gt;.txt.
        ///   ① 접속 정보(비번 마스킹)·실행방식·복호화 규칙
        ///   ② 테이블별 소스/적재/재사용/건너뜀/오류 집계
        ///   ③ 소스 PK → 타겟 RECORD_ID 매핑 (테이블별)
        ///   ④ 오류 표본
        /// </summary>
        private string WriteReportFile(MigrationReport report, string modeLabel, string stamp)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"dbtodb_report_{stamp}.txt");

            var sb = new StringBuilder();
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  DB → DB 이관 종합 보고서");
            sb.AppendLine($"  생성: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   실행방식: {modeLabel}");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("[접속 정보]");
            sb.AppendLine($"  소스 : {DbProvider.DisplayName(_opt.SourceKind)}  {MaskConn(_opt.SourceConnStr)}");
            sb.AppendLine($"  타겟 : MsSQL/NAIMS2  {MaskConn(_opt.TargetConnStr)}");
            sb.AppendLine($"  복호화 규칙 : {_crypto.RuleDescription()}");
            if (_opt.ClassFilter.Count > 0)
                sb.AppendLine($"  CLASS 필터 : {string.Join(", ", _opt.ClassFilter)}");
            sb.AppendLine(_opt.EmptyOnMissingFile
                ? "  파일 부재   : 빈 blob 으로 적재 (행을 살림)"
                : "  파일 부재   : 해당 행 스킵");
            sb.AppendLine(_opt.RebuildTargets
                ? "  적재 방식   : 전량 재구축 (대상 타겟을 비우고 처음부터 — 멱등 대조 없음)"
                : "  적재 방식   : 멱등 보충 (기존 행은 재사용하고 없는 것만 추가)");
            if (_opt.ForceReimportTables.Count > 0)
                sb.AppendLine($"  강제 재이관 : {string.Join(", ", _opt.ForceReimportTables)}");
            sb.AppendLine();

            sb.AppendLine("[테이블별 집계]  소스 / 적재 / 재사용 / 건너뜀 / 오류");
            foreach (var t in report.Tables)
                sb.AppendLine($"  {t.Source,-22} → {t.Target,-28} {t.SourceRows,6} /{t.Inserted,6} /{t.Reused,6} /{t.Skipped,6} /{t.Errors.Count,5}");
            sb.AppendLine($"  {"합계",-22}   {"",-28} {"",6} /{report.TotalInserted,6} /{report.TotalReused,6} /{report.TotalSkipped,6} /{report.TotalErrors,5}");
            sb.AppendLine();

            sb.AppendLine("[소스 PK → 타겟 RECORD_ID 매핑]  (재사용/신규 모두 포함)");
            string? curTable = null;
            foreach (var (table, srcId, tgtId) in _ids.Entries())
            {
                if (table != curTable) { sb.AppendLine($"  ── {table} ──"); curTable = table; }
                sb.AppendLine($"     {srcId,12} → {tgtId,12}");
            }
            sb.AppendLine();

            var withErr = report.Tables.Where(t => t.Errors.Count > 0).ToList();
            if (withErr.Count > 0)
            {
                sb.AppendLine("[오류 표본]");
                foreach (var t in withErr)
                {
                    sb.AppendLine($"  ── {t.Source} ({t.Errors.Count}건) ──");
                    foreach (var e in t.Errors) sb.AppendLine($"     {e}");
                }
            }

            System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            return path;
        }

        // ── 대상 선정 ──────────────────────────────────────────────────
        /// <summary>
        /// 이관 대상 테이블 플랜 목록을 만든다. JSON 분류(mapping_rules.json)에서 제외 분류에 속한
        /// 소스 테이블을 빼고, 남은 플랜을 FK 의존성 위상 순서(부모 먼저)로 정렬해 돌려준다.
        /// </summary>
        /// <returns>FK 위상 정렬된 테이블 플랜 목록(INSERT 실행 순서).</returns>
        private List<TablePlan> SelectPlans()
        {
            // JSON 분류로 제외 대상 소스 테이블 집합
            var excludedByCat = MigrationRules.Model.Plan
                .Where(p => _opt.ExcludeCategories.Contains(p.Category))
                .Select(p => p.Source)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var all = MigrationPlan.Defaults()
                .Where(p => !excludedByCat.Contains(p.SourceTable))
                .ToList();
            return MigrationPlan.BuildOrder(all);
        }

        /// <summary>소스 테이블명의 JSON 분류(Category)를 찾아 반환. 없으면 빈 문자열.</summary>
        /// <param name="src">소스 테이블명.</param>
        /// <returns>매핑 규칙상의 분류명, 미등록이면 "".</returns>
        private string Category(string src) =>
            MigrationRules.Model.Plan.FirstOrDefault(p =>
                p.Source.Equals(src, StringComparison.OrdinalIgnoreCase))?.Category ?? "";

        // ── 테이블 이관 ────────────────────────────────────────────────
        /// <summary>
        /// 오류 상세를 사유별로 묶어 남긴다.
        /// 같은 원인이 수십 행에서 반복되므로 행 번호를 떼고 묶어야 실제 원인이 드러난다.
        /// </summary>
        private void LogErrorDetail(TableResult res)
        {
            if (res.Errors.Count == 0) return;

            var groups = res.Errors
                .Select(StripRowPrefix)
                .GroupBy(m => m, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .ToList();

            foreach (var g in groups)
            {
                var msg = g.Key.Length > 300 ? g.Key[..300] + "…" : g.Key;
                _log($"      ↳ ({g.Count()}건) {msg}");
            }

            // 수집은 테이블당 20건에서 멈춘다 — 건수가 실제보다 적을 수 있음을 알린다.
            if (res.Errors.Count >= ErrorSampleLimit)
                _log($"      ↳ (오류 표본 {ErrorSampleLimit}건에서 수집 중단 — 실제 건너뜀 {res.Skipped}건 기준으로 판단)");
        }

        /// <summary>테이블당 보관하는 오류 표본 상한. 원인 파악에는 이 정도면 충분하다.</summary>
        private const int ErrorSampleLimit = 20;

        /// <summary>"행 PK=123 스킵: 사유" → "사유". 사유별 집계를 위해 행 식별자를 뗀다.</summary>
        private static string StripRowPrefix(string m)
        {
            if (!m.StartsWith("행 PK=", StringComparison.Ordinal)) return m;
            int i = m.IndexOf(": ", StringComparison.Ordinal);
            return i >= 0 ? m[(i + 2)..] : m;
        }

        /// <summary>
        /// 이 테이블이 현재 소스 종류에서 복호화가 필요한지.
        /// plan.NeedsDecryption 은 MsSQL 기준 고정이라 Oracle 소스에서는 틀린다
        /// (Oracle 은 ACTIVE_SONAR/TRANSIENT 가 평문, CLASSES/VESSEL_DATA 만 RAW 암호화).
        /// </summary>
        private bool NeedsDecryption(TablePlan plan) =>
            _opt.UseDecryption &&   // [찾기:복호토글] OFF 면 원문 그대로라 복호화 필요 없음(차단 안 함)
            CryptoContext.RequiresDecryption(_opt.SourceKind, plan.SourceTable);

        /// <summary>
        /// 테이블 한 개를 이관한다(비동기). 소스/타겟 컬럼 로드 → 플랜의 PK·FK 컬럼 존재 사전 검증 →
        /// 컬럼 매퍼로 소스→타겟 대응 → (정책 시) 자연키 재사용 선점 → 복호화 SELECT 로 소스 읽기 →
        /// CLASS 범위 필터 적용 → 행별로 멱등성 검사 후 INSERT 를 수행한다.
        /// 부작용: DryRun 은 음수 가상 RECORD_ID 를, Trial·Commit 은 실제 신규 RECORD_ID 를
        /// IdRemapper(_ids) 에 기록해 이후 자식 FK 치환이 이어지게 한다. res 에 집계/오류를 누적한다.
        /// 멱등성: ① 부모가 재사용(기존)이면 자식도 스킵(재사용 표시), ② 복합 자연키로 타겟에 이미 있으면 재사용.
        /// </summary>
        /// <param name="plan">이 테이블의 이관 규칙(소스·타겟·PK·FK·재사용/중복키 정책).</param>
        /// <param name="res">이 테이블의 결과 집계 객체(소스행/적재/재사용/건너뜀/오류를 갱신).</param>
        /// <param name="ct">취소 토큰(행 단위로 확인).</param>
        private async Task MigrateTableAsync(TablePlan plan, TableResult res, CancellationToken ct)
        {
            var kind = _opt.SourceKind;
            _currentResult = res;
            res.SourcePk = plan.SourcePk;
            var dedupKeys = ValidateDedupKeys(plan, res);
            var srcCols = await LoadSourceColumnsAsync(plan.SourceTable, ct);
            if (srcCols.Count == 0)
            {
                res.Errors.Add("소스 컬럼을 읽지 못함");
                return;
            }

            var tgtCols = _tgt.Columns(plan.TargetTable);
            if (tgtCols.Count == 0)
            {
                res.Errors.Add($"타겟 {plan.TargetTable} 스키마 없음");
                return;
            }

            // 플랜의 소스 PK/FK 컬럼명이 실제 소스 스키마에 존재하는지 사전 검증.
            // 없으면 row[컬럼]이 조용히 null → 0-sentinel 로 잘못 채워지므로, 삽입 전에 명확히 막는다.
            var srcNames = new HashSet<string>(srcCols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            if (!srcNames.Contains(plan.SourcePk))
            {
                res.Errors.Add($"플랜 오류: 소스 PK 컬럼 '{plan.SourcePk}' 가 {plan.SourceTable} 에 없음 " +
                               $"(실제: {string.Join(", ", srcCols.Take(12).Select(c => c.Name))}…)");
                return;
            }
            foreach (var fk in plan.ForeignKeys)
            {
                if (!srcNames.Contains(fk.SourceColumn))
                {
                    res.Errors.Add($"플랜 오류: FK 소스 컬럼 '{fk.SourceColumn}'(→{fk.ParentSourceTable}) 가 " +
                                   $"{plan.SourceTable} 에 없음 — 매핑 계획 수정 필요");
                    return;
                }
            }

            // 컬럼 매퍼로 소스→타겟 대응 (동일/별칭/정규화 등)
            var tgtAsInfo = tgtCols.Select((c, i) => new ColumnInfo
            { Name = c.Name, DataType = c.DataType, MaxLength = c.MaxLength, Ordinal = i }).ToList();
            var mappings = ColumnMapper.Build(srcCols, tgtAsInfo, plan.SourceTable);
            // 타겟컬럼 → 소스컬럼 (실매핑된 것만)
            var tgtToSrc = mappings
                .Where(m => m.Status is MappingStatus.Exact or MappingStatus.Renamed
                            && !string.IsNullOrEmpty(m.SourceName) && !string.IsNullOrEmpty(m.TargetName))
                .GroupBy(m => m.TargetName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SourceName, StringComparer.OrdinalIgnoreCase);

            // 재사용 정책: 소스(pk, 자연키) ↔ 타겟(자연키) 대조로 기존 RECORD_ID 선점
            if (plan.Reuse == ReusePolicy.ReuseByNaturalKey
                && !string.IsNullOrEmpty(plan.SourceKeyColumn) && !string.IsNullOrEmpty(plan.TargetKeyColumn))
            {
                await SeedReuseAsync(plan, srcCols, ct);
            }

            // 소스 읽기 (복호화 SELECT)
            await using var scn = DbProvider.CreateConnection(kind, _opt.SourceMode, _opt.SourceConnStr);
            await scn.OpenAsync(ct);
            if (NeedsDecryption(plan)) await _crypto.OpenKeyAsync(scn, ct);

            var rows = await ReadSourceRowsAsync(scn, kind, plan.SourceTable, srcCols, ct);
            if (NeedsDecryption(plan)) await _crypto.CloseKeyAsync(scn, ct);
            res.SourceRows = rows.Count;

            // CLASS 범위 필터: 허용 소스 PK 만 남긴다 (서브트리+참조 밖 행 제거)
            if (_classScope != null && _classScope.TryGetValue(plan.SourceTable, out var allowedPks))
            {
                int before = rows.Count;
                rows = rows.Where(r => allowedPks.Contains(ToLong(r.GetValueOrDefault(plan.SourcePk)))).ToList();
                res.SourceRows = rows.Count;
                if (before != rows.Count) _log($"    CLASS 필터: {before} → {rows.Count}행");
            }

            // 숨김(삭제 예정) 컬럼도, 현재 스키마에 NOT NULL 로 남아 있으면 채워야 INSERT 가 된다.
            // (nullable/기본값 있는 숨김 컬럼은 TryResolve 에서 생략된다)
            var insertable = tgtCols.Where(c => c.IsInsertable).ToList();

            // 부모 단위 멱등: 부모별 "타겟 기존 자식 수 / 소스 자식 수" 를 실행 전에 한 번 읽는다.
            //   (WasReused 가정 대신 실제 타겟 상태로 판정 — 자식을 지우고 재이관하면 다시 채워진다.)
            // [NAIMS-ISSUE:PARTIAL-SKIP-LOSS] 부분 실패 자식 영구 누락 방지
            //   · 복합키 dedup(②)이 있는 테이블은 ②가 행 단위라 누락 없이 멱등이 유지되므로
            //     부모 단위 스킵(①)을 아예 쓰지 않는다. ①은 "부모에 자식이 1건이라도 있으면
            //     전부 이관됨"으로 뭉뚱그려 판정해, 일부 행이 오류로 빠진 채 커밋되면
            //     재실행에서 그 행들이 영구 누락된다.
            //   · dedup 키가 없는 테이블은 부모별 자식 수를 소스와 비교해, 타겟이 부족하면
            //     (행 식별 불가 → 스킵은 유지하되) 경고를 남겨 침묵을 없앤다.
            Dictionary<long, ChildStat>? tgtChildCount = null;   // 타겟 부모 id → 기존 자식 수
            Dictionary<long, int>? srcChildCount = null;    // 소스 부모 FK 값 → 소스 자식 수
            var partialWarned = new HashSet<long>();        // 부분 누락 경고를 남긴 부모(중복 경고 방지)
            ForeignKeyLink? subtreeFk = null;
            if (plan.SubtreeParentColumn.Length > 0 && dedupKeys.Count == 0 && _opt.Writes)
            {
                subtreeFk = plan.ForeignKeys.FirstOrDefault(f =>
                    f.SourceColumn.Equals(plan.SubtreeParentColumn, StringComparison.OrdinalIgnoreCase));
                if (subtreeFk != null)
                {
                    tgtChildCount = await LoadParentChildCountsAsync(plan.TargetTable, subtreeFk.TargetColumn, ct);
                    srcChildCount = rows
                        .GroupBy(r => ToLong(r.GetValueOrDefault(subtreeFk.SourceColumn)))
                        .ToDictionary(g => g.Key, g => g.Count());
                }
            }

            var unresolvedParentWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (_opt.MaxRowsPerTable > 0 && (res.Inserted + res.Reused + res.Skipped) >= _opt.MaxRowsPerTable)
                    break;

                long srcPk = ToLong(row.GetValueOrDefault(plan.SourcePk));

                // 강제 재삽입 대상이면 멱등 검사를 모두 건너뛰고 항상 INSERT (삭제 후 재구축용).
                // 재구축 모드에서는 타겟을 이미 비웠으므로 멱등 대조가 무의미하다(그리고 위험하다 —
                // 같은 실행에서 방금 넣은 형제를 '기존'으로 오인할 수 있다). 전 테이블을 force 로 둔다.
                bool force = _opt.RebuildTargets || _opt.ForceReimportTables.Contains(plan.SourceTable);

                // 재사용: 이미 매핑돼 있으면 INSERT 생략
                if (!force && plan.Reuse == ReusePolicy.ReuseByNaturalKey && _ids.TryGet(plan.SourceTable, srcPk, out var reusedId))
                {
                    if (reusedId > 0) res.IdPairs.Add((srcPk, reusedId));
                    res.ReusedSourcePks.Add(srcPk);
                    res.Reused++;
                    continue;
                }

                // 멱등 ①(부모 단위): 타겟에 이미 이 부모의 자식이 있으면(=이미 이관됨) 스킵.
                //   실행 전 스냅샷(tgtChildCount)으로 판정하므로, 같은 실행에서 새로 넣는 형제는 영향 없음.
                //   자식을 지우고 재이관하면 부모 자식이 0 → 다시 채워진다. 손자도 이어서 스킵되게 표시.
                //   [NAIMS-ISSUE:PARTIAL-SKIP-LOSS] 타겟 자식 수가 소스보다 적으면 부분 누락이다.
                //   행 식별 키가 없어 어느 행이 빠졌는지 특정할 수 없으므로 스킵은 유지하되,
                //   침묵하지 않도록 부모당 1회 경고를 남겨 ForceReimport 재구축을 판단하게 한다.
                if (!force && subtreeFk != null && tgtChildCount != null)
                {
                    var parentSrcFk = row.GetValueOrDefault(subtreeFk.SourceColumn);
                    var parentTid = _ids.Translate(subtreeFk.ParentSourceTable, parentSrcFk);
                    if (parentTid is { } ptid && tgtChildCount.TryGetValue(ptid, out var stat) && stat.Count > 0)
                    {
                        long have = stat.Count;
                        long srcParentKey = ToLong(parentSrcFk);
                        int want = srcChildCount != null && srcChildCount.TryGetValue(srcParentKey, out var w) ? w : 0;
                        if (have < want && partialWarned.Add(ptid))
                        {
                            var msg = $"부모 {subtreeFk.ParentSourceTable}#{srcParentKey}: 타겟 자식 {have}건 / 소스 {want}건 " +
                                      "— 부분 누락 의심. 해당 서브트리를 지우고 ForceReimport 로 재구축 필요.";
                            res.PartialSkipWarnings++;
                            if (res.Errors.Count < ErrorSampleLimit) res.Errors.Add(msg);
                            _log("      ! " + msg);
                        }

                        // [NAIMS-ISSUE:REUSED-PARENT-NO-ID] 이 경로는 "이미 있으니 넘어간다" 만 하고
                        //   타겟 RECORD_ID 를 남기지 않았다. 그러면 이 행을 부모로 삼는 자식이 재실행에서
                        //   전부 FK 미해결로 빠진다(실측: HARMONICS 56건, SIG_SNIPPET 22건 전량 유실).
                        //   부모 밑에 소스도 타겟도 자식이 정확히 1건이면 그 짝은 추정이 아니라 확정이므로
                        //   여기서 ID 를 되찾아 등록한다. 2건 이상이면 어느 것이 어느 것인지 알 수 없어
                        //   등록하지 않고, 자식 쪽에서 'FK-부모ID미상' 으로 정확히 드러낸다.
                        long? recovered = (have == 1 && want == 1 && stat.OnlyId > 0) ? stat.OnlyId : null;
                        if (recovered is { } rid)
                        {
                            _ids.MarkReused(plan.SourceTable, srcPk, rid);
                            res.IdPairs.Add((srcPk, rid));
                        }
                        else
                        {
                            _ids.MarkReused(plan.SourceTable, srcPk);
                            if (plan.DedupKeyColumns.Count == 0 && string.IsNullOrEmpty(plan.TargetKeyColumn)
                                && unresolvedParentWarned.Add(plan.SourceTable))
                            {
                                var msg = $"{plan.SourceTable}: 부모단위 스킵으로 재사용했으나 식별 키가 없어 " +
                                          "타겟 RECORD_ID 를 되찾을 수 없습니다 — 이 테이블을 부모로 삼는 자식은 " +
                                          "이번 실행에서 이관되지 않습니다(FK-부모ID미상). " +
                                          "MigrationPlan 에 DedupKeyColumns 를 지정하거나 ForceReimport 로 재구축하세요.";
                                res.Errors.Add(msg);
                                _log("      ! " + msg);
                            }
                        }
                        res.ReusedSourcePks.Add(srcPk);
                        res.Reused++;
                        continue;
                    }
                }

                // 멱등 ②(복합 자연키): (부모 FK + 키 컬럼)로 타겟에 이미 있으면 재사용.
                if (!force && dedupKeys.Count > 0 && !_opt.IsDryRun)
                {
                    var existingId = await FindExistingByCompositeKeyAsync(plan, dedupKeys, tgtToSrc, row, srcPk, ct);
                    if (existingId is { } eid)
                    {
                        // [찾기:충돌해결] 타겟에 부합하는 행이 이미 있다. 사람이 정해 둔 것이 있으면 그대로 한다.
                        var decision = ConflictFor(plan.SourceTable, srcPk);
                        if (eid > 0 && decision != null && decision.Action != DbToDbOptions.ConflictDecision.Keep)
                        {
                            if (await OverwriteAsync(plan, insertable, tgtToSrc, row, srcPk, eid, decision, res, ct))
                            {
                                _ids.MarkReused(plan.SourceTable, srcPk, eid);
                                res.IdPairs.Add((srcPk, eid));
                                res.Overwritten++;
                                continue;
                            }
                        }
                        else if (eid > 0)
                        {
                            // 정해 둔 것이 없으면 종전대로 <유지>하되, 무엇이 달라지는지는 남긴다.
                            // 그래야 리허설을 보고 실제 이관 전에 정할 수 있다.
                            await NoteConflictAsync(plan, insertable, tgtToSrc, row, srcPk, eid, res, ct);
                        }

                        _ids.MarkReused(plan.SourceTable, srcPk, eid);
                        if (eid > 0) res.IdPairs.Add((srcPk, eid));
                        res.ReusedSourcePks.Add(srcPk);
                        res.Reused++;
                        continue;
                    }
                }

                // 멱등 ③(링크 튜플): 링크 테이블은 모든 FK 값 조합으로 대조 — 관계는 항상 채운다.
                //   같은 FK 조합이 이미 있으면 스킵, 없으면 아래 INSERT 로 관계 생성.
                if (!force && plan.DedupByForeignKeys && !_opt.IsDryRun)
                {
                    if (await LinkExistsAsync(plan, row, ct))
                    {
                        _ids.MarkReused(plan.SourceTable, srcPk);
                        res.ReusedSourcePks.Add(srcPk);
                        res.Reused++;
                        continue;
                    }
                }

                try
                {
                    var (cols, vals) = BuildInsert(plan, insertable, tgtToSrc, row, srcPk);
                    if (cols.Count == 0)
                    {
                        res.Skipped++;
                        continue;
                    }

                    // 값 산출이 끝난 지금이 "이 행이 어떤 파일을 못 찾았는지" 를 아는 유일한 시점이다.
                    if (_snips is { } sp && sp.CurrentMissing.Count > 0)
                        foreach (var (missKind, missFile) in sp.CurrentMissing)
                            res.MissingFileRefs.Add((srcPk, missKind, missFile));

                    if (_opt.IsDryRun)
                    {
                        // 계획만: 새 RECORD_ID 를 음수 가상값으로 배정해 자식 FK 치환이 이어지게 함
                        _ids.Add(plan.SourceTable, srcPk, -(res.Inserted + 1));
                        res.Inserted++;
                    }
                    else
                    {
                        // Trial·Commit: 공유 연결·트랜잭션으로 실제 INSERT (Trial 은 마지막에 롤백)
                        long newId = await ExecInsertAsync(_tcn!, _tx, plan.TargetTable, cols, vals, ct);
                        _ids.Add(plan.SourceTable, srcPk, newId);
                        if (newId > 0) res.IdPairs.Add((srcPk, newId));
                        res.Inserted++;

                        // [NAIMS-ISSUE:DERIVED-CHILD] 소스에 없어 산출해야 하는 자식행(AI 스펙트로그램)
                        await InsertDerivedChildAsync(plan, row, newId, res, ct);
                    }
                }
                catch (RowSkip rs)
                {
                    res.Skipped++;
                    res.AddSkipReason(rs.Category);   // [NAIMS-ISSUE:GATE-METRICS] 게이트 채점 근거
                    if (res.Errors.Count < ErrorSampleLimit) res.Errors.Add($"행 PK={srcPk} 스킵: {rs.Message}");
                }
                catch (Exception ex)
                {
                    res.Skipped++;
                    // [NAIMS-ISSUE:MISSING-FILE] 참조 파일 부재는 '예외' 로 뭉뚱그리면 안 된다.
                    //   원인이 코드가 아니라 자료이고, 조치도 다르다(파일을 구해 오는 것).
                    //   게이트가 이 둘을 같은 무게로 다루면 파일 몇 건 때문에 이관 전체가 막힌다.
                    // [찾기:파일부재해결] 사람이 '버림' 으로 정한 것은 사고가 아니라 결정이다 —
                    //   파일부재(고칠 것)·예외(도구 결함)와 같은 칸에 세면 판단이 흐려진다.
                    res.AddSkipReason(
                        ex is SnippetContentProvider.ManualFileDiscard ? SkipKind.ManualDiscard
                        : ex is System.IO.IOException ? SkipKind.MissingFile : SkipKind.Exception);
                    if (res.Errors.Count < ErrorSampleLimit) res.Errors.Add($"행 PK={srcPk}: {ExtractRaw.ExceptionText.Describe(ex)}");
                }
            }
        }

        /// <summary>dedup 키 값이 비어 대조가 불가능했음을 테이블·컬럼당 1회만 알린다.</summary>
        private readonly HashSet<string> _dedupWarned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>현재 이관 중인 테이블의 결과 객체(경고 카운터 누적용). 테이블마다 갱신된다.</summary>
        private TableResult? _currentResult;

        private const long UsPerDay = 86_400_000_000L;
        private const long MsPerDay = 86_400_000L;
        private readonly HashSet<string> _todWarned = new(StringComparer.Ordinal);

        // [NAIMS-ISSUE:TIME-OF-DAY-UNIT] A_START_MICROSEC 은 "자정 이후" 마이크로초다(Unix 시각 아님).
        //   정본 근거: AIATUSW GramForPreciseSSEventSpectroGramViewModel.cs:924
        //     n64StartMicroSec = Hour*3600000000 + Minute*60000000 + Second*1000000 + Millisecond*1000
        //   전시는 new DateTime(µs * 10) 으로 되읽으므로, Unix 초(예: 1204703084)를 ×1e6 하면
        //   서기 39년쯤으로 찍히고 시간축이 통째로 깨진다. 그런데 INSERT 는 멀쩡히 성공한다 —
        //   bigint 에 다 들어가기 때문. 그래서 게이트도 못 잡는다. 여기서 범위로 잡아 드러낸다.
        // [NAIMS-ISSUE:TIME-SENTINEL] 레거시는 "값 없음" 을 int.MaxValue(2,147,483,647) 로 적었다.
        //   실측: AI_SNIPPET 3건 중 1건이 그 값이고, 나머지는 67684·71199(자정 이후 초)로 정상이다.
        //   그대로 ×1e6 하면 2.1e15µs 가 들어가 전시 시간축이 통째로 깨진다. 컬럼이 NOT NULL 이라
        //   NULL 을 넣을 수도 없으므로 0(자정)으로 정규화하고 건수를 남긴다 — 조용히 버리지 않는다.
        private const long LegacyUnsetSeconds = int.MaxValue;
        private readonly HashSet<string> _sentinelWarned = new(StringComparer.Ordinal);

        /// <summary>레거시 '값 없음' 센티넬을 0 으로 정규화한다.</summary>
        private long NormalizeTimeOfDay(TablePlan plan, string column, long seconds)
        {
            if (seconds != LegacyUnsetSeconds && seconds >= 0) return seconds;
            if (_currentResult != null) _currentResult.TimeOfDayRangeWarnings++;
            if (_sentinelWarned.Add($"{plan.TargetTable}.{column}"))
                _log($"    ! {plan.TargetTable}.{column}: 소스 값이 {seconds:N0} — 레거시 '값 없음' 센티넬로 보고 " +
                     "0(자정)으로 적재합니다. 원래 시각은 소스에 없습니다.");
            return 0;
        }

        /// <summary>변환된 시각 값이 하루 범위를 넘으면 경고한다(소스 단위 오해 의심). 값 자체는 바꾸지 않는다.</summary>
        private void CheckTimeOfDay(TablePlan plan, string column, object? value, long perDay, string unit)
        {
            if (value is not long v || v < 0 || v <= perDay) return;
            if (_currentResult != null) _currentResult.TimeOfDayRangeWarnings++;
            if (!_todWarned.Add($"{plan.TargetTable}.{column}")) return;
            _log($"    ! {plan.TargetTable}.{column}: 변환값 {v:N0}{unit} 이 하루({perDay:N0}{unit})를 넘습니다. " +
                 "이 컬럼은 '자정 이후' 시각 규약이라, 소스가 Unix 초였다면 시간축이 깨집니다 — " +
                 "소스 컬럼 단위를 확인하세요.");
        }

        // [찾기:고아해결] 해결 창이 쓸 목록. 실제로 스킵되는 순간에만 남긴다 —
        //   0 센티넬이 정상 처리되는 행(nullable·0 허용)까지 담으면 목록이 결정할 것 없는 행으로 뒤덮인다
        //   (실측: PULSES 의 빈 세그먼트 슬롯 148건이 그렇게 올라왔다).
        private void NoteUnresolved(long srcPk, ForeignKeyLink fk, long rawId, string reason)
        {
            if (_currentResult == null) return;
            if (_currentResult.UnresolvedFks.Any(u => u.SourcePk == srcPk && u.FkColumn == fk.SourceColumn)) return;
            _currentResult.UnresolvedFks.Add((srcPk, fk.SourceColumn, fk.ParentSourceTable, rawId, reason));
        }

        private void WarnDedupUnusable(TablePlan plan, string keyCol, bool mapped)
        {
            if (_currentResult != null) _currentResult.DedupUnusableWarnings++;
            if (!_dedupWarned.Add($"{plan.SourceTable}.{keyCol}")) return;
            _log($"    ! {plan.SourceTable}: dedup 키 '{keyCol}' 값이 비어 있어 중복 대조를 건너뜁니다 " +
                 (mapped ? "(소스 값이 NULL 또는 복호화 실패)." : "(소스에 대응 컬럼 없음).") +
                 " 해당 행은 INSERT 됩니다 — 재실행 시 중복이 생길 수 있습니다.");
        }

        // [NAIMS-ISSUE:DEDUP-KEY-VALIDATE] 계획의 dedup 키를 실제 타겟 스키마와 대조
        /// <summary>
        /// 계획에 적힌 DedupKeyColumns 중 실제 타겟 테이블에 존재하는 것만 남긴다.
        ///
        /// 키 컬럼명은 현장 스키마 실측 없이 확정할 수 없는 것이 있어, 일부만 맞으면
        /// "부분 키"로 과도하게 재사용(서로 다른 행을 같은 행으로 오인)할 위험이 있다.
        /// 그래서 하나라도 어긋나면 그 테이블의 복합키 멱등을 통째로 끄고,
        /// 대신 "재실행 시 중복 위험"을 결과·로그에 분명히 남긴다(침묵 금지).
        /// </summary>
        private IReadOnlyList<string> ValidateDedupKeys(TablePlan plan, TableResult res)
        {
            if (plan.DedupKeyColumns.Count == 0) return Array.Empty<string>();

            var have = _tgt.Columns(plan.TargetTable)
                           .Select(c => c.Name)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = plan.DedupKeyColumns.Where(k => !have.Contains(k)).ToList();
            if (missing.Count == 0) return plan.DedupKeyColumns;

            var msg = $"dedup 키 컬럼 없음({string.Join(", ", missing)}) → {plan.TargetTable} 복합키 멱등 비활성. " +
                      "⚠ 재실행 시 중복 INSERT 위험 — 타겟 스키마 확인 후 MigrationPlan 의 DedupKeyColumns 를 교정하세요.";
            res.DedupDisabledColumns += missing.Count;
            res.Errors.Add(msg);
            _log("    ! " + msg);
            return Array.Empty<string>();
        }

        /// <summary>
        /// 복합 자연키(부모 FK + DedupKeyColumns)로 타겟에 이미 있는 행의 RECORD_ID 를 찾는다.
        /// 공유 타겟 연결·트랜잭션으로 조회하므로, 같은 실행에서 앞서 넣은 행도 대조된다.
        ///
        /// 부모 FK 는 SubtreeParentColumn(CLASS 계보)을 우선 쓰되, 지정이 없으면 첫 번째 필수 FK를
        /// 쓴다 — PULSES→ACTIVE_SONAR 처럼 CLASS 계보가 아닌 부모를 가진 테이블도 복합키 멱등을
        /// 쓸 수 있게 하기 위함이다. FK 가 아예 없는 테이블(WEAPON 등)은 키 컬럼만으로 대조한다.
        /// </summary>
        private async Task<long?> FindExistingByCompositeKeyAsync(
            TablePlan plan, IReadOnlyList<string> dedupKeys, IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, object?> row, long srcPk, CancellationToken ct)
        {
            var pfk = plan.SubtreeParentColumn.Length > 0
                ? plan.ForeignKeys.FirstOrDefault(f =>
                      f.SourceColumn.Equals(plan.SubtreeParentColumn, StringComparison.OrdinalIgnoreCase))
                : plan.ForeignKeys.FirstOrDefault(f => !f.Optional) ?? plan.ForeignKeys.FirstOrDefault();

            long? parentId = null;
            if (pfk != null)
            {
                parentId = _ids.Translate(pfk.ParentSourceTable, row.GetValueOrDefault(pfk.SourceColumn));
                if (parentId == null) return null;   // 부모를 못 찾으면 대조 불가 (아래 INSERT 단계에서 처리)
            }

            var where = new List<string>();
            var pars = new List<(string Name, object Value)>();
            int pn = 0;
            void AddEq(string col, object? val)
            {
                if (val == null || val is DBNull) { where.Add($"[{col}] IS NULL"); return; }
                var nm = "@k" + pn++;
                where.Add($"[{col}] = {nm}");
                pars.Add((nm, val));
            }

            if (pfk != null && parentId is { } pid) AddEq(pfk.TargetColumn, pid);

            // [NAIMS-ISSUE:DEDUP-NULL-COLLAPSE] 키 값이 하나라도 비어 있으면 대조하지 않는다.
            //   예전에는 NULL 을 그대로 조건에 넣어 "[키] IS NULL" 로 조회했는데,
            //   그러면 키가 빈 행들끼리 서로 같은 행으로 잡혀 N 건이 1 건으로 붕괴됐다
            //   (TRANSIENT.TR_NAME 복호 실패 → 클래스당 1건만 적재 → 그 자식 TR_FRAME 도 1벌만 남는 증상).
            //   대조 불가일 때는 재사용하지 않고 INSERT 로 보낸다(중복이 누락보다 낫고, 경고로 드러난다).
            foreach (var keyCol in dedupKeys)
            {
                object? val = tgtToSrc.TryGetValue(keyCol, out var srcCol)
                    ? _crypto.ResolveValue(plan.SourceTable, srcCol, row.GetValueOrDefault(srcCol), srcPk)
                    : null;
                val = LegacyText.NormalizeValue(val);   // [NAIMS-ISSUE:CHAR-PADDING] 대조 규칙도 LegacyText 한 곳에서만
                if (val is null or DBNull || (val is string es && es.Length == 0))
                {
                    WarnDedupUnusable(plan, keyCol, tgtToSrc.ContainsKey(keyCol));
                    return null;
                }
                AddEq(keyCol, val);
            }

            await using var cmd = _tcn!.CreateCommand();
            if (_tx != null) cmd.Transaction = _tx;
            cmd.CommandText = $"SELECT TOP 1 RECORD_ID FROM [{plan.TargetTable}] WHERE {string.Join(" AND ", where)}";
            foreach (var (nm, v) in pars)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = nm; p.Value = v;
                cmd.Parameters.Add(p);
            }
            var r = await cmd.ExecuteScalarAsync(ct);
            return r == null || r == DBNull.Value ? null : Convert.ToInt64(r);
        }

        /// <summary>
        /// 타겟에서 부모별 "이미 있는 자식 수"를 읽는다(부모 단위 멱등 판정용).
        /// 실행 전 상태 스냅샷이라, 같은 실행에서 새로 넣는 형제 행에는 영향을 주지 않는다.
        /// [NAIMS-ISSUE:PARTIAL-SKIP-LOSS] 예전에는 "자식이 있는 부모" 집합만 봐서
        /// 일부 자식이 실패한 채 커밋되면 재실행에서 영구 누락됐다. 이제 개수를 세어
        /// 소스보다 부족하면 호출부가 경고를 남긴다.
        /// 조회 실패는 삼키지 않는다 — 빈 결과로 진행하면 무통보 전량 중복이 되기 때문.
        /// </summary>
        /// <summary>부모별 타겟 자식 수와, 자식이 딱 1건일 때 그 자식의 RECORD_ID.</summary>
        private readonly record struct ChildStat(long Count, long OnlyId);

        // [NAIMS-ISSUE:REBUILD-VS-KEY]
        /// <summary>
        /// 이관 대상 타겟 테이블을 FK 역순(자식 → 부모)으로 비운다.
        ///
        /// 안전 원칙 — 계획에 있는 테이블만, 역순으로, 건수를 남기고, 실패는 삼키지 않는다.
        ///   · 계획 밖 테이블은 절대 건드리지 않는다(스키마 전체 TRUNCATE 같은 짓을 하지 않는다).
        ///   · FK 위반으로 지워지지 않으면 그대로 예외를 올린다 — 계획 밖 테이블(PREC_* 등)이
        ///     이 행들을 참조하고 있다는 뜻이고, 그건 사람이 판단할 일이지 도구가 밀어붙일 일이 아니다.
        ///   · TRUNCATE 가 아니라 DELETE 를 쓴다. TRUNCATE 는 FK 가 걸린 테이블에서 실패하고,
        ///     무엇보다 트랜잭션 롤백으로 되돌리는 Trial 리허설과 어울리지 않는다.
        /// </summary>
        private async Task ClearTargetsAsync(IReadOnlyList<TablePlan> plans, CancellationToken ct)
        {
            // [NAIMS-ISSUE:REBUILD-GUARD] 최종 방어선. UI 를 우회해 옵션이 만들어졌더라도 여기서 막힌다.
            if (_opt.RebuildBlockReason() is { } why)
                throw new InvalidOperationException("전량 재구축 거부 — " + why);

            _log($"── 전량 재구축: [{DbToDbOptions.DatabaseOf(_opt.TargetConnStr)}] 대상 타겟 비우기 (FK 역순) ──");
            long total = 0;
            // plans 는 부모 → 자식 순(위상 정렬)이므로 역순이 곧 삭제 순서다.
            // [NAIMS-ISSUE:DERIVED-CHILD-CLEAR] 산출 자식표(STD_AI_SPECTROGRAM 등)도 함께 비운다.
            //   자식표는 '계획' 이 아니라 부모 이관의 부산물이라 plans 에 없다. 그래서 재구축이
            //   제 손으로 만든 행에 제가 막혔다 — 실측: FK_STD_AI_SPECTROGRAM_SNIPPET 위반.
            //   게다가 오류 문구는 "계획 밖의 테이블(PREC_*)이 참조 중일 수 있다" 고 안내해
            //   사용자를 엉뚱한 데로 보냈다. 자식은 <부모보다 먼저> 지운다.
            async Task<int> DeleteAsync(string table)
            {
                await using var cmd = _tcn!.CreateCommand();
                if (_tx != null) cmd.Transaction = _tx;
                cmd.CommandText = $"DELETE FROM [{table}]";
                cmd.CommandTimeout = 300;
                return await cmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var plan in plans.Reverse())
            {
                ct.ThrowIfCancellationRequested();
                if (_classScope != null && !_classScope.ContainsKey(plan.SourceTable)) continue;

                // 이 계획이 만들어 내는 자식표를 먼저 → 그 다음 본 표
                var order = plan.DerivedChild is { } dc
                    ? new[] { dc.TargetTable, plan.TargetTable }
                    : new[] { plan.TargetTable };

                foreach (var table in order)
                {
                    int n;
                    try { n = await DeleteAsync(table); }
                    catch (SqlException ex)
                    {
                        _log($"  !! {table} 비우기 실패 — {ex.Message.Split('\n')[0]}");
                        throw new InvalidOperationException(
                            $"전량 재구축 중 [{table}] 을 비우지 못했습니다. " +
                            "이관 계획 밖의 테이블(PREC_* 등)이 이 행들을 참조하고 있을 수 있습니다. " +
                            "오류 본문의 제약 이름과 테이블을 보고 그 참조를 먼저 정리하거나, " +
                            "재구축 대신 멱등 모드로 진행하세요.", ex);
                    }
                    if (n > 0) { _log($"  · {table}: {n:N0}행 삭제"); total += n; }
                }
            }
            _log($"── 비우기 완료: 총 {total:N0}행 ──");
        }

        private async Task<Dictionary<long, ChildStat>> LoadParentChildCountsAsync(
            string targetTable, string parentFkColumn, CancellationToken ct)
        {
            var map = new Dictionary<long, ChildStat>();
            await using var cmd = _tcn!.CreateCommand();
            if (_tx != null) cmd.Transaction = _tx;
            // MIN(RECORD_ID) 는 COUNT=1 일 때만 의미가 있다(그 한 건이 곧 그 행).
            cmd.CommandText =
                $"SELECT [{parentFkColumn}], COUNT(*), MIN(RECORD_ID) FROM [{targetTable}] " +
                $"WHERE [{parentFkColumn}] IS NOT NULL GROUP BY [{parentFkColumn}]";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0))
                    map[Convert.ToInt64(r.GetValue(0))] = new ChildStat(
                        Convert.ToInt64(r.GetValue(1)),
                        r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2)));
            return map;
        }

        /// <summary>
        /// 링크(매핑) 테이블에 같은 FK 조합의 행이 이미 있는지 확인한다.
        /// 부모가 하나라도 재매핑 안 되면(이번에 안 옮겨진 부모) false 를 돌려주고,
        /// INSERT 단계에서 FK 미해결로 스킵되게 둔다.
        /// </summary>
        private async Task<bool> LinkExistsAsync(
            TablePlan plan, IReadOnlyDictionary<string, object?> row, CancellationToken ct)
        {
            var where = new List<string>();
            var pars = new List<(string Name, object Value)>();
            int pn = 0;
            foreach (var fk in plan.ForeignKeys)
            {
                var val = _ids.Translate(fk.ParentSourceTable, row.GetValueOrDefault(fk.SourceColumn));
                if (val == null) return false;   // 부모 미해결 → 대조 불가
                var nm = "@f" + pn++;
                where.Add($"[{fk.TargetColumn}] = {nm}");
                pars.Add((nm, val.Value));
            }
            if (where.Count == 0) return false;

            await using var cmd = _tcn!.CreateCommand();
            if (_tx != null) cmd.Transaction = _tx;
            cmd.CommandText = $"SELECT TOP 1 1 FROM [{plan.TargetTable}] WHERE {string.Join(" AND ", where)}";
            foreach (var (nm, v) in pars)
            {
                var p = cmd.CreateParameter(); p.ParameterName = nm; p.Value = v; cmd.Parameters.Add(p);
            }
            var r = await cmd.ExecuteScalarAsync(ct);
            return r != null && r != DBNull.Value;
        }

        // ── INSERT 한 행 구성 ──────────────────────────────────────────
        /// <summary>
        /// 소스 행 하나를 타겟 INSERT 의 (컬럼목록, 값목록)으로 구성한다.
        /// 삽입 가능 타겟 컬럼마다 <see cref="TryResolve"/> 로 값을 결정하고, false 인 컬럼은
        /// 생략(DB 기본값 사용)한다. NOT NULL 인데 값이 없으면 <see cref="RowSkip"/> 로 이 행을 스킵한다.
        /// </summary>
        /// <param name="plan">이 테이블 이관 규칙.</param>
        /// <param name="insertable">IDENTITY 등을 제외한 삽입 가능 타겟 컬럼 목록.</param>
        /// <param name="tgtToSrc">타겟 컬럼명 → 소스 컬럼명 매핑(실매핑된 것만).</param>
        /// <param name="row">소스 행(컬럼명→값).</param>
        /// <param name="srcPk">소스 PK 값(오류 메시지·값 해석에 사용).</param>
        /// <returns>INSERT 에 넣을 컬럼명 목록과 대응 값 목록. 빈 목록이면 넣을 것이 없음.</returns>
        private (List<string> cols, List<object?> vals) BuildInsert(
            TablePlan plan, IReadOnlyList<TargetColumn> insertable,
            IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, object?> row, long srcPk)
        {
            var cols = new List<string>();
            var vals = new List<object?>();
            var fkByTarget = plan.ForeignKeys.ToDictionary(f => f.TargetColumn, StringComparer.OrdinalIgnoreCase);

            foreach (var tc in insertable)
            {
                var name = tc.Name;
                bool has = TryResolve(plan, tc, tgtToSrc, fkByTarget, row, srcPk, out var value);
                if (!has) continue;                    // 생략(DB 기본값 사용)

                // [NAIMS-ISSUE:DBNULL-TYPE-INFER] "값 없음" 은 DBNull 로 <보내지 않고> 컬럼을 뺀다.
                //   컬럼을 빼는 것과 NULL 을 넣는 것은 결과가 같지만, 형식 추론 문제가 사라진다:
                //   AddWithValue(DBNull.Value) 는 SqlDbType 을 NVarChar 로 추정하는데,
                //   대상이 varbinary 면 "nvarchar 를 varbinary 로 암시적 변환할 수 없습니다" 로 터진다.
                //   실측 2026-08-19: 국기 없는 국가 1건(COUNTRY_ID=100000001)이 이 이유로 스킵됐다.
                //   값이 있을 때만 실어 보내면, 컬럼 형식을 몰라도 늘 옳다.
                if (value is DBNull) value = null;
                if (value == null)
                {
                    if (tc.IsNullable || tc.HasDefault) continue;
                    throw new RowSkip($"{name} 은 NOT NULL 인데 값이 없음", SkipKind.NotNull);
                }

                cols.Add(name);
                vals.Add(value);
            }
            return (cols, vals);
        }

        // [NAIMS-ISSUE:INSERT-VALUE-RESOLVE] 컬럼값 결정 우선순위·미선언 FK 방어
        /// <summary>타겟 컬럼 하나의 값을 규칙 우선순위로 결정한다. false = INSERT 목록에서 생략.</summary>
        private bool TryResolve(
            TablePlan plan, TargetColumn tc,
            IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, ForeignKeyLink> fkByTarget,
            IReadOnlyDictionary<string, object?> row, long srcPk, out object? value)
        {
            value = null;
            var T = plan.TargetTable;
            var name = tc.Name;

            // 0) 삭제 예정(숨김) 컬럼: nullable 이면 생략, NOT NULL 이면 placeholder 로 채운다.
            if (MigrationRules.IsHidden(T, name))
            {
                if (tc.IsNullable || tc.HasDefault) return false;
                value = ZeroFor(tc);
                return true;
            }

            // 1) 외래키 → 부모 새 RECORD_ID
            if (fkByTarget.TryGetValue(name, out var fk))
            {
                var raw = row.GetValueOrDefault(fk.SourceColumn);
                long rawId = ToLong(raw);
                var t = _ids.Translate(fk.ParentSourceTable, raw);

                // [NAIMS-ISSUE:MANUAL-FK] 사람이 미리 정해 둔 부모가 있으면 그것이 최우선이다.
                //   기계가 못 푸는 것을 사람이 이미 판단해 준 값이므로, 자동 판정보다 앞선다.
                //   값이 0 이면 "이 행은 버린다" 는 명시적 결정 — 조용한 누락과 구분해 사유를 남긴다.
                if (_opt.ManualFkMap.Count > 0 &&
                    _opt.ManualFkMap.TryGetValue(
                        DbToDbOptions.ManualFkKey(plan.SourceTable, srcPk, fk.SourceColumn), out var manual))
                {
                    if (manual <= 0)
                        throw new RowSkip(
                            $"{fk.SourceColumn}→{fk.ParentSourceTable}: 사용자가 '버림'으로 지정한 행",
                            SkipKind.ManualDiscard);

                    // [NAIMS-ISSUE:MANUAL-FK] 저장된 값은 타겟 RECORD_ID 가 아니라 <b>소스 부모 PK</b> 다.
                    //   타겟 RECORD_ID 는 IDENTITY 라 전량 재구축할 때마다 새로 매겨진다 —
                    //   그걸 저장하면 다음 재구축에서 엉뚱한 부모를 가리킨다(조용히, 그리고 확실하게).
                    //   소스 PK 는 변하지 않으므로 일반 FK 와 똑같이 IdRemapper 로 치환한다.
                    var mapped = _ids.Translate(fk.ParentSourceTable, manual);
                    if (mapped is { } mid) { value = mid; return true; }
                    throw new RowSkip(
                        $"{fk.SourceColumn}→{fk.ParentSourceTable}: 지정한 부모({manual})가 이번 실행에 이관되지 않았습니다",
                        SkipKind.FkParentMissing);
                }

                if (t == null)
                {

                    // 소스 값이 0/빈값 = 레거시의 "참조 없음" sentinel.
                    if (rawId <= 0)
                    {
                        // FK 제약이 없거나, 부모에 0번 "참조 없음" 센티넬 행이 있으면 0 을 그대로 둔다.
                        if (tc.AllowsZeroReference) { value = 0L; return true; }
                        // 부모에 0번 행이 없으면 0 은 FK 위반이다.
                        if (tc.IsNullable) { value = DBNull.Value; return true; }
                        NoteUnresolved(srcPk, fk, rawId, "참조 없음(0)");
                        throw new RowSkip(
                            $"{fk.SourceColumn}=0(참조 없음)인데 {name} 은 NOT NULL 이고 " +
                            $"{tc.FkParentTable} 에 0번 행이 없음 — 넣을 수 있는 값이 없음",
                            SkipKind.FkZeroSentinel);
                    }

                    // 부모를 못 찾음. NULL 을 허용하는 선택적 FK 만 비워 두고 넘어간다.
                    if (fk.Optional && tc.IsNullable) { value = DBNull.Value; return true; }

                    // [NAIMS-ISSUE:REUSED-PARENT-NO-ID] 부모가 이번 실행에서 '재사용' 으로 판정됐다면
                    //   부모는 타겟에 분명히 있다 — 우리가 그 RECORD_ID 를 모를 뿐이다. 소스 고아 FK 와
                    //   구분해 사유·문구를 달리한다(조치가 다르므로).
                    bool parentReused = _ids.WasReused(fk.ParentSourceTable, rawId);
                    NoteUnresolved(srcPk, fk, rawId, parentReused ? "부모 ID 미상" : "부모 없음");
                    var kind = parentReused ? SkipKind.FkParentIdUnknown : SkipKind.FkParentMissing;
                    var why = parentReused
                        ? $"FK {fk.SourceColumn}→{fk.ParentSourceTable} 부모는 타겟에 있으나 RECORD_ID 를 알 수 없음 " +
                          $"(부모가 '부모단위 스킵' 으로 재사용됐고 식별 키가 없음)"
                        : $"FK {fk.SourceColumn}→{fk.ParentSourceTable} 부모 미이관";
                    if (fk.Optional) why += $" (선택적이지만 {name} 은 NOT NULL)";
                    throw new RowSkip(why, kind);
                }
                value = t.Value;
                return true;
            }

            // 2) 타겟 컬럼별 규칙 (COLUMN_RULES)
            var cr = MigrationRules.ColumnRule(T, name);
            if (cr != null)
                return ResolveColumnRule(plan, tc, cr, tgtToSrc, row, srcPk, out value);

            // 3) 고정 기본값 (HAS_CLUTTER=0 등)
            var fd = MigrationRules.FixedDefault(T, name);
            if (fd != null) { value = ConvertScalar(fd, tc); return true; }

            // 4) STD_CLASSES 신규 컬럼 임시 -1
            if (T.Equals("STD_CLASSES", StringComparison.OrdinalIgnoreCase)
                && !tgtToSrc.ContainsKey(name)
                && MigrationRules.IsClassesFillColumn(name)
                && IsNumeric(tc) && !IsSystemColumn(name))
            {
                value = -1;
                return true;
            }

            // 안전장치: 선언되지 않은 FK(REF_*_ID)를 원본 값으로 복사하면 안 된다(재매핑 누락).
            //   plan.ForeignKeys 에 없으면 데이터 오염을 막기 위해 행을 스킵/생략한다.
            if (IsRefIdColumn(name))
            {
                if (tc.IsNullable || tc.HasDefault) return false;
                throw new RowSkip($"{name} 은 미선언 FK — MigrationPlan.ForeignKeys 에 추가 필요", SkipKind.FkUndeclared);
            }

            // 5) 일반 매핑 (소스 컬럼 → 타겟)
            if (tgtToSrc.TryGetValue(name, out var srcCol))
            {
                var raw = row.GetValueOrDefault(srcCol);
                raw = _crypto.ResolveValue(plan.SourceTable, srcCol, raw, srcPk);
                value = TransformMapped(plan, name, raw, tc);

                // [NAIMS-ISSUE:EMPTY-ON-MISSING] 소스가 비었는데 타겟이 NOT NULL 인 파일명 컬럼은
                //   제공자에게 넘긴다. 제공자는 "없으면 빈 문자열" 이라는 같은 정책을 이미 쓴다.
                //   실측 2026-08-19: 짝이 없는 AI 스니펫 3건(ai_53137632 등 — grm 만 있고 wav 없음)이
                //   AUDIO_FILE_NAME NOT NULL 위반으로 전부 스킵됐다. Oracle 은 '' 를 NULL 로 저장하므로
                //   "wav 가 없다" 는 사실이 그대로 NULL 이 되어 올라온 것이다.
                //   blob 은 빈 배열로 싣기로 해 놓고 그 짝인 파일명만 NOT NULL 로 막히면,
                //   자료가 없는 행은 <통째로> 사라진다 — 없는 것을 없는 대로 싣자는 정책과 어긋난다.
                if (value is null or DBNull && !tc.IsNullable && !tc.HasDefault
                    && _snips is { } fp && fp.Handles(T, name)
                    && fp.Resolve(plan, tc, row, out var filled))
                {
                    value = filled;
                }
                return true;
            }

            // 6) 타겟 전용 관례값
            switch (name.ToUpperInvariant())
            {
                case "IS_LEGACY":     value = 1; return true;
                case "IS_TRAINING":   value = 0; return true;
                case "DELETED":       value = 0; return true;
                case "CHNG_DATETIME": value = DateTime.Now; return true;
                case "ROWGUID":       return false;               // DB 기본값 newid()
            }

            // [NAIMS-ISSUE:FLAG-FILE] 6.5) 파일에서 오는 blob 은 규칙표 등재 여부와 무관하게 제공자가 맡는다.
            //   국기(STD_COUNTRY_CODES.FLAG_FILE)는 COLUMN_RULES 에 항목이 없고 nullable 이라,
            //   바로 아래 "nullable → 생략" 에 걸려 INSERT 목록에서 통째로 빠져 있었다.
            //   오류도 경고도 없이 전부 NULL 이 됐다 — 실측 2026-08-19: 8행 전부 NULL,
            //   CTRYIMAGE 에는 KR/US/JP/RU/KP/SC/Antigua 7개 파일이 멀쩡히 있었는데도.
            //
            //   어느 컬럼이 파일에서 오는지는 제공자가 Handles 로 이미 알고 있다. 그 지식을
            //   규칙표 등재 여부에 종속시키면, 표에 한 줄 빠뜨리는 것만으로 자료가 <b>조용히</b>
            //   사라진다. 규칙표는 '변환 방법'을 적는 곳이지 '이 컬럼이 존재하는가'를 정하는 곳이 아니다.
            if (_snips is { } fileProvider && fileProvider.Handles(T, name))
                return fileProvider.Resolve(plan, tc, row, out value);

            // 7) NOT NULL 인데 규칙이 없으면 행 스킵 대상
            if (!tc.IsNullable && !tc.HasDefault)
                throw new RowSkip($"{name} 값 결정 규칙 없음 (NOT NULL)", SkipKind.NoRule);
            return false;   // nullable/기본값 → 생략
        }

        /// <summary>COLUMN_RULES 한 건을 값으로 해석.</summary>
        private bool ResolveColumnRule(
            TablePlan plan, TargetColumn tc, RuleColumn cr,
            IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, object?> row, long srcPk, out object? value)
        {
            value = null;
            var name = tc.Name;
            var rule = cr.Rule;

            // 고정/산출 값
            switch (name.ToUpperInvariant())
            {
                case "REF_TARGET_TYPE":
                    value = MigrationRules.RefTargetType("VESSEL"); return true;   // 함정=5
                case "FILE_TYPE":
                    value = 1; return true;                                        // 이미지(png)
                case "INTEGRAL_TIME":
                    value = -1f; return true;
                case "GRAM_UPDATE_TIME":
                    value = (float)DateTimeOffset.Now.ToUnixTimeSeconds(); return true;
                case "SOURCE":
                    // 소스 SOURCE 를 그대로 (동일명칭)
                    if (tgtToSrc.TryGetValue(name, out var sc0))
                    { value = row.GetValueOrDefault(sc0) ?? DBNull.Value; return true; }
                    value = DBNull.Value; return true;
                case "REF_TARGET_ID":
                    // IMAGE_DATA: 소스 VESSEL_ID → VESSEL_DATA 신규 RECORD_ID
                    var vid = _ids.Translate("VESSEL_DATA", row.GetValueOrDefault("VESSEL_ID"));
                    if (vid == null) throw new RowSkip("REF_TARGET_ID: VESSEL_DATA 부모 미이관", SkipKind.FkParentMissing);
                    value = vid.Value; return true;
            }

            // [NAIMS-ISSUE:RULE-IS-COLUMN] 규칙 문구가 곧 소스 컬럼명인 경우가 있다
            //   (예: STD_REFERENCE_DATA.FILE_NAME 의 rule = "IMG_FILENAME").
            //   이름이 달라 ColumnMapper 가 못 잇고, 단위 변환도 아니라 어느 분기에도 안 걸려
            //   "규칙미구현" 으로 행이 통째로 스킵됐다(실측 IMAGE_DATA 3건 전량).
            //   규칙 문구가 이 소스 행에 실제로 있는 컬럼명과 정확히 같으면 그 값을 그대로 쓴다.
            if (row.ContainsKey(rule))
            {
                var raw = _crypto.ResolveValue(plan.SourceTable, rule, row.GetValueOrDefault(rule), srcPk);
                value = TransformMapped(plan, name, raw, tc);
                return true;
            }

            // ── 단위 변환 ─────────────────────────────────────────────────
            // [NAIMS-ISSUE:UNIT-CONV] 판정 순서 주의: "×1000000" 은 "×1000" 을 포함하므로
            // 반드시 긴 패턴(µs)을 먼저 본다. 예전에는 규칙 문구 순서에 따라 계수가 뒤바뀔 수 있었다.
            // 또한 소스가 NULL 이면 0 이 아니라 NULL 을 보존한다(0초와 "값 없음"은 다르다).

            // 초 → µs (AI/TR: A_START_MICROSEC)
            if (rule.Contains("×1000000") || rule.Contains("µs"))
            {
                var srcCol = tgtToSrc.GetValueOrDefault(name)
                             ?? (name.Contains("START") ? "A_START_TIME" : "A_END_TIME");
                var raw = row.GetValueOrDefault(srcCol);
                value = IsNull(raw) ? DBNull.Value : NormalizeTimeOfDay(plan, name, ToLong(raw)) * 1_000_000L;
                CheckTimeOfDay(plan, name, value, UsPerDay, "µs");
                return true;
            }

            // 초 → ms (NB: A_START_MILLISEC — 타겟 컬럼이 밀리초. AAS: NB DB a_start_time 도 초)
            if (rule.Contains("×1000") || rule.Contains("ms)") || rule.Contains("밀리"))
            {
                var srcCol = tgtToSrc.GetValueOrDefault(name)
                             ?? (name.Contains("START") ? "A_START_TIME" : "A_END_TIME");
                var raw = row.GetValueOrDefault(srcCol);
                value = IsNull(raw) ? DBNull.Value : NormalizeTimeOfDay(plan, name, ToLong(raw)) * 1_000L;
                CheckTimeOfDay(plan, name, value, MsPerDay, "ms");
                return true;
            }

            // km → 해리(NM): DISTANCE_KM → DET_DISTANCE_NM. 1 NM = 1.852 km.
            // 규칙 노트에만 있고 구현이 없어 km 값이 NM 컬럼에 무변환 적재되던 것을 바로잡는다.
            if (rule.Contains("÷1.852") || rule.Contains("/1.852") || rule.Contains("NM"))
            {
                var srcCol = tgtToSrc.GetValueOrDefault(name) ?? "DISTANCE_KM";
                var raw = row.GetValueOrDefault(srcCol);
                value = IsNull(raw) ? DBNull.Value : Convert.ToDouble(raw) / 1.852;
                return true;
            }

            // 스니펫 blob·산출 컬럼은 파일 제공자에 위임
            if (_snips != null && _snips.Handles(plan.TargetTable, name))
                return _snips.Resolve(plan, tc, row, out value);

            // 나머지(미결 등): nullable 이면 생략, 아니면 행 스킵
            if (tc.IsNullable || tc.HasDefault) return false;
            throw new RowSkip($"{name} 규칙 '{rule}' 미구현 (verdict={cr.Verdict})", SkipKind.RuleNotImplemented);
        }

        /// <summary>일반 매핑 값 변환: DELETED 형변환, COMMENT 말머리, 타입 캐스팅.</summary>
        private object? TransformMapped(TablePlan plan, string tgtCol, object? raw, TargetColumn tc)
        {
            if (raw == null || raw is DBNull)
                return DBNull.Value;

            // COMMENT 계열 말머리
            if (MigrationRules.IsCommentColumn(tgtCol))
                return MigrationRules.ApplyCommentPrefix(Convert.ToString(raw)) ?? (object)DBNull.Value;

            return ConvertScalar(raw, tc);
        }

        // ── 자체 검증: 문자열 패딩 ────────────────────────────────────
        // [NAIMS-ISSUE:CHAR-PADDING] [찾기:패딩검증]
        /// <summary>
        /// 이관 대상 타겟 테이블의 모든 문자열 컬럼을 훑어 공백 패딩 오염이 남았는지 확인한다.
        /// 커밋 전 같은 트랜잭션 안에서 실행하므로 <b>이번 실행이 넣은 행까지</b> 보인다.
        /// 1건이라도 있으면 예외를 던진다 → 호출부(RunAsync)의 catch 가 롤백한다.
        ///
        /// 두 가지를 본다.
        ///   · 유형 A(값 + 후행 공백) : DATALENGTH(c) &gt; DATALENGTH(RTRIM(c))
        ///   · 유형 B(공백만 저장)    : DATALENGTH(c) &gt; 0 AND LEN(c) = 0
        ///
        /// 판정에 <c>LEN(c) &lt; DATALENGTH(c)</c> 를 쓰면 안 된다 — varchar 에 든 한글은 1글자가
        /// 2바이트라 정상 데이터가 대량 오탐된다. 반드시 RTRIM 결과와 바이트 길이를 대조할 것.
        /// </summary>
        /// <param name="plans">이번 실행의 테이블 계획(타겟 테이블 목록을 여기서 뽑는다).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <exception cref="InvalidOperationException">오염이 1건이라도 남아 있을 때.</exception>
        private async Task VerifyNoStringPaddingAsync(IReadOnlyList<TablePlan> plans, CancellationToken ct)
        {
            var tables = plans.Select(p => p.TargetTable)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                              .ToList();

            var probes = new List<string>();
            foreach (var t in tables)
                foreach (var c in _tgt.Columns(t))
                {
                    if (!IsTextType(c.DataType)) continue;
                    probes.Add(
                        $"SELECT '{t}' T,'{c.Name}' C," +
                        $"SUM(CASE WHEN DATALENGTH([{c.Name}])>DATALENGTH(RTRIM([{c.Name}])) THEN 1 ELSE 0 END) PAD," +
                        $"SUM(CASE WHEN DATALENGTH([{c.Name}])>0 AND LEN([{c.Name}])=0 THEN 1 ELSE 0 END) BLANK " +
                        $"FROM [{t}]");
                }

            if (probes.Count == 0) return;

            // 한 문장에 컬럼 수만큼 UNION ALL 이 붙는다. 너무 길어지지 않게 나눠 실행한다.
            const int ChunkSize = 40;
            var bad = new List<(string T, string C, long Pad, long Blank)>();
            for (int i = 0; i < probes.Count; i += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = probes.Skip(i).Take(ChunkSize);
                await using var cmd = _tcn!.CreateCommand();
                if (_tx != null) cmd.Transaction = _tx;
                cmd.CommandText = string.Join(" UNION ALL ", chunk);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    long pad = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2));
                    long blank = r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3));
                    if (pad > 0 || blank > 0)
                        bad.Add((Convert.ToString(r.GetValue(0)) ?? "",
                                 Convert.ToString(r.GetValue(1)) ?? "", pad, blank));
                }
            }

            if (bad.Count == 0)
            {
                _log($"✔ 문자열 패딩 검증 통과 — {tables.Count}개 테이블 / {probes.Count}개 문자열 컬럼, 오염 0건");
                return;
            }

            _log($"!! 문자열 패딩 오염 {bad.Count}개 컬럼 — 커밋하지 않고 롤백합니다.");
            foreach (var b in bad.OrderByDescending(x => x.Pad + x.Blank).Take(20))
                _log($"   · {b.T}.{b.C}: 후행공백 {b.Pad}건, 공백만 {b.Blank}건");
            if (bad.Count > 20) _log($"   · … 외 {bad.Count - 20}개 컬럼");

            long totalPad = bad.Sum(b => b.Pad), totalBlank = bad.Sum(b => b.Blank);
            throw new InvalidOperationException(
                $"문자열 패딩 오염이 남아 있습니다 — {bad.Count}개 컬럼 / 후행공백 {totalPad}건 · 공백만 {totalBlank}건. " +
                "이관 코드의 정규화 누락(LegacyText 미경유)이거나, 이번 실행 이전에 적재된 기존 행입니다. " +
                "기존 행이면 백필 스크립트(sql/backfill_trim_padding.sql)를 먼저 돌린 뒤 다시 실행하십시오.");
        }

        /// <summary>타겟 컬럼이 문자열 타입인지(패딩 검증 대상 판정용).</summary>
        private static bool IsTextType(string dataType) => dataType.ToLowerInvariant() is
            "varchar" or "nvarchar" or "char" or "nchar";

        // ── 소스 읽기 ──────────────────────────────────────────────────
        /// <summary>
        /// 소스 테이블의 컬럼 메타(이름·타입·길이·NULL 허용)를 소스 종류별 INFORMATION_SCHEMA 쿼리로 읽는다.
        /// 부작용: 소스 DB 에 별도 읽기 연결을 열고 닫는다.
        /// </summary>
        /// <param name="table">조회할 소스 테이블명.</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>컬럼 메타 목록(순서 보존). 읽지 못하면 빈 목록.</returns>
        private async Task<List<ColumnInfo>> LoadSourceColumnsAsync(string table, CancellationToken ct)
        {
            var list = new List<ColumnInfo>();
            await using var cn = DbProvider.CreateConnection(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr);
            await cn.OpenAsync(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = DbProvider.ColumnsSql(_opt.SourceKind, _opt.SourceMode);
            var p = cmd.CreateParameter();
            p.ParameterName = DbProvider.ParamMarker(_opt.SourceKind, _opt.SourceMode, "t");
            p.Value = table;
            cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int ord = 0;
            while (await r.ReadAsync(ct))
            {
                list.Add(new ColumnInfo
                {
                    Name = Convert.ToString(r.GetValue(0)) ?? "",
                    DataType = Convert.ToString(r.GetValue(1)) ?? "",
                    MaxLength = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                    Nullable = Convert.ToString(r.GetValue(3)) ?? "",
                    Ordinal = ++ord,
                });
            }
            return list;
        }

        /// <summary>
        /// 소스 테이블의 행을 읽어 (컬럼명→값) 딕셔너리 목록으로 돌려준다.
        /// 각 컬럼은 <see cref="CryptoContext.SelectExpression"/> 로 SELECT 식을 만들어 암호화 컬럼은
        /// 복호화되어 나온다. MaxRowsPerTable 설정 시 상위 N행만, MsSQL 은 WITH(NOLOCK) 로 읽는다.
        /// </summary>
        /// <param name="cn">이미 열린 소스 연결(복호화 키가 필요하면 호출 전 열려 있어야 함).</param>
        /// <param name="kind">소스 DB 종류(MsSQL/Oracle) — SQL 방언 분기.</param>
        /// <param name="table">소스 테이블명.</param>
        /// <param name="cols">읽을 컬럼 메타 목록(SELECT 식 생성에 사용).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>행 목록. 각 행은 대소문자 무시 컬럼명 키의 딕셔너리(NULL 은 null).</returns>
        private async Task<List<Dictionary<string, object?>>> ReadSourceRowsAsync(
            DbConnection cn, DbProviderKind kind, string table, IReadOnlyList<ColumnInfo> cols, CancellationToken ct)
        {
            var exprs = cols.Select(c => _crypto.SelectExpression(kind, table, c));
            string colList = string.Join(", ", exprs);
            string quoted = DbProvider.QuoteIdent(kind, table);
            string sql = _opt.MaxRowsPerTable > 0
                ? DbProvider.TopRowsSql(kind, quoted, colList, _opt.MaxRowsPerTable)
                : (kind == DbProviderKind.Oracle
                    ? $"SELECT {colList} FROM {quoted}"
                    : $"SELECT {colList} FROM {quoted} WITH (NOLOCK)");

            var result = new List<Dictionary<string, object?>>();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++)
                    // [NAIMS-ISSUE:CHAR-PADDING] 소스 값이 이 도구 안으로 들어오는 <b>유일한</b> 문
                    //   — 레거시 char(N) 고정폭 패딩은 여기서 끝난다. 여기서 떼면 이후 모든 경로
                    //   (INSERT 값 결정·dedup 키 대조·자연키 재사용·미리보기)가 같은 값을 본다.
                    //   INSERT 문마다 TrimEnd 를 붙이는 방식은 한 군데만 빠뜨려도 오염이 되살아난다.
                    d[r.GetName(i)] = r.IsDBNull(i) ? null : LegacyText.NormalizeValue(r.GetValue(i));
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// 자연키 재사용 정책의 사전 선점. 소스의 (PK, 자연키) 목록을 읽어, 타겟에 이미 있는 같은 자연키의
        /// 기존 RECORD_ID 를 IdRemapper(_ids) 에 선점 등록한다 → 이후 이관 시 해당 행은 INSERT 없이 재사용.
        /// 부작용: 소스 DB 읽기 연결을 열고(암호화 시 복호화 키 개폐), 타겟은 공유 연결로 기존 행을 대조한다.
        /// </summary>
        /// <param name="plan">재사용 대상 테이블 규칙(소스/타겟 자연키 컬럼 포함).</param>
        /// <param name="srcCols">소스 컬럼 메타(자연키 컬럼의 복호화 SELECT 식 생성에 사용).</param>
        /// <param name="ct">취소 토큰.</param>
        private async Task SeedReuseAsync(TablePlan plan, IReadOnlyList<ColumnInfo> srcCols, CancellationToken ct)
        {
            // 소스 (pk, 자연키) 목록
            var pairs = new List<(long, string)>();
            await using (var scn = DbProvider.CreateConnection(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr))
            {
                await scn.OpenAsync(ct);
                if (NeedsDecryption(plan)) await _crypto.OpenKeyAsync(scn, ct);
                var keyCol = srcCols.FirstOrDefault(c =>
                    c.Name.Equals(plan.SourceKeyColumn, StringComparison.OrdinalIgnoreCase));
                string keyExpr = keyCol != null
                    ? _crypto.SelectExpression(_opt.SourceKind, plan.SourceTable, keyCol)
                    : DbProvider.QuoteColumn(_opt.SourceKind, plan.SourceKeyColumn);
                string pkq = DbProvider.QuoteColumn(_opt.SourceKind, plan.SourcePk);
                string q = DbProvider.QuoteIdent(_opt.SourceKind, plan.SourceTable);
                await using var cmd = scn.CreateCommand();
                cmd.CommandText = $"SELECT {pkq}, {keyExpr} FROM {q}";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    // [NAIMS-ISSUE:CHAR-PADDING] 자연키도 소스에서 직접 읽는 별도 경로다.
                    //   여기서 정규화하지 않으면 "ASW␣␣␣" 와 "ASW" 가 다른 키로 잡혀
                    //   이미 있는 행을 못 찾고 중복 INSERT 된다.
                    pairs.Add((ToLong(r.IsDBNull(0) ? null : r.GetValue(0)),
                               r.IsDBNull(1) ? "" : LegacyText.Normalize(Convert.ToString(r.GetValue(1)))));
                if (NeedsDecryption(plan)) await _crypto.CloseKeyAsync(scn, ct);
            }

            // 재사용 대조는 커밋된 기존 행을 읽으므로 공유 타겟 연결(트랜잭션 밖 읽기)로 처리한다.
            int matched = await _ids.SeedFromTargetAsync(
                _tcn!, plan.SourceTable, plan.TargetTable, plan.TargetKeyColumn, pairs, ct, _tx);
            if (matched > 0) _log($"    재사용 대조: 기존 {matched}건 선점");
        }

        // ── INSERT 실행 ───────────────────────────────────────────────
        /// <summary>
        /// 한 행을 실제 INSERT 하고 IDENTITY 로 채번된 신규 RECORD_ID 를 돌려준다.
        /// OUTPUT INSERTED.RECORD_ID 로 채번값을 회수하며, 값은 @p0..@pN 파라미터로 바인딩한다.
        /// 부작용: 타겟 DB 에 실제 쓰기(주어진 트랜잭션 안에서 실행).
        /// </summary>
        /// <param name="cn">공유 타겟 연결.</param>
        /// <param name="tx">트랜잭션(Trial·Commit). null 이면 자동 커밋 컨텍스트.</param>
        /// <param name="table">타겟 테이블명.</param>
        /// <param name="cols">삽입 컬럼명 목록.</param>
        /// <param name="vals">컬럼과 대응하는 값 목록(null 은 DBNull 로 치환).</param>
        /// <param name="ct">취소 토큰.</param>
        /// <returns>새로 채번된 RECORD_ID.</returns>
        // [NAIMS-ISSUE:DERIVED-CHILD] [찾기:스펙트로그램]
        /// <summary>
        /// 부모 INSERT 직후, 소스에 대응이 없어 산출이 필요한 자식행을 넣는다.
        /// 현재 대상은 STD_AI_SNIPPET → STD_AI_SPECTROGRAM 뿐이다(스니펫 원음에서 스펙트로그램 생성).
        /// 산출에 실패하면 부모까지 되돌리지 않고 오류만 남긴다 — 스니펫 자체는 유효하기 때문.
        /// 다만 기준식별은 이 자식이 있어야 스니펫을 전시하므로, 실패는 반드시 결과에 드러낸다.
        /// </summary>
        private async Task InsertDerivedChildAsync(
            TablePlan plan, IReadOnlyDictionary<string, object?> row, long parentId,
            TableResult res, CancellationToken ct)
        {
            if (plan.DerivedChild is not { } spec || _snips == null || parentId <= 0) return;

            try
            {
                var vals = _snips.BuildAiSpectrogramRow(row, DateTime.Now);
                if (vals == null)
                {
                    // [NAIMS-ISSUE:EMPTY-ON-MISSING] 참조 파일이 없어서 못 만든 것은 결함이 아니라
                    //   이미 '빈 값 적재' 로 집계된 사건의 결과다. 같은 사건을 오류로 또 세면
                    //   사람은 고칠 수 없는 항목을 붙들게 된다. 파일 부재가 원인이면 조용히 넘어간다
                    //   (건수는 04/07_missing_files 목록에 소스 PK 까지 남아 있다).
                    if (_snips.CurrentMissing.Count == 0 && res.Errors.Count < ErrorSampleLimit)
                        res.Errors.Add($"{spec.TargetTable}: 스펙트로그램 생성 불가(원음이 너무 짧음) — 부모 RECORD_ID={parentId}");
                    return;
                }

                // 타겟에 실제 있는 컬럼만 넣는다(스키마 차이로 INSERT 가 깨지지 않게).
                var have = _tgt.Columns(spec.TargetTable)
                               .Select(c => c.Name)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (have.Count == 0)
                {
                    if (res.Errors.Count < ErrorSampleLimit)
                        res.Errors.Add($"{spec.TargetTable}: 타겟 스키마 없음 — 스펙트로그램 자식행 생략");
                    return;
                }

                var cols = new List<string> { spec.ParentFkColumn };
                var pars = new List<object?> { parentId };
                foreach (var (k, v) in vals)
                    if (have.Contains(k)) { cols.Add(k); pars.Add(v); }

                await ExecInsertAsync(_tcn!, _tx, spec.TargetTable, cols, pars, ct);
                res.DerivedChildren++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (res.Errors.Count < ErrorSampleLimit)
                    res.Errors.Add($"{spec.TargetTable} 자식행 실패(부모 RECORD_ID={parentId}): " +
                                   ExtractRaw.ExceptionText.Describe(ex));
            }
        }

        // ══════════ [찾기:충돌해결] 멱등 충돌 ══════════
        // [NAIMS-ISSUE:IDEMPOTENT-CONFLICT]
        //   "이미 있으니 넘어간다" 는 안전하지만, 소스를 고쳐도 반영되지 않는다는 뜻이기도 하다.
        //   반대로 늘 덮어쓰면 타겟에서 손본 내용이 조용히 사라진다. 어느 쪽도 도구가 정할 일이 아니라,
        //   리허설에서 <무엇이 달라지는지> 먼저 보여 주고 실제 이관 전에 사람이 고르게 한다.

        /// <summary>한 번 실행에서 기록할 충돌 상한 — 이건 사람이 하나씩 보고 정할 목록이다.</summary>
        private const int ConflictSampleLimit = 300;

        private DbToDbOptions.ConflictDecision? ConflictFor(string sourceTable, long srcPk) =>
            _opt.ConflictDecisions.Count > 0
            && _opt.ConflictDecisions.TryGetValue(DbToDbOptions.ConflictKey(sourceTable, srcPk), out var d)
                ? d : null;

        /// <summary>기존 타겟 행과 "넣으려던 값"을 맞대 본다. 다른 것이 있으면 충돌로 남긴다.</summary>
        private async Task NoteConflictAsync(
            TablePlan plan, IReadOnlyList<TargetColumn> insertable,
            IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, object?> row, long srcPk, long targetId,
            TableResult res, CancellationToken ct)
        {
            if (res.Conflicts.Count >= ConflictSampleLimit) return;
            try
            {
                var (cols, vals) = BuildInsert(plan, insertable, tgtToSrc, row, srcPk);
                if (cols.Count == 0) return;

                var current = await ReadTargetRowAsync(plan.TargetTable, cols, targetId, ct);
                if (current == null) return;

                var c = new ConflictRef
                {
                    SourceTable = plan.SourceTable, TargetTable = plan.TargetTable,
                    SourcePk = srcPk, TargetRecordId = targetId,
                };
                for (int i = 0; i < cols.Count; i++)
                {
                    if (!current.TryGetValue(cols[i], out var now)) continue;
                    if (SameForConflict(now, vals[i])) continue;
                    c.Diffs.Add((cols[i], ShowVal(now), ShowVal(vals[i])));
                }
                // 값까지 같으면 정할 것이 없다 — 목록에 올리지 않는다.
                if (c.Diffs.Count > 0) res.Conflicts.Add(c);
            }
            catch (RowSkip) { /* 어차피 넣지 못할 행이면 충돌도 아니다 */ }
            catch (Exception ex) { _log($"  · 충돌 확인 실패(PK={srcPk}): {ex.Message}"); }
        }

        /// <summary>사람이 정한 대로 타겟 행을 갱신한다. 성공하면 true.</summary>
        private async Task<bool> OverwriteAsync(
            TablePlan plan, IReadOnlyList<TargetColumn> insertable,
            IReadOnlyDictionary<string, string> tgtToSrc,
            IReadOnlyDictionary<string, object?> row, long srcPk, long targetId,
            DbToDbOptions.ConflictDecision decision, TableResult res, CancellationToken ct)
        {
            try
            {
                var (cols, vals) = BuildInsert(plan, insertable, tgtToSrc, row, srcPk);
                if (cols.Count == 0) return false;

                // '새 값' 이면 사람이 적어 넣은 값이 소스 값보다 앞선다.
                if (decision.Action == DbToDbOptions.ConflictDecision.NewValue && decision.Values is { } uv)
                    for (int i = 0; i < cols.Count; i++)
                        if (uv.TryGetValue(cols[i], out var v)) vals[i] = string.IsNullOrEmpty(v) ? null : v;

                // RECORD_ID 는 건드리지 않는다 — 자식들이 이 번호로 붙어 있다.
                var setCols = new List<string>(); var setVals = new List<object?>();
                for (int i = 0; i < cols.Count; i++)
                {
                    if (cols[i].Equals("RECORD_ID", StringComparison.OrdinalIgnoreCase)) continue;
                    setCols.Add(cols[i]); setVals.Add(vals[i]);
                }
                if (setCols.Count == 0) return false;

                await using var cmd = _tcn!.CreateCommand();
                if (_tx != null) cmd.Transaction = _tx;
                cmd.CommandText = $"UPDATE [{plan.TargetTable}] SET " +
                    string.Join(", ", setCols.Select((c, i) => $"[{c}] = @p{i}")) +
                    " WHERE RECORD_ID = @rid";
                for (int i = 0; i < setVals.Count; i++)
                    // [NAIMS-ISSUE:CHAR-PADDING] 덮어쓰기도 타겟에 쓰는 경로다. 여기에는 사람이
                    //   해결 창에 <b>직접 입력한</b> 값도 섞여 들어오므로(붙여넣기로 공백이 딸려온다)
                    //   INSERT 와 같은 규칙을 태운다.
                    cmd.Parameters.AddWithValue("@p" + i, LegacyText.NormalizeValue(setVals[i]) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rid", targetId);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(ct);
                return true;
            }
            catch (RowSkip rs)
            {
                res.Errors.Add($"행 PK={srcPk} 덮어쓰기 불가: {rs.Message}");
                return false;
            }
            catch (Exception ex)
            {
                res.Errors.Add($"행 PK={srcPk} 덮어쓰기 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>타겟 행에서 지정한 컬럼들만 읽는다(충돌 대조용).</summary>
        private async Task<Dictionary<string, object?>?> ReadTargetRowAsync(
            string table, IReadOnlyList<string> cols, long recordId, CancellationToken ct)
        {
            await using var cmd = _tcn!.CreateCommand();
            if (_tx != null) cmd.Transaction = _tx;
            cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => "[" + c + "]"))} " +
                              $"FROM [{table}] WHERE RECORD_ID = @rid";
            cmd.Parameters.AddWithValue("@rid", recordId);
            cmd.CommandTimeout = 60;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++)
                map[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            return map;
        }

        /// <summary>
        /// 충돌 판정용 값 비교. 이관이 만든 값끼리 맞대는 것이라 단위·말머리 변환은 이미 끝나 있다.
        /// 남는 것은 표현 차이뿐이므로, 바이트·공백·부동소수 오차만 흡수한다.
        /// </summary>
        private static bool SameForConflict(object? a, object? b)
        {
            bool na = a is null or DBNull, nb = b is null or DBNull;
            if (na || nb) return na && nb;
            if (a is byte[] ba && b is byte[] bb) return ba.AsSpan().SequenceEqual(bb);
            if (a is DateTime da && b is DateTime db) return Math.Abs((da - db).TotalSeconds) < 1.0;
            if (IsNumeric(a) && IsNumeric(b))
            {
                double x = Convert.ToDouble(a), y = Convert.ToDouble(b);
                return Math.Abs(x - y) <= Math.Max(1e-6, Math.Max(Math.Abs(x), Math.Abs(y)) * 1e-6);
            }
            return string.Equals(Convert.ToString(a)?.Trim(), Convert.ToString(b)?.Trim(),
                                 StringComparison.Ordinal);
        }

        private static bool IsNumeric(object v) =>
            v is byte or sbyte or short or ushort or int or uint or long or ulong
              or float or double or decimal;

        private static string ShowVal(object? v) => v switch
        {
            null or DBNull => "(없음)",
            byte[] b => $"({b.Length:N0} 바이트)",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
            _ => Convert.ToString(v) ?? "",
        };

        private static async Task<long> ExecInsertAsync(
            SqlConnection cn, SqlTransaction? tx, string table,
            IReadOnlyList<string> cols, IReadOnlyList<object?> vals, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append("INSERT INTO [").Append(table).Append("] (");
            sb.Append(string.Join(", ", cols.Select(c => "[" + c + "]")));
            sb.Append(") OUTPUT INSERTED.RECORD_ID VALUES (");
            sb.Append(string.Join(", ", cols.Select((_, i) => "@p" + i)));
            sb.Append(");");

            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sb.ToString();
            if (tx != null) cmd.Transaction = tx;
            for (int i = 0; i < vals.Count; i++)
                // [NAIMS-ISSUE:CHAR-PADDING] 타겟 DB 로 값이 나가는 <b>마지막 문</b>.
                //   이 도구의 모든 INSERT(부모 행·산출 자식 행)가 여기를 지난다. 앞단(소스 읽기·
                //   ConvertScalar)에서 이미 정규화되지만, 값 결정 분기가 하나 늘 때마다 우회로가
                //   하나 생기는 구조를 두지 않으려고 여기서 한 번 더 건다 — 비용은 타입 검사 한 번이다.
                cmd.Parameters.AddWithValue("@p" + i, LegacyText.NormalizeValue(vals[i]) ?? DBNull.Value);
            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(id);
        }

        // ── 값 변환 유틸 ──────────────────────────────────────────────
        // [NAIMS-ISSUE:TYPE-CAST-SKIP] Oracle 타입 변환 실패로 행 스킵
        /// <summary>
        /// 원본 스칼라 값을 타겟 컬럼 타입에 맞게 캐스팅한다(문자열 플래그→정수, 길이 초과 문자열 절단 등).
        /// null/DBNull 은 DBNull 로, varbinary 등 미지원 타입은 원본 그대로 통과시킨다.
        /// 변환 실패는 조용히 넘기지 않고 <see cref="RowSkip"/> 로 이 행을 스킵하며 정확한 사유를 남긴다
        /// (특히 Oracle 소스는 타입 강제가 MsSQL 과 달라 이 경로가 중요).
        /// </summary>
        /// <param name="raw">소스에서 읽은 원본 값.</param>
        /// <param name="tc">대상 타겟 컬럼(타입·최대 길이 참조).</param>
        /// <returns>타겟 타입으로 변환된 값(또는 DBNull).</returns>
        private static object? ConvertScalar(object? raw, TargetColumn tc)
        {
            if (raw == null || raw is DBNull) return DBNull.Value;
            try
            {
                switch (tc.DataType.ToLowerInvariant())
                {
                    case "int": case "smallint": case "tinyint":
                        return raw is string si ? FlagToInt(si) : Convert.ToInt32(raw);
                    case "bigint":
                        return raw is string sl ? (long)FlagToInt(sl) : Convert.ToInt64(raw);
                    case "bit":
                        return raw is string sb ? FlagToInt(sb) != 0 : Convert.ToBoolean(raw);
                    case "real":
                        return Convert.ToSingle(raw);
                    case "float": case "decimal": case "numeric":
                        return Convert.ToDouble(raw);
                    case "datetime": case "datetime2": case "date":
                        return raw is DateTime dt ? dt : DateTime.Parse(Convert.ToString(raw)!);
                    case "varchar": case "nvarchar": case "char": case "nchar":
                        // [NAIMS-ISSUE:CHAR-PADDING] 최종 방어선. 소스 읽기(ReadSourceRowsAsync)에서
                        //   이미 정규화되지만, 여기로는 고정 기본값·규칙 산출값·파일 제공자 산출값처럼
                        //   소스를 거치지 않은 문자열도 들어온다. 타겟에 문자열이 실리는 마지막 지점이므로
                        //   한 번 더 통과시킨다 — 절단 전에 떼야 폭을 낭비하지 않는다.
                        var s = LegacyText.Normalize(Convert.ToString(raw));
                        if (tc.MaxLength > 0 && s.Length > tc.MaxLength) s = s.Substring(0, tc.MaxLength);
                        return s;
                    default:
                        return raw;   // varbinary 등은 그대로
                }
            }
            catch (Exception ex)
            {
                // 변환 실패를 조용히 원본 통과시키면 잘못된 값이 적재되거나 SQL 단에서
                // 원인 불명 오류가 난다. 행을 스킵하고 정확한 사유를 남긴다.
                // (Oracle 소스는 타입 강제가 MsSQL 과 달라 이 경로가 특히 중요하다)
                throw new RowSkip(
                    $"{tc.Name}: '{Trunc(Convert.ToString(raw), 40)}' → {tc.DataType} 변환 실패 ({ex.Message})",
                    SkipKind.TypeCast);
            }
        }

        /// <summary>문자열을 최대 <paramref name="n"/>자로 자르고 넘치면 말줄임표를 붙인다(오류 메시지용).</summary>
        /// <param name="s">원본 문자열(null/빈 값이면 "").</param>
        /// <param name="n">유지할 최대 글자 수.</param>
        /// <returns>절단된 문자열.</returns>
        private static string Trunc(string? s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

        /// <summary>플래그 문자열을 정수로. 숫자면 그대로, 아니면 T/Y→1 그 외 0 (레거시 DELETED='T' 방어).</summary>
        private static int FlagToInt(string s)
        {
            s = s.Trim();
            if (int.TryParse(s, out var n)) return n;
            return s.Length > 0 && (s[0] is 'T' or 't' or 'Y' or 'y') ? 1 : 0;
        }

        /// <summary>타겟 컬럼 타입에 맞는 안전 placeholder (삭제 예정 NOT NULL 컬럼 채움용).</summary>
        private static object ZeroFor(TargetColumn tc) => tc.DataType.ToLowerInvariant() switch
        {
            "int" or "smallint" or "tinyint" => 0,
            "bigint" => 0L,
            "bit" => false,
            "real" => 0f,
            "float" or "decimal" or "numeric" => 0d,
            "datetime" or "datetime2" or "date" => DateTime.Now,
            "varbinary" or "binary" => Array.Empty<byte>(),
            _ => "",
        };

        /// <summary>컬럼명이 REF_*_ID 형태의 참조 FK 컬럼인지 판정(미선언 FK 오염 방어에 사용).</summary>
        private static bool IsRefIdColumn(string n) =>
            n.StartsWith("REF_", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith("_ID", StringComparison.OrdinalIgnoreCase);

        /// <summary>타겟 컬럼이 수치형(int·bigint·real·float·decimal 등)인지 판정.</summary>
        private static bool IsNumeric(TargetColumn tc) => tc.DataType.ToLowerInvariant() is
            "int" or "smallint" or "tinyint" or "bigint" or "real" or "float" or "decimal" or "numeric";

        /// <summary>컬럼명이 시스템 관리 컬럼(RECORD_ID·ROWGUID·DELETED 등)인지 판정 — 임의 채움 대상에서 제외.</summary>
        private static bool IsSystemColumn(string n) => n.ToUpperInvariant() is
            "RECORD_ID" or "ROWGUID" or "CHNG_DATETIME" or "DELETED" or "IS_LEGACY" or "IS_TRAINING";

        /// <summary>임의 값을 long 으로 안전 변환. null/DBNull 또는 파싱 실패 시 0(참조 없음 sentinel).</summary>
        /// <summary>DB 값이 "값 없음"인지. 단위 변환에서 NULL 을 0 으로 뭉개지 않기 위해 쓴다.</summary>
        private static bool IsNull(object? v) => v is null or DBNull;

        private static long ToLong(object? v)
        {
            if (v == null || v is DBNull) return 0;
            return long.TryParse(Convert.ToString(v), out var l) ? l : 0;
        }

        // [NAIMS-ISSUE:GATE-METRICS] 스킵 사유 분류
        /// <summary>스킵 사유 분류 상수. 게이트 채점이 이 문자열로 판정하므로 임의로 바꾸지 말 것.</summary>
        internal static class SkipKind
        {
            /// <summary>부모가 이번 실행에서 이관되지 않아 FK 를 채울 수 없음 — 데이터 구조 손실.</summary>
            public const string FkParentMissing = "FK-부모미이관";

            // [NAIMS-ISSUE:REUSED-PARENT-NO-ID] '부모가 없다' 와 '부모는 있는데 그 타겟 ID 를 모른다' 는
            //   원인도 조치도 다르다. 전자는 소스 데이터(고아 FK) 문제라 이관으로 못 고치고,
            //   후자는 도구 문제라 고칠 수 있다. 한 사유로 뭉뚱그리면 고칠 수 있는 것을
            //   "소스 탓"으로 오진하게 된다 — 실제로 재실행에서 HARMONICS 56건이 그렇게 묻혔다.
            public const string FkParentIdUnknown = "FK-부모ID미상";
            /// <summary>레거시 0("참조 없음")인데 타겟 부모에 0번 센티넬 행이 없음.</summary>
            public const string FkZeroSentinel = "FK-센티넬없음";
            /// <summary>계획에 선언되지 않은 REF_*_ID — 매핑 계획 수정 필요.</summary>
            public const string FkUndeclared = "FK-미선언";
            /// <summary>NOT NULL 인데 채울 값이 없음.</summary>
            public const string NotNull = "NOTNULL-값없음";
            /// <summary>값 결정 규칙 자체가 없음.</summary>
            public const string NoRule = "규칙없음";
            /// <summary>규칙은 있으나 구현되지 않음(미결 등).</summary>
            public const string RuleNotImplemented = "규칙미구현";
            // [NAIMS-ISSUE:MANUAL-FK] 사람이 명시적으로 버리기로 한 행 — 사고가 아니라 결정이다.
            //   자동 스킵과 같은 칸에 세면 "고칠 것" 과 "이미 판단한 것" 이 뒤섞인다.
            /// <summary>사용자가 해결 창에서 '버림'으로 지정.</summary>
            public const string ManualDiscard = "사용자-버림";
            /// <summary>소스 값을 타겟 타입으로 변환할 수 없음(Oracle 소스에서 빈발).</summary>
            public const string TypeCast = "타입변환";
            /// <summary>참조 파일(.wav/.grm/이미지)이 없어 blob 을 채우지 못함 — 자료 문제이지 코드 문제가 아니다.</summary>
            public const string MissingFile = "파일없음";
            /// <summary>분류되지 않은 예외.</summary>
            public const string Exception = "예외";
        }

        /// <summary>이 행을 건너뛰라는 신호(값 결정 불가 등).</summary>
        private sealed class RowSkip : Exception
        {
            /// <summary>사유 분류(<see cref="SkipKind"/>). 게이트 자동 채점에 쓰인다.</summary>
            public string Category { get; }

            public RowSkip(string m, string category = SkipKind.Exception) : base(m) => Category = category;
        }
    }
}
