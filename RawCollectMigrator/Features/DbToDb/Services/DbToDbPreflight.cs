using System.Data.Common;
using System.IO;
using Microsoft.Data.SqlClient;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>환경 점검 결과.</summary>
    public sealed class PreflightReport
    {
        public bool SourceOk { get; set; }
        public string SourceMsg { get; set; } = "";
        public bool TargetOk { get; set; }
        public string TargetMsg { get; set; } = "";

        public DecryptMode DecryptMode { get; set; }
        public bool CanDecrypt { get; set; }
        public string DecryptMsg { get; set; } = "";
        public List<string> EncryptedBlocked { get; } = new();

        public List<(string Root, bool Ok, int Files)> Roots { get; } = new();
        public List<(string Table, int Referenced, int Found)> Snippets { get; } = new();

        /// <summary>암호화 이관까지 전부 가능한 상태인지.
        /// Referenced &lt; 0 은 스니펫 참조 조회 자체가 실패한 것(= "참조 0건 통과" 오판 방지) — 준비 미완으로 본다.</summary>
        public bool FullyReady =>
            SourceOk && TargetOk && CanDecrypt
            && Roots.All(r => r.Ok)
            && Snippets.All(s => s.Referenced >= 0 && (s.Referenced == 0 || s.Found > 0));
    }

    /// <summary>
    /// 실제 이관 전 환경 점검. 두 전제를 구체 수치로 판정한다.
    ///   ① 복호화 GRANT — 소스 계정이 암호화 컬럼을 실제로 풀 수 있는가
    ///   ② 파일망       — 스니펫 참조 파일이 실제로 몇 개 존재하는가
    /// </summary>
    public sealed class DbToDbPreflight
    {
        private readonly DbToDbOptions _opt;
        private readonly Action<string> _log;

        public DbToDbPreflight(DbToDbOptions opt, Action<string>? log = null)
        {
            _opt = opt;
            _log = log ?? (_ => { });
        }

        // 암호화로 복호화가 필요한 이관 대상 (배포·이력 제외 범위 안).
        private static readonly string[] EncryptedTables =
            { "CLASSES", "VESSEL_DATA", "ACTIVE_SONAR", "TRANSIENT" };

        // 스니펫 소스 테이블 (파일 참조).
        private static readonly string[] SnippetTables =
            { "NB_SNIPPET", "AI_SNIPPET", "TR_SNIPPET" };

        public async Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            var rep = new PreflightReport();
            _log("═══ 환경 점검 시작 ═══");

            // ── ① 연결 ──
            (rep.SourceOk, rep.SourceMsg) = await TestConnAsync(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, ct);
            _log($"[소스] {(rep.SourceOk ? "OK" : "실패")} — {rep.SourceMsg}");
            (rep.TargetOk, rep.TargetMsg) = await TestConnAsync(DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr, ct);
            _log($"[타겟] {(rep.TargetOk ? "OK" : "실패")} — {rep.TargetMsg}");

            // ── ② 복호화 GRANT ──
            if (rep.SourceOk)
            {
                var crypto = new CryptoContext();
                rep.DecryptMode = await crypto.ProbeAsync(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, ct);
                rep.CanDecrypt = crypto.CanDecrypt;
                rep.DecryptMsg = crypto.Diagnosis;
                _log($"[복호화] {(rep.CanDecrypt ? "가능" : "불가")} ({rep.DecryptMode}) — {rep.DecryptMsg}");
                if (!rep.CanDecrypt && _opt.SourceKind == DbProviderKind.MsSql)
                {
                    rep.EncryptedBlocked.AddRange(EncryptedTables);
                    _log("  → 다음 테이블 이관 불가(복호화 필요): " + string.Join(", ", EncryptedTables));
                    _log("  → 소스 서버(sysadmin)에서 아래 GRANT 실행 후 재점검하세요:");
                    foreach (var line in CryptoContext.GrantScript(SourceUser(_opt.SourceConnStr)).Split('\n'))
                        if (line.Trim().Length > 0) _log("      " + line.TrimEnd());
                }
            }

            // ── ③ 파일망 접근 ──
            _log("[파일망] 루트 점검…");
            var indexes = new List<FileIndex>();
            foreach (var root in _opt.SnippetRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                ct.ThrowIfCancellationRequested();
                var idx = FileIndex.Build(root, ct);
                indexes.Add(idx);
                rep.Roots.Add((root, idx.RootExists, idx.FileCount));
                _log($"  {(idx.RootExists ? "OK" : "접근불가")} — {root}  (파일 {idx.FileCount:N0}개)");
            }

            // ── ④ 스니펫 참조 → 실제 파일 존재율 ──
            if (rep.SourceOk && indexes.Any(i => i.RootExists))
            {
                var resolver = new SnippetFileResolver(
                    _opt.SnippetRoots.Select(r => new SnippetFileResolver.RootSpec("CLASSIFICATION", r)));
                foreach (var table in SnippetTables)
                {
                    ct.ThrowIfCancellationRequested();
                    var (refc, found) = await CheckSnippetAsync(table, resolver, ct);
                    rep.Snippets.Add((table, refc, found));
                    if (refc < 0)
                        // 조회 실패를 "참조 0건 통과"로 오판하지 않는다 — 이 검사 실패로 FullyReady 는 false.
                        _log($"  {table}: 참조 조회 실패 — 검사 불통과(전체 이관 준비 미완). 소스 접속/권한/테이블을 확인하세요.");
                    else
                        _log($"  {table}: 참조 {refc}건 중 {found}건 파일 존재"
                             + (refc > 0 ? $" ({100.0 * found / refc:F0}%)" : ""));
                }
            }

            _log("═══ 판정 ═══");
            _log(rep.CanDecrypt ? "① 복호화: 준비됨" : "① 복호화: GRANT 필요 (미암호화 테이블만 이관 가능)");
            bool filesOk = rep.Roots.Any(r => r.Ok);
            _log(filesOk ? "② 파일망: 접근 가능" : "② 파일망: 접근 불가 (스니펫 blob 이관 불가)");
            _log(rep.FullyReady ? "★ 전체 이관 준비 완료" : "△ 부분 이관 가능 — 위 항목 확인");
            return rep;
        }

        private async Task<(bool ok, string msg)> TestConnAsync(
            DbProviderKind kind, DbConnectionMode mode, string conn, CancellationToken ct)
        {
            try
            {
                await using var cn = DbProvider.CreateConnection(kind, mode, conn);
                await cn.OpenAsync(ct);
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = DbProvider.VersionSql(kind);
                var v = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "";
                var first = v.Split('\n').FirstOrDefault()?.Trim() ?? DbProvider.DisplayName(kind);
                return (true, first.Length > 70 ? first[..70] : first);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>스니펫 테이블의 *FILENAME 값들을 읽어 파일 해결률을 센다.
        /// 반환: (참조 건수, 존재 건수). 조회 실패 시 (-1, 0) — "참조 0건 통과"와 구분되는 실패 표식이며
        /// 호출부가 이를 검사 실패(FullyReady=false)로 처리한다. 전 행을 리스트에 담지 않고 스트리밍으로 센다.</summary>
        private async Task<(int referenced, int found)> CheckSnippetAsync(
            string table, SnippetFileResolver resolver, CancellationToken ct)
        {
            var kind = _opt.SourceKind;
            var mode = _opt.SourceMode;
            try
            {
                await using var cn = DbProvider.CreateConnection(kind, mode, _opt.SourceConnStr);
                await cn.OpenAsync(ct);

                // 이 테이블의 *FILENAME 컬럼 찾기
                var fileCols = new List<string>();
                await using (var c1 = cn.CreateCommand())
                {
                    c1.CommandText = DbProvider.ColumnsSql(kind, mode);
                    var p = c1.CreateParameter();
                    p.ParameterName = DbProvider.ParamMarker(kind, mode, "t");
                    p.Value = table;
                    c1.Parameters.Add(p);
                    await using var r = await c1.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var col = Convert.ToString(r.GetValue(0)) ?? "";
                        if (col.EndsWith("FILENAME", StringComparison.OrdinalIgnoreCase)) fileCols.Add(col);
                    }
                }
                if (fileCols.Count == 0) return (0, 0);

                string list = string.Join(", ", fileCols.Select(c => DbProvider.QuoteColumn(kind, c)));
                string q = DbProvider.QuoteIdent(kind, table);
                await using var c2 = cn.CreateCommand();
                c2.CommandText = kind == DbProviderKind.Oracle
                    ? $"SELECT {list} FROM {q}"
                    : $"SELECT {list} FROM {q} WITH (NOLOCK)";
                await using var rr = await c2.ExecuteReaderAsync(ct);
                int referenced = 0, found = 0;
                while (await rr.ReadAsync(ct))
                    for (int i = 0; i < rr.FieldCount; i++)
                        if (!rr.IsDBNull(i))
                        {
                            var n = Convert.ToString(rr.GetValue(i))?.Trim();
                            if (string.IsNullOrEmpty(n)) continue;
                            referenced++;
                            if (resolver.Resolve(n, ct).Found) found++;
                        }
                return (referenced, found);
            }
            catch (OperationCanceledException) { throw; }   // 취소가 "조회 실패"로 둔갑하는 것 방지
            catch (Exception ex)
            {
                _log($"  {table}: 참조 조회 오류 — {ex.Message}");
                return (-1, 0);   // 실패 표식 — (0,0)="참조 없음 통과" 와 구분
            }
        }

        private static string SourceUser(string conn)
        {
            foreach (var part in conn.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Replace(" ", "").Equals("UserId", StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
            return "NaimsMig";
        }
    }
}
