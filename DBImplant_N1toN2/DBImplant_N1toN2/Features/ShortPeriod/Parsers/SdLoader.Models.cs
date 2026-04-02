using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    public record SsParam(
        double DtThreshold, int DtThresholdStep,
        double BwThreshold, int BwThresholdStep,
        int Window, int KaiserBesselParameter,
        int Overlap, int Fft, double FreqResolution);

    public static partial class SdLoader
    {
        /*────────────────────── Event / Segment / Frame ───────────────────*/
        // ── SdLoader 내부 (Event / Segment / Frame 관련 코드)
        public class ShortPeriodEvent : INotifyPropertyChanged
        {
            private long _eventID;
            public long EventID { get => _eventID; set => Set(ref _eventID, value); }

            private long _startTime;
            public long StartTime { get => _startTime; set => Set(ref _startTime, value); }

            private long _endTime;
            public long EndTime { get => _endTime; set => Set(ref _endTime, value); }

            private long _eventLength;
            public long EventLength { get => _eventLength; set => Set(ref _eventLength, value); }

            private string _detectingType;
            public string DetectingType { get => _detectingType; set => Set(ref _detectingType, value); }

            private long _pri;
            public long PRI { get => _pri; set => Set(ref _pri, value); }

            private string _trmMode;
            public string TRMMode { get => _trmMode; set => Set(ref _trmMode, value); }

            private double _spl;
            public double SPL { get => _spl; set => Set(ref _spl, value); }

            private string _modulation;
            public string Modulation { get => _modulation; set => Set(ref _modulation, value); }

            private string _comment;
            public string Comment { get => _comment; set => Set(ref _comment, value); }

            private string _gramSnippetFileName;
            public string GramSnippetFileName { get => _gramSnippetFileName; set => Set(ref _gramSnippetFileName, value); }

            private byte[] _gramFile;
            public byte[] GramFile { get => _gramFile; set => Set(ref _gramFile, value); }

            private string _audioSnippetFileName;
            public string AudioSnippetFileName { get => _audioSnippetFileName; set => Set(ref _audioSnippetFileName, value); }

            private byte[] _audioSnippetFile;
            public byte[] AudioSnippetFile { get => _audioSnippetFile; set => Set(ref _audioSnippetFile, value); }

            private byte[] _sigFile;
            public byte[] SigFile { get => _sigFile; set => Set(ref _sigFile, value); }

            private DateTime _sigStartTime;
            public DateTime SigStartTime { get => _sigStartTime; set => Set(ref _sigStartTime, value); }

            private string _snippetComment;
            public string SnippetComment { get => _snippetComment; set => Set(ref _snippetComment, value); }

            public int SegmentCount { get; set; }
            public int AiSegmentCount { get; set; }
            public int TrSegmentCount { get; set; }
            public int UnkSegmentCount { get; set; }
            public bool ManualOrAuto { get; set; }
            public string DirPath { get; set; }

            /* AI 전용 */
            public long lPID { get; set; }
            public long lPulseLength { get; set; }
            public long lPri { get; set; }
            public string strTrmMode { get; set; }
            public double fSpl { get; set; }
            public string strPulseModulation { get; set; }
            public int nSegmentNum { get; set; }
            public string strSonarName { get; set; }
            public string strPulseName { get; set; }
            public double fScore { get; set; }
            public string strGramSnippetFileName { get; set; }
            public string strAudioSnippetFileName { get; set; }
            public string strDetectionSeaAreaLatitude { get; set; }
            public string strDetectionSeaAreaLongitude { get; set; }
            public double fDistance { get; set; }
            public int nGramSnippetNum { get; set; }
            public int nAudioSnippetNum { get; set; }
            public byte byteAppliedState { get; set; }
            public int lDBApplyID { get; set; }
            public string strDBAppliedDate { get; set; }
            public byte byteSnippetAppliedState { get; set; }

            public ObservableCollection<ShortPeriodSegment> Segments { get; set; } = new ObservableCollection<ShortPeriodSegment>();

            public event PropertyChangedEventHandler PropertyChanged;

            protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value)) return false;
                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

           public virtual void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public class ShortPeriodSegment : INotifyPropertyChanged
        {
            public int SegmentID { get; set; }
            public long StartTime { get; set; }
            public long EndTime { get; set; }
            public long Length { get; set; }
            public double LDAx { get; set; }
            public double LDAy { get; set; }
            public int DetectingType { get; set; }
            public int FrameCount { get; set; }
            public double StartFreq { get; set; }
            public double EndFreq { get; set; }
            public int Modulation { get; set; }
            public double CenterFreq { get; set; }
            public double BandWidth { get; set; }

            public string GramSnippetFileName { get; set; }
            public byte[] GramFile { get; set; }
            public string SnippetFileName { get; set; }
            public byte[] SnippetFile { get; set; }

            public long DBSegID { get; set; }
            public double PatternValue { get; set; }

            /* AI 전용 */
            public int lSID { get; set; }
            public double fStartFreq { get; set; }
            public double fEndFreq { get; set; }
            public long lSegmentLength { get; set; }
            public string strModulation { get; set; }
            public double fScore { get; set; }
            public byte byteAppliedState { get; set; }
            public int lDBApplyID { get; set; }

            /* TR 전용 */
            public string strGroup { get; set; }
            public string strTransientName { get; set; }
            public string strComments { get; set; }
            public int nAudioSnippetNum { get; set; }
            public string strDBAppliedDate { get; set; }

            public ObservableCollection<ShortPeriodFrame> Frames { get; set; } = new ObservableCollection<ShortPeriodFrame>();

            public event PropertyChangedEventHandler PropertyChanged;

            public void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public class ShortPeriodFrame : INotifyPropertyChanged
        {
            public long FrameID { get; set; }
            public double Frequency { get; set; }
            public List<double> MFBs { get; set; } = new List<double>();
            public float Score { get; set; }
            public byte AppliedState { get; set; }
            public int DBApplyID { get; set; }
            public long DBSegID { get; set; }
            public long DBFrmID { get; set; }
            public byte SnippetAppliedState { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            public void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
