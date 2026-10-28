using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RawCollectMigrator.Features.DbToDb;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.Startup
{
    // [찾기:시작화면] [찾기:접속설정]
    /// <summary>
    /// 앱을 켜면 가장 먼저 뜨는 연결·경로 설정 화면.
    ///
    /// 이 화면의 존재 이유: 예전에는 작업 탭이 바로 열리고 탭마다 접속칸이 따로 있어,
    /// "지금 어느 서버에 붙어 있는지"가 화면마다 달랐다. 이제 접속·경로는 <see cref="AppSession"/>
    /// 한 곳에만 있고, 타겟 연결이 확인되기 전에는 작업 화면으로 넘어갈 수 없다.
    ///
    /// 소스(구 NAIMS)는 DB→DB 이관에만 필요하므로 확인하지 않아도 진행할 수 있다.
    /// </summary>
    public partial class StartupView : UserControl
    {
        private AppSession S => AppSession.Current;
        private bool _loading;

        /// <summary>[작업 시작] 을 눌렀을 때 발생. MainWindow 가 작업 화면으로 전환한다.</summary>
        public event EventHandler? StartRequested;

        public StartupView()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                LoadFromSession();
                await AutoCheckAsync();   // [찾기:자동점검] 켜자마자 스스로 확인한다
            };
        }

        /// <summary>자동 점검이 이미 한 번 돌았는지(설정 화면으로 되돌아올 때 중복 실행 방지).</summary>
        private bool _autoChecked;

        // [찾기:자동점검]
        /// <summary>
        /// 화면이 뜨자마자 타겟·소스 연결을 스스로 확인한다.
        /// 사용자가 버튼을 하나씩 누르지 않아도 "기본 설정 그대로면 바로 시작 가능한" 상태로 만든다.
        /// 배경에서 돌며 UI 를 막지 않고, 실패해도 화면은 그대로 쓸 수 있다(수동 재시도 가능).
        /// </summary>
        private async Task AutoCheckAsync()
        {
            if (_autoChecked) return;
            _autoChecked = true;

            TargetStatus.Text = "자동 확인 중…";
            SourceStatus.Text = "자동 확인 중…";

            await VerifyTargetAsync();
            await VerifySourceAsync();   // 소스는 실패해도 진행을 막지 않는다(파일 이관 전용 사용 대비)
        }

        /// <summary>AppSession 값을 화면에 채운다(이때 발생하는 TextChanged 는 무시한다).</summary>
        private void LoadFromSession()
        {
            _loading = true;
            try
            {
                TargetBox.Text = S.TargetConn;
                SourceBox.Text = S.SourceConn;
                SrcOracle.IsChecked = S.SourceKind == DbProviderKind.Oracle;
                SrcMsSql.IsChecked = S.SourceKind == DbProviderKind.MsSql;
                ModeCombo.SelectedIndex = S.SourceMode == DbConnectionMode.Direct ? 1 : 0;
                MsuBox.Text = S.MsuRoot;
                ClassBox.Text = S.ClassificationRoot;
                CollectBox.Text = S.CollectRawRoot;
                NbBox.Text = S.NarrowBandRoot;
                SdBox.Text = S.ShortPeriodRoot;
                SaveCredChk.IsChecked = S.SaveCredentials;
            }
            finally { _loading = false; }

            CheckPaths();
            RefreshRecents();
            Refresh();
        }

        // ══════════ [찾기:접속기억] 최근 접속 ══════════
        // 한 번 붙어 본 대상은 ConnectionVault 가 기억한다. 여기서는 그 목록을 보여 주고,
        // 고르면 <비밀번호까지 완성된> 문자열을 그대로 넣은 뒤 곧바로 연결을 확인한다.
        // 현장에서 접속문자열을 손으로 다시 짜는 일 자체를 없애는 것이 목적이다.

        /// <summary>최근 접속 드롭다운을 다시 채우고, 지금 쓰는 대상을 골라 둔다.</summary>
        private void RefreshRecents()
        {
            bool prev = _loading;
            _loading = true;   // ItemsSource·SelectedItem 설정이 SelectionChanged 를 깨우지 않게
            try
            {
                TargetRecent.ItemsSource = ConnectionVault.Recent(ConnectionRole.Target);
                SourceRecent.ItemsSource = ConnectionVault.Recent(ConnectionRole.Source);

                TargetRecent.SelectedItem = Match(TargetRecent,
                    ConnectionVault.KeyOf(ConnectionRole.Target, DbProviderKind.MsSql,
                                          DbConnectionMode.Direct, S.TargetConn));
                SourceRecent.SelectedItem = Match(SourceRecent,
                    ConnectionVault.KeyOf(ConnectionRole.Source, S.SourceKind, S.SourceMode, S.SourceConn));
            }
            finally { _loading = prev; }
        }

        /// <summary>드롭다운에서 같은 대상을 찾는다(없으면 null → 선택 없음).</summary>
        private static ConnectionProfile? Match(ComboBox box, string key) =>
            (box.ItemsSource as System.Collections.Generic.IEnumerable<ConnectionProfile>)
                ?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>최근 목록에서 타겟을 고르면 그대로 넣고 곧바로 확인한다.</summary>
        private async void OnPickTargetRecent(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TargetRecent.SelectedItem is not ConnectionProfile prof) return;

            _loading = true;
            try { TargetBox.Text = prof.ConnStr; } finally { _loading = false; }
            S.TargetConn = prof.ConnStr;
            await VerifyTargetAsync();
        }

        /// <summary>최근 목록에서 소스를 고르면 종류·접속방식까지 함께 되살린다.</summary>
        private async void OnPickSourceRecent(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SourceRecent.SelectedItem is not ConnectionProfile prof) return;

            _loading = true;
            try
            {
                SrcOracle.IsChecked = prof.Kind == DbProviderKind.Oracle;
                SrcMsSql.IsChecked = prof.Kind == DbProviderKind.MsSql;
                ModeCombo.SelectedIndex = prof.Mode == DbConnectionMode.Direct ? 1 : 0;
                SourceBox.Text = prof.ConnStr;
            }
            finally { _loading = false; }

            S.SourceKind = prof.Kind;
            S.SourceMode = prof.Mode;
            S.SourceConn = prof.ConnStr;
            await VerifySourceAsync();
        }

        private void OnForgetTarget(object sender, RoutedEventArgs e) =>
            Forget(TargetRecent, ConnectionVault.KeyOf(ConnectionRole.Target, DbProviderKind.MsSql,
                                                       DbConnectionMode.Direct, S.TargetConn));

        private void OnForgetSource(object sender, RoutedEventArgs e) =>
            Forget(SourceRecent, ConnectionVault.KeyOf(ConnectionRole.Source, S.SourceKind,
                                                       S.SourceMode, S.SourceConn));

        /// <summary>고른 항목(없으면 지금 쓰는 대상)을 기억에서 지운다.</summary>
        private void Forget(ComboBox box, string fallbackKey)
        {
            var key = box.SelectedItem is ConnectionProfile p ? p.Key : fallbackKey;
            ConnectionVault.Forget(key);
            RefreshRecents();
        }

        // [찾기:비밀번호저장]
        /// <summary>[비밀번호까지 저장] 을 끄면 <b>이미 저장된</b> 비밀번호까지 지운다 —
        /// 껐는데 옛 비밀번호가 파일에 남아 있으면 껐다는 말이 거짓이 된다.</summary>
        private void OnSaveCredChanged(object sender, RoutedEventArgs e)
        {
            S.SaveCredentials = SaveCredChk.IsChecked == true;
            if (!S.SaveCredentials) ConnectionVault.ClearSecrets();
            S.Save();
            RefreshRecents();
        }

        // ── 입력 반영 ──────────────────────────────────────────────

        private void OnTargetChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            S.TargetConn = TargetBox.Text.Trim();   // 값이 바뀌면 TargetVerified 가 자동으로 풀린다
            TargetStatus.Text = "접속 문자열이 바뀌었습니다 — [연결 확인]을 다시 눌러 주세요.";
            Refresh();
        }

        private void OnSourceChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            S.SourceConn = SourceBox.Text.Trim();
            Refresh();
        }

        /// <summary>소스 종류(Oracle/MsSQL) 전환. 접속문자열이 기본 템플릿 그대로면 해당 종류 템플릿으로 바꾼다.</summary>
        private void OnSourceShapeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            S.SourceKind = SrcOracle.IsChecked == true ? DbProviderKind.Oracle : DbProviderKind.MsSql;
            SwapTemplateIfUntouched();
            Refresh();
        }

        /// <summary>접속 방식(ODBC/다이렉트) 전환. 형식이 완전히 다르므로 손대지 않은 템플릿이면 교체한다.</summary>
        private void OnSourceModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !IsLoaded) return;
            S.SourceMode = ModeCombo.SelectedIndex == 1 ? DbConnectionMode.Direct : DbConnectionMode.Odbc;
            SwapTemplateIfUntouched();
            Refresh();
        }

        /// <summary>현재 접속문자열이 알려진 기본 템플릿 4종 중 하나면 새 종류·방식 템플릿으로 교체한다.</summary>
        private void SwapTemplateIfUntouched()
        {
            var cur = SourceBox.Text.Trim();
            bool untouched =
                cur.Length == 0 ||
                (from k in new[] { DbProviderKind.Oracle, DbProviderKind.MsSql }
                 from m in new[] { DbConnectionMode.Odbc, DbConnectionMode.Direct }
                 select DbProvider.DefaultSourceFor(k, m)).Any(t => string.Equals(t.Trim(), cur, StringComparison.OrdinalIgnoreCase));

            if (!untouched) return;
            // 템플릿을 갈아끼울 때도 기억해 둔 비밀번호를 바로 채워 넣는다.
            var next = ConnectionVault.Complete(ConnectionRole.Source, S.SourceKind, S.SourceMode,
                                                DbProvider.DefaultSourceFor(S.SourceKind, S.SourceMode));
            _loading = true;
            try { SourceBox.Text = next; } finally { _loading = false; }
            S.SourceConn = next;
        }

        // [찾기:DSN없이접속]
        /// <summary>
        /// DSN 형식 접속문자열을 드라이버 직접 지정(DSN-less)으로 바꾼다.
        /// DSN 이 이 프로세스에서 안 보일 때(IM002) 곧바로 우회할 수 있는 경로다.
        /// </summary>
        private void OnDsnLess(object sender, RoutedEventArgs e)
        {
            var cur = SourceBox.Text.Trim();
            var converted = OdbcCatalog.ToDsnLess(cur);
            if (converted == cur)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "변환할 DSN 을 찾지 못했습니다.\n\n" + OdbcCatalog.DiagnoseDsn(cur),
                    "DSN 없이 접속", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SourceBox.Text = converted;
            S.SourceConn = converted;
            SourceStatus.Text = "DSN 없이 붙도록 바꿨습니다 — [연결 확인]을 눌러 확인하세요.";
            Refresh();
        }

        // [찾기:다이렉트전환]
        /// <summary>
        /// ODBC 접속문자열을 Oracle 다이렉트(관리형 드라이버)로 바꾸고 접속 방식도 함께 전환한다.
        /// DSN 등록·드라이버 비트수·프로세스 권한 의존을 통째로 없애는 경로다.
        /// </summary>
        private void OnToDirect(object sender, RoutedEventArgs e)
        {
            if (S.SourceKind != DbProviderKind.Oracle)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "다이렉트 전환은 Oracle 소스에만 적용됩니다.",
                    "다이렉트로 전환", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var converted = OdbcCatalog.ToOracleDirect(SourceBox.Text.Trim());
            if (converted.Length == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "접속 대상(host:port/service)을 알아내지 못했습니다.\n\n" +
                    "DSN 에 DBQ 가 없거나 DSN 자체가 보이지 않습니다. 아래 형식으로 직접 입력하세요:\n" +
                    "  User Id=LEGACYNAIMS;Password=…;Data Source=localhost:1522/LegacyNaims;",
                    "다이렉트로 전환", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _loading = true;
            try { ModeCombo.SelectedIndex = 1; SourceBox.Text = converted; }   // 1 = 다이렉트
            finally { _loading = false; }
            S.SourceMode = DbConnectionMode.Direct;
            S.SourceConn = converted;
            SourceStatus.Text = "다이렉트(관리형 드라이버)로 바꿨습니다 — [연결 확인]을 눌러 확인하세요. " +
                                "DSN·Oracle 클라이언트 설치가 필요 없습니다.";
            Refresh();
        }

        private void OnResetSource(object sender, RoutedEventArgs e)
        {
            var next = ConnectionVault.Complete(ConnectionRole.Source, S.SourceKind, S.SourceMode,
                                                DbProvider.DefaultSourceFor(S.SourceKind, S.SourceMode));
            SourceBox.Text = next;
            S.SourceConn = next;
            Refresh();
        }

        private void OnPathChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            S.ClassificationRoot = ClassBox.Text.Trim();
            S.CollectRawRoot = CollectBox.Text.Trim();
            S.NarrowBandRoot = NbBox.Text.Trim();
            S.ShortPeriodRoot = SdBox.Text.Trim();
            CheckPaths();
        }

        // ── 연결 확인 ──────────────────────────────────────────────

        /// <summary>타겟(NAIMS2)에 실제로 접속해 버전을 읽어 확인한다. 성공해야 [작업 시작]이 열린다.</summary>
        private async void OnVerifyTarget(object sender, RoutedEventArgs e) => await VerifyTargetAsync();

        /// <summary>타겟 연결 확인 본체(버튼·자동 점검 공용).</summary>
        private async Task VerifyTargetAsync()
        {
            // [찾기:접속완성] 비밀번호가 비어 있으면 기억해 둔 값으로 채운 뒤 시도한다.
            var conn = ConnectionVault.Complete(ConnectionRole.Target, DbProviderKind.MsSql,
                                                DbConnectionMode.Direct, TargetBox.Text.Trim());
            if (conn != TargetBox.Text.Trim())
            {
                _loading = true;
                try { TargetBox.Text = conn; } finally { _loading = false; }
            }
            S.TargetConn = conn;

            TargetStatus.Text = "확인 중…";
            var (ok, msg) = await TestAsync(DbProviderKind.MsSql, DbConnectionMode.Direct, S.TargetConn);
            S.TargetVerified = ok;
            S.TargetInfo = msg;
            TargetStatus.Text = (ok ? "✅ " : "❌ ") + msg;

            // [찾기:접속기억] 성공한 것만 기억한다 — 실패한 문자열을 남기면 같은 실패를 되풀이한다.
            if (ok)
            {
                ConnectionVault.Remember(ConnectionRole.Target, DbProviderKind.MsSql,
                                         DbConnectionMode.Direct, S.TargetConn);
                RefreshRecents();
            }
            Refresh();
        }

        /// <summary>소스(구 NAIMS)에 접속해 확인한다. 실패해도 파일 이관은 진행할 수 있다.</summary>
        private async void OnVerifySource(object sender, RoutedEventArgs e) => await VerifySourceAsync();

        /// <summary>소스 연결 확인 본체(버튼·자동 점검 공용).</summary>
        private async Task VerifySourceAsync()
        {
            // [찾기:접속완성] 타겟과 같은 규칙 — 기억해 둔 비밀번호로 채운 뒤 시도한다.
            var filled = ConnectionVault.Complete(ConnectionRole.Source, S.SourceKind, S.SourceMode,
                                                  SourceBox.Text.Trim());
            if (filled != SourceBox.Text.Trim())
            {
                _loading = true;
                try { SourceBox.Text = filled; } finally { _loading = false; }
            }
            S.SourceConn = filled;
            SourceStatus.Text = "확인 중…";

            // [찾기:DSN자동폴백] IM002 면 DSN 없이 한 번 더 시도하고, 성공하면 그 문자열을 채택한다.
            var (ok, msg, working) = await TestWithFallbackAsync(S.SourceKind, S.SourceMode, S.SourceConn);
            if (ok && working != S.SourceConn)
            {
                _loading = true;
                try { SourceBox.Text = working; } finally { _loading = false; }
                S.SourceConn = working;
            }

            S.SourceVerified = ok;
            S.SourceInfo = msg;
            SourceStatus.Text = (ok ? "✅ " : "❌ ") + msg
                + (ok ? "" : "   (파일 이관만 할 경우 이 항목은 없어도 됩니다)");

            // [찾기:접속기억] 폴백으로 문자열이 바뀌었으면 실제로 붙은 그 값을 기억한다.
            if (ok)
            {
                ConnectionVault.Remember(ConnectionRole.Source, S.SourceKind, S.SourceMode, S.SourceConn);
                RefreshRecents();
            }
            Refresh();
        }

        // [찾기:DSN자동폴백]
        /// <summary>
        /// 지정 DB에 접속해 버전 SQL 을 실행한다. 실패 사유는 문자열로 돌려준다.
        ///
        /// ODBC 에서 IM002(데이터 원본 이름 없음)가 나면 <b>DSN-less 로 한 번 자동 재시도</b>한다.
        /// 이 PC 처럼 ODBC 등록이 어긋나 있으면(목록에는 있는데 서브키가 고아이거나 그 반대)
        /// DSN 이름 해석만 실패하고 드라이버 자체는 멀쩡한 경우가 있다 — 그때 DSN 을 빼고
        /// 드라이버·대상을 직접 지정하면 그대로 붙는다. 현장에서 이 한 번의 재시도가
        /// 이관 전체를 멈추게 두지 않는다.
        /// </summary>
        /// <returns>성공 여부, 표시 메시지, 실제로 접속에 성공한 문자열(폴백됐으면 바뀐 값).</returns>
        private static async Task<(bool ok, string msg, string workingConn)> TestWithFallbackAsync(
            DbProviderKind kind, DbConnectionMode mode, string conn)
        {
            var (ok, msg) = await TestAsync(kind, mode, conn);
            if (ok || mode != DbConnectionMode.Odbc || !msg.Contains("IM002"))
                return (ok, msg, conn);

            var dsnLess = OdbcCatalog.ToDsnLess(conn);
            if (dsnLess == conn || dsnLess.Length == 0) return (ok, msg, conn);

            var (ok2, msg2) = await TestAsync(kind, mode, dsnLess);
            if (!ok2) return (false, msg + "\n[자동 폴백] DSN 없이도 시도했으나 실패 — " + msg2, conn);

            return (true,
                msg2 + "\n[자동 폴백] DSN 이름 해석이 실패해 DSN 없이 붙었습니다 " +
                       "(드라이버·대상 직접 지정). 접속문자열을 이 값으로 바꿨습니다.",
                dsnLess);
        }

        /// <summary>지정 DB에 접속해 버전 SQL 을 실행한다. 실패 사유는 문자열로 돌려준다.</summary>
        private static async Task<(bool ok, string msg)> TestAsync(
            DbProviderKind kind, DbConnectionMode mode, string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return (false, "접속 문자열이 비어 있습니다.");
            try
            {
                await using var cn = DbProvider.CreateConnection(kind, mode, conn);
                await cn.OpenAsync();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = DbProvider.VersionSql(kind);
                var v = Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "";
                var line = v.Split('\n')[0].Trim();
                return (true, $"{AppSession.ServerOf(conn)} — {(line.Length > 120 ? line[..120] + "…" : line)}");
            }
            catch (Exception ex)
            {
                var msg = Features.ExtractRaw.ExceptionText.Describe(ex);
                // [찾기:접속계측] 화면 값과 실제 전달값이 다를 수 있으므로 시도 내용을 먼저 박아 둔다.
                msg += "\n" + DbProvider.DescribeAttempt(kind, mode, conn);
                // [찾기:IM002] ODBC 실패는 메시지만으로 원인을 못 가린다 — 이 프로세스가 본 것을 함께 보여준다.
                if (mode == DbConnectionMode.Odbc || msg.Contains("IM00"))
                    msg += "\n" + OdbcCatalog.DiagnoseDsn(conn);
                return (false, msg);
            }
        }

        /// <summary>연결 탐색 창을 열어 고른 항목을 타겟/소스에 반영한다.</summary>
        private void OnDiscover(object sender, RoutedEventArgs e)
        {
            var win = new EndpointBrowserWindow { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true) return;

            if (win.PickedSource is { } src)
            {
                S.SourceKind = src.Kind;
                S.SourceMode = src.Mode;
                // 탐색이 찾아 준 대상도 전에 붙어 봤다면 비밀번호를 채워 준다.
                S.SourceConn = ConnectionVault.Complete(ConnectionRole.Source, src.Kind, src.Mode, src.ConnStr);
            }
            if (!string.IsNullOrEmpty(win.PickedTarget))
                S.TargetConn = ConnectionVault.Complete(ConnectionRole.Target, DbProviderKind.MsSql,
                                                        DbConnectionMode.Direct, win.PickedTarget!);

            LoadFromSession();
        }

        // ── 경로 ──────────────────────────────────────────────────

        private void OnPickMsu(object sender, RoutedEventArgs e) => Pick(MsuBox, "MSU 루트 폴더 선택");
        private void OnPickClass(object sender, RoutedEventArgs e) => Pick(ClassBox, "CLASSIFICATIONDB 폴더 선택");
        private void OnPickCollect(object sender, RoutedEventArgs e) => Pick(CollectBox, "수집원음자료 폴더 선택");
        private void OnPickNb(object sender, RoutedEventArgs e) => Pick(NbBox, "협대역원음자료 폴더 선택");
        private void OnPickSd(object sender, RoutedEventArgs e) => Pick(SdBox, "단주기원음자료 폴더 선택");

        private void Pick(TextBox tb, string title)
        {
            var dlg = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = Directory.Exists(tb.Text.Trim()) ? tb.Text.Trim() : S.MsuRoot,
            };
            if (dlg.ShowDialog() == true) tb.Text = dlg.FolderName;
        }

        /// <summary>MSU 루트에서 하위 4개 폴더를 유추해 채운다.</summary>
        private void OnDeriveFromMsu(object sender, RoutedEventArgs e)
        {
            S.MsuRoot = MsuBox.Text.Trim();
            S.DeriveFromMsu();
            LoadFromSession();
        }

        /// <summary>각 폴더의 접근 가능 여부를 표시한다(존재하지 않아도 진행은 막지 않는다).</summary>
        private void CheckPaths()
        {
            string Mark(string label, string path) =>
                $"{label} {(Directory.Exists(path) ? "✓" : "✗")}";

            PathStatus.Text = string.Join("   ·   ", new[]
            {
                Mark("CLASSIFICATION", ClassBox.Text.Trim()),
                Mark("수집원음", CollectBox.Text.Trim()),
                Mark("협대역", NbBox.Text.Trim()),
                Mark("단주기", SdBox.Text.Trim()),
            }) + "     (✗ 는 지금 접근이 안 되는 폴더입니다 — 해당 작업만 못 할 뿐 시작은 가능합니다)";
        }

        // ── 시작 / 저장 ────────────────────────────────────────────

        /// <summary>타겟 확인 여부에 따라 [작업 시작] 활성 상태와 안내 문구를 갱신한다.</summary>
        private void Refresh()
        {
            BtnStart.IsEnabled = S.IsReady;
            ReadyText.Text = S.IsReady
                ? "타겟 확인됨 — 시작할 수 있습니다."
                : "① 타겟 [연결 확인]을 통과해야 시작할 수 있습니다.";

            HintEmptyPassword();
        }

        // [찾기:비밀번호입력] 비밀번호는 소스코드·설정파일에 평문으로 두지 않는다.
        //   현장·실운영 계정의 기본 접속문자열은 "Pwd=" 가 비어 있고, 여기서 한 번 채워 넣어야 한다.
        //   한 번 붙고 나면 ConnectionVault 가 DPAPI 로 기억하므로 다음부터는 자동으로 채워진다.
        /// <summary>비밀번호 칸이 비어 있으면 그 사실을 상태줄에 알린다(사용자 입력 없이는 접속이 안 되므로).</summary>
        private void HintEmptyPassword()
        {
            if (NeedsPassword(S.TargetConn) && !S.TargetVerified)
                TargetStatus.Text = "🔑 비밀번호가 비어 있습니다 — 접속문자열의 Password= 뒤에 입력한 뒤 [연결 확인]을 누르세요. " +
                                    "(한 번 붙고 나면 이 대상은 기억되어 다음부터 자동으로 채워집니다)";

            if (NeedsPassword(S.SourceConn) && !S.SourceVerified)
                SourceStatus.Text = "🔑 비밀번호가 비어 있습니다 — 접속문자열의 Pwd= 뒤에 입력한 뒤 [연결 확인]을 누르세요. " +
                                    "(파일 이관만 할 경우에는 비워 두어도 됩니다)";
        }

        /// <summary>비밀번호 인증인데 값이 비어 있는지(판정은 SecretStore 한 곳에서만).</summary>
        private static bool NeedsPassword(string conn) => SecretStore.NeedsPassword(conn);

        private void OnSaveOnly(object sender, RoutedEventArgs e)
        {
            Persist();
            MessageBox.Show(Window.GetWindow(this),
                "설정을 저장했습니다.\n\n" + AppSession.ConfigPath,
                "설정 저장", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            Persist();
            StartRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Persist()
        {
            S.MsuRoot = MsuBox.Text.Trim();
            S.SaveCredentials = SaveCredChk.IsChecked == true;
            if (!S.SaveCredentials) ConnectionVault.ClearSecrets();
            S.Save();
        }
    }
}
