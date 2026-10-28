using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.DbBrowser;
using DBImplant_N1toN2.Features.FileBrowser;
using DBImplant_N1toN2.Features.Migration.ViewModels;
using DBImplant_N1toN2.Features.NarrowBand.ViewModels;
using DBImplant_N1toN2.Features.Raw.ViewModels;
using DBImplant_N1toN2.Features.ShortPeriod.ViewModels;
using DBImplant_N1toN2.Infrastructure.Commands;
using static DBImplant_N1toN2.Models.AppSettings;

namespace DBImplant_N1toN2.Features.Workspace
{
    public sealed class WorkspaceViewModel : INotifyPropertyChanged
    {
        private readonly ILegacyDbConnection _db;

        public DbBrowserViewModel DbBrowser { get; }
        public FileBrowserViewModel FileBrowser { get; }
        public MigrationPreviewViewModel MigrationPreview { get; }
        public MigrationExecuteViewModel MigrationExecute { get; }
        public ProgressOverlayViewModel ProgressOverlay { get; } = new();

        public string Title { get; }

        public ICommand ShowPreviewCommand { get; }
        public ICommand ShowExecuteCommand { get; }
        public ICommand ChangeMsuPathCommand { get; }
        public AsyncRelayCommand ScanAllCommand { get; }

        public WorkspaceViewModel(ILegacyDbConnection db, string msuRootPath)
        {
            _db = db;
            Title = $"NAIMS Migration — {db.DbTypeName} / {msuRootPath}";

            DbBrowser = new DbBrowserViewModel(db);
            FileBrowser = new FileBrowserViewModel(msuRootPath);
            MigrationPreview = new MigrationPreviewViewModel(db, FileBrowser);
            MigrationExecute = new MigrationExecuteViewModel(db, FileBrowser);

            ShowPreviewCommand = new RelayCommand(() =>
            {
                MigrationPreview.IsVisible = true;
                MigrationPreview.LoadPreviewCommand.Execute(null);
            });
            ShowExecuteCommand = new RelayCommand(() => MigrationExecute.IsVisible = true);

            ChangeMsuPathCommand = new RelayCommand(() =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "MSU 루트 경로 변경" };
                if (dlg.ShowDialog() == true && Directory.Exists(dlg.FolderName))
                {
                    Roots.SetMsuRoot(dlg.FolderName);
                    FileBrowser.ReloadPaths();
                }
            });

            ScanAllCommand = new AsyncRelayCommand(ScanAllWithProgressAsync);
        }

        private async Task ScanAllWithProgressAsync()
        {
            ProgressOverlay.Show("전체 스캔+파싱", "MSU 폴더 스캔 준비 중...", indeterminate: true);
            try
            {
                // 순차 실행: 수집원음→협대역→단주기
                await RunTabScanAsync("수집원음자료", FileBrowser.RawTab, 0, 3);
                await RunTabScanAsync("협대역원음자료", FileBrowser.NbTab, 1, 3);
                await RunTabScanAsync("단주기분석자료", FileBrowser.SdTab, 2, 3);

                ProgressOverlay.Update("스캔+파싱 완료!", 1.0);
                await Task.Delay(600);
            }
            catch (Exception ex)
            {
                ProgressOverlay.Update($"오류: {ex.Message}", 0);
                await Task.Delay(2000);
            }
            finally
            {
                ProgressOverlay.Hide();
            }
        }

        private async Task RunTabScanAsync(string name, RawTabViewModel tab, int index, int total)
        {
            double baseProgress = (double)index / total;
            ProgressOverlay.Update($"[{index + 1}/{total}] {name} 스캔+파싱 중...", baseProgress);

            if (!tab.ScanParseCommand.CanExecute(null)) return;

            void OnProgress(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "Progress")
                {
                    double combined = baseProgress + tab.Progress / total;
                    ProgressOverlay.Update($"[{index + 1}/{total}] {name}: {tab.StatusText}", combined);
                }
            }

            tab.PropertyChanged += OnProgress;
            tab.ScanParseCommand.Execute(null);
            while (tab.IsRunning) await Task.Delay(100);
            tab.PropertyChanged -= OnProgress;
        }

        private async Task RunTabScanAsync(string name, NarrowBandTabViewModel tab, int index, int total)
        {
            double baseProgress = (double)index / total;
            ProgressOverlay.Update($"[{index + 1}/{total}] {name} 스캔+파싱 중...", baseProgress);

            if (!tab.ScanParseCommand.CanExecute(null)) return;

            void OnProgress(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "Progress")
                {
                    double combined = baseProgress + tab.Progress / total;
                    ProgressOverlay.Update($"[{index + 1}/{total}] {name}: {tab.StatusText}", combined);
                }
            }

            tab.PropertyChanged += OnProgress;
            tab.ScanParseCommand.Execute(null);
            while (tab.IsRunning) await Task.Delay(100);
            tab.PropertyChanged -= OnProgress;
        }

        private async Task RunTabScanAsync(string name, ShortPeriodTabViewModel tab, int index, int total)
        {
            double baseProgress = (double)index / total;
            ProgressOverlay.Update($"[{index + 1}/{total}] {name} 스캔+파싱 중...", baseProgress);

            if (!tab.ScanParseCommand.CanExecute(null)) return;

            void OnProgress(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "Progress")
                {
                    double combined = baseProgress + tab.Progress / total;
                    ProgressOverlay.Update($"[{index + 1}/{total}] {name}: {tab.StatusText}", combined);
                }
            }

            tab.PropertyChanged += OnProgress;
            tab.ScanParseCommand.Execute(null);
            while (tab.IsRunning) await Task.Delay(100);
            tab.PropertyChanged -= OnProgress;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
