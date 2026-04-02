using DBImplant_N1toN2.Features.ShortPeriod.Models;
using DBImplant_N1toN2.Features.ShortPeriod.Parsers;
using DBImplant_N1toN2.Infrastructure;
using DBImplant_N1toN2.Infrastructure.Commands;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static DBImplant_N1toN2.Features.ShortPeriod.Parsers.SdLoader;

namespace DBImplant_N1toN2.Features.ShortPeriod.ViewModels
{
    public sealed partial class ShortPeriodTabViewModel : TabViewModel, INotifyPropertyChanged
    {
        private string _rootPath;
        public string RootPath
        {
            get => _rootPath;
            private set => Set(ref _rootPath, value);
        }

        public RelayCommand SelectRootPathCommand { get; }

        public ObservableCollection<SdPackageItem> Packages { get; } = new();

        private SdPackageItem? _selectedPackage;
        public SdPackageItem? SelectedPackage
        {
            get => _selectedPackage;
            set
            {
                if (!Set(ref _selectedPackage, value)) return;
                LoadSelectedToView();
            }
        }

        // ====== 선택 패키지 전시용 컬렉션(“모두 담아서 전시”) ======
        public ObservableCollection<FileRow> ViewRootSignalWavs { get; } = new();

        public ObservableCollection<ShortPeriodEvent> ViewSpsEvents { get; } = new();
        public ObservableCollection<ShortPeriodSegment> ViewSpsSegments { get; } = new();
        private ShortPeriodEvent? _selectedSpsEvent;
        public ShortPeriodEvent? SelectedSpsEvent
        {
            get => _selectedSpsEvent;
            set { if (Set(ref _selectedSpsEvent, value)) RebuildSegments(ViewSpsSegments, _selectedSpsEvent); }
        }

        public ObservableCollection<ShortPeriodEvent> ViewLstEvents { get; } = new();
        public ObservableCollection<ShortPeriodSegment> ViewLstSegments { get; } = new();
        private ShortPeriodEvent? _selectedLstEvent;
        public ShortPeriodEvent? SelectedLstEvent
        {
            get => _selectedLstEvent;
            set { if (Set(ref _selectedLstEvent, value)) RebuildSegments(ViewLstSegments, _selectedLstEvent); }
        }

        public bool ViewSetExists { get; private set; }
        public string ViewSetPath { get; private set; } = "";
        public double ViewBw { get; private set; }
        public double ViewDt { get; private set; }
        public string ViewSetError { get; private set; } = "";

        // AI
        public ObservableCollection<ShortPeriodEvent> ViewAiEvents { get; } = new();
        public ObservableCollection<ShortPeriodSegment> ViewAiSegments { get; } = new();
        private ShortPeriodEvent? _selectedAiEvent;
        public ShortPeriodEvent? SelectedAiEvent
        {
            get => _selectedAiEvent;
            set { if (Set(ref _selectedAiEvent, value)) RebuildSegments(ViewAiSegments, _selectedAiEvent); }
        }

        public ObservableCollection<FileRow> ViewAiSnippetWavs { get; } = new();
        public ObservableCollection<FileRow> ViewAiSnippetGrms { get; } = new();
        public ObservableCollection<AiClaPulseRow> ViewAiClaPulses { get; } = new();
        public ObservableCollection<AiTmpEventRow> ViewAiTmpEvents { get; } = new();
        public ObservableCollection<AiTmpCandidateRow> ViewAiTmpCandidates { get; } = new();
        public ObservableCollection<AiTmpSegmentRow> ViewAiTmpSegments { get; } = new();
        public string ViewAiErrors { get; private set; } = "";

        // TR
        public ObservableCollection<ShortPeriodEvent> ViewTrEvents { get; } = new();
        public ObservableCollection<ShortPeriodSegment> ViewTrSegments { get; } = new();
        public ObservableCollection<ShortPeriodFrame> ViewTrFrames { get; } = new();

        private ShortPeriodEvent? _selectedTrEvent;
        public ShortPeriodEvent? SelectedTrEvent
        {
            get => _selectedTrEvent;
            set
            {
                if (!Set(ref _selectedTrEvent, value)) return;
                RebuildSegments(ViewTrSegments, _selectedTrEvent);
                ViewTrFrames.Clear();
            }
        }

        private ShortPeriodSegment? _selectedTrSegment;
        public ShortPeriodSegment? SelectedTrSegment
        {
            get => _selectedTrSegment;
            set
            {
                if (!Set(ref _selectedTrSegment, value)) return;
                ViewTrFrames.Clear();
                if (_selectedTrSegment?.Frames != null)
                {
                    foreach (var fr in _selectedTrSegment.Frames)
                        ViewTrFrames.Add(fr);
                }
            }
        }

        public ObservableCollection<FileRow> ViewTrSnippetWavs { get; } = new();
        public ObservableCollection<FileRow> ViewTrSnippetGrms { get; } = new();
        public ObservableCollection<TrClaSegmentRow> ViewTrClaSegments { get; } = new();
        public ObservableCollection<TrTmpEventRow> ViewTrTmpEvents { get; } = new();
        public ObservableCollection<TrTmpSegmentRow> ViewTrTmpSegments { get; } = new();
        public string ViewTrErrors { get; private set; } = "";

        // ====== 상태/진행 ======
        private string _statusText = "대기";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private double _progress;
        public double Progress { get => _progress; set => Set(ref _progress, value); }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (!Set(ref _isRunning, value)) return;
                ScanParseCommand.RaiseCanExecuteChanged();
                DbImportCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                SelectRootPathCommand.RaiseCanExecuteChanged();
            }
        }

        public AsyncRelayCommand ScanParseCommand { get; }
        public AsyncRelayCommand DbImportCommand { get; }
        public RelayCommand CancelCommand { get; }

        private CancellationTokenSource? _cts;

        public ShortPeriodTabViewModel(string header, string rootPath) : base(header)
        {
            _rootPath = rootPath;

            ScanParseCommand = new AsyncRelayCommand(ScanParseAsync, () => !IsRunning);
            DbImportCommand = new AsyncRelayCommand(DbImportAsync, () => !IsRunning && Packages.Any(p => p.IsChecked));
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
            SelectRootPathCommand = new RelayCommand(SelectRootPath, () => !IsRunning);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
