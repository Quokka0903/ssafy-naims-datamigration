using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    // TR 이벤트/세그먼트 결과 후보
    public class TR_EVENT_RESULT
    {
        public long EventID { get; set; }
        public long ConfirmedCount { get; set; }
        public long SegmentCount { get; set; }
        public List<TR_SEGMENT_RESULT_CANDIDATE> listSegmentResult { get; } = new List<TR_SEGMENT_RESULT_CANDIDATE>();
    }

    public class TR_SEGMENT_RESULT_CANDIDATE
    {
        public int ConfirmedItem { get; set; }
        public long SegmentID { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public double Score { get; set; }
        // 필요하면 추가 필드…
    }

    // AI 이벤트/후보/세그먼트 결과 후보
    public class AI_EVENT_RESULT
    {
        public int EventID { get; set; }            // C++ long = 4 bytes
        public string ConfirmedSonarName { get; set; } = string.Empty;
        public string ConfirmedPulseName { get; set; } = string.Empty;
        public string ConfirmedComment { get; set; } = string.Empty;
        public float ConfirmedScore { get; set; }
        public int ConfirmMode { get; set; }
        public int ConfirmedDBPulseID { get; set; } // C++ long = 4 bytes
        public List<AI_RESULT_CANDIDATE> listCandidate { get; } = new List<AI_RESULT_CANDIDATE>();
    }

    public class AI_RESULT_CANDIDATE
    {
        public int DBPulseID { get; set; }          // C++ long = 4 bytes
        public string PulseName { get; set; } = string.Empty;
        public string SonarName { get; set; } = string.Empty;
        public double Score { get; set; }
        public int Rank { get; set; }
        public int SegmentCount { get; set; }
        public bool IsSelected { get; set; }
        public List<AI_SEGMENT_RESULT> listSegmentResult { get; } = new List<AI_SEGMENT_RESULT>();
    }

    public class AI_SEGMENT_RESULT
    {
        public int DbSegmentID { get; set; }   // m_lDBSegmentID (C++ long = 4 bytes)
        public int SegmentID { get; set; }     // m_lSegmentID   (C++ long = 4 bytes)
        public int Rank { get; set; }          // m_iRank
        public double Score { get; set; }      // m_dScore
    }

    // TR_RESULT_ITEM (52 bytes with MSVC default alignment):
    //   long  db_tr_seg_id    (4)
    //   char  tr_source_name[30] (30)
    //   byte  tr_type         (1)
    //   byte  _pad            (1)  // align align float to 4
    //   float score           (4)
    //   float match_rate      (4)
    //   int   rank            (4)
    //   BOOL  b_IsConfirmed   (4)   total = 52
    public sealed class TrResultItem
    {
        public int DbTrSegId { get; set; }
        public string SourceName { get; set; } = "";
        public byte Type { get; set; }
        public float Score { get; set; }
        public float MatchRate { get; set; }
        public int Rank { get; set; }
        public bool IsConfirmed { get; set; }
    }

    // TR_SEGMENT_RESULT (636 bytes with MSVC default alignment):
    //   long  tr_segment_id           (4)  offset 0
    //   int   m_iTRResultItemCount    (4)  offset 4
    //   int   m_iConfirmedItem        (4)  offset 8
    //   char  m_strSourceName[31]    (31)  offset 12
    //   byte  m_byteType              (1)  offset 43
    //   char  m_strComment[81]       (81)  offset 44
    //   byte  _pad[3]                 (3)  offset 125  // align align float to 4
    //   float match_rate              (4)  offset 128
    //   char  m_strAudioSnippetFileName[81] (81) offset 132
    //   byte  _pad[3]                 (3)  offset 213  // align align BOOL(int) to 4
    //   BOOL  m_bIsMergedCls          (4)  offset 216
    //   TR_RESULT_ITEM m_TRResultItem[8] (416) offset 220
    //   total = 636

    public sealed class TrTmpEventResult
    {
        public int EventId { get; set; }
        public int ConfirmedCount { get; set; }
        public int SegmentCount { get; set; }
        public List<TrTmpSegmentResult> Segments { get; } = new List<TrTmpSegmentResult>();
    }

    public sealed class TrTmpSegmentResult
    {
        public int SegmentId { get; set; }        // tr_segment_id
        public int ConfirmedItem { get; set; }    // m_iConfirmedItem (1~8 / -1 / -2)
        public string SourceName { get; set; } = ""; // m_strSourceName
        public byte Type { get; set; }            // m_byteType
        public string Comment { get; set; } = ""; // m_strComment
        public float MatchRate { get; set; }      // match_rate
        public string AudioSnippetFileName { get; set; } = "";
        public bool IsMergedCls { get; set; }     // m_bIsMergedCls
        public TrResultItem? ConfirmedResultItem { get; set; } // m_TRResultItem[m_iConfirmedItem-1]
    }
}
