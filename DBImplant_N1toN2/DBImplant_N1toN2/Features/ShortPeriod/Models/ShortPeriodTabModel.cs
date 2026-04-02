using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Models
{
    public sealed class PackageRow
    {
        public string PackageDir { get; }
        public string PreciseName { get; }

        public string Status { get; set; } = "";
        public string Error { get; set; } = "";

        public int EventCount { get; set; }
        public int SegmentCount { get; set; }
        public int FrameCount { get; set; }

        public bool HasAiCla { get; set; }
        public bool HasTrCla { get; set; }
        public bool HasSps { get; set; }
        public string SsParamSummary { get; set; } = "";

        public ObservableCollection<ShortPeriodEvent>? ImportEvents { get; set; }

        public PackageRow(string pkgDir)
        {
            PackageDir = pkgDir;
            PreciseName = Path.GetFileName(pkgDir.TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    public sealed class EventRow
    {
        public long EventID { get; }
        public string DetectingType { get; }
        public long StartTime { get; }
        public long EndTime { get; }
        public int SegmentCount { get; }
        public bool ManualOrAuto { get; }
        public double Score { get; }
        public string SonarName { get; }
        public string PulseName { get; }

        public EventRow(ShortPeriodEvent ev)
        {
            EventID = ev.EventID;
            DetectingType = ev.DetectingType ?? "";
            StartTime = ev.StartTime;
            EndTime = ev.EndTime;
            SegmentCount = ev.Segments?.Count ?? 0;
            ManualOrAuto = ev.ManualOrAuto;
            Score = ev.fScore;
            SonarName = ev.strSonarName ?? "";
            PulseName = ev.strPulseName ?? "";
        }
    }

    public sealed class SegmentRow
    {
        public int SegmentID { get; }
        public int DetectingType { get; }
        public double StartFreq { get; }
        public double EndFreq { get; }
        public int FrameCount { get; }
        public double PatternValue { get; }
        public string Group { get; }
        public string TransientName { get; }
        public long DBSegID { get; }

        public SegmentRow(ShortPeriodSegment seg)
        {
            SegmentID = seg.SegmentID;
            DetectingType = seg.DetectingType;
            StartFreq = seg.StartFreq;
            EndFreq = seg.EndFreq;
            FrameCount = seg.Frames?.Count ?? seg.FrameCount;
            PatternValue = seg.PatternValue;
            Group = seg.strGroup ?? "";
            TransientName = seg.strTransientName ?? "";
            DBSegID = seg.DBSegID;
        }
    }

    public sealed class FrameRow
    {
        public int Index { get; }
        public long DBFrmID { get; }
        public float Score { get; }
        public int MfbCount { get; }
        public string MfbPreview { get; }

        public FrameRow(ShortPeriodFrame fr, int index)
        {
            Index = index;
            DBFrmID = fr.DBFrmID;
            Score = fr.Score;
            MfbCount = fr.MFBs?.Count ?? 0;

            MfbPreview = fr.MFBs == null
                ? ""
                : string.Join(", ", fr.MFBs.Take(8).Select(v => v.ToString("0.###"))) + (fr.MFBs.Count > 8 ? " ..." : "");
        }
    }
}
