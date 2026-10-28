using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 소스 → 타겟 테이블 이관 계획 한 건 (JSON plan[] 항목).
    /// </summary>
    public sealed class RulePlanItem
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string Pk { get; set; } = "";
        public int Level { get; set; }
        public string Category { get; set; } = "";
    }

    /// <summary>수동 컬럼 대응 (JSON manual[] 항목). Target 이 null 이면 "대응 없음/미결".</summary>
    public sealed class RuleManual
    {
        public string Source { get; set; } = "";
        public string Column { get; set; } = "";
        public string? Target { get; set; }
        public string Rule { get; set; } = "";
        public string Note { get; set; } = "";
    }

    /// <summary>타겟 컬럼별 산출/변환 규칙 (JSON columnRules[] 항목).</summary>
    public sealed class RuleColumn
    {
        public string Target { get; set; } = "";
        public string Column { get; set; } = "";
        public string Rule { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string Note { get; set; } = "";

        public string Key => $"{Target}.{Column}";
    }

    /// <summary>타겟 고정 기본값 (JSON targetFixedDefault[] 항목).</summary>
    public sealed class RuleFixedDefault
    {
        public string Target { get; set; } = "";
        public string Column { get; set; } = "";
        public string Value { get; set; } = "";

        public string Key => $"{Target}.{Column}";
    }

    public sealed class RuleClassesFill
    {
        public string Value { get; set; } = "-1";
        public List<string> Except { get; set; } = new();
        public string Note { get; set; } = "";
    }

    public sealed class RuleEncrypted
    {
        public List<string> Mssql { get; set; } = new();
        public List<string> Oracle { get; set; } = new();
    }

    /// <summary>
    /// mapping_rules.json 역직렬화 모델. Python 의 build_mapping / dump_rules_json.py 와
    /// 동일한 스키마다. 이 JSON 이 매핑 규칙의 단일 원본(SSOT)이다.
    /// </summary>
    public sealed class MigrationRulesModel
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("plan")] public List<RulePlanItem> Plan { get; set; } = new();
        [JsonPropertyName("excluded")] public Dictionary<string, string> Excluded { get; set; } = new();
        [JsonPropertyName("referenceOnly")] public Dictionary<string, string> ReferenceOnly { get; set; } = new();
        [JsonPropertyName("dropped")] public Dictionary<string, string> Dropped { get; set; } = new();
        [JsonPropertyName("aliases")] public Dictionary<string, string> Aliases { get; set; } = new();
        [JsonPropertyName("targetOnlyNotes")] public Dictionary<string, string> TargetOnlyNotes { get; set; } = new();
        [JsonPropertyName("manual")] public List<RuleManual> Manual { get; set; } = new();
        [JsonPropertyName("columnRules")] public List<RuleColumn> ColumnRules { get; set; } = new();
        [JsonPropertyName("targetFixedDefault")] public List<RuleFixedDefault> TargetFixedDefault { get; set; } = new();
        [JsonPropertyName("classesFillDefault")] public RuleClassesFill ClassesFillDefault { get; set; } = new();
        [JsonPropertyName("encrypted")] public RuleEncrypted Encrypted { get; set; } = new();
        [JsonPropertyName("gramTypeMap")] public Dictionary<string, int> GramTypeMap { get; set; } = new();
        [JsonPropertyName("refTargetTypeCodes")] public Dictionary<string, int> RefTargetTypeCodes { get; set; } = new();

        /// <summary>COMMENT 계열 컬럼 이관 시 붙일 말머리 (예: "[NAIMS-I]"). 모든 이관 트랙 공통.</summary>
        [JsonPropertyName("commentPrefix")] public string CommentPrefix { get; set; } = "";

        /// <summary>문서·이관에서 제외할 삭제 예정 타겟 컬럼 "table.column".</summary>
        [JsonPropertyName("hiddenTargetColumns")] public List<string> HiddenTargetColumns { get; set; } = new();
    }

    /// <summary>
    /// mapping_rules.json 을 로드해 매핑 규칙을 제공하는 단일 접근점.
    ///
    /// Python(docs/schema) 문서 파이프라인과 C# 실행기가 <b>같은 JSON</b> 을 읽는다.
    /// 규칙 수정은 mapping_rules.json 에서 하고, 문서는 파이프라인을, 코드는 재빌드를
    /// 하면 양쪽이 자동으로 일치한다. (2026-07-23 SSOT 통합)
    /// </summary>
    public static class MigrationRules
    {
        private static readonly Lazy<MigrationRulesModel> _model = new(Load);

        public static MigrationRulesModel Model => _model.Value;

        /// <summary>JSON 파일 경로(출력 폴더). 테스트에서 재지정 가능.</summary>
        public static string RulesPath { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "Features", "DbToDb", "mapping_rules.json");

        private static MigrationRulesModel Load()
        {
            var path = ResolvePath();
            if (path == null)
                throw new FileNotFoundException(
                    "mapping_rules.json 을 찾을 수 없습니다. csproj 의 CopyToOutputDirectory 설정을 확인하세요.");

            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            return JsonSerializer.Deserialize<MigrationRulesModel>(json, opts)
                   ?? throw new InvalidDataException("mapping_rules.json 파싱 결과가 비어 있습니다.");
        }

        // [NAIMS-ISSUE:MAPPING-RULES-JSON] 단일exe 게시 시 mapping_rules 누락→FileNotFound
        /// <summary>출력 폴더 → 프로젝트 폴더 순으로 JSON 을 찾는다(개발 편의).</summary>
        private static string? ResolvePath()
        {
            if (File.Exists(RulesPath)) return RulesPath;

            // 출력 폴더 바로 아래(플랫 복사)도 확인
            var flat = Path.Combine(AppContext.BaseDirectory, "mapping_rules.json");
            if (File.Exists(flat)) return flat;

            // 개발 중 소스 트리
            var src = Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "Features", "DbToDb", "mapping_rules.json");
            return File.Exists(src) ? Path.GetFullPath(src) : null;
        }

        // ── 편의 접근자 ────────────────────────────────────────────────

        private static Dictionary<string, RuleColumn>? _colRules;
        public static IReadOnlyDictionary<string, RuleColumn> ColumnRules =>
            _colRules ??= Model.ColumnRules.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string>? _fixedDefaults;
        public static IReadOnlyDictionary<string, string> FixedDefaults =>
            _fixedDefaults ??= Model.TargetFixedDefault.ToDictionary(
                r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string>? _aliases;
        public static IReadOnlyDictionary<string, string> Aliases =>
            _aliases ??= new Dictionary<string, string>(Model.Aliases, StringComparer.OrdinalIgnoreCase);

        // [찾기:dedup감사] 별칭은 소스→타겟 방향이라, 타겟 컬럼명으로 소스명을 되찾으려면 역방향이 필요하다.
        private static Dictionary<string, string>? _reverseAliases;

        /// <summary>
        /// 타겟 컬럼명에 대응하는 소스 컬럼명을 돌려준다(별칭에 없으면 이름이 같다고 본다).
        /// 같은 타겟명으로 가는 소스 별칭이 여럿이면 되돌릴 수 없으므로 그대로 둔다 —
        /// 잘못 되돌리느니 이름이 같다고 두고 호출부가 "소스에 없는 컬럼" 으로 드러내는 편이 낫다.
        /// </summary>
        public static string SourceNameOf(string targetColumn)
        {
            if (_reverseAliases == null)
            {
                var rev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (src, tgt) in Aliases)
                {
                    if (!rev.TryAdd(tgt, src)) dup.Add(tgt);
                }
                foreach (var d in dup) rev.Remove(d);
                _reverseAliases = rev;
            }
            return _reverseAliases.TryGetValue(targetColumn, out var s) ? s : targetColumn;
        }

        private static Dictionary<string, string>? _targetOnly;
        public static IReadOnlyDictionary<string, string> TargetOnlyNotes =>
            _targetOnly ??= new Dictionary<string, string>(Model.TargetOnlyNotes, StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? _dropped;
        public static IReadOnlySet<string> DroppedColumns =>
            _dropped ??= new HashSet<string>(Model.Dropped.Keys, StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? _excluded;
        public static IReadOnlySet<string> ExcludedTables =>
            _excluded ??= new HashSet<string>(Model.Excluded.Keys, StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? _refOnly;
        public static IReadOnlySet<string> ReferenceOnlyTables =>
            _refOnly ??= new HashSet<string>(Model.ReferenceOnly.Keys, StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? _encMs;
        public static IReadOnlySet<string> EncryptedMsSql =>
            _encMs ??= new HashSet<string>(Model.Encrypted.Mssql, StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? _encOra;
        public static IReadOnlySet<string> EncryptedOracle =>
            _encOra ??= new HashSet<string>(Model.Encrypted.Oracle, StringComparer.OrdinalIgnoreCase);

        /// <summary>레거시 .grm 헤더 cGram 태그 → NAIMS2 GRAM_TYPE 코드.</summary>
        public static int? GramTypeCode(string cGramTag)
        {
            if (string.IsNullOrEmpty(cGramTag)) return null;
            var t = cGramTag.Trim().ToUpperInvariant();
            // "V1".."V4" 는 두 글자, 그 외(L/D)는 첫 글자로 판정
            if (Model.GramTypeMap.TryGetValue(t, out var v)) return v;
            var head = t.Substring(0, 1);
            return Model.GramTypeMap.TryGetValue(head, out var v2) ? v2 : (int?)null;
        }

        /// <summary>REF_TARGET_TYPE 코드 (VESSEL=5 등).</summary>
        public static int RefTargetType(string name) =>
            Model.RefTargetTypeCodes.TryGetValue(name.ToUpperInvariant(), out var v) ? v : -1;

        public static RuleColumn? ColumnRule(string targetTable, string targetColumn) =>
            ColumnRules.TryGetValue($"{targetTable}.{targetColumn}", out var r) ? r : null;

        public static string? FixedDefault(string targetTable, string targetColumn) =>
            FixedDefaults.TryGetValue($"{targetTable}.{targetColumn}", out var v) ? v : null;

        public static bool IsClassesFillColumn(string targetColumn) =>
            !Model.ClassesFillDefault.Except.Contains(targetColumn, StringComparer.OrdinalIgnoreCase);

        /// <summary>COMMENT 계열 컬럼 이관 시 붙일 말머리(예: "[NAIMS-I]").</summary>
        public static string CommentPrefix => Model.CommentPrefix;

        private static HashSet<string>? _hidden;
        public static IReadOnlySet<string> HiddenTargetColumns =>
            _hidden ??= new HashSet<string>(Model.HiddenTargetColumns, StringComparer.OrdinalIgnoreCase);

        public static bool IsHidden(string targetTable, string targetColumn) =>
            HiddenTargetColumns.Contains($"{targetTable}.{targetColumn}");

        /// <summary>대상 컬럼이 COMMENT 계열(말머리 대상)인지. REF_COMMENT 는 고정값이라 제외.</summary>
        public static bool IsCommentColumn(string targetColumn) =>
            targetColumn.EndsWith("COMMENT", StringComparison.OrdinalIgnoreCase)
            && !targetColumn.Equals("REF_COMMENT", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 레거시 COMMENT 값에 말머리를 붙인다. 모든 이관 트랙(DB→DB / 원음 / 발췌)이 공통으로 호출한다.
        /// 빈 값·이미 말머리가 있는 값은 그대로 둔다.
        /// </summary>
        public static string? ApplyCommentPrefix(string? legacyComment)
        {
            var pfx = CommentPrefix;
            if (string.IsNullOrEmpty(pfx)) return legacyComment;
            var v = legacyComment ?? "";
            if (v.StartsWith(pfx, StringComparison.Ordinal)) return v;   // 중복 방지
            return string.IsNullOrWhiteSpace(v) ? pfx : pfx + " " + v;
        }
    }
}
