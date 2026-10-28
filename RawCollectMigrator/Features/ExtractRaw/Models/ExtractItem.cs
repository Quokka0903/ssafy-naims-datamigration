using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.ExtractRaw
{
    /// <summary>
    /// 발췌 원음 자료 스캔 결과 한 건.
    ///
    /// 마이그레이션 범위 (사용자 확정):
    ///   ● 협대역: Signal.wav (원음 1개) + 보조정보.alg
    ///   ● 단주기: Signal_*.wav (원음 N개) + SPSlist.txt + 이벤트리스트.lst + 보조정보.alg
    /// 신호처리 결과(.fea, .grm, .set 등)는 이관 대상 아님.
    /// </summary>
    public sealed class ExtractItem : INotifyPropertyChanged
    {
        /// <summary>자료 폴더 절대 경로(협대역 항목 폴더 또는 단주기 *.sd 폴더).</summary>
        public string ItemPath { get; init; } = "";
        /// <summary>목록 표시용 폴더명.</summary>
        public string ItemName { get; init; } = "";
        /// <summary>자료 종류(협대역/단주기). 완비 판정·표시 로직의 분기 기준.</summary>
        public ExtractKind Kind { get; init; }

        /// <summary>이관 대상 WAV(원음) 개수. 협대역 1, 단주기 N.</summary>
        public int WavCount { get; init; }
        /// <summary>WAV 파일 총 바이트. SizeText 표시용.</summary>
        public long WavTotalBytes { get; init; }
        /// <summary>이관 대상 WAV 절대 경로 목록.</summary>
        public string[] WavPaths { get; init; } = System.Array.Empty<string>();

        /// <summary>보조정보.alg 존재 여부.</summary>
        public bool HasAlg { get; init; }
        /// <summary>보조정보.alg 절대 경로(없으면 null).</summary>
        public string? AlgPath { get; init; }

        /// <summary>자료관리파일.DMF 존재 여부. 메타데이터라 없어도 이관은 진행된다.</summary>
        public bool HasDmf { get; init; }
        /// <summary>자료관리파일.DMF 절대 경로(없으면 null). 항목 폴더 → 상위 연동ID 폴더 순으로 찾는다.</summary>
        public string? DmfPath { get; init; }
        /// <summary>스캔 시 파싱해 둔 DMF. 없으면 null.</summary>
        public Infrastructure.DmfInfo? Dmf { get; init; }

        /// <summary>SPSlist.txt 존재 여부(단주기 필수).</summary>
        public bool HasSpsList { get; init; }
        /// <summary>SPSlist.txt 절대 경로(없으면 null).</summary>
        public string? SpsListPath { get; init; }
        /// <summary>이벤트리스트.lst 존재 여부(단주기 필수).</summary>
        public bool HasEventList { get; init; }
        /// <summary>이벤트리스트.lst 절대 경로(없으면 null).</summary>
        public string? EventListPath { get; init; }

        // 단주기 인덱스 파싱 결과 (스캔 시 산정)
        public int SpsEventCount { get; init; }         // SPSlist.txt 이벤트 수(이관과 동일한 바이너리 파서로 산정)
        public int LstEventCount { get; init; }         // 이벤트리스트.lst 이벤트 헤더 라인
        public int LstSegmentCount { get; init; }       // 이벤트리스트.lst 세그먼트 라인

        /// <summary>자료 폴더 최종 수정 시각.</summary>
        public DateTime LastWriteTime { get; init; }

        /// <summary>Kind의 한글 표시 문자열(협대역/단주기).</summary>
        public string KindText => Kind switch
        {
            ExtractKind.NarrowBand  => "협대역",
            ExtractKind.ShortPeriod => "단주기",
            _ => ""
        };

        /// <summary>
        /// 이관 가능(필수 파일 완비) 여부. 협대역은 WAV만, 단주기는 WAV+SPSlist+이벤트리스트 모두 필요.
        /// IsSelected 체크 가능 여부의 전제 조건.
        /// </summary>
        /// <remarks>
        /// [NAIMS-ISSUE:SPSLIST-PRIORITY] 단주기 인덱스는 SPSlist.txt 와 이벤트리스트.lst 중
        /// <b>하나만</b> 있으면 이관 가능하다. 이관기가 SPSlist 를 우선 쓰고 없거나 못 읽을 때만
        /// .lst 로 내려가기 때문이다(ExtractMigrationExecutor). 예전처럼 둘 다 요구하면
        /// SPSlist 만 있는 폴더(= 정밀분석을 거치지 않아 .lst 가 생기지 않은 자료.
        /// 실측: NAIMS1 실데이터 ISUS90_20191126151223_00_000_001.sd)가 통째로 이관 대상에서 빠진다.
        /// </remarks>
        public bool IsReady => Kind switch
        {
            ExtractKind.NarrowBand  => WavCount >= 1,
            ExtractKind.ShortPeriod => WavCount >= 1 && (HasSpsList || HasEventList),
            _ => false
        };

        /// <summary>완비 여부 및 누락 파일 목록을 요약한 표시 문자열.</summary>
        public string StatusText
        {
            get
            {
                var miss = new List<string>();
                if (WavCount == 0) miss.Add("WAV");
                // 인덱스는 둘 중 하나만 있으면 되므로(IsReady 참조) 둘 다 없을 때만 누락으로 본다.
                if (Kind == ExtractKind.ShortPeriod && !HasSpsList && !HasEventList)
                    miss.Add("SPSlist.txt/이벤트리스트.lst");
                if (!HasAlg) miss.Add("보조정보.alg");
                // 자료관리파일.DMF 는 있으면 필드를 채우고 없으면 넘어가는 메타데이터라
                // 누락 목록에 넣지 않는다(사용자 확정 2026-08-19).
                if (miss.Count > 0) return "✗ " + string.Join("/", miss) + " 누락";
                // 완비지만 인덱스가 한쪽만 있는 경우는 어느 쪽으로 이관되는지 보이게 한다.
                if (Kind == ExtractKind.ShortPeriod && !HasEventList) return "✓ 완비 (SPSlist만)";
                if (Kind == ExtractKind.ShortPeriod && !HasSpsList)   return "✓ 완비 (이벤트리스트만)";
                return "✓ 완비";
            }
        }

        /// <summary>WAV 총 용량을 사람이 읽는 단위(B/KB/MB/GB)로 표시.</summary>
        public string SizeText     => FormatBytes(WavTotalBytes);
        /// <summary>WAV 개수 표시(0이면 "-").</summary>
        public string WavCountText => WavCount == 0 ? "-" : WavCount.ToString();
        /// <summary>보조정보.alg 유무 표시.</summary>
        public string AlgText      => HasAlg ? "✓" : "✗";
        /// <summary>자료관리파일.DMF 유무 + 수집체계 표시. 없으면 "-"(누락이 아니라 선택 입력).</summary>
        public string DmfText      => !HasDmf ? "-"
            : (string.IsNullOrEmpty(Dmf?.RecordingSystem) ? "✓" : $"✓ ({Dmf!.RecordingSystem})");
        /// <summary>SPSlist.txt 유무 + 이벤트 수 표시(협대역은 "-").</summary>
        public string SpsText      => Kind == ExtractKind.ShortPeriod
            ? (HasSpsList   ? $"✓ ({SpsEventCount})"  : "✗") : "-";
        /// <summary>이벤트리스트.lst 유무 + 이벤트 수 표시(협대역은 "-").</summary>
        public string EvtText      => Kind == ExtractKind.ShortPeriod
            ? (HasEventList ? $"✓ ({LstEventCount})"  : "✗") : "-";
        /// <summary>이벤트리스트.lst 세그먼트 수 표시(협대역은 "-").</summary>
        public string SegText      => Kind == ExtractKind.ShortPeriod
            ? (HasEventList ? LstSegmentCount.ToString() : "-") : "-";

        // ── 이관 대상 선택 ──
        private bool _isSelected;
        /// <summary>
        /// 이관 대상 체크 상태. IsReady가 아니면 항상 false로 강제(미완비 항목은 선택 불가).
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected && IsReady;
            set { if (!IsReady) value = false; Set(ref _isSelected, value); }
        }

        // ── 이관 결과 ──
        private string _migStatus = "대기";
        /// <summary>이관 진행 상태("대기"/진행/완료 등). 변경 시 MigResultText 갱신 통지.</summary>
        public string MigStatus
        {
            get => _migStatus;
            set { if (Set(ref _migStatus, value)) OnChanged(nameof(MigResultText)); }
        }

        private string _migError = "";
        /// <summary>이관 실패 메시지. 비어있지 않으면 MigResultText가 이 값을 우선 표시.</summary>
        public string MigError
        {
            get => _migError;
            set { if (Set(ref _migError, value)) OnChanged(nameof(MigResultText)); }
        }

        /// <summary>이관으로 신규 발번된 신호 ID(DB SIG_ID). 미이관 시 null.</summary>
        public int? MigSigId { get; set; }
        /// <summary>이관된 보조정보(SUPPLEMENT) 건수.</summary>
        public int MigSupplementCount { get; set; }
        /// <summary>이관된 데이터 저장(DATA_STORE) 건수.</summary>
        public int MigDataStoreCount { get; set; }
        /// <summary>이관된 상세(이벤트/세그먼트 등) 건수.</summary>
        public int MigDetailCount { get; set; }

        /// <summary>
        /// 단주기 이벤트를 실제로 어느 파일에서 읽었는지(+SPSlist 레이아웃). 이관 로그에 그대로 남는다.
        /// 예: "SPSlist.txt[AAS packed/A_detect+counts+dir255] 이벤트 34".
        /// SPSlist 우선 규칙이 실제로 적용됐는지, .lst 로 내려갔다면 왜인지를 사후에 확인하기 위한 값이다.
        /// </summary>
        public string MigEventSource { get; set; } = "";

        /// <summary>
        /// PREC_SRC_SIG 메타 필드(RECORDING_*)를 어느 파일에서 채웠는지. 이관 로그에 그대로 남는다.
        /// DMF 가 있는데 값이 안 들어간 경우를 사후에 잡아내기 위한 것이다.
        /// </summary>
        public string MigMetaSource { get; set; } = "";

        /// <summary>이관 결과 요약 문자열. 오류 우선, "대기" 상태는 공백, 완료 시 발번 ID·건수 표기.</summary>
        public string MigResultText
        {
            get
            {
                if (!string.IsNullOrEmpty(MigError)) return MigError;
                if (MigStatus == "대기") return "";
                return $"SIG_ID={MigSigId}, SUPP={MigSupplementCount}, STORE={MigDataStoreCount}, 상세={MigDetailCount}";
            }
        }

        // ── 확장 상세 (더블클릭 시 로드) ──
        private bool _isExpanded;
        /// <summary>상세 행 확장 여부(더블클릭 토글).</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }
        /// <summary>상세 정보 지연 로드 완료 여부(중복 로드 방지 플래그).</summary>
        public bool DetailLoaded { get; set; }

        private AlgInfo? _parsedAlg;
        /// <summary>보조정보.alg 요약 파싱 결과. 변경 시 AlgSummary 갱신 통지.</summary>
        public AlgInfo? ParsedAlg
        {
            get => _parsedAlg;
            set
            {
                if (Set(ref _parsedAlg, value))
                    OnChanged(nameof(AlgSummary));
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
        /// <summary>상세 파싱 진행 중 여부. 변경 시 AlgSummary("파싱 중...") 갱신 통지.</summary>
        public bool DetailLoading
        {
            get => _detailLoading;
            set
            {
                if (Set(ref _detailLoading, value))
                    OnChanged(nameof(AlgSummary));
            }
        }

        /// <summary>보조정보.alg 주요 필드(진수체계/계급/함정, 임무 정보 등)를 여러 줄로 요약. 파싱 상태에 따라 안내 문구 표시.</summary>
        public string AlgSummary
        {
            get
            {
                if (DetailLoading && ParsedAlg == null) return "(파싱 중...)";
                if (ParsedAlg == null)
                    return HasAlg ? "(alg 미파싱)" : "(보조정보.alg 없음)";
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

        // alg 전문 (EUC-KR 디코드)
        private string _algFullText = "";
        public string AlgFullText { get => _algFullText; set => Set(ref _algFullText, value); }

        // SPS/LST 파싱 결과
        /// <summary>SPSlist.txt에서 파싱한 이벤트 목록(단주기).</summary>
        public System.Collections.ObjectModel.ObservableCollection<SdEvent> SpsEvents { get; } = new();
        /// <summary>이벤트리스트.lst에서 파싱한 이벤트 목록(단주기).</summary>
        public System.Collections.ObjectModel.ObservableCollection<SdEvent> LstEvents { get; } = new();

        private LstHeader? _lstHeader;
        /// <summary>이벤트리스트.lst 헤더 파싱 결과. 변경 시 LstHeaderSummary 갱신 통지.</summary>
        public LstHeader? LstHeader
        {
            get => _lstHeader;
            set { if (Set(ref _lstHeader, value)) OnChanged(nameof(LstHeaderSummary)); }
        }
        /// <summary>LST 헤더 요약 문자열(파싱 전이면 안내 문구).</summary>
        public string LstHeaderSummary => _lstHeader?.Summary ?? "(파싱 전)";

        private string _lstSummary = "";
        public string LstSummary { get => _lstSummary; set => Set(ref _lstSummary, value); }
        private string _spsSummary = "";
        public string SpsSummary { get => _spsSummary; set => Set(ref _spsSummary, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>백킹 필드 값이 실제로 바뀌었을 때만 대입 후 변경 통지하고 true 반환(불필요한 통지 억제).</summary>
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnChanged(name);
            return true;
        }
        /// <summary>PropertyChanged 이벤트 발생(계산 프로퍼티 수동 통지에도 사용).</summary>
        private void OnChanged(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>바이트를 B/KB/MB/GB 단위 문자열로 변환.</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
            return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        }
    }

    /// <summary>발췌 원음 자료 종류.</summary>
    public enum ExtractKind
    {
        /// <summary>협대역: Signal.wav 1개 + 보조정보.alg.</summary>
        NarrowBand,
        /// <summary>단주기: Signal_*.wav N개 + SPSlist.txt + 이벤트리스트.lst + 보조정보.alg.</summary>
        ShortPeriod
    }

    /// <summary>디렉터리를 훑어 협대역/단주기 발췌 항목 목록을 산출하는 스캐너.</summary>
    public static class ExtractScanner
    {
        /// <summary>rootPath 바로 아래 각 하위 폴더를 협대역 항목으로 스캔해 이름순 정렬 반환.</summary>
        public static List<ExtractItem> ScanNarrowBand(string rootPath)
        {
            var list = new List<ExtractItem>();
            if (!Directory.Exists(rootPath)) return list;

            foreach (var dir in Directory.EnumerateDirectories(rootPath))
            {
                try { list.Add(ScanOneNb(dir)); }
                catch { }
            }
            return list.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>rootPath 하위 전체에서 *.sd 폴더를 찾아 단주기 항목으로 스캔해 이름순 정렬 반환.</summary>
        public static List<ExtractItem> ScanShortPeriod(string rootPath)
        {
            var list = new List<ExtractItem>();
            if (!Directory.Exists(rootPath)) return list;

            IEnumerable<string> sdDirs;
            try { sdDirs = Directory.EnumerateDirectories(rootPath, "*.sd", SearchOption.AllDirectories); }
            catch { return list; }

            foreach (var dir in sdDirs)
            {
                try { list.Add(ScanOneSd(dir)); }
                catch { }
            }
            return list.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>협대역 폴더 하나를 스캔해 ExtractItem 생성(WAV·alg 탐지, 완비 시 기본 선택).</summary>
        private static ExtractItem ScanOneNb(string dir)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var wavs = FindNarrowBandWavs(dir);
            long wavBytes = 0;
            foreach (var w in wavs) { try { wavBytes += new FileInfo(w).Length; } catch { } }

            var (algPath, hasAlg) = FindFirst(dir, "보조정보.alg");
            var dmfPath = Infrastructure.DmfParser.FindForItem(dir);
            var dmf = Infrastructure.DmfParser.TryParse(dmfPath);
            DateTime lw = TryGetLw(dir);

            var row = new ExtractItem
            {
                ItemPath = dir,
                ItemName = name,
                Kind = ExtractKind.NarrowBand,
                WavPaths = wavs,
                WavCount = wavs.Length,
                WavTotalBytes = wavBytes,
                HasAlg = hasAlg,
                AlgPath = algPath,
                HasDmf = dmf != null,
                DmfPath = dmfPath,
                Dmf = dmf,
                LastWriteTime = lw,
            };
            row.IsSelected = row.IsReady;   // 이관 가능한 항목만 기본 체크
            return row;
        }

        /// <summary>단주기 *.sd 폴더 하나를 스캔해 ExtractItem 생성(WAV·SPS·LST·alg 탐지 및 인덱스 건수 산정).</summary>
        private static ExtractItem ScanOneSd(string dir)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string[] wavs;
            try
            {
                wavs = Directory.EnumerateFiles(dir, "Signal_*.wav", SearchOption.AllDirectories)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                .ToArray();
            }
            catch { wavs = Array.Empty<string>(); }

            long wavBytes = 0;
            foreach (var w in wavs) { try { wavBytes += new FileInfo(w).Length; } catch { } }

            var (sps, hasSps) = FindFirst(dir, "SPSlist.txt");
            var (evt, hasEvt) = FindFirst(dir, "이벤트리스트.lst");
            var (alg, hasAlg) = FindFirst(dir, "보조정보.alg");
            var dmfPath = Infrastructure.DmfParser.FindForItem(dir);
            var dmf = Infrastructure.DmfParser.TryParse(dmfPath);

            var stats = SdIndexParser.Count(sps, evt);

            DateTime lw = TryGetLw(dir);

            var row = new ExtractItem
            {
                ItemPath = dir,
                ItemName = name,
                Kind = ExtractKind.ShortPeriod,
                WavPaths = wavs,
                WavCount = wavs.Length,
                WavTotalBytes = wavBytes,
                HasSpsList = hasSps,
                SpsListPath = sps,
                HasEventList = hasEvt,
                EventListPath = evt,
                HasAlg = hasAlg,
                AlgPath = alg,
                HasDmf = dmf != null,
                DmfPath = dmfPath,
                Dmf = dmf,
                SpsEventCount = stats.SpsEventCount,
                LstEventCount = stats.LstEventLineCount,
                LstSegmentCount = stats.LstSegmentLineCount,
                LastWriteTime = lw,
            };
            row.IsSelected = row.IsReady;
            return row;
        }

        /// <summary>협대역 원음 WAV 1개를 탐색(Signal.wav 우선 → 최상위 임의 wav → 하위 임의 wav 순).</summary>
        private static string[] FindNarrowBandWavs(string dir)
        {
            try
            {
                var top = Directory.EnumerateFiles(dir, "Signal.wav", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (top != null) return new[] { top };
                var anyTop = Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (anyTop != null) return new[] { anyTop };
                var anyDeep = Directory.EnumerateFiles(dir, "*.wav", SearchOption.AllDirectories).FirstOrDefault();
                return anyDeep != null ? new[] { anyDeep } : Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>지정 파일명을 최상위 우선, 없으면 하위 전체에서 탐색해 (경로, 존재여부) 반환.</summary>
        private static (string? path, bool exists) FindFirst(string dir, string fileName)
        {
            try
            {
                var top = Path.Combine(dir, fileName);
                if (File.Exists(top)) return (top, true);
                var deep = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                return deep != null ? (deep, true) : (null, false);
            }
            catch { return (null, false); }
        }

        /// <summary>폴더 최종 수정 시각 조회(실패 시 DateTime.MinValue).</summary>
        private static DateTime TryGetLw(string dir)
        {
            try { return Directory.GetLastWriteTime(dir); }
            catch { return DateTime.MinValue; }
        }
    }
}
