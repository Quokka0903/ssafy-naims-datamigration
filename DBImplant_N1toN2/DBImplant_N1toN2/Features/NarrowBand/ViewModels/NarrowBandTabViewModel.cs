using DBImplant_N1toN2.Features.NarrowBand.Models;
using DBImplant_N1toN2.Features.NarrowBand.Parsers;
using DBImplant_N1toN2.Features.Supplement;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.NarrowBand.Models.NbAuxReaders;
using static DBImplant_N1toN2.Features.NarrowBand.Parsers.NbResultReader;
using static DBImplant_N1toN2.Features.Supplement.AlgReader;

namespace DBImplant_N1toN2.Features.NarrowBand.ViewModels
{
    public sealed partial class NarrowBandTabViewModel : TabViewModel, INotifyPropertyChanged
    {
        private string _rootPath;
        public string RootPath
        {
            get => _rootPath;
            set => Set(ref _rootPath, value);
        }

        public RelayCommand SelectRootPathCommand { get; }

        public ObservableCollection<NbPackageViewModel> Packages { get; } = new();

        private NbPackageViewModel? _selectedPackage;
        public NbPackageViewModel? SelectedPackage
        {
            get => _selectedPackage;
            set => Set(ref _selectedPackage, value);
        }

        private string _statusText = "대기 중";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!Set(ref _isRunning, value)) return;
                ScanParseCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                DbImportCommand.RaiseCanExecuteChanged();
                SelectRootPathCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand ScanParseCommand { get; }
        public RelayCommand DbImportCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<object> OpenGramCompareCommand { get; }

        private CancellationTokenSource? _cts;

