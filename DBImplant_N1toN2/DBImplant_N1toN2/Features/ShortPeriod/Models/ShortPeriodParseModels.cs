using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.Models
{
    public sealed class FileRow
    {
        public string RelativePath { get; }
        public string FullPath { get; }
        public long Length { get; }
        public DateTime LastWriteTime { get; }

        public FileRow(string rel, string full, long len, DateTime lwt)
        {
            RelativePath = rel;
            FullPath = full;
            Length = len;
            LastWriteTime = lwt;
        }
    }

    public sealed class SetParamRow
    {
        public bool Exists { get; set; }
        public string? FilePath { get; set; }
        public double DetectThreshold { get; set; }
        public double DtThreshold { get; set; }
        public double BwThreshold { get; set; }
        public string? Error { get; set; }
    }

    public sealed class LstParseRow
    {
        public bool Exists { get; set; }
        public string? FilePath { get; set; }
        public EventListReader.Header? Header { get; set; }
        public ObservableCollection<ShortPeriodEvent> Events { get; } = new();
        public string? Error { get; set; }
    }

    public sealed class SpsParseRow
    {
        public bool Exists { get; set; }
        public string? FilePath { get; set; }
        public ObservableCollection<ShortPeriodEvent> Events { get; } = new();
        public string? Error { get; set; }
    }

    public sealed class AiClaPulseRow
    {
        public long PID { get; set; }
        public string SonarName { get; set; } = "";
        public string PulseName { get; set; } = "";
        public double Score { get; set; }
        public int SegmentCount { get; set; }
        public string GramSnippet { get; set; } = "";
        public string AudioSnippet { get; set; } = "";
        public string Comment { get; set; } = "";
    }

    public sealed class TrClaSegmentRow
    {
        public long EventID { get; set; }
        public long SegmentID { get; set; }
        public string Group { get; set; } = "";
        public string TransientName { get; set; } = "";
        public int FrameNum { get; set; }
        public double Score { get; set; }
        public string AudioSnippet { get; set; } = "";
        public byte AppliedState { get; set; }
        public int DBApplyID { get; set; }
        public byte SnippetAppliedState { get; set; }
    }

    public sealed class AiTmpEventRow
    {
        public long EventID { get; set; }
        public string ConfirmedSonarName { get; set; } = "";
        public string ConfirmedPulseName { get; set; } = "";
        public float ConfirmedScore { get; set; }
        public int CandidateCount { get; set; }
    }

    public sealed class AiTmpCandidateRow
    {
        public long EventID { get; set; }
        public int Rank { get; set; }
        public long DBPulseID { get; set; }
        public string SonarName { get; set; } = "";
        public string PulseName { get; set; } = "";
        public double Score { get; set; }
        public int SegmentCount { get; set; }
        public bool IsSelected { get; set; }
    }

    public sealed class AiTmpSegmentRow
    {
        public long EventID { get; set; }
        public int Rank { get; set; }
        public long SegmentID { get; set; }
        public long DbSegmentID { get; set; }
        public double Score { get; set; }
        public double StartFreq { get; set; }
        public double EndFreq { get; set; }
    }

    public sealed class TrTmpEventRow
    {
        public long EventID { get; set; }
        public long ConfirmedCount { get; set; }
        public long SegmentCount { get; set; }
    }

    public sealed class TrTmpSegmentRow
    {
        public long EventID { get; set; }
        public int SegmentID { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public double Score { get; set; }      // MatchRate 매핑

        public string SourceName { get; set; } = "";
        public byte Type { get; set; }
        public string Comment { get; set; } = "";
        public bool IsMergedCls { get; set; }  // Comment에 "동시식별" 포함 여부
    }


    public sealed class AnalysisFolderParseRow
    {
        public bool Exists { get; set; }
        public string? FolderPath { get; set; }

        public LstParseRow Lst { get; } = new();
        public ObservableCollection<FileRow> SnippetWavs { get; } = new();
        public ObservableCollection<FileRow> SnippetGrms { get; } = new();

        public bool ClaExists { get; set; }
        public string? ClaPath { get; set; }
        public string? ClaError { get; set; }

        public ObservableCollection<AiClaPulseRow> AiClaPulses { get; } = new();          // AI 전용
        public ObservableCollection<TrClaSegmentRow> TrClaSegments { get; } = new();      // TR 전용

        public bool TmpExists { get; set; }
        public string? TmpPath { get; set; }
        public string? TmpError { get; set; }

        public ObservableCollection<AiTmpEventRow> AiTmpEvents { get; } = new();
        public ObservableCollection<AiTmpCandidateRow> AiTmpCandidates { get; } = new();
        public ObservableCollection<AiTmpSegmentRow> AiTmpSegments { get; } = new();

        public ObservableCollection<TrTmpEventRow> TrTmpEvents { get; } = new();
        public ObservableCollection<TrTmpSegmentRow> TrTmpSegments { get; } = new();
    }

    public sealed class SdPackageParseResult
    {
        public string SdDir { get; set; } = "";
        public string PreciseName { get; set; } = "";

        public ObservableCollection<FileRow> RootSignalWavs { get; } = new();

        public SpsParseRow Sps { get; } = new();
        public LstParseRow EventLst { get; } = new();
        public SetParamRow SetParam { get; } = new();

        public AnalysisFolderParseRow Ai { get; } = new();
        public AnalysisFolderParseRow Tr { get; } = new();
    }

    public sealed class SdPackageItem : INotifyPropertyChanged
    {
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }
        private bool _isChecked;

        public string SdDir { get; }
        public string PreciseName { get; }

        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }
        private string _status = "대기";

        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }
        private string _error = "";

        public int RootWavCount { get; set; }
        public int SpsEventCount { get; set; }
        public int LstEventCount { get; set; }
        public bool HasSet { get; set; }
        public int AiEventCount { get; set; }
        public int TrEventCount { get; set; }

        public SdPackageParseResult? Parsed { get; set; }

        public SdPackageItem(string sdDir)
        {
            SdDir = sdDir;
            PreciseName = System.IO.Path.GetFileName(sdDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }
}
