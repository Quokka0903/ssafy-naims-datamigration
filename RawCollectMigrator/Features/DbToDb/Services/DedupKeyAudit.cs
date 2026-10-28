using System.Text;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:dedup감사] [NAIMS-ISSUE:DEDUP-KEY-VERIFY]
    /// <summary>
    /// 이관 계획의 dedup 키가 <b>실제 소스 데이터에서 고유한지</b> 세어 본다.
    ///
    /// 왜 이 검사가 따로 필요한가 — 키가 고유하지 않으면 dedup 은 "중복 방지" 가 아니라 "행 삭제" 가 된다.
    /// 그런데 그 손실은 <b>어떤 게이트에도 걸리지 않는다</b>. 스킵도 오류도 아니고 "재사용" 으로 집계되기
    /// 때문이다. 실측(2026-08-19): TR_FRAME 의 키를 MFB_NO 로 잡았는데 그 컬럼은 순번이 아니라
    /// "유효 MFB 밴드 개수" 라 495행 전부 32 였다. 495행이 3행으로 뭉개졌고, 게이트에는
    /// "재사용 492건" 이라는 정상적으로 보이는 숫자로 나왔다. 커밋했으면 특징벡터 99% 가 조용히 사라졌다.
    ///
    /// 컬럼 이름만 보고는 알 수 없다(HARM_NO 는 순번이 맞았고 MFB_NO 는 개수였다).
    /// 소스에서 직접 세는 것 말고는 드러낼 방법이 없다.
    ///
    /// <b>실행기의 빈 값 가드를 그대로 반영한다</b> — 키 값이 하나라도 비면 실행기는 대조를 포기하고
    /// INSERT 한다(DEDUP-NULL-COLLAPSE). 그래서 그런 행은 붕괴 계산에서 제외한다.
    /// 그러지 않으면 "실제로는 안 사라지는데 사라진다" 고 과대 보고해, 진짜 붕괴가 묻힌다.
    ///
    /// 소스 읽기 전용.
    /// </summary>
    public static class DedupKeyAudit
    {
        /// <param name="SourceTable">소스 테이블.</param>
        /// <param name="Kind">복합키 / FK튜플 / 자연키.</param>
        /// <param name="Columns">실제로 대조에 쓰이는 소스 컬럼들(부모 FK 포함).</param>
        /// <param name="Rows">대조 대상 행 수(키가 빈 행 제외).</param>
        /// <param name="Combos">그 행들의 고유 키 조합 수.</param>
        /// <param name="Skipped">키가 비어 대조에서 빠지는 행 수(실행기가 INSERT 하는 행).</param>
        public sealed record KeyAudit(
            string SourceTable, string Kind, IReadOnlyList<string> Columns,
            long Rows, long Combos, long Skipped, string? Error = null)
        {
            /// <summary>이 키로 dedup 하면 사라질 행 수.</summary>
            public long Collapse => Error == null ? Math.Max(0, Rows - Combos) : 0;

            /// <summary>키가 사실상 상수라 부모당 1행만 남는 수준인가(가장 위험한 형태).</summary>
            public bool NearConstant => Error == null && Rows >= 10 && Combos > 0 && Rows / Combos >= 10;

            public string Verdict =>
                Error != null   ? "조회 실패"
                : Rows == 0     ? "소스 비어있음"
                : Collapse == 0 ? "고유"
                : NearConstant  ? "사실상 상수 — 전량 붕괴"
                                : "중복";
        }

        /// <summary>
        /// 전 테이블의 dedup 키 고유성을 검사한다.
        /// </summary>
        /// <param name="opt">소스 접속 정보.</param>
        /// <param name="log">진행 로그.</param>
        /// <param name="map">
        /// 방금 산출한 컬럼 매핑. 계획의 dedup 키는 <b>타겟</b> 컬럼명이라 소스명으로 되돌려야 하는데,
        /// 별칭표는 소스→타겟 단방향이고 같은 소스명이 표마다 다른 타겟명으로 가기도 한다
        /// (GRAM_FILENAME → STD_NB_GRAM_SNIPPET_FILE_NAME / GRAM_FILE_NAME).
        /// 그래서 별칭 역추적 대신 <b>표 단위로 확정된 이 매핑</b>을 쓴다.
        /// </param>
        public static async Task<List<KeyAudit>> RunAsync(
            DbToDbOptions opt, SchemaMapResult map, Action<string> log, CancellationToken ct = default)
        {
            var result = new List<KeyAudit>();
            var plans = MigrationPlan.BuildOrder(MigrationPlan.Defaults());

            // (타겟표, 타겟컬럼) → 소스컬럼
            var t2s = new Dictionary<(string, string), string>();
            foreach (var r in map.Rows)
                if (r.SourceColumn.Length > 0 && r.TargetColumn.Length > 0)
                    t2s[(r.TargetTable.ToUpperInvariant(), r.TargetColumn.ToUpperInvariant())] = r.SourceColumn;

            await using var cn = DbProvider.CreateConnection(opt.SourceKind, opt.SourceMode, opt.SourceConnStr);
            await cn.OpenAsync(ct);

            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                if (opt.ExcludeCategories.Count > 0 &&
                    MigrationPlan.Excluded.Contains(plan.SourceTable)) continue;

                var (kind, cols) = KeyColumnsFor(plan, t2s);
                if (cols.Count == 0) continue;

                try
                {
                    var srcCols = await TableInspector.GetColumnsAsync(
                        opt.SourceKind, opt.SourceMode, opt.SourceConnStr, plan.SourceTable, ct);
                    var have = srcCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // 매핑에서 온 이름이 소스에 없으면 검사 자체가 성립하지 않는다 — 조용히 넘기지 않는다.
                    var missing = cols.Where(c => !have.Contains(c)).ToList();
                    if (missing.Count > 0)
                    {
                        result.Add(new KeyAudit(plan.SourceTable, kind, cols, 0, 0, 0,
                            $"소스에 없는 컬럼: {string.Join(", ", missing)}"));
                        continue;
                    }

                    string tbl = DbProvider.QuoteIdent(opt.SourceKind, plan.SourceTable);
                    string colList = string.Join(", ", cols.Select(c => DbProvider.QuoteColumn(opt.SourceKind, c)));
                    string usable = UsableWhere(opt.SourceKind, srcCols, cols);

                    long total   = await ScalarAsync(cn, $"SELECT COUNT(*) FROM {tbl}", ct);
                    long rows    = await ScalarAsync(cn, $"SELECT COUNT(*) FROM {tbl} WHERE {usable}", ct);
                    long combos  = await ScalarAsync(cn,
                        $"SELECT COUNT(*) FROM (SELECT DISTINCT {colList} FROM {tbl} WHERE {usable}) x", ct);

                    result.Add(new KeyAudit(plan.SourceTable, kind, cols, rows, combos, total - rows));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Add(new KeyAudit(plan.SourceTable, kind, cols, 0, 0, 0,
                        ExtractRaw.ExceptionText.Describe(ex)));
                }
            }

            int bad = result.Count(r => r.Collapse > 0);
            log(bad == 0
                ? $"  dedup 키 고유성: {result.Count}개 표 전부 이상 없음"
                : $"  ! dedup 키 고유성: {bad}개 표에서 붕괴 예상 — 아래 내역 확인");
            foreach (var r in result.Where(r => r.Collapse > 0 || r.Error != null))
                log($"    · {r.SourceTable}: {r.Rows:N0}행 → {r.Combos:N0}조합  " +
                    $"[{r.Verdict}] {r.Collapse:N0}행 유실 예상  ({string.Join(", ", r.Columns)})"
                    + (r.Error != null ? $"  — {r.Error}" : ""));

            return result;
        }

        /// <summary>실행기와 같은 규칙으로 이 표의 대조 컬럼을 정한다.</summary>
        private static (string Kind, List<string> Cols) KeyColumnsFor(
            TablePlan plan, IReadOnlyDictionary<(string, string), string> t2s)
        {
            // 실행기의 FindExistingByCompositeKeyAsync 와 같은 부모 FK 선택 규칙
            var pfk = plan.SubtreeParentColumn.Length > 0
                ? plan.ForeignKeys.FirstOrDefault(f =>
                      f.SourceColumn.Equals(plan.SubtreeParentColumn, StringComparison.OrdinalIgnoreCase))
                : plan.ForeignKeys.FirstOrDefault(f => !f.Optional) ?? plan.ForeignKeys.FirstOrDefault();

            if (plan.DedupKeyColumns.Count > 0)
            {
                var cols = new List<string>();
                if (pfk != null) cols.Add(pfk.SourceColumn);
                // 매핑표에 있으면 그것이 정답(표 단위 확정). 없을 때만 별칭 역추적으로 보완한다.
                cols.AddRange(plan.DedupKeyColumns.Select(k =>
                    t2s.TryGetValue((plan.TargetTable.ToUpperInvariant(), k.ToUpperInvariant()), out var sc)
                        ? sc : MigrationRules.SourceNameOf(k)));
                return ("복합키", cols);
            }
            if (plan.DedupByForeignKeys && plan.ForeignKeys.Count > 0)
                return ("FK튜플", plan.ForeignKeys.Select(f => f.SourceColumn).ToList());
            if (plan.SourceKeyColumn.Length > 0)
                return ("자연키", new List<string> { plan.SourceKeyColumn });

            return ("", new List<string>());
        }

        /// <summary>
        /// 실행기가 실제로 대조하는 행만 남기는 조건 — 키 값이 하나라도 비면 대조하지 않는다.
        /// 문자열은 공백만 있는 경우도 '빈 값' 으로 본다(레거시 CHAR 고정폭 패딩 때문).
        /// </summary>
        private static string UsableWhere(DbProviderKind kind, IReadOnlyList<ColumnInfo> srcCols, IReadOnlyList<string> cols)
        {
            var sb = new StringBuilder();
            foreach (var c in cols)
            {
                var q = DbProvider.QuoteColumn(kind, c);
                if (sb.Length > 0) sb.Append(" AND ");
                sb.Append(q).Append(" IS NOT NULL");

                var info = srcCols.FirstOrDefault(x => x.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
                bool isText = info != null && info.DataType.Contains("char", StringComparison.OrdinalIgnoreCase);
                if (isText)
                    sb.Append(kind == DbProviderKind.Oracle
                        ? $" AND TRIM({q}) IS NOT NULL"
                        : $" AND LTRIM(RTRIM({q})) <> ''");
            }
            return sb.Length > 0 ? sb.ToString() : "1=1";
        }

        private static async Task<long> ScalarAsync(System.Data.Common.DbConnection cn, string sql, CancellationToken ct)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 180;
            var v = await cmd.ExecuteScalarAsync(ct);
            return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
        }
    }
}
