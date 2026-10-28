using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:연결탐색창]
    /// <summary>
    /// 접속 가능한 DB 를 4개 층(로컬 ODBC·TNS / UDP 1434 / 서브넷 스캔 / 자격증명 판정)으로 찾아
    /// 고른 항목을 소스·타겟 접속문자열로 돌려주는 창.
    ///
    /// 안전 규약:
    ///   · 서브넷 스캔은 대상 대역을 명시해 사용자 확인을 받은 뒤에만 실행한다(망 스캔이므로).
    ///   · 접속 판정은 사용자가 입력한 계정 1세트로만 시도한다(자동 조합 금지).
    ///   · 계정 잠김이 관측되면 <see cref="EndpointDiscovery"/> 의 잠금 가드가 해당 호스트를 차단한다.
    /// </summary>
    public partial class EndpointBrowserWindow : Window
    {
        private readonly ObservableCollection<DbEndpoint> _rows = new();
        private CancellationTokenSource? _cts;

        /// <summary>[소스로 사용] 결과. 취소 시 null.</summary>
        public (DbProviderKind Kind, DbConnectionMode Mode, string ConnStr)? PickedSource { get; private set; }

        /// <summary>[타겟으로 사용] 결과(항상 MsSQL 다이렉트). 취소 시 null.</summary>
        public string? PickedTarget { get; private set; }

        public EndpointBrowserWindow()
        {
            InitializeComponent();
            Grid.ItemsSource = _rows;

            HeaderText.Text = $"연결 탐색 — 이 앱은 {OdbcCatalog.Bitness} 프로세스입니다 " +
                              "(같은 비트수의 ODBC 드라이버만 실제 접속에 쓸 수 있습니다)";

            foreach (var c in EndpointDiscovery.LocalSubnets()) CidrBox.Items.Add(c);
            if (CidrBox.Items.Count > 0) CidrBox.SelectedIndex = 0;

            // [찾기:계정자동채움] 이미 설정된 소스 접속문자열의 계정/비밀번호를 미리 채워 둔다.
            //   접속 판정을 누를 때마다 같은 값을 다시 타이핑하지 않게 하기 위함이다.
            //   (자격증명을 조합해 여러 번 시도하지는 않는다 — 잠금 방지)
            var sess = Infrastructure.AppSession.Current;
            UserBox.Text = Infrastructure.AppSession.UserOf(sess.SourceConn);
            PwdBox.Password = Infrastructure.AppSession.PasswordOf(sess.SourceConn);

            // 목록에서 고른 항목이 DSN 이면, 그 DSN 에 등록된 계정으로 바꿔 준다.
            Grid.SelectionChanged += (_, _) => FillAccountFromSelection();

            Loaded += async (_, _) => await ScanLocalAsync();
        }

        /// <summary>
        /// 선택한 후보가 DSN 이고 그 DSN 에 계정이 등록돼 있으면 계정 칸을 그 값으로 맞춘다.
        /// 비밀번호는 레지스트리에서 읽지 않으므로 건드리지 않는다.
        /// </summary>
        private void FillAccountFromSelection()
        {
            if (Grid.SelectedItem is not DbEndpoint ep || ep.Dsn.Length == 0) return;
            var dsn = OdbcCatalog.DataSources()
                .FirstOrDefault(d => d.Name.Equals(ep.Dsn, StringComparison.OrdinalIgnoreCase));
            if (dsn is { UserId.Length: > 0 }) UserBox.Text = dsn.UserId;
        }

        // ── ① 로컬 카탈로그 ─────────────────────────────────────────────
        private async void OnScanLocal(object sender, RoutedEventArgs e) => await ScanLocalAsync();

        /// <summary>ODBC DSN + tnsnames.ora 를 읽어 후보를 채운다(네트워크 접근 없음).</summary>
        private async Task ScanLocalAsync()
        {
            StatusText.Text = "로컬 카탈로그 읽는 중…";
            try
            {
                var found = await Task.Run(() => EndpointDiscovery.FromLocalCatalog());
                MergeInto(found);

                int drivers = OdbcCatalog.DriversDetailed().Count(d => d.UsableHere);
                int tnsFiles = TnsCatalog.LocateFiles().Count;
                StatusText.Text = $"로컬 — 후보 {found.Count}건 · 사용 가능 드라이버 {drivers}개 · tnsnames {tnsFiles}개";
            }
            catch (Exception ex)
            {
                StatusText.Text = "로컬 탐색 오류: " + ex.Message;
            }
        }

        // ── ② 망 자동 발견 ──────────────────────────────────────────────
        private async void OnDiscoverBrowser(object sender, RoutedEventArgs e)
        {
            BtnBrowser.IsEnabled = false;
            StatusText.Text = "UDP 1434 브로드캐스트 응답 수집 중…";
            try
            {
                var found = await EndpointDiscovery.DiscoverSqlBrowserAsync();
                MergeInto(found);
                StatusText.Text = found.Count > 0
                    ? $"망 발견 — SQL Server 인스턴스 {found.Count}건"
                    : "망 발견 — 응답 없음 (SQL Browser 미가동이거나 UDP 1434 차단. '없음'이 아니라 '확인 불가'입니다)";
            }
            catch (Exception ex)
            {
                StatusText.Text = "망 발견 오류: " + ex.Message;
            }
            finally { BtnBrowser.IsEnabled = true; }
        }

        // ── ③ 서브넷 스캔 ───────────────────────────────────────────────
        private async void OnScanSubnet(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;

            var cidr = (CidrBox.Text ?? "").Trim();
            int[] ports;
            try
            {
                ports = (PortsBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.Parse(s.Trim())).ToArray();
                if (ports.Length == 0) throw new FormatException("포트가 비었습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "포트 목록을 해석할 수 없습니다: " + ex.Message,
                    "서브넷 스캔", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> hosts;
            try { hosts = EndpointDiscovery.ExpandCidr(cidr); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "서브넷 스캔", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 망 스캔은 반드시 사전 확인을 받는다.
            var confirm =
                $"다음 대역을 스캔합니다.\n\n" +
                $"  대역   : {cidr}   (호스트 {hosts.Count}개)\n" +
                $"  포트   : {string.Join(", ", ports)}\n" +
                $"  시도수 : {hosts.Count * ports.Length}회 TCP 연결\n\n" +
                "이 동작은 망 스캔이며 방화벽·IDS 로그에 기록됩니다.\n" +
                "사전 승인을 받은 대역인지 확인하고 진행하세요.";
            if (MessageBox.Show(this, confirm, "서브넷 스캔 확인",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            _cts = new CancellationTokenSource();
            BtnScan.IsEnabled = false;
            BtnStop.IsEnabled = true;
            Progress.Value = 0;

            var progress = new Progress<EndpointDiscovery.ScanProgress>(p =>
            {
                Progress.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                StatusText.Text = $"스캔 [{p.Done}/{p.Total}] {p.Current}";
            });

            try
            {
                var found = await EndpointDiscovery.ScanSubnetAsync(
                    cidr, ports, progress: progress, ct: _cts.Token);
                MergeInto(found);
                StatusText.Text = $"스캔 완료 — 응답 {found.Count}건 " +
                                  $"(프로토콜 확인 {found.Count(f => f.State >= ProbeState.Reachable)}건)";
                Progress.Value = 100;
            }
            catch (OperationCanceledException) { StatusText.Text = "스캔 중지됨"; }
            catch (Exception ex) { StatusText.Text = "스캔 오류: " + ex.Message; }
            finally
            {
                _cts?.Dispose(); _cts = null;
                BtnScan.IsEnabled = true;
                BtnStop.IsEnabled = false;
            }
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ── ④ 접속 판정 ────────────────────────────────────────────────
        private async void OnProbe(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => r.IsSelected).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "[선택] 열을 체크한 항목이 없습니다.",
                    "접속 시험", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool integrated = IntegratedChk.IsChecked == true;
            string user = UserBox.Text.Trim();
            string pwd = PwdBox.Password;

            if (!integrated && user.Length == 0)
            {
                MessageBox.Show(this, "계정을 입력하거나 통합 인증을 선택하세요.\n\n" +
                    "자격증명을 자동으로 조합해 여러 번 시도하지 않습니다 — 계정 잠금을 막기 위함입니다.",
                    "접속 시험", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnProbe.IsEnabled = false;
            int done = 0;
            try
            {
                foreach (var ep in targets)
                {
                    var mode = ResolveMode(ep);
                    StatusText.Text = $"접속 시험 [{++done}/{targets.Count}] {ep.Target}";
                    await EndpointDiscovery.ProbeCredentialsAsync(ep, mode, user, pwd, integrated);
                    Progress.Value = 100.0 * done / targets.Count;
                }

                int ok = targets.Count(t => t.State == ProbeState.Connected);
                int auth = targets.Count(t => t.State == ProbeState.AuthRequired);
                int locked = targets.Count(t => t.State == ProbeState.Locked);
                StatusText.Text = $"접속 시험 완료 — 성공 {ok} / 계정필요 {auth} / 잠김 {locked}";

                if (locked > 0)
                    MessageBox.Show(this,
                        "⚠ 계정 잠김이 관측되었습니다.\n\n해당 호스트에 대한 추가 시도를 자동으로 차단했습니다.\n" +
                        "관리자가 계정 잠금을 해제한 뒤 [잠금 가드 해제]를 눌러 주세요.",
                        "계정 잠김", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { StatusText.Text = "접속 시험 오류: " + ex.Message; }
            finally { BtnProbe.IsEnabled = true; }
        }

        private void OnClearLock(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this,
                    "계정 잠금 가드를 해제합니다.\n\n실제로 계정 잠금이 풀린 뒤에만 눌러야 합니다.\n" +
                    "잠긴 상태에서 재시도하면 잠금이 연장될 수 있습니다.",
                    "잠금 가드 해제", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            EndpointDiscovery.ClearLockGuard();
            StatusText.Text = "잠금 가드 해제됨";
        }

        /// <summary>화면 콤보(자동/ODBC/다이렉트)와 후보 성격으로 접속 방식을 정한다.</summary>
        private DbConnectionMode ResolveMode(DbEndpoint ep) => ModeCombo.SelectedIndex switch
        {
            1 => DbConnectionMode.Odbc,
            2 => DbConnectionMode.Direct,
            // 자동: DSN 후보면 ODBC, 그 외에는 다이렉트
            _ => ep.Dsn.Length > 0 ? DbConnectionMode.Odbc : DbConnectionMode.Direct,
        };

        // ── 결과 반영 ──────────────────────────────────────────────────
        /// <summary>새로 찾은 후보를 기존 목록과 합쳐 중복을 제거한다.</summary>
        private void MergeInto(IEnumerable<DbEndpoint> found)
        {
            var merged = EndpointDiscovery.Merge(_rows.ToList(), found);
            _rows.Clear();
            foreach (var m in merged) _rows.Add(m);
        }

        // ── 선택 반환 ──────────────────────────────────────────────────
        private DbEndpoint? Current =>
            Grid.SelectedItem as DbEndpoint ?? _rows.FirstOrDefault(r => r.IsSelected);

        private void OnUseAsSource(object sender, RoutedEventArgs e)
        {
            if (Current is not { } ep)
            {
                MessageBox.Show(this, "목록에서 항목을 고르세요.", "소스로 사용",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mode = ResolveMode(ep);
            // 비밀번호는 창에 입력한 값을 그대로 실어 보낸다(사용자가 방금 입력한 것).
            var conn = ep.BuildConnectionString(mode, UserBox.Text.Trim(), PwdBox.Password,
                                                IntegratedChk.IsChecked == true);
            PickedSource = (ep.Kind, mode, conn);
            DialogResult = true;
        }

        private void OnUseAsTarget(object sender, RoutedEventArgs e)
        {
            if (Current is not { } ep)
            {
                MessageBox.Show(this, "목록에서 항목을 고르세요.", "타겟으로 사용",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (ep.Kind != DbProviderKind.MsSql)
            {
                MessageBox.Show(this, "타겟(NAIMS2)은 MsSQL 만 가능합니다.", "타겟으로 사용",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PickedTarget = ep.BuildConnectionString(
                DbConnectionMode.Direct, UserBox.Text.Trim(), PwdBox.Password,
                IntegratedChk.IsChecked == true);
            DialogResult = true;
        }

        private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
