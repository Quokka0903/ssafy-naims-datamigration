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

public static partial class SdPackageImporter
{
    /*──────────────  C. EVENT / SEGMENT / FRAME INSERT  ─────────────*/

    private static async Task InsertShortPeriodEventAsync(
        SqlConnection c, SqlTransaction tx,
        int preciseAnalId, ObservableCollection<ShortPeriodEvent> eventList)
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
