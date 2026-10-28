using System.Text.RegularExpressions;

namespace RawCollectMigrator.Features.DbToDb
{
    public enum MappingStatus
    {
        /// <summary>이름이 그대로 일치.</summary>
        Exact,
        /// <summary>규칙/별칭으로 매칭됨 (이름은 다르지만 대응 확정).</summary>
        Renamed,
        /// <summary>소스에만 존재 — 이관 시 버려지거나 수동 결정 필요.</summary>
        SourceOnly,
        /// <summary>타겟에만 존재 — 기본값/NULL/신규 산출 필요.</summary>
        TargetOnly
    }

    public sealed class ColumnMapping
    {
        public string SourceName { get; init; } = "";
        public string SourceType { get; init; } = "";
        public string TargetName { get; init; } = "";
        public string TargetType { get; init; } = "";
        public MappingStatus Status { get; init; }
        public string Rule { get; init; } = "";
        public string Note { get; init; } = "";

        public string SourceText => string.IsNullOrEmpty(SourceName) ? "-" : $"{SourceName}  ({SourceType})";
        public string TargetText => string.IsNullOrEmpty(TargetName) ? "-" : $"{TargetName}  ({TargetType})";

        public string StatusText => Status switch
        {
            MappingStatus.Exact      => "일치",
            MappingStatus.Renamed    => "이름변경",
            MappingStatus.SourceOnly => "소스에만",
            MappingStatus.TargetOnly => "타겟에만",
            _ => ""
        };

        /// <summary>DataGrid 행 색상 트리거용.</summary>
        public string StatusKey => Status.ToString();

