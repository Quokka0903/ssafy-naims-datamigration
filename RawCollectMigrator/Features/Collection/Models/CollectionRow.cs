using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.Collection
{
    /// <summary>
    /// 수집원음자료 임무폴더 한 건. RAW_COLLECT_SIG 1건 + 보조정보.alg(SUPPLEMENT) + Signal_*.wav N건(SCHANNEL_INFO)
    /// </summary>
    public sealed class CollectionRow : INotifyPropertyChanged
    {
        public string MissionFolderPath { get; init; } = "";
        public string MissionName { get; init; } = "";
        public bool HasSupplementAlg { get; init; }
        public string? SupplementAlgPath { get; init; }
        public int WavCount { get; init; }
        public string[] WavFiles { get; init; } = System.Array.Empty<string>();
        public bool HasNbFolder { get; init; }
        public long TotalSizeBytes { get; init; }
        public DateTime LastWriteTime { get; init; }

        // WAV(=RAW_SCHANNEL_INFO 삽입 대상)이 하나도 없으면 마이그레이션 대상 자체가 성립하지 않음.
        public bool CanBeSelected => WavCount > 0;

        // 사용자 선택 — 마이그레이션 대상 여부. WAV가 없는 행은 강제 false.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected && CanBeSelected;
            set
            {
                if (!CanBeSelected) value = false;
                Set(ref _isSelected, value);
            }
        }

        // 상세 확장 상태 (DataGridRow.DetailsVisibility 바인딩 대상)
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        // 상세 로드 여부 (최초 확장 시 1회 로드)
        public bool DetailLoaded { get; set; }

        private AlgInfo? _parsedAlg;
        public AlgInfo? ParsedAlg
        {
            get => _parsedAlg;
            set
            {
                if (Set(ref _parsedAlg, value))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlgSummary)));
            }
        }

        /// <summary>보조정보.alg 전체 파싱 결과. SupplementDetailTemplate 바인딩 대상.</summary>
        private Features.Supplement.SupplementView? _supplement;
        public Features.Supplement.SupplementView? Supplement
        {
            get => _supplement;
            set => Set(ref _supplement, value);
        }

        private bool _detailLoading;
        public bool DetailLoading
        {
            get => _detailLoading;
            set
            {
                if (Set(ref _detailLoading, value))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlgSummary)));
            }
        }

        public ObservableCollection<WavDetailRow> WavDetails { get; } = new();

        public string AlgSummary
        {
            get
            {
                if (DetailLoading && ParsedAlg == null) return "(파싱 중...)";
                if (ParsedAlg == null)
                    return HasSupplementAlg ? "(alg 미파싱 — 확장 시 파싱)" : "(보조정보.alg 없음)";
                var a = ParsedAlg;
                return
                    $"진수체계   : {a.DetPlatform}\n" +
                    $"진수계급   : {a.DetVesselClass}\n" +
                    $"  └ RECORDING_SYSTEM = {a.RecordingSystemMapped}\n" +
                    $"진수함정   : {a.DetVesselName}\n" +
                    $"임무기간   : {a.MissionDateFrom} ~ {a.MissionDateTo}\n" +
                    $"임무해역   : {a.MissionSeaArea}\n" +
                    $"임무요약   : {a.MissionSummary}\n" +
                    $"임무코멘트 : {a.MissionComment}";
            }
        }

        // 마이그레이션 결과
        private string _status = "대기";
        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _errorMessage = "";
        public string ErrorMessage
        {
            get => _errorMessage;
            set => Set(ref _errorMessage, value);
        }

        private int? _insertedCollectSigId;
        public int? InsertedCollectSigId
        {
            get => _insertedCollectSigId;
            set => Set(ref _insertedCollectSigId, value);
        }

        private int _insertedSupplementCount;
        public int InsertedSupplementCount
        {
            get => _insertedSupplementCount;
            set => Set(ref _insertedSupplementCount, value);
        }

        private int _insertedSchannelCount;
        public int InsertedSchannelCount
        {
            get => _insertedSchannelCount;
            set => Set(ref _insertedSchannelCount, value);
        }

        public string SizeText => FormatBytes(TotalSizeBytes);

        public string SupplementText => HasSupplementAlg ? "✓ 있음" : "✗ 없음";

        public string ResultSummary
        {
            get
            {
                if (Status == "대기") return "";
                if (!string.IsNullOrEmpty(ErrorMessage)) return ErrorMessage;
                return $"COLLECT_SIG_ID={InsertedCollectSigId}, SUPP={InsertedSupplementCount}, SCH={InsertedSchannelCount}";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Status) || name == nameof(ErrorMessage)
                || name == nameof(InsertedCollectSigId) || name == nameof(InsertedSupplementCount)
                || name == nameof(InsertedSchannelCount))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultSummary)));
            }
            return true;
        }
    }

    /// <summary>
    /// 확장 상세영역에 뿌리는 WAV 파일 한 건.
    /// </summary>
    public sealed class WavDetailRow
    {
        public string FileName { get; init; } = "";
        public string FullPath { get; init; } = "";
        public long SizeBytes { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int BitsPerSample { get; init; }
        public float AudioLengthSec { get; init; }

        public string SizeText => FormatBytes(SizeBytes);
        public string HeaderText => SampleRate > 0
            ? $"{SampleRate:N0} Hz / {Channels}ch / {BitsPerSample}bit"
            : "(헤더 읽기 실패)";
        public string AudioLengthText
        {
            get
            {
                if (AudioLengthSec <= 0) return "-";
                if (AudioLengthSec < 60) return $"{AudioLengthSec:F1} s";
                int m = (int)(AudioLengthSec / 60);
                float s = AudioLengthSec - m * 60;
                return $"{m}m {s:F1}s";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }
    }
}
