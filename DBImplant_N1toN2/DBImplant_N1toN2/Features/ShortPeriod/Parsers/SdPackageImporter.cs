using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DBImplant_N1toN2.Audio;
using System.Diagnostics;
using System.Globalization;
using System.Collections.ObjectModel;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers;

/// <summary>
/// *.sd 패키지를 읽어 DB에 삽입하는 클래스
/// </summary>
public static partial class SdPackageImporter
{
    /*────────────────────────────────── 공통 유틸 ──────────────────────────────────*/

    public enum DetectType { UNK = 0, AI = 1, TR = 2 }

    public static int DetectTypeToInt(string type) => type switch
    {
        "AI" => 1,
        "TR" => 2,
        _ => 0
    };

    public static string IntToDetectType(int type) => type switch
    {
        1 => "AI",
        2 => "TR",
        _ => "UNKOWN"
    };

    private static int TryParseInt(string s) =>
            int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParse(s, out var d) ? d : null;

    
    private static double CalcDurationMs(long start, long end, long preCalcMs)
            => start > 0 && end > 0 && end >= start ? end - start : preCalcMs;

    /// <summary>DB 중복 체크</summary>
    private static async Task<bool> ExistsAsync(
        SqlConnection c, SqlTransaction tx,
        string table, string keyCol, string keyVal)
    {
        string sql = $"SELECT 1 FROM {table} WITH(NOLOCK) WHERE {keyCol} = @val";
        await using var cmd = new SqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@val", keyVal);
        return await cmd.ExecuteScalarAsync() != null;
    }

    /*────────────────────────────── 전체 흐름 메서드 ───────────────────────────────*/

