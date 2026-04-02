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
        SdPackage,
        NarrowBandGroup
    }

    public sealed class RawPackageItem : INotifyPropertyChanged
    {
        public RawPackageKind Kind { get; }
        public string CollectionDir { get; }
        public string PackageDir { get; }     // sdDir or nbDir
        public string DisplayName { get; }    // UI 표기용

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
