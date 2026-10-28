using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:덤프복구] [GUIDE:DUMP-RESTORE] [찾기:덤프복구] [찾기:imp] [찾기:impdp]
    //  ▣ 이 파일 = 오프라인 W/S 로 반입한 Oracle 덤프(.dmp)를 대상 스키마로 되살리는 오케스트레이터.
    //
    //  ▶ 핵심 사실(정직): .dmp 복구는 순수 .NET/관리코드로 불가능하다.
    //    Oracle 이 제공하는 클라이언트 도구(imp.exe / impdp.exe)를 반드시 거쳐야 한다.
    //    이 클래스는 그 도구를 "대리 실행"하고 표준출력을 로그 콜백으로 흘려보낸다.
    //    (= 04_restore_dump.ps1 스크립트가 하던 일을 프로그램 버튼으로 옮긴 것)
    //
    //  ▶ 레거시(USPASK_CDMU) 실제 백업 방식과 정합 [찾기:레거시백업정합]:
    //    현장 원본 backup.bat = 클래식 EXP 를 **테이블별로 34개 .DMP** 로 쪼갠 것.
    //      예) EXP USERID=SYSTEM/NAIMS FILE=...OracleTable_Classes.DMP TABLES=(SYSTEM.CLASSES)
    //    → 복구도 recovery.bat 처럼 **파일마다 imp 를 반복**해야 한다. (단일 파일만이 아님)
    //    → 레거시 덤프의 **소유자는 SYSTEM** 이므로 classic 은 fromuser=SYSTEM 이 기본.
    //    → recovery.bat 은 **dropdb.txt(전체 DROP) 를 먼저** 돌린 뒤 imp 했다.
    //       classic imp 는 append(INSERT) 전용이라, 재실행 시 CleanMode 로 선(先)정리해야 중복이 안 생긴다.
    //    ※ 우리 자체 보존덤프(expdp schemas=NAIMS 단일 .dmp)는 소유자 NAIMS·Data Pump →
    //       그 경우엔 SourceSchema=NAIMS, impdp(table_exists_action=replace) 로 자동 멱등.
    //
    //  ▶ 덤프 종류에 따라 복구 도구가 갈린다(섞으면 실패):
    //      classic exp  → imp.exe   (헤더가 "EXPORT:V..." 아스키로 시작 / 클라이언트측 파일 읽기)
    //      Data Pump    → impdp.exe (바이너리 헤더 / 서버측 DIRECTORY 객체 경유)
    //    DetectKind() 가 파일 앞부분을 읽어 자동 판별한다. 애매하면 사용자가 지정 가능.
    //
    //  ▶ ORACLE_HOME 자동 탐색: 환경변수 → 레지스트리(HKLM\SOFTWARE\ORACLE\KEY_*) 순.
    //    거기서 bin\imp.exe / bin\impdp.exe 를 찾는다.
    //
    //  ▶ Data Pump 는 서버 프로세스가 파일을 읽으므로, 덤프가 놓인 폴더를 가리키는
    //    DIRECTORY 객체를 관리형 연결로 CREATE 한 뒤 impdp directory=... 로 실행한다.
    //    (덤프를 ORACLE_HOME 안으로 복사하지 않아도 됨)
    //
    //  ▶ 복구≠끝: 이 단계는 **테이블만** 되살린다. 이름컬럼 복호에 필요한 CRYPTO 패키지/
    //    PUBLIC SYNONYM/GRANT 는 덤프에 없으므로, 복구 뒤 [Oracle 암호화 추출](CryptIT/03_) 을
    //    따로 심어야 migrator 가 CRYPTO.DECRYPT 로 이름을 읽는다.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Oracle 덤프의 산출 도구 종류. imp/impdp 선택을 가른다.</summary>
    public enum OracleDumpKind
    {
        /// <summary>판별 실패(파일 없음/너무 작음). 사용자가 종류를 골라야 한다.</summary>
        Unknown,
        /// <summary>classic exp 산출물 → imp.exe 로 복구.</summary>
        ClassicExp,
        /// <summary>Data Pump expdp 산출물 → impdp.exe 로 복구.</summary>
        DataPump
    }

    /// <summary>
    /// 재적재 전 대상 스키마 정리 방식. classic imp 는 append(INSERT) 전용이라 재실행 중복을 막으려면 선정리가 필요하다.
    /// (Data Pump 는 table_exists_action=replace 로 자체 멱등이므로 보통 None 으로 둔다.)
    /// </summary>
    public enum RestoreCleanMode
    {
        /// <summary>정리 안 함. 빈 스키마에 첫 복구할 때. (classic 재실행 시 중복 위험)</summary>
        None,
        /// <summary>대상 스키마의 기존 테이블을 TRUNCATE(구조 유지, 행만 비움). 권한이 약할 때 안전.</summary>
        TruncateTargetTables,
        /// <summary>대상 스키마의 기존 테이블을 DROP(구조까지 제거). recovery.bat 의 dropdb.txt 와 동일 — imp 가 DDL 로 재생성.</summary>
        DropTargetTables
    }

    /// <summary>덤프 복구 실행 옵션(화면 입력을 담는 값 객체).</summary>
    public sealed class DumpRestoreOptions
    {
        /// <summary>
        /// 복구 대상 경로. **파일 하나** 또는 **.dmp 가 든 폴더**(테이블별 다중 .DMP 일괄 복구) 둘 다 가능.
        /// 로컬 디스크 권장(서버가 읽을 수 있어야 함).
        /// </summary>
        public string DumpPath = "";

        /// <summary>imp/impdp 접속 문자열. 형식: user/pwd@host:port/service (DBA 권한 필요, 예: system/…@localhost:1522/NAIMS12).</summary>
        public string DbaConnect = "";

        /// <summary>재적재 대상 스키마(우리 도구가 붙는 스키마). 기본 LEGACYNAIMS.</summary>
        public string TargetSchema = "LEGACYNAIMS";

        /// <summary>
        /// 원본 스키마(classic imp 의 fromuser / Data Pump 의 remap 원본).
        /// 비우면: classic 은 레거시 관례상 <see cref="LegacyClassicOwner"/>(SYSTEM), Data Pump 는 대상과 동일로 가정.
        /// </summary>
        public string SourceSchema = "";

        /// <summary>사용자가 강제로 지정한 종류. Unknown 이면 파일 헤더로 자동 판별.</summary>
        public OracleDumpKind ForcedKind = OracleDumpKind.Unknown;

        /// <summary>재적재 전 대상 스키마 정리 방식. 기본 None(빈 스키마 첫 복구 전제).</summary>
        public RestoreCleanMode CleanMode = RestoreCleanMode.None;
    }

    /// <summary>
    /// [찾기:덤프복구] Oracle 덤프(.dmp)를 대상 스키마로 복구한다. imp.exe/impdp.exe 대리 실행.
    /// 단일 파일/폴더(다중 .DMP)·classic 다중 imp 반복·재실행 선정리(CleanMode)를 지원한다.
    /// </summary>
    public static class DumpRestore
    {
        /// <summary>Data Pump 복구에 임시로 만드는 DIRECTORY 객체 이름.</summary>
        private const string RestoreDirObject = "MIG_RESTORE_DIR";

        /// <summary>레거시 classic 덤프의 소유자(backup.bat: TABLES=(SYSTEM.*)). SourceSchema 미지정 시 classic 기본 fromuser.</summary>
        public const string LegacyClassicOwner = "SYSTEM";

        /// <summary>정리(DROP/TRUNCATE) 를 절대 허용하지 않는 보호 스키마. 사고 방지용.</summary>
        private static readonly HashSet<string> ProtectedSchemas = new(StringComparer.OrdinalIgnoreCase)
        {
            "SYS", "SYSTEM", "SYSAUX", "DBSNMP", "OUTLN", "XDB", "CTXSYS", "MDSYS",
            "ORDSYS", "WMSYS", "APEX_PUBLIC_USER", "GSMADMIN_INTERNAL", "AUDSYS", "PUBLIC"
        };

        // ── 종류 판별 ─────────────────────────────────────────────────────────
        /// <summary>
        /// [찾기:덤프판별] 파일 앞부분을 읽어 classic exp / Data Pump 를 구분한다.
        /// classic exp 덤프는 헤더 근처에 아스키 "EXPORT:V" 문자열이 있다.
        /// 없으면 Data Pump 로 간주한다(Data Pump 헤더는 바이너리라 이 마커가 없음).
        /// </summary>
        public static OracleDumpKind DetectKind(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[512];
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) return OracleDumpKind.Unknown;
                // ASCII 로만 스캔(멀티바이트 무시) — "EXPORT:V" 탐색
                var ascii = Encoding.ASCII.GetString(buf, 0, n);
                if (ascii.Contains("EXPORT:V", StringComparison.Ordinal))
                    return OracleDumpKind.ClassicExp;
                return OracleDumpKind.DataPump;
            }
            catch
            {
                return OracleDumpKind.Unknown;
            }
        }

        // ── 덤프 파일 목록 해석 ────────────────────────────────────────────────
        /// <summary>
        /// [찾기:덤프목록] DumpPath 를 실제 복구할 .dmp 파일 목록으로 해석한다.
        /// 파일이면 그 하나, 폴더면 그 안의 *.dmp/*.DMP 전부(이름 오름차순).
        /// backup.bat 의 테이블별 34개 .DMP 를 폴더째 넘기는 시나리오를 지원한다.
        /// </summary>
        public static IReadOnlyList<string> ResolveDumpFiles(string dumpPath)
        {
            if (string.IsNullOrWhiteSpace(dumpPath)) return Array.Empty<string>();
            if (Directory.Exists(dumpPath))
            {
                return Directory.EnumerateFiles(dumpPath)
                    .Where(f => f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (File.Exists(dumpPath)) return new[] { dumpPath };
            return Array.Empty<string>();
        }

        // ── ORACLE_HOME / 도구 탐색 ──────────────────────────────────────────
        /// <summary>
        /// [찾기:오라클홈] ORACLE_HOME 을 환경변수 → 레지스트리 순으로 찾는다.
        /// 반환된 홈의 bin\ 아래에 imp.exe/impdp.exe 가 있어야 유효하다.
        /// </summary>
        public static string? FindOracleHome()
        {
            // 1) 환경변수
            var env = Environment.GetEnvironmentVariable("ORACLE_HOME");
            if (IsUsableHome(env)) return env!;

            // 2) 레지스트리 HKLM\SOFTWARE\ORACLE\KEY_* 의 ORACLE_HOME 값
            try
            {
                using var oracle = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ORACLE");
                if (oracle != null)
                {
                    foreach (var sub in oracle.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase)) continue;
                        using var k = oracle.OpenSubKey(sub);
                        var home = k?.GetValue("ORACLE_HOME") as string;
                        if (IsUsableHome(home)) return home!;
                    }
                }
            }
            catch { /* 레지스트리 접근 실패는 무시하고 null 반환 */ }

            return null;
        }

        /// <summary>홈 경로가 실제로 imp.exe 또는 impdp.exe 를 담고 있는지.</summary>
        private static bool IsUsableHome(string? home)
        {
            if (string.IsNullOrWhiteSpace(home)) return false;
            var bin = Path.Combine(home, "bin");
            return File.Exists(Path.Combine(bin, "imp.exe"))
                || File.Exists(Path.Combine(bin, "impdp.exe"));
        }

        // ── 실행 ─────────────────────────────────────────────────────────────
        /// <summary>
        /// [찾기:덤프복구실행] 덤프를 대상 스키마로 복구한다.
        /// 파일목록 해석 → 종류 판별 → ORACLE_HOME/도구 확보 → (선택)선정리 →
        /// classic=파일마다 imp 반복 / DataPump=impdp 1회. 출력은 log 콜백으로 실시간 스트리밍.
        /// 모든 파일이 성공(경고 포함)해야 true.
        /// </summary>
        public static async Task<bool> RunAsync(DumpRestoreOptions opt, Action<string> log, CancellationToken ct)
        {
            var files = ResolveDumpFiles(opt.DumpPath);
            if (files.Count == 0)
            {
                log("!! 복구할 .dmp 를 찾을 수 없습니다(파일 또는 .dmp 가 든 폴더): " + opt.DumpPath);
                return false;
            }
            if (string.IsNullOrWhiteSpace(opt.DbaConnect))
            {
                log("!! DBA 접속 문자열이 비어 있습니다. 예: system/비밀번호@localhost:1522/NAIMS12");
                return false;
            }

            // 종류는 첫 파일(또는 강제값)로 판별 — 한 배치는 같은 종류라고 가정한다.
            var kind = opt.ForcedKind != OracleDumpKind.Unknown ? opt.ForcedKind : DetectKind(files[0]);
            log($"── 복구 대상: {files.Count}개 파일  ·  종류: {KindLabel(kind)}");
            if (Directory.Exists(opt.DumpPath))
                foreach (var f in files) log("     · " + Path.GetFileName(f));
            if (kind == OracleDumpKind.Unknown)
            {
                log("!! 종류를 판별하지 못했습니다. 화면에서 classic/Data Pump 를 직접 지정하세요.");
                return false;
            }

            var home = FindOracleHome();
            if (home == null)
            {
                log("!! ORACLE_HOME 을 찾지 못했습니다(imp.exe/impdp.exe 없음). Oracle 클라이언트/서버가 설치돼 있어야 합니다.");
                return false;
            }
            log("── ORACLE_HOME = " + home);

            // 원본 스키마 기본값: classic 은 레거시 관례상 SYSTEM, Data Pump 는 대상과 동일.
            var tgtSchema = opt.TargetSchema.Trim();
            string srcSchema;
            if (!string.IsNullOrWhiteSpace(opt.SourceSchema))
                srcSchema = opt.SourceSchema.Trim();
            else if (kind == OracleDumpKind.ClassicExp)
            {
                srcSchema = LegacyClassicOwner;
                log($"── 원본 스키마 미지정 → classic 관례상 '{LegacyClassicOwner}' 로 가정(fromuser). 자체 NAIMS 덤프면 화면에 NAIMS 를 넣으세요.");
            }
            else
                srcSchema = tgtSchema;

            // (선택) 재적재 전 대상 스키마 선정리 — recovery.bat 의 dropdb.txt 대응.
            if (opt.CleanMode != RestoreCleanMode.None)
            {
                if (ProtectedSchemas.Contains(tgtSchema))
                {
                    log($"!! 보호 스키마('{tgtSchema}')는 정리(DROP/TRUNCATE)를 거부합니다. 대상 스키마를 전용 계정(예: LEGACYNAIMS)으로 지정하세요.");
                    return false;
                }
                bool cleaned = await CleanTargetSchemaAsync(opt.DbaConnect, tgtSchema, opt.CleanMode, log, ct);
                if (!cleaned)
                {
                    log("!! 선정리 실패 — 복구를 중단합니다(중복 적재 방지).");
                    return false;
                }
            }
            else if (kind == OracleDumpKind.ClassicExp)
            {
                log("── 선정리 없음(CleanMode=None). classic imp 는 append 전용이라, 기존 데이터가 있으면 중복될 수 있습니다.");
            }

            try
            {
                bool ok = kind == OracleDumpKind.ClassicExp
                    ? await RunClassicImpBatchAsync(home, opt, files, srcSchema, tgtSchema, log, ct)
                    : await RunDataPumpAsync(home, opt, files, srcSchema, tgtSchema, log, ct);

                if (ok)
                {
                    log("── ✅ 복구 완료. 다음 단계: [연결 시험] → [Oracle 암호화 추출](CRYPTO/CryptIT 심기) → 시험 실행.");
                    log("     ※ 덤프엔 CRYPTO 패키지/SYNONYM 이 없으므로, 이름컬럼 복호를 쓰려면 암호화 추출 단계가 반드시 필요합니다.");
                }
                return ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log("!! 복구 중 오류: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// [찾기:imp배치] classic exp 덤프들을 imp.exe 로 **파일마다 반복** 복구. recovery.bat 과 동일한 형태.
        /// 클라이언트측 파일 읽기라 DIRECTORY 객체가 필요 없다. fromuser=원본 touser=대상 으로 재적재.
        /// ※ 테이블별 개별 .DMP 는 서로 FK 순서를 보장하지 못한다 — 행은 정상 적재되나,
        ///   부모가 아직 없을 때의 FK 제약 생성은 스킵될 수 있다(우리 migrator 는 소스를 읽기만 하므로 무해).
        /// </summary>
        private static async Task<bool> RunClassicImpBatchAsync(
            string home, DumpRestoreOptions opt, IReadOnlyList<string> files, string srcSchema, string tgtSchema,
            Action<string> log, CancellationToken ct)
        {
            var imp = Path.Combine(home, "bin", "imp.exe");
            if (!File.Exists(imp)) { log("!! imp.exe 없음: " + imp); return false; }

            log($"── imp 배치 시작: fromuser={srcSchema} touser={tgtSchema}  ({files.Count}개)");
            // [찾기:parfile] 접속정보(userid)는 명령줄 대신 임시 parfile 로 전달 —
            // 프로세스 목록 평문 노출과 암호 특수문자 인자 파싱 문제를 함께 막는다.
            var parfile = WriteUseridParfile(opt.DbaConnect);
            try
            {
                bool allOk = true;
                int idx = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    var logPath = Path.Combine(
                        Path.GetDirectoryName(file)!,
                        Path.GetFileNameWithoutExtension(file) + "_imp.log");

                    // imp parfile="…"(userid) file="…" fromuser=SRC touser=TGT log="…" ignore=y
                    var args = $"parfile=\"{parfile}\" file=\"{file}\" "
                             + $"fromuser={srcSchema} touser={tgtSchema} log=\"{logPath}\" ignore=y";
                    log($"── [{idx}/{files.Count}] imp ← {Path.GetFileName(file)}");

                    int code = await RunProcAsync(imp, args, home, log, ct);
                    bool one = InterpretExit(code, logPath, log);
                    allOk &= one;
                }
                log($"── imp 배치 종료: {(allOk ? "전체 완료(경고 포함 가능)" : "일부 실패 — 로그 확인")}");
                return allOk;
            }
            finally
            {
                TryDeleteParfile(parfile, log);
            }
        }

        /// <summary>
        /// [찾기:impdp] Data Pump 덤프를 impdp.exe 로 복구. 서버측 파일 접근이라 DIRECTORY 객체를 만든 뒤 실행한다.
        /// 다중 파일(%U 분할 등)은 콤마로 묶어 한 잡으로 임포트. 원본≠대상이면 remap_schema.
        /// </summary>
        private static async Task<bool> RunDataPumpAsync(
            string home, DumpRestoreOptions opt, IReadOnlyList<string> files, string srcSchema, string tgtSchema,
            Action<string> log, CancellationToken ct)
        {
            var impdp = Path.Combine(home, "bin", "impdp.exe");
            if (!File.Exists(impdp)) { log("!! impdp.exe 없음: " + impdp); return false; }

            var dumpDir = Path.GetDirectoryName(files[0])!;
            var dumpFiles = string.Join(",", files.Select(f => Path.GetFileName(f)));
            var logFile = Path.GetFileNameWithoutExtension(files[0]) + "_imp.log";

            // 덤프가 놓인 폴더를 가리키는 DIRECTORY 객체를 만든다(관리형 연결).
            log($"── DIRECTORY 객체 생성: {RestoreDirObject} → {dumpDir}");
            await CreateDirectoryObjectAsync(opt.DbaConnect, dumpDir, log, ct);

            // schemas / remap_schema 결정
            string schemaArg = string.Equals(srcSchema, tgtSchema, StringComparison.OrdinalIgnoreCase)
                ? $"schemas={tgtSchema}"
                : $"schemas={srcSchema} remap_schema={srcSchema}:{tgtSchema}";

            // [찾기:parfile] 접속정보(userid)는 명령줄 대신 임시 parfile 로 전달(프로세스 목록 평문 노출 방지).
            var parfile = WriteUseridParfile(opt.DbaConnect);
            try
            {
                var args = $"parfile=\"{parfile}\" directory={RestoreDirObject} dumpfile=\"{dumpFiles}\" "
                         + $"logfile=\"{logFile}\" {schemaArg} table_exists_action=replace";
                log($"── impdp 실행: {schemaArg}  (dumpfile={dumpFiles})");
                log($"   (로그: {Path.Combine(dumpDir, logFile)})");

                int code = await RunProcAsync(impdp, args, home, log, ct);
                return InterpretExit(code, Path.Combine(dumpDir, logFile), log);
            }
            finally
            {
                TryDeleteParfile(parfile, log);
            }
        }

        /// <summary>
        /// [찾기:선정리] 재적재 전 대상 스키마의 기존 테이블을 정리한다(recovery.bat 의 dropdb.txt 대응).
        /// all_tables 로 대상 소유자의 테이블을 열거해 DROP(CASCADE CONSTRAINTS PURGE) 또는 TRUNCATE.
        /// 보호 스키마는 호출 전에 이미 차단된다. 개별 실패는 건너뛰며 끝까지 시도하되,
        /// 1건이라도 실패가 남으면 false(중복 적재 방지 — classic imp 는 append 전용). 접속 실패도 false.
        /// </summary>
        private static async Task<bool> CleanTargetSchemaAsync(
            string dbaConnect, string tgtSchema, RestoreCleanMode mode, Action<string> log, CancellationToken ct)
        {
            var owner = SanitizeIdentifier(tgtSchema).ToUpperInvariant();
            if (owner.Length == 0) { log("!! 대상 스키마 이름이 올바르지 않습니다: " + tgtSchema); return false; }

            List<string> tables = new();
            try
            {
                // 접속문자열 변환 실패(형식 오류)도 아래 catch 에서 사유와 함께 false 로 떨어진다.
                var managed = ImpConnectToManaged(dbaConnect);
                await using var conn = DbProvider.CreateConnection(DbProviderKind.Oracle, DbConnectionMode.Direct, managed);
                await conn.OpenAsync(ct);
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = $"SELECT table_name FROM all_tables WHERE owner = '{owner}' ORDER BY table_name";
                    using var rd = await q.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        tables.Add(rd.GetString(0));
                }

                if (tables.Count == 0) { log($"── 선정리: '{owner}' 스키마에 정리할 테이블이 없습니다(빈 스키마)."); return true; }

                string verb = mode == RestoreCleanMode.DropTargetTables ? "DROP" : "TRUNCATE";
                log($"── 선정리({verb}): '{owner}' 스키마 테이블 {tables.Count}개 정리 시작.");
                int done = 0, failed = 0;
                foreach (var t in tables)
                {
                    ct.ThrowIfCancellationRequested();
                    var safeT = SanitizeIdentifier(t);
                    if (safeT.Length == 0) continue;
                    var sql = mode == RestoreCleanMode.DropTargetTables
                        ? $"DROP TABLE \"{owner}\".\"{safeT}\" CASCADE CONSTRAINTS PURGE"
                        : $"TRUNCATE TABLE \"{owner}\".\"{safeT}\"";
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = sql;
                        await cmd.ExecuteNonQueryAsync(ct);
                        done++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        log($"   · {verb} 실패(계속): {safeT} — {ex.Message}");
                    }
                }
                log($"── 선정리 종료: 성공 {done} / 실패 {failed}.");
                // classic imp 는 append(INSERT) 전용 — 정리 안 된 테이블 위에 그대로 진행하면 중복 적재된다.
                // 1건이라도 실패하면 복구를 중단시키도록 false 를 돌려준다.
                if (failed > 0)
                {
                    log($"!! 선정리 실패 {failed}건 — 남은 데이터 위에 imp 하면 중복되므로 복구를 진행하지 않습니다.");
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log("!! 선정리 접속/조회 실패: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// [찾기:종료코드해석] imp/impdp 종료 코드를 해석해 "완료 여부"를 정한다.
        ///   0 = 오류 없이 완료 / 5(impdp) = 완료했으나 무해할 수 있는 오류 포함(예: 이미 존재 ORA-31684)
        ///   그 외 = 실패로 간주.
        ///   ※ classic imp 는 경고가 있으면 흔히 종료코드 1(EX_OKWARN)을 낸다 — 데이터는 적재된 경우가 많아
        ///     '완료(경고)'로 보고 로그 확인을 안내한다.
        /// </summary>
        private static bool InterpretExit(int code, string logPath, Action<string> log)
        {
            log($"── 종료 코드 = {code}");
            if (code == 0) { log("── 결과: 오류 없이 완료 ✓"); return true; }
            if (code == 1 || code == 5)
            {
                log("── 결과: 완료(경고/무해한 오류 포함 가능 — 예: 이미 존재·일부 FK 제약 스킵). 로그 확인:");
                log("   " + logPath);
                return true;
            }
            log($"!! 결과: 실패로 간주(코드 {code}). 로그를 확인하세요: {logPath}");
            return false;
        }

        /// <summary>
        /// [찾기:디렉터리객체] impdp 가 읽을 수 있도록, 덤프 폴더를 가리키는 DIRECTORY 객체를 CREATE 한다.
        /// DbaConnect(user/pwd@host:port/service)를 관리형 접속문자열로 변환해 접속한다.
        /// </summary>
        private static async Task CreateDirectoryObjectAsync(
            string dbaConnect, string dumpDir, Action<string> log, CancellationToken ct)
        {
            var managed = ImpConnectToManaged(dbaConnect);
            await using var conn = DbProvider.CreateConnection(DbProviderKind.Oracle, DbConnectionMode.Direct, managed);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            // 경로에 작은따옴표가 들어갈 일은 거의 없지만 방어적으로 이스케이프.
            var safeDir = dumpDir.Replace("'", "''");
            cmd.CommandText = $"CREATE OR REPLACE DIRECTORY {RestoreDirObject} AS '{safeDir}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// [찾기:접속변환] imp 형식(user/pwd@host:port/service)을 관리형 드라이버 접속문자열로 변환.
        /// 예: system/&lt;비밀번호&gt;@localhost:1522/NAIMS12
        ///   → User Id="system";Password="&lt;비밀번호&gt;";Data Source="localhost:1522/NAIMS12";
        /// 분리 규칙: 접속대상은 **마지막 @** 뒤(암호에 @ 가 있어도 호스트부엔 @ 가 없음),
        /// 사용자/암호는 **첫 /** 로 나눈다(사용자명엔 / 가 없고 암호엔 올 수 있음).
        /// 값은 큰따옴표로 인용해 암호의 ';' 등 특수문자가 접속문자열을 깨뜨리지 않게 한다.
        /// 분리가 모호한 입력(@·/ 누락, 빈 조각)은 명확한 메시지로 실패시킨다.
        /// </summary>
        private static string ImpConnectToManaged(string impConnect)
        {
            var s = impConnect.Trim();
            int at = s.LastIndexOf('@');
            if (at < 0)
                throw new FormatException(
                    "DBA 접속 문자열에 '@' 가 없어 접속 대상을 알 수 없습니다. " +
                    "형식: user/암호@host:port/service (예: system/pw@localhost:1522/NAIMS12)");
            string userPwd = s[..at];
            string dataSource = s[(at + 1)..];
            if (dataSource.Length == 0)
                throw new FormatException("'@' 뒤의 접속 대상(host:port/service)이 비어 있습니다.");

            int slash = userPwd.IndexOf('/');
            if (slash <= 0 || slash == userPwd.Length - 1)
                throw new FormatException(
                    "사용자/암호를 분리할 수 없습니다(빈 사용자명 또는 빈 암호). " +
                    "형식: user/암호@host:port/service — 암호가 비면 imp/impdp 가 대화형으로 암호를 물어 멈춥니다.");
            string user = userPwd[..slash];
            string pwd = userPwd[(slash + 1)..];

            return $"User Id={QuoteConnValue(user)};Password={QuoteConnValue(pwd)};Data Source={QuoteConnValue(dataSource)};";
        }

        /// <summary>접속문자열 값 인용 — 내부 큰따옴표는 두 번 써서 이스케이프(관리형 드라이버 규약).</summary>
        private static string QuoteConnValue(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";

        /// <summary>
        /// [찾기:parfile] imp/impdp 접속정보(userid=…)를 담은 임시 parfile 을 만든다.
        /// 명령줄 인자 대신 이 파일을 parfile= 로 넘기면
        ///   ① 작업관리자/프로세스 목록에 암호가 평문 노출되지 않고
        ///   ② 암호의 특수문자('@'/';' 등)로 인한 셸/인자 파싱 깨짐이 없다.
        /// (userid 문자열 자체의 Oracle 해석은 동일 — 암호에 @ 가 있으면 user/"p@ss"@svc 처럼 인용해 입력.)
        /// 호출부가 finally 에서 <see cref="TryDeleteParfile"/> 로 반드시 삭제한다.
        /// BOM 없이 기록 — 접속정보는 ASCII 전제(비ASCII 암호는 UTF-8 로 적히므로 환경에 따라 실패할 수 있음).
        /// </summary>
        private static string WriteUseridParfile(string dbaConnect)
        {
            var path = Path.Combine(Path.GetTempPath(), "mig_imp_" + Guid.NewGuid().ToString("N") + ".par");
            File.WriteAllText(path, "userid=" + dbaConnect.Trim() + Environment.NewLine, new UTF8Encoding(false));
            return path;
        }

        /// <summary>임시 parfile 삭제(암호가 든 파일이므로 사용 즉시 지운다). 실패해도 복구 흐름은 막지 않는다.</summary>
        private static void TryDeleteParfile(string path, Action<string> log)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { log($"!! 임시 parfile 삭제 실패(수동 삭제 요망): {path} — {ex.Message}"); }
        }

        /// <summary>
        /// [찾기:식별자검증] Oracle 식별자(스키마/테이블명)에서 안전한 문자만 남긴다(SQL 인젝션 방어).
        /// all_tables 에서 온 이름은 이미 신뢰 가능하지만, 인라인 DDL 이므로 방어적으로 필터한다.
        /// </summary>
        private static string SanitizeIdentifier(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw.Trim())
                if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#') sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// [찾기:프로세스실행] 외부 실행파일을 돌리고 표준출력/오류를 줄 단위로 로그 콜백에 흘려보낸다.
        /// 취소 시 프로세스를 강제 종료한다. 종료 코드를 반환한다.
        /// Oracle 콘솔 도구는 Windows OEM 코드페이지(한국어=949)로 출력하므로 그 인코딩으로 읽는다.
        /// </summary>
        private static async Task<int> RunProcAsync(
            string exe, string args, string workDir, Action<string> log, CancellationToken ct)
        {
            Encoding outEnc;
            try
            {
                Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                outEnc = Encoding.GetEncoding(949);
            }
            catch { outEnc = Encoding.UTF8; }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardInput = true,   // [찾기:행업차단] 아래에서 즉시 닫아 대화형 암호 프롬프트 무한대기를 막는다
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = outEnc,
                StandardErrorEncoding = outEnc,
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };

            proc.Start();
            // [찾기:행업차단] imp/impdp 는 접속정보가 불완전하면 "Password:" 를 물으며 stdin 을 기다린다.
            // stdin 을 즉시 닫아(EOF) 프롬프트가 즉시 실패로 떨어지게 해 무한 행업을 차단한다.
            try { proc.StandardInput.Close(); } catch { /* 이미 종료됐으면 무시 */ }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 이미 종료됨 */ }
            }))
            {
                await proc.WaitForExitAsync(ct);
            }
            // .NET 문서화된 동작: WaitForExitAsync/타임아웃 오버로드는 리다이렉트 출력 드레인을 보장하지 않는다.
            // 인자 없는 WaitForExit() 를 한 번 더 호출해야 OutputDataReceived 로 남은 출력이 전부 흘러나온다.
            proc.WaitForExit();
            return proc.ExitCode;
        }

        /// <summary>종류 라벨 문구.</summary>
        public static string KindLabel(OracleDumpKind kind) => kind switch
        {
            OracleDumpKind.ClassicExp => "classic exp (→ imp.exe)",
            OracleDumpKind.DataPump => "Data Pump (→ impdp.exe)",
            _ => "판별 불가",
        };
    }
}
