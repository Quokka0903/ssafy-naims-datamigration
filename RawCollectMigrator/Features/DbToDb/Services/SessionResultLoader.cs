using System.IO;
using System.Text.Json;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:사후조회]
    /// <summary>
    /// 지난 세션 폴더에서 이관 결과를 되살린다.
    ///
    /// 왜 필요한가 — 지금까지 결과 창은 <b>커밋 직후 메모리에 있는 리포트</b>로만 열렸다.
    /// 앱을 닫으면 그 결과를 다시 볼 방법이 없었고, "어제 이관한 게 어떻게 됐더라" 를
    /// 확인하려면 DB 를 직접 뒤지는 수밖에 없었다.
    ///
    /// 다행히 필요한 것은 이미 파일로 남아 있다 — <c>06_mapping_records.json</c> 에
    /// 표별 집계와 (소스 PK → 타겟 RECORD_ID) 짝이 전부 들어 있다.
    /// 그것으로 <see cref="MigrationReport"/> 를 재구성하면 결과 창·레코드 매칭·미리보기가
    /// 그대로 다시 동작한다. DB 를 다시 읽는 것이 아니라 <b>그때 무슨 일이 있었는지</b>를 읽는 것이다.
    ///
    /// 되살릴 수 없는 것은 분명히 해 둔다 — 오류 표본 문구, 스킵 사유 세부는
    /// 요약 파일에만 있고 여기서는 건수만 돌아온다. 없는 것을 있는 것처럼 만들지 않는다.
    /// </summary>
    public static class SessionResultLoader
    {
        /// <summary>세션 폴더 한 곳의 요약(고르기 화면에 뿌릴 정보).</summary>
        /// <param name="Dir">폴더 절대 경로.</param>
        /// <param name="SessionId">폴더명에서 뽑은 세션 ID.</param>
        /// <param name="When">생성 시각(파일 시각 기준).</param>
        /// <param name="HasRecords">레코드 짝 파일이 있는지 — 없으면 결과 창을 열어도 대조할 것이 없다.</param>
        public sealed record SessionInfo(string Dir, string SessionId, DateTime When, bool HasRecords,
                                         bool Committed, string Summary);

        /// <summary>logs 아래의 세션 폴더를 최신 순으로 나열한다.</summary>
        public static List<SessionInfo> List(string logsRoot)
        {
            var list = new List<SessionInfo>();
            if (!Directory.Exists(logsRoot)) return list;

            foreach (var dir in Directory.EnumerateDirectories(logsRoot, "session_*"))
            {
                try
                {
                    string id = Path.GetFileName(dir).Replace("session_", "");
                    bool rec = File.Exists(Path.Combine(dir, "06_mapping_records.json"));
                    bool committed = File.Exists(Path.Combine(dir, "05_commit.json"));
                    string summary = committed ? "커밋 완료" : rec ? "레코드 짝 있음" : "리허설만";

                    // 요약 한 줄을 붙일 수 있으면 붙인다(없어도 목록은 나와야 한다).
                    var sum = Path.Combine(dir, "00_summary.txt");
                    if (File.Exists(sum))
                    {
                        var first = File.ReadLines(sum).FirstOrDefault(l => l.Trim().Length > 0);
                        if (first is { Length: > 0 }) summary += " · " + first.Trim();
                    }

                    list.Add(new SessionInfo(dir, id, Directory.GetCreationTime(dir), rec, committed, summary));
                }
                catch { /* 한 폴더가 깨져도 나머지 목록은 보여야 한다 */ }
            }
            return list.OrderByDescending(s => s.When).ToList();
        }

        /// <summary>기본 logs 폴더(실행 파일 옆).</summary>
        public static string DefaultLogsRoot => Path.Combine(AppContext.BaseDirectory, "logs");

        /// <summary>
        /// 세션 폴더의 06_mapping_records.json 으로 리포트를 재구성한다.
        /// 파일이 없거나 형식이 다르면 null — 조용히 빈 리포트를 돌려주지 않는다
        /// (빈 결과 창은 "이관이 아무것도 안 됐다" 로 오해된다).
        /// </summary>
        public static MigrationReport? Load(string sessionDir, out string error)
        {
            error = "";
            var path = Path.Combine(sessionDir, "06_mapping_records.json");
            if (!File.Exists(path))
            {
                error = "이 세션에는 06_mapping_records.json 이 없습니다 — 커밋까지 간 세션만 되살릴 수 있습니다.";
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                var rep = new MigrationReport { DryRun = GetBool(root, "dryRun") };

                // 표별 집계
                var byTable = new Dictionary<string, TableResult>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tables.EnumerateArray())
                    {
                        var r = new TableResult
                        {
                            Source = GetStr(t, "source"),
                            Target = GetStr(t, "target"),
                            SourceRows = GetInt(t, "sourceRows"),
                        };
                        r.Inserted = GetInt(t, "inserted");
                        r.Reused = GetInt(t, "reused");
                        r.Skipped = GetInt(t, "skipped");
                        r.DerivedChildren = GetInt(t, "derivedChildren");
                        if (t.TryGetProperty("skipReasons", out var sr) && sr.ValueKind == JsonValueKind.Object)
                            foreach (var kv in sr.EnumerateObject())
                                r.SkipReasons[kv.Name] = kv.Value.TryGetInt32(out var n) ? n : 0;
                        rep.Tables.Add(r);
                        byTable[r.Source] = r;
                    }
                }

                // 레코드 짝 — 소스 PK 컬럼명도 여기서 되살린다(표에는 없다).
                if (root.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pairs.EnumerateArray())
                    {
                        string src = GetStr(p, "sourceTable");
                        if (!byTable.TryGetValue(src, out var tr)) continue;
                        if (tr.SourcePk.Length == 0) tr.SourcePk = GetStr(p, "sourcePkColumn");
                        long s = GetLong(p, "sourcePk"), g = GetLong(p, "targetRecordId");
                        if (s > 0) tr.IdPairs.Add((s, g));
                        if (GetStr(p, "kind") == "재사용" && s > 0) tr.ReusedSourcePks.Add(s);
                    }
                }

                if (rep.Tables.Count == 0)
                {
                    error = "표 정보를 읽지 못했습니다(형식이 다를 수 있습니다).";
                    return null;
                }
                return rep;
            }
            catch (Exception ex)
            {
                error = "읽기 실패: " + ex.Message;
                return null;
            }
        }

        private static string GetStr(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        private static int GetInt(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
        private static long GetLong(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.TryGetInt64(out var i) ? i : 0;
        private static bool GetBool(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
