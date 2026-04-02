using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DBImplant_N1toN2.Infrastructure.Commands;
using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Models;
using static DBImplant_N1toN2.Models.AppSettings;
using DBImplant_N1toN2.Features.Workspace;

namespace DBImplant_N1toN2.Features.Setup
{
    public sealed class SetupViewModel : INotifyPropertyChanged
    {
        // ── DB 설정 ──────────────────────────────────────────────────────────
        private LegacyDbType _dbType = LegacyDbType.MsSql;
        public LegacyDbType DbType
        {
            get => _dbType;
            set { if (Set(ref _dbType, value)) RefreshCanPrepare(); }
        }

        public bool IsMsSql
        {
            get => DbType == LegacyDbType.MsSql;
            set { if (value) DbType = LegacyDbType.MsSql; }
        }
        public bool IsOracle
        {
            get => DbType == LegacyDbType.Oracle;
            set { if (value) DbType = LegacyDbType.Oracle; }
        }

        private string _connectionString = string.Empty;
        public string ConnectionString
        {
            get => _connectionString;
            set { if (Set(ref _connectionString, value)) RefreshCanPrepare(); }
        }

        // ── MSU 경로 ─────────────────────────────────────────────────────────
        private string _msuRootPath = string.Empty;
        public string MsuRootPath
        {
            get => _msuRootPath;
            set { if (Set(ref _msuRootPath, value)) RefreshCanPrepare(); }
        }

        // ── 상태 ─────────────────────────────────────────────────────────────
        private string _statusText = "레거시 DB 유형을 선택하고, 연결 문자열과 MSU 경로를 입력하세요.";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { if (Set(ref _isBusy, value)) RefreshCanPrepare(); }
        }

        // ── Commands ─────────────────────────────────────────────────────────
        public AsyncRelayCommand PrepareCommand { get; }
        public RelayCommand BrowseMsuPathCommand { get; }

        // ── Event ────────────────────────────────────────────────────────────
        public event EventHandler<WorkspaceViewModel>? MigrationReady;

        // ── Ctor ─────────────────────────────────────────────────────────────
        public SetupViewModel()
        {
            PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
            BrowseMsuPathCommand = new RelayCommand(BrowseMsuPath);
        }

        private bool CanPrepare =>
            !IsBusy &&
            !string.IsNullOrWhiteSpace(ConnectionString) &&
            !string.IsNullOrWhiteSpace(MsuRootPath) &&
            Directory.Exists(MsuRootPath);

        private void RefreshCanPrepare()
        {
            PrepareCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsMsSql));
            Raise(nameof(IsOracle));
        }

        // ── 마이그레이션 준비 ─────────────────────────────────────────────────
        private async Task PrepareAsync()
        {
            IsBusy = true;
            StatusText = "DB 연결 테스트 중...";

            try
            {
                App.WriteLog($"PrepareAsync 시작 — DbType={DbType}, ConnStr={ConnectionString}, MSU={MsuRootPath}");

                var db = LegacyDbConnectionFactory.Create(DbType, ConnectionString);
                App.WriteLog("DB 팩토리 생성 완료. 연결 테스트 시작...");

                bool ok = await db.TestConnectionAsync();
                App.WriteLog($"연결 테스트 결과: {ok}");

                if (!ok)
                {
                    StatusText = "DB 연결 실패. 연결 문자열을 확인하세요.";
                    db.Dispose();
                    return;
                }

                StatusText = "연결 성공. 작업공간 구성 중...";
                App.WriteLog("MSU Root 설정 중...");
                Roots.SetMsuRoot(MsuRootPath);

                App.WriteLog("WorkspaceViewModel 생성 중...");
                var workspace = new WorkspaceViewModel(db, MsuRootPath);

                App.WriteLog("MigrationReady 이벤트 발생...");
                MigrationReady?.Invoke(this, workspace);
                App.WriteLog("Workspace 전환 완료");
            }
            catch (Exception ex)
            {
                App.WriteLog($"PrepareAsync 예외: {ex}");
                StatusText = $"오류: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void BrowseMsuPath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "MSU 루트 경로 선택 (예: D:\\MSU)",
            };
            if (dialog.ShowDialog() == true)
                MsuRootPath = dialog.FolderName;
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
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
