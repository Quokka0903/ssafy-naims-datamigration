using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Simul_AcousticInfoAnalysis.Import;
using Simul_AcousticInfoAnalysis.BWFReader;
using static Simul_AcousticInfoAnalysis.SqlConnect;
using static Simul_AcousticInfoAnalysis.ReadFileToDB.EventListReader;
using static Simul_AcousticInfoAnalysis.Import.SdLoader;
using static Simul_AcousticInfoAnalysis.ReadFileToDB.ClaFileReader;
using System.Diagnostics;
using System.Globalization;

namespace Simul_AcousticInfoAnalysis;

/// <summary>
/// *.sd 패키지를 읽어 DB에 삽입하는 클래스
/// </summary>
public static class SdPackageImporter
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
        DateTime.TryParse(s, out var d) ? d : (DateTime?)null;

    
    private static double CalcDurationMs(long start, long end, long preCalcMs)
            => (start > 0 && end > 0 && end >= start) ? end - start : preCalcMs;

    /// <summary>DB 중복 체크</summary>
    private static async Task<bool> ExistsAsync(
        SqlConnection c, SqlTransaction tx,
        string table, string keyCol, string keyVal)
    {
        string sql = $"SELECT 1 FROM {table} WITH(NOLOCK) WHERE {keyCol} = @val";
        await using var cmd = new SqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@val", keyVal);
        return (await cmd.ExecuteScalarAsync()) != null;
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
        try { pkg = SdLoader.Load(pkgDir); }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR ] {preciseName} : {ex.Message}");
            return;
        }

        // 병합: EventList + Ai/Tr + .cla → importEvents
        List<ShortPeriodEvent> importEvents =
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

    /*──────────────  C. EVENT / SEGMENT / FRAME INSERT  ─────────────*/

    private static async Task InsertShortPeriodEventAsync(
        SqlConnection c, SqlTransaction tx,
        int preciseAnalId, List<SdLoader.ShortPeriodEvent> eventList)
    {
        /*――― 1) EVENT ―――*/
        const string evSql = @"
INSERT INTO PREC_SS_EVENT
 (REF_PREC_ANALYSIS_ID, START_TIME, END_TIME, EVENT_LENGTH_MICROSEC, DETECTING_TYPE,
  MANUAL_AUTO, RAW_ANAL_COMMENT, AI_SEGMENT_CNT, TR_SEGMENT_CNT, UNK_SEGMENT_CNT,
  PRI, SPL, TRM_MODE, MODULATION, SCORE, DB_PIS_ID,
  IS_CLASSIFIED, IS_SNIPPET, CLASSIFICATION_COMMENT,
  SIG_START_TIME, SIG_FILE, GRAM_SNIPPET_FILE, AUDIO_SNIPPET_FILE,
  SNIPPET_COMMENT,
  DETECTION_SEA_AREA_LATITUDE, DETECTION_SEA_AREA_LONGITUDE,
  CHNG_DATETIME, DELETED, IS_TRAINING,
  SONAR_NAME, PULSE_NAME)
VALUES
 (@paId,@st,@et,@len,@dtype,
  @man,N'NAIMS-I Imported',@aiCnt,@trCnt,@unkCnt,
  @pri,@spl,@trm,@mod,@score,0,
  @isCls,@isSnip,@clsCmt,
  @sigStart,@sig,@gram,@audio,
  @snipCmt,
  @lat,@lon,
  SYSDATETIME(),0,0,
  @sonar,@pulse);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

        /*――― 2) SEGMENT ―――*/
        const string segSql = @"
INSERT INTO PREC_SS_SEGMENT
 (REF_PREC_SS_EVENT_ID, START_TIME, END_TIME, SEGMENT_LENGTH_MICROSEC, LDA_X, LDA_Y,
  DETECT_TYPE, FRAME_COUNT, START_FREQ, END_FREQ,
  MODULATION, CENTER_FREQ, BANDWIDTH, SCORE, DB_SEG_ID,
  IS_SELECTED, HAS_SNIPPET, SNIPPET_FILE, GRAM_FILE,
  MATCH_RATE, TR_SOURCE_NAME, TR_TYPE, COMMENT, DB_APPLIED_DATE,
  IS_CONFIRMED, IS_MERGED_CLASSIFICATION, PATTERN_VALUE,
  CHNG_DATETIME, DELETED, IS_TRAINING)
VALUES
 (@peId,@st,@et,@len,@ldaX,@ldaY,
  @dtype,@frmCnt,@f0,@f1,
  @mod,@fc,@bw,@score,@dbSeg,
  @isSel,@hasSnip,@snip,@gram,
  0,@trSrc,@trType,@segCmt,@dbDate,
  N'N',N'N',@ptn,
  SYSDATETIME(),0,0);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

        /*――― 3) FRAME ―――*/
        const string frmSql = @"
INSERT INTO PREC_SS_FRAME
 (REF_PREC_SS_SEGMENT_ID,
  XI_01,XI_02,XI_03,XI_04,XI_05,XI_06,XI_07,XI_08,
  XI_09,XI_10,XI_11,XI_12,XI_13,XI_14,XI_15,XI_16,
  XI_17,XI_18,XI_19,XI_20,XI_21,XI_22,XI_23,XI_24,
  XI_25,XI_26,XI_27,XI_28,XI_29,XI_30,XI_31,XI_32,
  SCORE, SEG_ID, DB_FRM_ID, DB_FRAME_INDEX,
  CHNG_DATETIME, DELETED, IS_TRAINING)
VALUES
 (@psId,
  @x1,@x2,@x3,@x4,@x5,@x6,@x7,@x8,
  @x9,@x10,@x11,@x12,@x13,@x14,@x15,@x16,
  @x17,@x18,@x19,@x20,@x21,@x22,@x23,@x24,
  @x25,@x26,@x27,@x28,@x29,@x30,@x31,@x32,
  @score,@segId,@dbFrmId,0,
  SYSDATETIME(),0,0);";

        await using var evCmd = new SqlCommand(evSql, c, tx);
        await using var segCmd = new SqlCommand(segSql, c, tx);
        await using var frCmd = new SqlCommand(frmSql, c, tx);

        /*――― EVENT LOOP ―――*/
        foreach (var ev in eventList)
        {
            #region EVENT PARAMS
            evCmd.Parameters.Clear();
            evCmd.Parameters.AddWithValue("@paId", preciseAnalId);
            evCmd.Parameters.AddWithValue("@st", ev.StartTime);
            evCmd.Parameters.AddWithValue("@et", ev.EndTime);
            evCmd.Parameters.Add("@len", SqlDbType.Float)
                 .Value = CalcDurationMs(ev.StartTime, ev.EndTime, ev.EventLength);

            evCmd.Parameters.AddWithValue("@dtype", DetectTypeToInt(ev.DetectingType));
            evCmd.Parameters.AddWithValue("@man", ev.ManualOrAuto ? 1 : 0);

            evCmd.Parameters.AddWithValue("@aiCnt",
                ev.Segments.Count(s => s.DetectingType == (int)DetectType.AI));
            evCmd.Parameters.AddWithValue("@trCnt",
                ev.Segments.Count(s => s.DetectingType == (int)DetectType.TR));
            evCmd.Parameters.AddWithValue("@unkCnt",
                ev.Segments.Count(s => s.DetectingType == (int)DetectType.UNK));

            evCmd.Parameters.AddWithValue("@pri", ev.PRI);
            evCmd.Parameters.AddWithValue("@spl", ev.SPL);
            evCmd.Parameters.AddWithValue("@trm", ev.TRMMode ?? "");
            evCmd.Parameters.AddWithValue("@mod", ev.Modulation ?? "");
            evCmd.Parameters.AddWithValue("@score",
                ev.fScore != 0 ? ev.fScore : 0);

            /* 분류/스니펫 상태 */
            evCmd.Parameters.AddWithValue("@isCls", ev.byteAppliedState == 1 ? "Y" : "N");
            evCmd.Parameters.AddWithValue("@isSnip", ev.byteSnippetAppliedState == 1 ? "Y" : "N");
            evCmd.Parameters.AddWithValue("@clsCmt", "NAIMS-I Imported");

            /* Signal 시작 시각 */
            evCmd.Parameters.AddWithValue("@sigStart", ev.SigStartTime  > new DateTime(1753, 1, 1) ? ev.SigStartTime : new DateTime(1753, 1, 1) );

            evCmd.Parameters.Add("@sig", SqlDbType.VarBinary)
                 .Value = ev.SigFile ?? Array.Empty<byte>();
            evCmd.Parameters.Add("@gram", SqlDbType.VarBinary)
                 .Value = ev.GramFile ?? Array.Empty<byte>();
            evCmd.Parameters.Add("@audio", SqlDbType.VarBinary)
                 .Value = ev.AudioSnippetFile ?? Array.Empty<byte>();
            evCmd.Parameters.AddWithValue("@snipCmt", ev.SnippetComment ?? "");

            /* 위치 정보 -> INT(4) */
            evCmd.Parameters.AddWithValue("@lat",
                TryParseInt(ev.strDetectionSeaAreaLatitude));
            evCmd.Parameters.AddWithValue("@lon",
                TryParseInt(ev.strDetectionSeaAreaLongitude));

            evCmd.Parameters.AddWithValue("@sonar", ev.strSonarName ?? "NAIMS-I Imported");
            evCmd.Parameters.AddWithValue("@pulse", ev.strPulseName ?? "NAIMS-I Imported");
            #endregion

            int preciseEventId = (int)await evCmd.ExecuteScalarAsync();

            /*――― SEGMENT LOOP ―――*/
            foreach (var seg in ev.Segments)
            {
                #region SEG PARAMS
                segCmd.Parameters.Clear();
                segCmd.Parameters.AddWithValue("@peId", preciseEventId);
                segCmd.Parameters.AddWithValue("@st", seg.StartTime);
                segCmd.Parameters.AddWithValue("@et", seg.EndTime);
                segCmd.Parameters.Add("@len", SqlDbType.Float)
                      .Value = CalcDurationMs(seg.StartTime, seg.EndTime, seg.Length);

                segCmd.Parameters.AddWithValue("@ldaX", seg.LDAx);
                segCmd.Parameters.AddWithValue("@ldaY", seg.LDAy);
                segCmd.Parameters.AddWithValue("@dtype", seg.DetectingType);
                segCmd.Parameters.AddWithValue("@frmCnt", seg.FrameCount);
                segCmd.Parameters.AddWithValue("@f0", seg.StartFreq);
                segCmd.Parameters.AddWithValue("@f1", seg.EndFreq);

                segCmd.Parameters.AddWithValue("@mod", seg.Modulation);
                segCmd.Parameters.AddWithValue("@fc", seg.CenterFreq);
                segCmd.Parameters.AddWithValue("@bw", seg.BandWidth);
                segCmd.Parameters.AddWithValue("@score", seg.fScore);
                segCmd.Parameters.AddWithValue("@dbSeg", seg.DBSegID);
                segCmd.Parameters.AddWithValue("@isSel", seg.byteAppliedState == 1 ? "Y" : "N");

                segCmd.Parameters.AddWithValue("@hasSnip", seg.SnippetFile != null ? "Y" : "N");
                segCmd.Parameters.Add("@snip", SqlDbType.VarBinary)
                      .Value = seg.SnippetFile ?? Array.Empty<byte>();
                segCmd.Parameters.Add("@gram", SqlDbType.VarBinary)
                      .Value = seg.GramFile ?? Array.Empty<byte>();

                segCmd.Parameters.AddWithValue("@trSrc", seg.strTransientName ?? "");
                segCmd.Parameters.AddWithValue("@trType", seg.strGroup ?? "");
                segCmd.Parameters.AddWithValue("@segCmt", seg.strComments ?? "NAIMS-I Imported");
                segCmd.Parameters.AddWithValue("@dbDate",
                    ParseDate(seg.strDBAppliedDate) ?? (object)DBNull.Value);

                segCmd.Parameters.AddWithValue("@ptn", seg.PatternValue);
                #endregion

                int preciseSegId = (int)await segCmd.ExecuteScalarAsync();

                /*――― FRAME LOOP ―――*/
                foreach (var fr in seg.Frames)
                {
                    frCmd.Parameters.Clear();
                    frCmd.Parameters.AddWithValue("@psId", preciseSegId);

                    // 32-bin MFB
                    for (int i = 0; i < 32; i++)
                    {
                        float xi = i < fr.MFBs.Count ? (float)fr.MFBs[i] : 0f;
                        frCmd.Parameters.Add($"@x{i + 1}", SqlDbType.Real).Value = xi;
                    }

                    frCmd.Parameters.AddWithValue("@score", fr.Score);
                    frCmd.Parameters.AddWithValue("@segId", preciseSegId);
                    frCmd.Parameters.AddWithValue("@dbFrmId", fr.DBFrmID);

                    await frCmd.ExecuteNonQueryAsync();
                }
            }
        }
    }

}
