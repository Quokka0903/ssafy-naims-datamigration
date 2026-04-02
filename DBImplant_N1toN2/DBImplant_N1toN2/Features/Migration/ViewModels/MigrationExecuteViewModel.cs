using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.FileBrowser;
using DBImplant_N1toN2.Features.Migration.Models;
using DBImplant_N1toN2.Features.Migration.Steps;
using DBImplant_N1toN2.Infrastructure.Commands;

namespace DBImplant_N1toN2.Features.Migration.ViewModels
{
    public sealed class MigrationExecuteViewModel : INotifyPropertyChanged
    {
        private readonly ILegacyDbConnection _db;
        private readonly FileBrowserViewModel _fileBrowser;
        private CancellationTokenSource? _cts;

        // ── Properties ───────────────────────────────────────────────────────
        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set => Set(ref _isVisible, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (Set(ref _isRunning, value))
                {
                    RunCommand.RaiseCanExecuteChanged();
                    CancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isComplete;
        public bool IsComplete
        {
            get => _isComplete;
            private set => Set(ref _isComplete, value);
        }

        private string _targetConnectionString = "";
        public string TargetConnectionString
        {
            get => _targetConnectionString;
            set
            {
                if (Set(ref _targetConnectionString, value))
                    RunCommand.RaiseCanExecuteChanged();
            }
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            private set => Set(ref _progressPercent, value);
        }

        private string _currentStepName = "";
        public string CurrentStepName
        {
            get => _currentStepName;
            private set => Set(ref _currentStepName, value);
        }

        private string _resultSummary = "";
        public string ResultSummary
        {
            get => _resultSummary;
            private set => Set(ref _resultSummary, value);
        }

        public ObservableCollection<MigrationLogEntry> LogEntries { get; } = new();

        // ── Commands ─────────────────────────────────────────────────────────
        public AsyncRelayCommand RunCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand CloseCommand { get; }

        // ── Ctor ─────────────────────────────────────────────────────────────
        public MigrationExecuteViewModel(ILegacyDbConnection db, FileBrowserViewModel fileBrowser)
        {
            _db = db;
            _fileBrowser = fileBrowser;

            RunCommand = new AsyncRelayCommand(RunMigrationAsync,
                () => !IsRunning && !string.IsNullOrWhiteSpace(TargetConnectionString));
            CancelCommand = new RelayCommand(RequestCancel, () => IsRunning);
            CloseCommand = new RelayCommand(Close);
        }

        // ── Core Logic ───────────────────────────────────────────────────────
        private async Task RunMigrationAsync()
        {
            IsRunning = true;
            IsComplete = false;
            ProgressPercent = 0;
            LogEntries.Clear();
            ResultSummary = "";

            _cts = new CancellationTokenSource();

            AddLog("INFO", "마이그레이션 시작...");

            var ctx = new MigrationContext
            {
                LegacyDb = _db,
                FileBrowser = _fileBrowser,
                TargetConnectionString = TargetConnectionString,
            };

            var pipeline = new MigrationPipeline(ctx);

            var progressHandler = new Progress<MigrationProgress>(p =>
            {
                double pct = p.TotalSteps > 0 ? (double)p.CurrentStep / p.TotalSteps * 100.0 : 0;
                ProgressPercent = pct;
                CurrentStepName = p.StepName;

                if (!string.IsNullOrEmpty(p.Detail))
                    AddLog("INFO", $"[{p.StepName}] {p.Detail}");
            });

            try
            {
                bool success = await Task.Run(() => pipeline.RunAsync(progressHandler, _cts.Token));

                ProgressPercent = 100;
                ResultSummary = success
                    ? "마이그레이션이 성공적으로 완료되었습니다."
                    : "마이그레이션이 취소되었습니다.";
                AddLog(success ? "INFO" : "WARN", ResultSummary);
            }
            catch (Exception ex)
            {
                ResultSummary = $"마이그레이션 실패: {ex.Message}";
                AddLog("ERROR", ResultSummary);
            }
            finally
            {
                IsRunning = false;
                IsComplete = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void RequestCancel()
        {
            _cts?.Cancel();
            AddLog("WARN", "취소 요청됨...");
        }

        private void Close()
        {
            IsVisible = false;
            IsComplete = false;
            IsRunning = false;
            ProgressPercent = 0;
            CurrentStepName = "";
            ResultSummary = "";
            LogEntries.Clear();
        }

        private void AddLog(string level, string message)
        {
            var entry = new MigrationLogEntry(DateTime.Now, level, message);
            if (Application.Current?.Dispatcher?.CheckAccess() == false)
                Application.Current.Dispatcher.Invoke(() => LogEntries.Add(entry));
            else
                LogEntries.Add(entry);
        }

        // ── INPC ─────────────────────────────────────────────────────────────
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