    /// <summary>
    /// 루트 경로에서 모든 *.sd 패키지를 찾아 DB에 Import한다.
    /// </summary>
    /// <param name="rootDir">탐색 시작 폴더</param>
    /// <param name="sqlConnStr">DB 커넥션 문자열</param>
    /// <param name="recursive">true면 하위 폴더까지, false면 1-depth만</param>
    public static async Task ImportAsync(string rootDir,
                                         string sqlConnStr,
                                         bool recursive = true)
    {
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException(rootDir);

        /* ① .sd 폴더 목록 수집 */
        var pkgDirs = Directory
            .EnumerateDirectories(rootDir, "*.sd",
                                  recursive ? SearchOption.AllDirectories
                                            : SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int ok = 0, skip = 0, fail = 0;

        /* ② 패키지 루프 */
        foreach (var pkg in pkgDirs)
        {
            try
            {
                await ImportOneAsync(pkg, sqlConnStr);
                ok++;
            }
            catch (ImportSkippedException)   // 이미 등록된 분석
            { skip++; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] {pkg} : {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine($"=== Import 완료  OK:{ok}, Skip:{skip}, Fail:{fail} ===");
    }

    /*― 선택 ― 이미 등록된 분석은 예외로 던져서 상위 루프에서 skip 카운팅 ―*/
    private sealed class ImportSkippedException : Exception
    {
        public ImportSkippedException(string msg) : base(msg) { }
    }
    
    /*────────────────────────────  A. 엔트리 포인트  ───────────────────────────*/
    public static async Task ImportOneAsync(string pkgDir, string sqlConnStr)
    {
        string preciseName = Path.GetFileName(pkgDir);

        /* 0) 패키지 로드 & 병합 */
        SdLoaded pkg;
        try { pkg = Load(pkgDir); }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR ] {preciseName} : {ex.Message}");
            return;
        }

        // 병합: EventList + Ai/Tr + .cla → importEvents
        ObservableCollection<ShortPeriodEvent> importEvents =
            SdImportIntegrator.BuildImportDbList(pkg);

        /* 1) DB 연결 & 트랜잭션 */
        await using var conn = new SqlConnection(sqlConnStr);
        await conn.OpenAsync();
        await using var tx = conn.BeginTransaction();

        /* 2) 중복 체크 */
        if (await ExistsAsync(conn, tx, "PREC_ANALYSIS",
                              "PRECISE_NAME", preciseName))
        {
            Console.WriteLine($"[SKIP] {preciseName} (이미 등록)");
            return;
        }

        /* 3) PRECISE_ANALYSIS */
        int preciseAnalId = await InsertPreciseAnalysisAsync(conn, tx, preciseName);

        /* 4) SS_PARAM & SS_PRECISE_ANALYSIS */
        if (pkg.SsParam is not null)
        {
            int ssParamId = await InsertSsParamAsync(conn, tx, pkg.SsParam);
            await InsertSsPreciseAnalysisAsync(conn, tx,
                                               preciseAnalId, pkg.SsParam);
        }

        /* 5) SS_PRECISE_EVENT / SEG / FRAME */
        if (importEvents.Any())
            await InsertShortPeriodEventAsync(conn, tx, preciseAnalId, importEvents);

        /* 6) (선택) TR 결과 전용 테이블 */
        // if (pkg.TrCla is not null) await InsertTransientAsync(conn, tx, pkg.TrCla);

        await tx.CommitAsync();
        Console.WriteLine($"[ OK ] {preciseName}");
    }

    /*───────────────────  B. PRECISE_ANALYSIS  ─────────────────*/

    private static async Task<int> InsertPreciseAnalysisAsync(
        SqlConnection c, SqlTransaction tx, string preciseName)
    {
        const string sql = @"
INSERT INTO PREC_ANALYSIS
      (REF_PREC_SRC_SIG_ID, PRECISE_NAME, OPERATOR_ID, OPERATION_STATE,
       MISSION_ALLOC_DATE, ACCESS_LEVEL, CHNG_DATETIME,
       COMMENT, DELETED, IS_TRAINING)
VALUES(0, @name, SYSTEM_USER, N'IMPORTED',
       SYSDATETIME(), 0, SYSDATETIME(),
       N'자동 이관', 0, 0);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

        await using var cmd = new SqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@name", preciseName);
        return (int)await cmd.ExecuteScalarAsync();
    }

    private static async Task<int> InsertSsParamAsync(
        SqlConnection c, SqlTransaction tx, SsParam p)
    {
        const string sql = @"
INSERT INTO PREC_SS_PARAM
 (REF_OPERATOR_ID, DT_THRESHOLD, DT_THRESHOLD_STEP, BW_THRESHOLD, BW_THRESHOLD_STEP,
  WINDOW, KAISER_BESSEL_PARAMETER, OVERLAP, N_FFT, CHNG_DATETIME, COMMENT,
  DELETED, IS_TRAINING, FREQ_RESOLUTION)
VALUES (SYSTEM_USER, @dtThr, @dtStep, @bwThr, @bwStep,
        @win, @kB, @ovl, @fft, SYSDATETIME(), N'자동 이관',
        0, 0, @freqRes);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

        await using var cmd = new SqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@dtThr", p.DtThreshold);
        cmd.Parameters.AddWithValue("@dtStep", p.DtThresholdStep);
        cmd.Parameters.AddWithValue("@bwThr", p.BwThreshold);
        cmd.Parameters.AddWithValue("@bwStep", p.BwThresholdStep);
        cmd.Parameters.AddWithValue("@win", p.Window);
        cmd.Parameters.AddWithValue("@kB", p.KaiserBesselParameter);
        cmd.Parameters.AddWithValue("@ovl", p.Overlap);
        cmd.Parameters.AddWithValue("@fft", p.Fft);
        cmd.Parameters.AddWithValue("@freqRes", p.FreqResolution);

        return (int)await cmd.ExecuteScalarAsync();
    }

    private static async Task InsertSsPreciseAnalysisAsync(
        SqlConnection c, SqlTransaction tx,
        int preciseAnalId, SsParam p)
    {
        const string sql = @"
INSERT INTO PREC_SS_ANALYSIS
 (REF_PREC_ANALYSIS_ID, DT_THRESHOLD, DT_THRESHOLD_STEP, BW_THRESHOLD, BW_THRESHOLD_STEP,
  WINDOW, KAISER_BESSEL_PARAMETER, OVERLAP, N_FFT, CHNG_DATETIME,
  DELETED, IS_TRAINING, FREQ_RESOLUTION)
VALUES (@paId, @dtThr, @dtStep, @bwThr, @bwStep,
        @win, @kB, @ovl, @fft, SYSDATETIME(),
        0, 0, @freqRes);";

        await using var cmd = new SqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@paId", preciseAnalId);
        cmd.Parameters.AddWithValue("@dtThr", p.DtThreshold);
        cmd.Parameters.AddWithValue("@dtStep", p.DtThresholdStep);
        cmd.Parameters.AddWithValue("@bwThr", p.BwThreshold);
        cmd.Parameters.AddWithValue("@bwStep", p.BwThresholdStep);
        cmd.Parameters.AddWithValue("@win", p.Window);
        cmd.Parameters.AddWithValue("@kB", p.KaiserBesselParameter);
        cmd.Parameters.AddWithValue("@ovl", p.Overlap);
        cmd.Parameters.AddWithValue("@fft", p.Fft);
        cmd.Parameters.AddWithValue("@freqRes", p.FreqResolution);

        await cmd.ExecuteNonQueryAsync();
    }


}
