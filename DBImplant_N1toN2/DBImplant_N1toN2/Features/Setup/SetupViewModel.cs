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
            set { if (Set(ref _dbType, value)) { MaybeSwapTemplate(); RefreshCanPrepare(); } }
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

        // ── 접속 방식 (ODBC 기본 / 다이렉트) ─────────────────────────────────
        private LegacyConnectionMode _connectionMode = LegacyConnectionMode.Odbc;
        public LegacyConnectionMode ConnectionMode
        {
            get => _connectionMode;
            set { if (Set(ref _connectionMode, value)) { MaybeSwapTemplate(); RefreshCanPrepare(); Raise(nameof(IsOdbc)); Raise(nameof(IsDirect)); } }
        }
        public bool IsOdbc
        {
            get => ConnectionMode == LegacyConnectionMode.Odbc;
            set { if (value) ConnectionMode = LegacyConnectionMode.Odbc; }
        }
        public bool IsDirect
        {
            get => ConnectionMode == LegacyConnectionMode.Direct;
            set { if (value) ConnectionMode = LegacyConnectionMode.Direct; }
        }

        // 접속 방식 × DB 종류별 기본 접속문자열 템플릿 (DSN-less 예시).
        //   · DSN 방식으로 쓰려면 "DSN=<등록이름>;Uid=...;Pwd=...;" 로 바꾸면 된다.
        //   · 드라이버 이름(Oracle in ..., ODBC Driver 17 for SQL Server)은 설치 환경에 맞게 조정한다.
        private const string TplMsSqlDirect = "Server=localhost,1433; Database=NAIMS; Trusted_Connection=True; TrustServerCertificate=True;";
        private const string TplMsSqlOdbc   = "Driver={ODBC Driver 17 for SQL Server};Server=localhost,1433;Database=NAIMS;Trusted_Connection=Yes;TrustServerCertificate=Yes;";
        private const string TplOracleDirect = "User Id=SYSTEM;Password=oracle;Data Source=localhost:1521/XEPDB1;";
        // 레거시 실측(USPASK_CDMU): SQLConnect(DSN="naims", user="system", pwd="naims"). ODBC 는 이 DSN 형식이 운영 경로.
        private const string TplOracleOdbc   = "DSN=naims;Uid=system;Pwd=naims;";

        private static string TemplateFor(LegacyDbType db, LegacyConnectionMode mode)
        {
            if (mode == LegacyConnectionMode.Odbc)
                return db == LegacyDbType.Oracle ? TplOracleOdbc : TplMsSqlOdbc;
            return db == LegacyDbType.Oracle ? TplOracleDirect : TplMsSqlDirect;
        }

        private static bool IsKnownTemplate(string s)
        {
            s = s.Trim();
            return s == TplMsSqlDirect.Trim() || s == TplMsSqlOdbc.Trim()
                || s == TplOracleDirect.Trim() || s == TplOracleOdbc.Trim();
        }

        /// <summary>접속문자열이 알려진 기본 템플릿 그대로면 현재 종류·방식 템플릿으로 교체(사용자 입력은 보존).</summary>
        private void MaybeSwapTemplate()
        {
            if (IsKnownTemplate(ConnectionString))
                ConnectionString = TemplateFor(DbType, ConnectionMode);
        }

        // ODBC 가 기본이므로 초기 템플릿도 ODBC 형식으로 시작한다.
        private string _connectionString = TplMsSqlOdbc;
        public string ConnectionString
        {
            get => _connectionString;
            set { if (Set(ref _connectionString, value)) RefreshCanPrepare(); }
        }

        // ── MSU 경로 ─────────────────────────────────────────────────────────
        private string _msuRootPath = @"\\192.168.111.165\naims_share\MSU";
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
                App.WriteLog($"PrepareAsync 시작 — DbType={DbType}, Mode={ConnectionMode}, ConnStr={ConnectionString}, MSU={MsuRootPath}");

                var db = LegacyDbConnectionFactory.Create(DbType, ConnectionString, ConnectionMode);
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

                // 마이그레이션 준비 직후 자동 스캔+파싱
                App.WriteLog("전체 스캔+파싱 자동 실행...");
                workspace.ScanAllCommand.Execute(null);

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
