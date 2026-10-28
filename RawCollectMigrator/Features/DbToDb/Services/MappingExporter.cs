using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:매핑리스트] [GUIDE:MAPPING-EXPORT] [찾기:매핑리스트] [찾기:사후매핑]
    //  ▣ 이 파일 = "매핑 리스트" 두 종류를 파일로 떨어뜨린다. 둘은 성격이 다르다.
    //
    //    ① 사전 매핑 리스트 (스키마)  — 이관 "전"
    //       소스테이블.컬럼 → 타겟테이블.컬럼 + 상태(일치/이름변경/소스에만/타겟에만) + 규칙
    //       근거: 소스 INFORMATION_SCHEMA 실측 + 타겟 실측 + mapping_rules.json(SSOT)
    //       용도: 담당자 검토, 게이트 G2 판정(타겟 미확정·미결 규칙 적발)
    //
    //    ② 사후 매핑 리스트 (레코드)  — Commit "후"
    //       소스 PK → 타겟 RECORD_ID + 신규/재사용 구분
    //       근거: 이번 실행이 실제로 기록한 TableResult.IdPairs
    //       용도: "무엇이 무엇으로 옮겨졌는가"의 증거. 검증·역추적의 유일한 근거다.
    //
    //  ▶ ②는 반드시 Commit 실행 결과로만 만든다. Trial 의 RECORD_ID 는 롤백돼 존재하지 않는다.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>사전 매핑 리스트의 한 행(컬럼 1개).</summary>
    public sealed class SchemaMapRow
    {
        public string SourceTable { get; init; } = "";
        public string SourceColumn { get; init; } = "";
        public string SourceType { get; init; } = "";
        public string TargetTable { get; init; } = "";
        public string TargetColumn { get; init; } = "";
        public string TargetType { get; init; } = "";
        /// <summary>일치 / 이름변경 / 소스에만 / 타겟에만.</summary>
        public string Status { get; init; } = "";
        /// <summary>mapping_rules.json 의 산출·변환 규칙(없으면 빈 문자열).</summary>
        public string Rule { get; init; } = "";
        /// <summary>규칙 판정(확정/미결/제외 등).</summary>
        public string Verdict { get; init; } = "";
        public string Note { get; init; } = "";
        /// <summary>FK·암호화·숨김·고정기본값·dedup키 등 부가 표식(쉼표 구분).</summary>
        public string Flags { get; init; } = "";
    }

    /// <summary>사전 매핑 리스트의 테이블 단위 요약.</summary>
    public sealed class SchemaMapTable
    {
        public string SourceTable { get; init; } = "";
        public string TargetTable { get; init; } = "";
        public string Category { get; init; } = "";
        public int Level { get; init; }
        public long? SourceRows { get; set; }
        public long? TargetRows { get; set; }
        public int Exact { get; set; }
        public int Renamed { get; set; }
        public int SourceOnly { get; set; }
        public int TargetOnly { get; set; }
        /// <summary>이 테이블에서 발견된 문제(타겟 미확정, 소스 컬럼 부재 등).</summary>
        public List<string> Problems { get; } = new();
    }

    /// <summary>사전 매핑 리스트 산출 결과.</summary>
    public sealed class SchemaMapResult
    {
        public List<SchemaMapTable> Tables { get; } = new();
        public List<SchemaMapRow> Rows { get; } = new();
        /// <summary>전체 수준 문제(게이트 G2 판정 근거).</summary>
        public List<string> Problems { get; } = new();

        public int TotalRows => Rows.Count;
        public int Unmapped => Rows.Count(r => r.Status == "소스에만");
        public int Pending => Rows.Count(r =>
            r.Verdict.Contains("미결") || r.Verdict.Contains("확인"));
        public int UndecidedTargets => Tables.Count(t => t.TargetTable.StartsWith('('));
    }

    /// <summary>매핑 리스트(사전 스키마 / 사후 레코드)를 만들어 파일로 남긴다.</summary>
    public static class MappingExporter
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            // 한글이 \uXXXX 로 깨지지 않게 — 사람이 읽는 산출물이다.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // ════════════════════════════════════════════════════════════
        //  ① 사전 매핑 리스트 (스키마)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 이관 대상 전 테이블을 순회하며 컬럼 매핑을 산출한다(소스·타겟 DB 읽기 전용).
        /// 실행기와 같은 경로(<see cref="ColumnMapper.Build"/> + <see cref="MigrationRules"/>)를 쓰므로,
        /// 여기 나온 대응이 곧 실제 INSERT 에 쓰일 대응이다.
        /// </summary>
        /// <param name="opt">소스/타겟 접속·제외 분류가 담긴 실행 옵션.</param>
        /// <param name="log">진행 로그 싱크(없으면 무시).</param>
        /// <param name="ct">취소 토큰.</param>
        public static async Task<SchemaMapResult> BuildSchemaMapAsync(
            DbToDbOptions opt, Action<string>? log = null, CancellationToken ct = default)
        {
            var say = log ?? (_ => { });
            var result = new SchemaMapResult();

            // 실행기와 동일한 대상 선정(분류 제외 + FK 위상 정렬)
            var excludedByCat = MigrationRules.Model.Plan
                .Where(p => opt.ExcludeCategories.Contains(p.Category))
                .Select(p => p.Source)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plans = MigrationPlan.BuildOrder(
                MigrationPlan.Defaults().Where(p => !excludedByCat.Contains(p.SourceTable)));

            say($"매핑 리스트 산출 — 대상 {plans.Count}개 테이블");

            TargetSchema tgt;
            try
            {
                tgt = await TargetSchema.LoadAsync(opt.TargetConnStr, plans.Select(p => p.TargetTable), ct);
            }
            catch (Exception ex)
            {
                result.Problems.Add("타겟 스키마 로드 실패 — " + ExtractRaw.ExceptionText.Describe(ex));
                return result;
            }

            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();

                var cat = MigrationRules.Model.Plan
                    .FirstOrDefault(p => p.Source.Equals(plan.SourceTable, StringComparison.OrdinalIgnoreCase))
                    ?.Category ?? "";

                var t = new SchemaMapTable
                {
                    SourceTable = plan.SourceTable,
                    TargetTable = plan.TargetTable,
                    Category = cat,
                    Level = plan.Level,
                };
                result.Tables.Add(t);

                // 타겟 미확정은 그 자체로 게이트 차단 사유다.
                if (plan.TargetTable.StartsWith('('))
                {
                    var msg = $"{plan.SourceTable}: 타겟 테이블 미확정 — {plan.TargetTable}";
                    t.Problems.Add(msg);
                    result.Problems.Add(msg);
                    continue;
                }

                List<ColumnInfo> srcCols;
                try
                {
                    srcCols = await TableInspector.GetColumnsAsync(
                        opt.SourceKind, opt.SourceMode, opt.SourceConnStr, plan.SourceTable, ct);
                }
                catch (Exception ex)
                {
                    var msg = $"{plan.SourceTable}: 소스 컬럼 조회 실패 — {ex.Message}";
                    t.Problems.Add(msg); result.Problems.Add(msg);
                    continue;
                }

                if (srcCols.Count == 0)
                {
                    var msg = $"{plan.SourceTable}: 소스 테이블이 없거나 컬럼을 읽지 못함";
                    t.Problems.Add(msg); result.Problems.Add(msg);
                    continue;
                }

                var tgtCols = tgt.Columns(plan.TargetTable);
                if (tgtCols.Count == 0)
                {
                    var msg = $"{plan.TargetTable}: 타겟 스키마 없음";
                    t.Problems.Add(msg); result.Problems.Add(msg);
                    continue;
                }

                // 플랜의 PK/FK 컬럼이 실제 소스에 있는지 — 실행 중이 아니라 여기서 먼저 잡는다.
                var srcNames = new HashSet<string>(srcCols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                if (!srcNames.Contains(plan.SourcePk))
                {
                    var msg = $"{plan.SourceTable}: 플랜 PK '{plan.SourcePk}' 가 소스에 없음";
                    t.Problems.Add(msg); result.Problems.Add(msg);
                }
                foreach (var fk in plan.ForeignKeys)
                {
                    if (!srcNames.Contains(fk.SourceColumn))
                    {
                        var msg = $"{plan.SourceTable}: 플랜 FK 컬럼 '{fk.SourceColumn}'(→{fk.ParentSourceTable}) 가 소스에 없음";
                        t.Problems.Add(msg); result.Problems.Add(msg);
                    }
                }
                // dedup 키가 타겟에 없으면 재실행 중복 위험 — 실행기와 같은 판정을 미리 알린다.
                var tgtNames = new HashSet<string>(tgtCols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var k in plan.DedupKeyColumns.Where(k => !tgtNames.Contains(k)))
                {
                    var msg = $"{plan.TargetTable}: dedup 키 '{k}' 가 타겟에 없음 — 복합키 멱등 비활성(재실행 중복 위험)";
                    t.Problems.Add(msg); result.Problems.Add(msg);
                }

                var tgtAsInfo = tgtCols.Select((c, i) => new ColumnInfo
                {
                    Name = c.Name, DataType = c.DataType, MaxLength = c.MaxLength, Ordinal = i,
                    Nullable = c.IsNullable ? "YES" : "NO",
                }).ToList();

                var mappings = ColumnMapper.Build(srcCols, tgtAsInfo, plan.SourceTable);
                var fkByTarget = plan.ForeignKeys.ToDictionary(f => f.TargetColumn, StringComparer.OrdinalIgnoreCase);
                var dedup = new HashSet<string>(plan.DedupKeyColumns, StringComparer.OrdinalIgnoreCase);

                foreach (var m in mappings)
                {
                    var cr = m.TargetName.Length > 0
                        ? MigrationRules.ColumnRule(plan.TargetTable, m.TargetName) : null;

                    var flags = new List<string>();
                    if (m.TargetName.Length > 0 && fkByTarget.ContainsKey(m.TargetName)) flags.Add("FK");
                    if (m.TargetName.Length > 0 && MigrationRules.IsHidden(plan.TargetTable, m.TargetName)) flags.Add("숨김");
                    if (m.TargetName.Length > 0 && MigrationRules.FixedDefault(plan.TargetTable, m.TargetName) != null) flags.Add("고정값");
                    if (m.TargetName.Length > 0 && dedup.Contains(m.TargetName)) flags.Add("dedup키");
                    if (m.SourceName.Length > 0 &&
                        CryptoContext.IsEncrypted(opt.SourceKind, plan.SourceTable, m.SourceName)) flags.Add("암호화");
                    if (m.SourceName.Equals(plan.SourcePk, StringComparison.OrdinalIgnoreCase)) flags.Add("소스PK");
                    if (plan.Reuse == ReusePolicy.ReuseByNaturalKey &&
                        m.SourceName.Equals(plan.SourceKeyColumn, StringComparison.OrdinalIgnoreCase)) flags.Add("자연키");

                    result.Rows.Add(new SchemaMapRow
                    {
                        SourceTable = plan.SourceTable,
                        SourceColumn = m.SourceName,
                        SourceType = m.SourceType,
                        TargetTable = plan.TargetTable,
                        TargetColumn = m.TargetName,
                        TargetType = m.TargetType,
                        Status = m.StatusText,
                        Rule = cr?.Rule ?? m.Rule,
                        Verdict = cr?.Verdict ?? "",
                        Note = cr?.Note ?? m.Note,
                        Flags = string.Join("+", flags),
                    });

                    switch (m.Status)
                    {
                        case MappingStatus.Exact: t.Exact++; break;
                        case MappingStatus.Renamed: t.Renamed++; break;
                        case MappingStatus.SourceOnly: t.SourceOnly++; break;
                        case MappingStatus.TargetOnly: t.TargetOnly++; break;
                    }
                }

                // 행수(대조용). 실패해도 매핑 산출은 계속한다.
                t.SourceRows = await TableInspector.TryGetRowCountAsync(
                    opt.SourceKind, opt.SourceMode, opt.SourceConnStr, plan.SourceTable, ct);
                t.TargetRows = await TableInspector.TryGetRowCountAsync(
                    DbProviderKind.MsSql, DbConnectionMode.Direct, opt.TargetConnStr, plan.TargetTable, ct);

                say($"  · {plan.SourceTable} → {plan.TargetTable} : 매핑 {t.Exact + t.Renamed} " +
                    $"(일치 {t.Exact}/이름변경 {t.Renamed}) · 소스에만 {t.SourceOnly} · 타겟에만 {t.TargetOnly}" +
                    $" · 소스 {t.SourceRows?.ToString("N0") ?? "?"}행 / 타겟 {t.TargetRows?.ToString("N0") ?? "?"}행");
            }

            say($"매핑 리스트 완료 — 컬럼 {result.TotalRows}행 / 문제 {result.Problems.Count}건");
            return result;
        }

        /// <summary>사전 매핑 리스트를 TSV + JSON 두 형식으로 쓴다.</summary>
        /// <param name="map">산출 결과.</param>
        /// <param name="dir">저장 폴더(없으면 만든다).</param>
        /// <param name="baseName">파일 이름(확장자 제외).</param>
        /// <returns>쓴 파일 경로들.</returns>
        public static List<string> WriteSchemaMap(SchemaMapResult map, string dir, string baseName = "02_mapping_schema")
        {
            Directory.CreateDirectory(dir);
            var written = new List<string>();

            var tsv = Path.Combine(dir, baseName + ".tsv");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t",
                "소스테이블", "소스컬럼", "소스타입", "타겟테이블", "타겟컬럼", "타겟타입",
                "상태", "규칙", "판정", "표식", "비고"));
            foreach (var r in map.Rows)
                sb.AppendLine(string.Join("\t",
                    Cell(r.SourceTable), Cell(r.SourceColumn), Cell(r.SourceType),
                    Cell(r.TargetTable), Cell(r.TargetColumn), Cell(r.TargetType),
                    Cell(r.Status), Cell(r.Rule), Cell(r.Verdict), Cell(r.Flags), Cell(r.Note)));
            File.WriteAllText(tsv, sb.ToString(), new UTF8Encoding(true));   // BOM: 엑셀에서 한글 깨짐 방지
            written.Add(tsv);

            var json = Path.Combine(dir, baseName + ".json");
            File.WriteAllText(json, JsonSerializer.Serialize(new
            {
                generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                summary = new
                {
                    tables = map.Tables.Count,
                    columns = map.TotalRows,
                    unmappedSourceColumns = map.Unmapped,
                    pendingVerdicts = map.Pending,
                    undecidedTargets = map.UndecidedTargets,
                    problems = map.Problems.Count,
                },
                tables = map.Tables,
                problems = map.Problems,
                rows = map.Rows,
            }, Json), new UTF8Encoding(false));
            written.Add(json);

            return written;
        }

        // ════════════════════════════════════════════════════════════
        //  ② 사후 매핑 리스트 (레코드)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Commit 결과의 (소스 PK → 타겟 RECORD_ID) 짝을 파일로 쓴다.
        /// 신규 INSERT 인지 기존 행 재사용인지 구분해 남긴다 — 재실행 멱등이 실제로 걸렸는지의 증거다.
        /// </summary>
        /// <param name="report">Commit 실행이 돌려준 리포트.</param>
        /// <param name="dir">저장 폴더.</param>
        /// <param name="baseName">파일 이름(확장자 제외).</param>
        public static List<string> WriteRecordMap(
            MigrationReport report, string dir, string baseName = "06_mapping_records")
        {
            Directory.CreateDirectory(dir);
            var written = new List<string>();

            var rows = new List<object>();
            var tsv = Path.Combine(dir, baseName + ".tsv");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", "소스테이블", "소스PK컬럼", "소스PK", "타겟테이블", "타겟RECORD_ID", "구분"));

            foreach (var t in report.Tables)
            {
                foreach (var (src, tgt) in t.IdPairs)
                {
                    var kind = t.ReusedSourcePks.Contains(src) ? "재사용" : "신규";
                    sb.AppendLine(string.Join("\t",
                        Cell(t.Source), Cell(t.SourcePk), src, Cell(t.Target), tgt, kind));
                    rows.Add(new
                    {
                        sourceTable = t.Source,
                        sourcePkColumn = t.SourcePk,
                        sourcePk = src,
                        targetTable = t.Target,
                        targetRecordId = tgt,
                        kind,
                    });
                }
            }
            File.WriteAllText(tsv, sb.ToString(), new UTF8Encoding(true));
            written.Add(tsv);

            var json = Path.Combine(dir, baseName + ".json");
            File.WriteAllText(json, JsonSerializer.Serialize(new
            {
                generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                dryRun = report.DryRun,
                summary = new
                {
                    pairs = rows.Count,
                    inserted = report.TotalInserted,
                    reused = report.TotalReused,
                    skipped = report.TotalSkipped,
                    errors = report.TotalErrors,
                },
                tables = report.Tables.Select(t => new
                {
                    source = t.Source,
                    target = t.Target,
                    sourceRows = t.SourceRows,
                    inserted = t.Inserted,
                    reused = t.Reused,
                    skipped = t.Skipped,
                    derivedChildren = t.DerivedChildren,
                    errors = t.Errors.Count,
                    skipReasons = t.SkipReasons,
                    pairs = t.IdPairs.Count,
                }),
                pairs = rows,
            }, Json), new UTF8Encoding(false));
            written.Add(json);

            return written;
        }

        // ════════════════════════════════════════════════════════════
        //  ③ 빈 값으로 적재된 행 목록 (파일 부재)
        // ════════════════════════════════════════════════════════════

        // [NAIMS-ISSUE:EMPTY-ON-MISSING] [찾기:빈파일목록]
        /// <summary>
        /// 참조 파일이 없어 blob 을 빈 값으로 넣은 행 목록을 남긴다.
        ///
        /// 왜 필요한가 — 이 행들은 "이관됨"으로 집계되지만 실제 자료가 없다. 나중에 NAS 에서
        /// 파일을 확보했을 때 <b>어느 행을 다시 채워야 하는지</b> 특정할 수단이 있어야 한다.
        /// 이 목록이 없으면 DATALENGTH(blob)=0 으로 타겟을 뒤지는 수밖에 없고,
        /// 그건 "원래 빈 파일" 과 "못 찾은 파일" 을 구분하지 못한다.
        ///
        /// 부재 건이 하나도 없으면 파일을 만들지 않는다(빈 파일이 쌓이면 오히려 헷갈린다).
        /// </summary>
        /// <returns>쓴 파일 경로들(부재 0건이면 빈 목록).</returns>
        public static List<string> WriteMissingFileList(
            MigrationReport report, string dir, string baseName = "07_missing_files")
        {
            var written = new List<string>();
            var all = report.Tables
                .SelectMany(t => t.MissingFileRefs.Select(m => (t.Source, t.SourcePk, t.Target, m)))
                .ToList();
            if (all.Count == 0) return written;

            Directory.CreateDirectory(dir);

            var tsv = Path.Combine(dir, baseName + ".tsv");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t",
                "소스테이블", "소스PK컬럼", "소스PK", "타겟테이블", "부재종류", "파일명"));
            foreach (var (src, pkCol, tgt, m) in all)
                sb.AppendLine(string.Join("\t",
                    Cell(src), Cell(pkCol), m.SourcePk, Cell(tgt), Cell(m.Kind), Cell(m.FileName)));
            File.WriteAllText(tsv, sb.ToString(), new UTF8Encoding(true));
            written.Add(tsv);

            var json = Path.Combine(dir, baseName + ".json");
            File.WriteAllText(json, JsonSerializer.Serialize(new
            {
                generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                dryRun = report.DryRun,
                note = "이 행들은 적재됐지만 blob 이 비어 있습니다. 파일 확보 후 전량 재구축으로 채우세요.",
                summary = new
                {
                    refs = all.Count,
                    rows = all.Select(x => (x.Source, x.m.SourcePk)).Distinct().Count(),
                    byKind = all.GroupBy(x => x.m.Kind)
                                .ToDictionary(g => g.Key, g => g.Count()),
                    byTable = all.GroupBy(x => x.Source)
                                 .ToDictionary(g => g.Key, g => g.Count()),
                },
                items = all.Select(x => new
                {
                    sourceTable = x.Source,
                    sourcePkColumn = x.SourcePk,
                    sourcePk = x.m.SourcePk,
                    targetTable = x.Target,
                    kind = x.m.Kind,
                    fileName = x.m.FileName,
                }),
            }, Json), new UTF8Encoding(false));
            written.Add(json);

            return written;
        }

        /// <summary>탭·개행이 섞여 TSV 열이 깨지지 않게 치환한다.</summary>
        private static string Cell(string? s) =>
            (s ?? "").Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');
    }
}