        public NarrowBandTabViewModel(string header, string rootPath) : base(header)
        {
            _rootPath = rootPath;

            ScanParseCommand = new RelayCommand(async () => await ScanParseAllAsync(), () => !IsRunning && Packages.Count > 0);
            DbImportCommand = new RelayCommand(() => { /* TODO */ }, () => !IsRunning && Packages.Count > 0);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            SelectRootPathCommand = new RelayCommand(SelectRootPath, () => !IsRunning);

            OpenGramCompareCommand = new RelayCommand<object>(o =>
            {
                string? path = null;
                if (o != null)
                {
                    var prop = o.GetType().GetProperty("GrmPath") ?? o.GetType().GetProperty("FullPath");
                    path = prop?.GetValue(o) as string;
                }
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".grm", StringComparison.OrdinalIgnoreCase))
                {
                    var win = new Features.Migration.Views.GramCompareWindow(path);
                    win.Show();
                }
            });

            // 1) 초기: 목록만 스캔해서 1컬럼 전시
            ScanNbFolders();
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

    public sealed partial class NbPackageViewModel : INotifyPropertyChanged
    {
        public string NbDir { get; }
        public string PackageName => Path.GetFileName(NbDir);

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        private string _status = "미파싱";
        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        private string? _error;
        public string? Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        private string? _algPath;
        public string? AlgPath
        {
            get => _algPath;
            private set => Set(ref _algPath, value);
        }

        private string? _parsedAlgError;
        public string? ParsedAlgError
        {
            get => _parsedAlgError;
            private set => Set(ref _parsedAlgError, value);
        }

        private SupplementSheet? _parsedAlg;
        public SupplementSheet? ParsedAlg
        {
            get => _parsedAlg;
            private set => Set(ref _parsedAlg, value);
        }

        // ===== 2컬럼(루트 파일 목록) =====
        public ObservableCollection<FileItemViewModel> RootItems { get; } = new();

        // 2컬럼 2row: set 파싱 요약
        private string? _setPath;
        public string? SetPath { get => _setPath; private set => Set(ref _setPath, value); }

        private string? _setError;
        public string? SetError { get => _setError; private set => Set(ref _setError, value); }

        private NbParameterSet? _parsedSet;
        public NbParameterSet? ParsedSet { get => _parsedSet; private set => Set(ref _parsedSet, value); }

        // 루트 주요 파일들(원하면 컬럼에도 표시)
        public string? SignalWavPath { get; private set; }

        public int RootGrmCount { get; private set; }
        public int RootWavCount { get; private set; } // Signal.wav 포함 루트 wav 개수
        public bool HasPreciseDir { get; private set; }

        // ===== 3컬럼(정밀분석 폴더 내용 Top) =====
        public ObservableCollection<FileItemViewModel> PreciseItems { get; } = new();

        // ===== 4컬럼(파싱 결과) =====
        private NbFeatureInfo? _parsedFea;
        public NbFeatureInfo? ParsedFea { get => _parsedFea; private set => Set(ref _parsedFea, value); }

        private List<ScoringParam>? _parsedDat;
        public List<ScoringParam>? ParsedDat { get => _parsedDat; private set => Set(ref _parsedDat, value); }

        private NbClassificationResult? _parsedCla;
        public NbClassificationResult? ParsedCla { get => _parsedCla; private set => Set(ref _parsedCla, value); }

        public string? FeaPath { get; private set; }
        public string? DatPath { get; private set; }
        public string? ClaPath { get; private set; }

        // ===== 5컬럼(Snippets/SnpTmp) =====
        public ObservableCollection<SnippetPair> Snippets { get; } = new();
        public ObservableCollection<SnippetPair> TmpSnippets { get; } = new();

        public NbPackageViewModel(string nbDir)
        {
            NbDir = nbDir;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class FileItemViewModel
    {
        public string RelativePath { get; init; } = "";
        public long Length { get; init; }
        public DateTime LastWriteTime { get; init; }
        public string FullPath { get; init; } = "";
        public bool IsDirectory { get; init; }
    }

    internal sealed class NbPackageData
    {
        public List<FileItemViewModel> RootItems { get; init; } = new();
        public List<FileItemViewModel> PreciseItems { get; init; } = new();

        public string? SetPath { get; init; }
        public string? SetError { get; init; }
        public NbParameterSet? ParsedSet { get; init; }

        public string? SignalWavPath { get; init; }
        public int RootGrmCount { get; init; }
        public int RootWavCount { get; init; }
        public bool HasPreciseDir { get; init; }

        public string? AlgPath { get; init; }
        public string? AlgError { get; init; }
        public SupplementSheet? ParsedAlg { get; init; }
        public string? FeaPath { get; init; }
        public string? DatPath { get; init; }
        public string? ClaPath { get; init; }

        public NbFeatureInfo? ParsedFea { get; init; }
        public List<ScoringParam>? ParsedDat { get; init; }
        public NbClassificationResult? ParsedCla { get; init; }

        public List<SnippetPair> Snippets { get; init; } = new();
        public List<SnippetPair> TmpSnippets { get; init; } = new();

        public string? Error { get; init; }

        public static NbPackageData Load(string nbDir, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // ---- .alg 찾기(우선순위: 루트 -> 정밀분석) ----
                string? algPath = null;
                SupplementSheet? parsedAlg = null;
                string? algError = null;

                try
                {
                    // 1) 루트 top에서 *.alg
                    algPath = Directory.EnumerateFiles(nbDir, "*.alg", SearchOption.TopDirectoryOnly)
                                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                       .FirstOrDefault();

                    // 2) 없으면 정밀분석 top에서 *.alg
                    if (algPath == null)
                    {
                        var preciseDir2 = Path.Combine(nbDir, "정밀분석");
                        if (Directory.Exists(preciseDir2))
                        {
                            algPath = Directory.EnumerateFiles(preciseDir2, "*.alg", SearchOption.TopDirectoryOnly)
                                               .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                               .FirstOrDefault();
                        }
                    }

                    if (algPath != null)
                    {
                        parsedAlg = ReadSupplementAlg(algPath);
                    }
                }
                catch (Exception ex)
                {
                    algError = ex.Message;
                }

                // 1) 루트 파일 리스트 (2컬럼)
                var rootItems = new List<FileItemViewModel>();
                foreach (var f in Directory.EnumerateFileSystemEntries(nbDir, "*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();

                    var isDir = Directory.Exists(f);
                    if (isDir)
                    {
                        rootItems.Add(new FileItemViewModel
                        {
                            IsDirectory = true,
                            RelativePath = Path.GetFileName(f),
                            Length = 0,
                            LastWriteTime = Directory.GetLastWriteTime(f),
                            FullPath = f
                        });
                    }
                    else
                    {
                        var fi = new FileInfo(f);
                        rootItems.Add(new FileItemViewModel
                        {
                            IsDirectory = false,
                            RelativePath = Path.GetFileName(f),
                            Length = fi.Length,
                            LastWriteTime = fi.LastWriteTime,
                            FullPath = fi.FullName
                        });
                    }
                }

                // 루트 카운트
                var rootGrmCount = Directory.EnumerateFiles(nbDir, "*.grm", SearchOption.TopDirectoryOnly).Count();
                var rootWavCount = Directory.EnumerateFiles(nbDir, "*.wav", SearchOption.TopDirectoryOnly).Count();

                // 2) 기존 NbLoader로 로드+파싱 (요구: ScanParseCommand에서만)
                var loaded = NbLoader.Load(nbDir); // 내부에서 PreciseDir 있으면 ParseFiles까지 함

                ct.ThrowIfCancellationRequested();

                // 3) 정밀분석 폴더 Top (3컬럼)
                var preciseItems = new List<FileItemViewModel>();
                var preciseDir = Path.Combine(nbDir, "정밀분석");
                var hasPreciseDir = Directory.Exists(preciseDir);

                if (hasPreciseDir)
                {
                    foreach (var p in Directory.EnumerateFileSystemEntries(preciseDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        ct.ThrowIfCancellationRequested();

                        var isDir = Directory.Exists(p);
                        if (isDir)
                        {
                            preciseItems.Add(new FileItemViewModel
                            {
                                IsDirectory = true,
                                RelativePath = Path.GetFileName(p),
                                Length = 0,
                                LastWriteTime = Directory.GetLastWriteTime(p),
                                FullPath = p
                            });
                        }
                        else
                        {
                            var fi = new FileInfo(p);
                            preciseItems.Add(new FileItemViewModel
                            {
                                IsDirectory = false,
                                RelativePath = Path.GetFileName(p),
                                Length = fi.Length,
                                LastWriteTime = fi.LastWriteTime,
                                FullPath = fi.FullName
                            });
                        }
                    }
                }

                // Set 파싱 오류는 NbLoaded에 별도 필드가 아직 없어서 여기서 try/catch로 보강
                string? setError = null;
                NbParameterSet? parsedSet = null;
                if (loaded.SetPath != null)
                {
                    try
                    {
                        parsedSet = loaded.ParsedSet ?? NbAuxReaders.ReadNbParameterSet(loaded.SetPath);
                    }
                    catch (Exception ex)
                    {
                        setError = ex.Message;
                    }
                }

                // 로더가 ParseFiles로 채운 결과 사용
                return new NbPackageData
                {
                    RootItems = rootItems.OrderBy(x => x.IsDirectory ? 0 : 1).ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                    PreciseItems = preciseItems.OrderBy(x => x.IsDirectory ? 0 : 1).ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),

                    SetPath = loaded.SetPath,
                    SetError = setError,
                    ParsedSet = parsedSet,

                    SignalWavPath = loaded.SignalWavPath,
                    RootGrmCount = rootGrmCount,
                    RootWavCount = rootWavCount,
                    HasPreciseDir = hasPreciseDir,

                    FeaPath = loaded.FeaPath,
                    DatPath = loaded.DatPath,
                    ClaPath = loaded.ClaPath,

                    ParsedFea = loaded.ParsedFea,
                    ParsedDat = loaded.ParsedDat,
                    ParsedCla = loaded.ParsedCla,
                    ParsedAlg = parsedAlg,

                    Snippets = loaded.Snippets ?? new List<SnippetPair>(),
                    TmpSnippets = loaded.TmpSnippets ?? new List<SnippetPair>(),

                    Error = loaded.Report != null && !loaded.Report.Ok ? loaded.Report.BuildMessage() : null
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new NbPackageData { Error = ex.Message };
            }
        }
    }
}