        /// <summary>타입이 서로 호환되지 않아 변환이 필요한지.</summary>
        public bool TypeChanged =>
            !string.IsNullOrEmpty(SourceType) &&
            !string.IsNullOrEmpty(TargetType) &&
            !string.Equals(SourceType, TargetType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 구 NAIMS → NAIMS2 컬럼 매칭기.
    ///
    /// 실측(2026-07-21)으로 확인된 신규 DB 명명 규칙을 규칙화한다:
    ///   * 기본키          : &lt;TABLE&gt;_ID       -> RECORD_ID
    ///   * 외래키          : &lt;X&gt;_ID           -> REF_STD_&lt;대상테이블&gt;_ID
    ///   * 변경일시        : DATE_SET_CHNG     -> CHNG_DATETIME
    ///   * 코멘트          : COMMENTS(n)       -> COMMENT(n)
    ///   * 일자 -> 일시    : DET_DATE          -> DET_DATETIME
    ///   * 개수 표기       : NO_FRAME          -> FRAME_NUM
    ///   * 시각 단위 명시  : A_START_TIME      -> A_START_MILLISEC
    ///   * 스니펫 파일명   : GRAM_FILENAME     -> STD_*_GRAM_SNIPPET_FILE_NAME
    ///                       (실제 바이너리는 CLASSIFI_* FILESTREAM 컬럼에 별도 적재)
    ///   * 폐기            : SELF_DB (신규 체계에서 사용 안 함)
    /// </summary>
    public static class ColumnMapper
    {
        // 규칙은 단일 원본(mapping_rules.json)에서 로드한다. 하드코딩하지 않는다.
        // (2026-07-23 SSOT 통합 — Python 문서 파이프라인과 동일 JSON)

        /// <summary>신규 체계에서 사용하지 않는 소스 컬럼.</summary>
        private static IReadOnlySet<string> DroppedSourceColumns => MigrationRules.DroppedColumns;

        /// <summary>소스에 대응이 없고 타겟에서 새로 채우는 컬럼(설명).</summary>
        private static IReadOnlyDictionary<string, string> TargetOnlyNotes =>
            MigrationRules.TargetOnlyNotes;

        /// <summary>고정 별칭 (소스 → 타겟).</summary>
        private static IReadOnlyDictionary<string, string> Aliases => MigrationRules.Aliases;

        /// <summary>
        /// 소스/타겟 컬럼 목록으로 매핑을 만든다.
        /// </summary>
        /// <param name="sourceCols">소스 테이블 컬럼(순서 유지)</param>
        /// <param name="targetCols">타겟 테이블 컬럼</param>
        /// <param name="sourceTable">소스 테이블명 (PK 추론용)</param>
        public static List<ColumnMapping> Build(
            IReadOnlyList<ColumnInfo> sourceCols,
            IReadOnlyList<ColumnInfo> targetCols,
            string sourceTable)
        {
            var result = new List<ColumnMapping>();
            var targetByName = targetCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── 1패스: 이름 정확 일치(대소문자 무시)를 전부 먼저 예약한다.
            //    앞 순서 소스 컬럼이 별칭/정규화 규칙으로 뒤 컬럼의 정확 일치 대상을
            //    선점하는 탐욕 순회 문제 방지 (예: COMMENTS 별칭이 COMMENT 를 선점
            //    → 소스 COMMENT 가 미발견 처리되던 버그).
            var exactReserved = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sourceCols)
            {
                if (DroppedSourceColumns.Contains(s.Name)) continue;   // 폐기 컬럼은 예약 대상 아님
                if (targetByName.TryGetValue(s.Name, out var t) && !usedTargets.Contains(t.Name))
                {
                    exactReserved[s.Name] = t;
                    usedTargets.Add(t.Name);
                }
            }

            // ── 2패스: 소스 순서대로 결과 생성. 예약된 정확 일치를 우선 사용하고,
            //    나머지 컬럼에만 별칭/PK/FK/파일명/정규화 규칙을 적용한다.
            foreach (var s in sourceCols)
            {
                // 신규 체계에서 폐기된 컬럼
                if (DroppedSourceColumns.Contains(s.Name))
                {
                    result.Add(new ColumnMapping
                    {
                        SourceName = s.Name, SourceType = s.TypeText,
                        Status = MappingStatus.SourceOnly,
                        Rule = "폐기",
                        Note = "신규 체계에서 사용하지 않음."
                    });
                    continue;
                }

                ColumnInfo? target;
                string rule;
                if (exactReserved.TryGetValue(s.Name, out var reserved))
                {
                    (target, rule) = (reserved, "동일");
                }
                else
                {
                    (target, rule) = FindTarget(s, sourceCols, targetCols, targetByName, usedTargets, sourceTable);
                    if (target != null) usedTargets.Add(target.Name);
                }

                if (target == null)
                {
                    result.Add(new ColumnMapping
                    {
                        SourceName = s.Name, SourceType = s.TypeText,
                        Status = MappingStatus.SourceOnly,
                        Rule = "-",
                        Note = "대응 컬럼 미발견 — 수동 확인 필요."
                    });
                    continue;
                }

                bool exact = string.Equals(s.Name, target.Name, StringComparison.OrdinalIgnoreCase);

                result.Add(new ColumnMapping
                {
                    SourceName = s.Name, SourceType = s.TypeText,
                    TargetName = target.Name, TargetType = target.TypeText,
                    Status = exact ? MappingStatus.Exact : MappingStatus.Renamed,
                    Rule = exact ? "동일" : rule,
                    Note = BuildTypeNote(s, target)
                });
            }

            // 타겟에만 있는 컬럼
            foreach (var t in targetCols)
            {
                if (usedTargets.Contains(t.Name)) continue;
                TargetOnlyNotes.TryGetValue(t.Name, out var note);
                result.Add(new ColumnMapping
                {
                    TargetName = t.Name, TargetType = t.TypeText,
                    Status = MappingStatus.TargetOnly,
                    Rule = "신규",
                    Note = note ?? "소스에 대응 없음 — 기본값/NULL 또는 산출값."
                });
            }

            return result;
        }

        private static (ColumnInfo? target, string rule) FindTarget(
            ColumnInfo s,
            IReadOnlyList<ColumnInfo> sourceCols,
            IReadOnlyList<ColumnInfo> targetCols,
            Dictionary<string, ColumnInfo> targetByName,
            HashSet<string> used,
            string sourceTable)
        {
            // 1) 이름 그대로 일치
            if (targetByName.TryGetValue(s.Name, out var exact) && !used.Contains(exact.Name))
                return (exact, "동일");

            // 2) 고정 별칭
            if (Aliases.TryGetValue(s.Name, out var alias)
                && targetByName.TryGetValue(alias, out var aliased)
                && !used.Contains(aliased.Name))
                return (aliased, "별칭");

            // 3) 기본키: 소스의 첫 컬럼이면서 <TABLE>_ID 형태 -> RECORD_ID
            bool isFirst = sourceCols.Count > 0
                        && string.Equals(sourceCols[0].Name, s.Name, StringComparison.OrdinalIgnoreCase);
            if (isFirst && s.Name.EndsWith("_ID", StringComparison.OrdinalIgnoreCase)
                && targetByName.TryGetValue("RECORD_ID", out var pk) && !used.Contains(pk.Name))
                return (pk, "PK→RECORD_ID");

            // 4) 외래키: <X>_ID -> REF_..._ID  (어간 유사도로 후보 선정)
            //    타겟 컬럼 순서에 의존하던 FirstOrDefault 를 결정적 선택으로 교체:
            //    ① 정규화 정확 일치(core==stem) 우선
            //    ② 없으면 접두 매치 중 어간 길이 차가 최소인 것 (동률이면 매칭 포기 —
            //       틀린 FK 로 이관하는 것보다 미발견 처리가 안전하다)
            if (s.Name.EndsWith("_ID", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Normalize(s.Name[..^3]);   // "_ID" 제거
                var candidates = targetCols
                    .Where(t => !used.Contains(t.Name) &&
                                t.Name.StartsWith("REF_", StringComparison.OrdinalIgnoreCase) &&
                                t.Name.EndsWith("_ID", StringComparison.OrdinalIgnoreCase))
                    .Select(t => (Col: t, Core: RefCore(t.Name)))
                    .ToList();

                // ① 정규화 정확 일치
                var exacts = candidates.Where(x => x.Core == stem).ToList();
                if (exacts.Count == 1) return (exacts[0].Col, "FK→REF_*_ID");
                if (exacts.Count > 1) return (null, "");   // 정확 후보가 복수 — 모호하므로 포기

                // ② 접두 매치 (복수형 차이 등): 길이 차 최소인 후보를 결정적으로 선택
                var prefixes = candidates
                    .Where(x => x.Core.StartsWith(stem, StringComparison.Ordinal)
                             || stem.StartsWith(x.Core, StringComparison.Ordinal))
                    .Select(x => (x.Col, Diff: Math.Abs(x.Core.Length - stem.Length)))
                    .ToList();
                if (prefixes.Count > 0)
                {
                    int minDiff = prefixes.Min(x => x.Diff);
                    var best = prefixes.Where(x => x.Diff == minDiff).ToList();
                    if (best.Count == 1) return (best[0].Col, "FK→REF_*_ID");
                    return (null, "");   // 길이 차 동률 — 순서 의존 오매칭 위험, 미발견 처리
                }
            }

            // 5) 스니펫 파일명: <X>_FILENAME -> *_FILE_NAME  (뒷부분 키워드 일치)
            if (s.Name.EndsWith("FILENAME", StringComparison.OrdinalIgnoreCase))
            {
                var kind = s.Name.Replace("_FILENAME", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("FILENAME", "", StringComparison.OrdinalIgnoreCase);
                var fileCol = targetCols.FirstOrDefault(t =>
                    !used.Contains(t.Name) &&
                    t.Name.EndsWith("FILE_NAME", StringComparison.OrdinalIgnoreCase) &&
                    (kind.Length == 0 || t.Name.Contains(kind, StringComparison.OrdinalIgnoreCase)));
                if (fileCol != null) return (fileCol, "파일명");
            }

            // 6) 밑줄/복수형을 무시한 정규화 일치
            var norm = Normalize(s.Name);
            var loose = targetCols.FirstOrDefault(t =>
                !used.Contains(t.Name) && Normalize(t.Name) == norm);
            if (loose != null) return (loose, "정규화");

            return (null, "");
        }

        /// <summary>REF_STD_CLASSES_ID → 정규화된 어간("CLASS") 추출. FK 후보 비교용.</summary>
        private static string RefCore(string refColumn)
        {
            // REF_STD_CLASSES_ID -> "CLASSES" -> Normalize -> "CLASS"
            var core = refColumn;
            core = Regex.Replace(core, "^REF_", "", RegexOptions.IgnoreCase);
            core = Regex.Replace(core, "_ID$", "", RegexOptions.IgnoreCase);
            core = Regex.Replace(core, "^STD_", "", RegexOptions.IgnoreCase);
            return Normalize(core);
        }

        /// <summary>밑줄 제거 + 대문자화 + 흔한 복수형 정규화.</summary>
        private static string Normalize(string name)
        {
            var s = name.Replace("_", "").ToUpperInvariant();
            if (s.EndsWith("IES", StringComparison.Ordinal)) s = s[..^3] + "Y";
            else if (s.EndsWith("ES", StringComparison.Ordinal) && s.Length > 4) s = s[..^2];
            else if (s.EndsWith("S", StringComparison.Ordinal) && s.Length > 3) s = s[..^1];
            return s;
        }

        private static string BuildTypeNote(ColumnInfo s, ColumnInfo t)
        {
            if (string.Equals(s.DataType, t.DataType, StringComparison.OrdinalIgnoreCase))
            {
                // 길이 축소 확인
                if (s.MaxLength.HasValue && t.MaxLength.HasValue
                    && t.MaxLength.Value > 0 && s.MaxLength.Value > t.MaxLength.Value)
                    return $"길이 축소 {s.MaxLength}→{t.MaxLength} — 잘림 주의.";
                return "";
            }
            return $"타입 변경 {s.DataType} → {t.DataType}";
        }
    }
}
