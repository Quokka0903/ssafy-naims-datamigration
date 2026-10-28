using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RawCollectMigrator.Features.DbToDb;
using Xunit;
using Xunit.Abstractions;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 리그 통합: 레거시 Oracle(ODBC) 소스 → localhost NAIMS2(테스트) DryRun/Trial.
    ///
    /// 자격증명·머신 종속 경로는 소스에 하드코딩하지 않고 전부 환경변수로 주입한다:
    ///   NAIMS_TEST_SRC_CONN  소스 ODBC 연결문자열 (예: DSN=LegacyNaims;Uid=…;Pwd=…;) — 필수(없으면 Skip)
    ///   NAIMS_TEST_DST_CONN  타겟 NAIMS2 연결문자열 — 선택(미설정 시 localhost 통합보안 기본값, 자격증명 미포함)
    ///   NAIMS_TEST_DBA_CONN  덤프 복구 DBA 접속 (예: system/…@localhost:1522/NAIMS12) — Rig_RestoreDump 전용
    ///   NAIMS_TEST_DUMP_PATH 복구할 .dmp 파일(또는 폴더) 경로 — Rig_RestoreDump 전용
    ///   NAIMS_TEST_LOG_DIR   리그 로그 출력 폴더 — 선택(미설정 시 %TEMP%)
    ///   NAIMS_RIG=1          실제 쓰기 리그(impdp 복구·Trial INSERT) opt-in.
    ///                        미설정이면 해당 테스트는 Skip — 일반 `dotnet test` 전체 실행에서 절대 돌지 않는다.
    /// </summary>
    public class DbToDbRigTests
    {
        private readonly ITestOutputHelper _out;

        public DbToDbRigTests(ITestOutputHelper output) => _out = output;

        // ── 환경변수 주입 (자격증명·머신 종속 값 하드코딩 금지) ──────────────
        private static string? SrcConn  => Environment.GetEnvironmentVariable("NAIMS_TEST_SRC_CONN");
        private static string? DbaConn  => Environment.GetEnvironmentVariable("NAIMS_TEST_DBA_CONN");
        private static string? DumpPath => Environment.GetEnvironmentVariable("NAIMS_TEST_DUMP_PATH");
        private static bool RigEnabled  => Environment.GetEnvironmentVariable("NAIMS_RIG") == "1";

        /// <summary>타겟은 통합보안(자격증명 미포함)이라 기본값 허용. 다른 인스턴스는 환경변수로 재지정.</summary>
        private static string DstConn =>
            Environment.GetEnvironmentVariable("NAIMS_TEST_DST_CONN")
            ?? "Server=localhost;Database=NAIMS2;Trusted_Connection=yes;Encrypt=false;TrustServerCertificate=true;";

        private static string LogDir =>
            Environment.GetEnvironmentVariable("NAIMS_TEST_LOG_DIR") ?? Path.GetTempPath();

        private static string LogFile(string name) => Path.Combine(LogDir, name);

        private const string SkipNoSrc =
            "NAIMS_TEST_SRC_CONN 미설정 — 소스 ODBC 연결문자열이 없어 리그 건너뜀.";
        private const string SkipNoRig =
            "NAIMS_RIG=1 미설정 — 실제 쓰기 리그는 opt-in 시에만 실행.";

        private static DbToDbOptions NewOptions(RunMode mode, int maxRows, bool useDecryption = true) => new()
        {
            SourceKind = DbProviderKind.Oracle,
            SourceMode = DbConnectionMode.Odbc,
            SourceConnStr = SrcConn!,
            TargetConnStr = DstConn,
            Mode = mode,
            MaxRowsPerTable = maxRows,
            UseDecryption = useDecryption,
        };

        /// <summary>실행 후 리포트를 반환한다. 로그는 진단용으로 파일·테스트 출력에 남긴다(검증 대상 아님).</summary>
        private async Task<(MigrationReport Report, string Log)> RunReportedAsync(DbToDbOptions opt, string logName)
        {
            var log = new StringBuilder();
            log.AppendLine($"===== MODE={opt.Mode}  {DateTime.Now:HH:mm:ss} =====");
            try
            {
                var exec = new DbToDbExecutor(opt, s => log.AppendLine(s));
                var rep = await exec.RunAsync(CancellationToken.None);
                log.AppendLine("---- SUMMARY ----");
                log.AppendLine($"DryRun={rep.DryRun} Tables={rep.Tables.Count} Inserted={rep.TotalInserted} Reused={rep.TotalReused} Skipped={rep.TotalSkipped} Errors={rep.TotalErrors}");
                foreach (var t in rep.Tables) log.AppendLine("  " + t.Summary);
                return (rep, log.ToString());
            }
            finally
            {
                var path = LogFile(logName);
                File.AppendAllText(path, log.ToString() + Environment.NewLine);
                _out.WriteLine($"리그 로그: {path}");
            }
        }

        // ── 읽기 전용 리그 (소스 연결만 있으면 실행) ─────────────────────────

        /// <summary>복호화식 자동 추출(읽기 전용 SELECT). 암호화 소스에 붙었으면 반드시 성공해야 한다.</summary>
        [SkippableFact]
        public async Task Rig_DiscoverCrypto()
        {
            Skip.If(string.IsNullOrEmpty(SrcConn), SkipNoSrc);

            var log = new StringBuilder();
            bool ok = await CryptoContext.DiscoverOracleCryptoAsync(SrcConn!, DbConnectionMode.Odbc, s => log.AppendLine(s));
            log.AppendLine("RESULT ok=" + ok);

            var path = LogFile("naims_rig_discover.log");
            File.WriteAllText(path, log.ToString());
            _out.WriteLine($"리그 로그: {path}");

            // 파일 존재 같은 공허한 확인이 아니라 디스커버리 결과 자체를 검증한다.
            Assert.True(ok, $"Oracle 복호화식 추출 실패 — {path} 확인");
        }

        /// <summary>복호화 불러오기 OFF: 암호화 테이블이 '복호화 불가'로 차단되지 않고 원문 그대로 계획되는지 (DryRun, 읽기 전용).</summary>
        [SkippableFact]
        public async Task Rig_DryRun_NoDecryption()
        {
            Skip.If(string.IsNullOrEmpty(SrcConn), SkipNoSrc);

            var (rep, log) = await RunReportedAsync(
                NewOptions(RunMode.DryRun, maxRows: 5, useDecryption: false),
                "naims_rig_nodecrypt.log");

            // 결과(리포트) 검증: 계획이 실제로 세워졌고, 복호화 OFF 차단이 전혀 없어야 한다.
            Assert.True(rep.DryRun, "DryRun 모드인데 리포트가 DryRun 이 아님");
            Assert.NotEmpty(rep.Tables);
            Assert.DoesNotContain("복호화 불가", log);
        }

        // ── 쓰기 리그 (NAIMS_RIG=1 opt-in 시에만 실행) ──────────────────────

        /// <summary>덤프 복구(impdp) 실코드 경로 검증: 테스트 Data Pump 덤프를 LEGACYNAIMS 로 실제 복구.</summary>
        [SkippableFact]
        public async Task Rig_RestoreDump()
        {
            Skip.IfNot(RigEnabled, SkipNoRig);
            Skip.If(string.IsNullOrEmpty(DbaConn), "NAIMS_TEST_DBA_CONN 미설정 — DBA 접속 문자열이 없어 복구 리그 건너뜀.");
            Skip.If(string.IsNullOrEmpty(DumpPath), "NAIMS_TEST_DUMP_PATH 미설정 — 덤프 경로가 없어 복구 리그 건너뜀.");
            Skip.IfNot(File.Exists(DumpPath) || Directory.Exists(DumpPath), $"덤프 경로 없음: {DumpPath}");

            var log = new StringBuilder();
            var opt = new DumpRestoreOptions
            {
                DumpPath = DumpPath!,
                DbaConnect = DbaConn!,
                TargetSchema = Environment.GetEnvironmentVariable("NAIMS_TEST_RESTORE_SCHEMA") ?? "LEGACYNAIMS",
                SourceSchema = Environment.GetEnvironmentVariable("NAIMS_TEST_RESTORE_SRC_SCHEMA") ?? "LEGACYNAIMS",
            };

            // 판별·홈 탐색은 결과값으로 직접 검증한다.
            var kind = File.Exists(opt.DumpPath) ? DumpRestore.DetectKind(opt.DumpPath) : OracleDumpKind.Unknown;
            var home = DumpRestore.FindOracleHome();
            log.AppendLine("DetectKind=" + kind);
            log.AppendLine("OracleHome=" + (home ?? "(null)"));
            if (File.Exists(opt.DumpPath))
                Assert.NotEqual(OracleDumpKind.Unknown, kind);   // 판별 실패면 복구 이전에 계약 위반
            Assert.False(string.IsNullOrEmpty(home), "ORACLE_HOME 탐색 실패 — imp/impdp 실행 불가 환경");

            bool ok = await DumpRestore.RunAsync(opt, s => log.AppendLine(s), CancellationToken.None);
            log.AppendLine("RESULT ok=" + ok);

            var path = LogFile("naims_rig_restore.log");
            File.WriteAllText(path, log.ToString());
            _out.WriteLine($"리그 로그: {path}");

            Assert.True(ok, $"덤프 복구 실패 — {path} 확인");
        }

        /// <summary>DryRun(읽기 전용) → Trial(INSERT 후 롤백) 연속 실행. Trial 이 실제 INSERT 를 치므로 opt-in 리그.</summary>
        [SkippableFact]
        public async Task Rig_DryRun_then_Trial()
        {
            Skip.IfNot(RigEnabled, SkipNoRig);
            Skip.If(string.IsNullOrEmpty(SrcConn), SkipNoSrc);

            // 헤드리스에선 UI [Oracle 암호화 추출] 이 없으므로 디스커버리를 먼저 호출해 복호 템플릿을 세운다.
            await CryptoContext.DiscoverOracleCryptoAsync(SrcConn!, DbConnectionMode.Odbc, _ => { });

            var (dry, _) = await RunReportedAsync(NewOptions(RunMode.DryRun, maxRows: 50), "naims_rig_dbtodb.log");
            var (trial, _) = await RunReportedAsync(NewOptions(RunMode.Trial, maxRows: 50), "naims_rig_dbtodb.log");

            // 로그 파일 존재 확인 대신 리포트 자체를 검증한다.
            Assert.True(dry.DryRun, "DryRun 리포트 플래그 불일치");
            Assert.False(trial.DryRun, "Trial 리포트가 DryRun 으로 표기됨");
            Assert.NotEmpty(dry.Tables);
            Assert.NotEmpty(trial.Tables);
            Assert.Equal(0, dry.TotalErrors);
            Assert.Equal(0, trial.TotalErrors);

            // Trial 은 실 INSERT 경로를 태우므로 뭔가는 처리(적재·재사용·건너뜀)됐어야 한다.
            Assert.True(trial.TotalInserted + trial.TotalReused + trial.TotalSkipped > 0,
                "Trial 이 아무 행도 처리하지 않음 — 소스가 비었거나 이관 경로가 죽어 있음");
        }
    }
}
