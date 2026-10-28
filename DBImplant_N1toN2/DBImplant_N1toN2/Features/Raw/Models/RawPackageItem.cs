using DBImplant_N1toN2.Features.Raw.Parsers;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DBImplant_N1toN2.Features.Raw.Models
{
    public enum RawPackageKind
    {
        CollectionFolder,    // 임무 폴더 전체 (→ RAW_COLLECT_SIG)
        SdPackage,
        NarrowBandGroup
    }

    public sealed class RawPackageItem : INotifyPropertyChanged
    {
        public RawPackageKind Kind { get; }
        public string CollectionDir { get; }
        public string PackageDir { get; }     // sdDir or nbDir
        public string DisplayName { get; }    // UI 표기용

        /// <summary>트리 구조: CollectionFolder 아래 SD/NB 자식 노드</summary>
        public ObservableCollection<RawPackageItem> Children { get; } = new();

        private ShortPeriodSdPackage? _sd;
        private ObservableCollection<NarrowBandGroup> _nbGroups;

        // Sd 속성 (0개 또는 1개)
        public ShortPeriodSdPackage? Sd
        {
            get => _sd;
            set
            {
                if (_sd != value)
                {
                    _sd = value;
                    OnPropertyChanged();
                }
            }
        }

        // NbGroup 속성 (0개 또는 N개)
        public ObservableCollection<NarrowBandGroup> NbGroups
        {
            get => _nbGroups;
            set
            {
                if (_nbGroups != value)
                {
                    _nbGroups = value;
                    OnPropertyChanged();
                }
            }
        }

        // 파일 메타데이터 목록
        public ObservableCollection<FileParseRow> Files { get; } = new();

        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        private string _status = "대기";
        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string _error = "";
        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        /// <summary>폴더 요약 행을 만들 때 쓰는 스캔 집계값(생성자 인자 보관).</summary>
        private readonly int _sdCount, _nbWavCount;

        // 생성자 (임무 폴더 — RAW_COLLECT_SIG 대응)
        // 폴더 탐색·재귀 파일 카운트 같은 IO 는 생성자에서 하지 않는다(UI 스레드 프리징 방지).
        // 실제 요약 행은 BuildFolderSummaryRows 가 백그라운드에서 만든다.
        public RawPackageItem(string collectionDir, int sdCount, int nbWavCount)
        {
            Kind = RawPackageKind.CollectionFolder;
            CollectionDir = collectionDir;
            PackageDir = collectionDir;
            _sdCount = sdCount;
            _nbWavCount = nbWavCount;
            var folderName = Path.GetFileName(collectionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DisplayName = $"📁 {folderName}";
            Status = "폴더";
        }

        /// <summary>
        /// 임무 폴더 요약 행(폴더구조 + 협대역 파일 + .sd 하위)을 만든다.
        /// 재귀 파일 카운트를 포함한 파일시스템 IO 라 반드시 백그라운드 스레드에서 호출한다.
        /// </summary>
        public void BuildFolderSummaryRows(List<FileParseRow> rows)
        {
            var folderName = Path.GetFileName(CollectionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            rows.Add(new FileParseRow("폴더구조", folderName, CollectionDir, 0,
                Directory.GetLastWriteTime(CollectionDir),
                $"→ RAW_COLLECT_SIG | SD패키지: {_sdCount}개, 협대역 WAV: {_nbWavCount}개",
                null));

            // 협대역 하위 폴더 탐색
            var nbDir = Path.Combine(CollectionDir, "협대역");
            if (Directory.Exists(nbDir))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(nbDir, "*.*", SearchOption.TopDirectoryOnly).OrderBy(x => x))
                    {
                        var fi = new FileInfo(f);
                        var ext = fi.Extension.ToUpperInvariant();
                        string target = ext switch
                        {
                            ".WAV" => "→ RAW_SCHANNEL_SS_EVENT",
                            ".GRM" when fi.Name.Contains("_Lofar") => "→ RAW_SCHANNEL_SS_SEGMENT.ANLAY_LOFAR_GRAM_FILE",
                            ".GRM" when fi.Name.Contains("_Demon") => "→ RAW_SCHANNEL_SS_SEGMENT.ANLAY_DEMON_GRAM_FILE",
                            ".GRM" when fi.Name.Contains("_Event") => "→ EXTRACT_DATA_STORE.DATA",
                            ".TXT" when fi.Name.Contains("_NBlist") => "→ 발췌구간 참조",
                            ".TXT" when fi.Name.Contains("_SPSlist") => "→ RAW_SCHANNEL_SS_EVENT (SPS)",
                            ".DAT" when fi.Name.Contains("_Detect") => "→ 탐지 임계값",
                            ".TXT" when fi.Name.Contains("Drawing") => "→ 커서 마크 참조",
                            _ => ""
                        };
                        rows.Add(new FileParseRow($"협대역/{ext}", fi.Name, fi.FullName,
                            fi.Length, fi.LastWriteTime, target, null));
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(new FileParseRow("협대역", "(탐색 실패)", nbDir, 0,
                        DateTime.MinValue, "", ex.Message));
                }
            }

            // .sd 하위 폴더 탐색
            try
            {
                foreach (var d in Directory.GetDirectories(CollectionDir, "*.sd"))
                {
                    var sdName = Path.GetFileName(d);
                    var fileCount = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length;
                    rows.Add(new FileParseRow("SD폴더", sdName, d, 0,
                        Directory.GetLastWriteTime(d),
                        $"→ 단주기 파싱 (파일 {fileCount}개)", null));
                }
            }
            catch (Exception ex)
            {
                rows.Add(new FileParseRow("SD폴더", "(탐색 실패)", CollectionDir, 0,
                    DateTime.MinValue, "", ex.Message));
            }
        }

        // 생성자 (SD Package)
        public RawPackageItem(string collectionDir, ShortPeriodSdPackage sd)
        {
            Kind = RawPackageKind.SdPackage;
            CollectionDir = collectionDir;
            PackageDir = sd.DirectoryPath;
            Sd = sd;

            // SPSlist.txt 파일 경로만 담기 (경로가 있을 때만)
            if (!string.IsNullOrEmpty(sd.SpsListPath))
            {
                Files.Add(new FileParseRow(
                    Kind: "SPSlist.txt",
                    FileName: "SPSlist.txt",
                    FullPath: sd.SpsListPath,
                    Size: File.Exists(sd.SpsListPath) ? new FileInfo(sd.SpsListPath).Length : 0L,
                    LastWriteTime: File.GetLastWriteTime(sd.SpsListPath),
                    Summary: "SPSlist 파일",
                    Error: ""
                ));
            }

            var sdName = Path.GetFileName(sd.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DisplayName = $"[SD] {sdName}";
        }

        // 생성자 (NarrowBandGroup)
        public RawPackageItem(string collectionDir, string nbDir, ObservableCollection<NarrowBandGroup> groups)
        {
            Kind = RawPackageKind.NarrowBandGroup;
            CollectionDir = collectionDir;
            PackageDir = nbDir;
            NbGroups = groups;

            DisplayName = $"[NB] {groups.First().BaseName}";

            // 각 그룹의 .grm, .txt 메타데이터 파일 경로 추가
            foreach (var nbGroup in NbGroups)
            {
                AddFile(nbGroup.DemonGrmPath, "Demon.grm");
                AddFile(nbGroup.DemonDrawingListPath, "Demondrawinglist.txt");
                AddFile(nbGroup.DetectDatPath, "Detect.dat");
                AddFile(nbGroup.EventGrmPath, "Event.grm");
                AddFile(nbGroup.LofarGrmPath, "Lofar.grm");
                AddFile(nbGroup.LofarDrawingListPath, "LofarDrawinglist.txt");
                AddFile(nbGroup.NbListPath, "NBlist.txt");
                AddFile(nbGroup.SpsListPath, "SPSlist.txt");
            }
        }

        private void AddFile(string? path, string fileType)
        {
            if (!string.IsNullOrEmpty(path))
            {
                Files.Add(new FileParseRow(
                    Kind: fileType,
                    FileName: Path.GetFileName(path),
                    FullPath: path,
                    Size: new FileInfo(path).Length,
                    LastWriteTime: File.GetLastWriteTime(path),
                    Summary: $"파일: {fileType}",
                    Error: ""
                ));
            }
        }

        // PropertyChanged 이벤트 처리
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
