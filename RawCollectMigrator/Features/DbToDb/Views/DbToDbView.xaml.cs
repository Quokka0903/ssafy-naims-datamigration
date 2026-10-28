using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace RawCollectMigrator.Features.DbToDb
{
    public partial class DbToDbView : UserControl
    {
        private readonly ObservableCollection<TableMapItem> _map = new();
        private readonly ObservableCollection<SnippetFileRow> _files = new();
        private readonly ObservableCollection<ClassScopeItem> _classItems = new();
        private TableMapItem? _current;

        /// <summary>미리보기 전/후 전시·규칙 표시에 쓰는 뷰 전용 복호화 컨텍스트.</summary>
        private readonly CryptoContext _viewCrypto = new();

        /// <summary>
        /// 테이블 매핑 목록을 Category별로 그룹화해 좌측 목록에 바인딩하고,
        /// 파일/CLASS 컬렉션을 그리드에 연결한 뒤 로드 완료 시 스니펫 루트 접근성을 점검(ProbeRoot)한다.
        /// </summary>
        public DbToDbView()
        {
            InitializeComponent();

            var view = new CollectionViewSource { Source = _map };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TableMapItem.Category)));
            TableList.ItemsSource = view.View;

            FileGrid.ItemsSource = _files;
            ClassList.ItemsSource = _classItems;
            ReloadMap();
            Loaded += (_, _) => { SyncFromSession(); ProbeRoot(); };

            // 시작 화면에서 접속·경로를 바꾸면 이 화면의 표시도 따라 바뀌게 한다.
            Session.PropertyChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(SyncFromSession));

            // [찾기:로그배치] 실행 로그는 큐에 모았다가 200ms 주기로 한 번에 화면 반영한다.
            _logTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _logTimer.Tick += (_, _) => FlushRunLog();
            _logTimer.Start();
        }

        // [찾기:접속설정] 접속 정보의 단일 원본은 AppSession(시작 화면에서 설정)이다.
        //   예전에는 이 화면의 라디오/콤보/텍스트박스가 원본이라 탭마다 값이 갈렸다.
        private static Infrastructure.AppSession Session => Infrastructure.AppSession.Current;

        private DbProviderKind SourceKind => Session.SourceKind;
        private DbConnectionMode SourceMode => Session.SourceMode;
        private string SrcConn => Session.SourceConn.Trim();
        private string DstConn => Session.TargetConn.Trim();

        /// <summary>AppSession 값을 이 화면의 표시용 칸에 반영한다(읽기 전용 전시 + CLASSIFICATION 경로).</summary>
        private void SyncFromSession()
        {
            SrcConnBox.Text = $"[{DbProvider.DisplayName(Session.SourceKind)} · {DbProvider.ModeName(Session.SourceMode)}]  "
                            + Infrastructure.AppSession.StripPassword(Session.SourceConn);
            DstConnBox.Text = Infrastructure.AppSession.StripPassword(Session.TargetConn);
            if (NbRootBox.Text != Session.ClassificationRoot)
                NbRootBox.Text = Session.ClassificationRoot;
        }

        /// <summary>매핑 목록을 기본값(TableMapRegistry.Defaults)으로 되돌리고 작업 패널을 감춘다. 선택 상태·상세 로드 결과가 초기화된다.</summary>
        private void ReloadMap()
        {
            _map.Clear();
            foreach (var m in TableMapRegistry.Defaults()) _map.Add(m);
            _current = null;
            WorkspacePanel.Visibility = Visibility.Collapsed;
            NoSelectionHint.Visibility = Visibility.Visible;
        }

        private void OnReloadMap(object sender, RoutedEventArgs e) => ReloadMap();

        // ── 소스 DB 종류 ──
        // [찾기:연결탐색창]
        /// <summary>
        /// 연결 탐색 창을 연다. 로컬 ODBC·TNS 카탈로그, 망 자동 발견(UDP 1434), 서브넷 스캔,
        /// 자격증명 접속 판정으로 후보를 찾아 고른 항목을 소스·타겟 접속문자열에 채운다.
        /// 소스로 고르면 DB 종류·접속 방식 라디오/콤보도 함께 맞춘다.
        /// </summary>
        private void OnBrowseOdbc(object sender, RoutedEventArgs e)
        {
            var win = new EndpointBrowserWindow { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true) return;

            // 고른 후보는 화면이 아니라 AppSession(단일 원본)에 반영한다.
            if (win.PickedSource is { } src)
            {
                Session.SourceKind = src.Kind;
                Session.SourceMode = src.Mode;
                Session.SourceConn = src.ConnStr;
            }

            if (!string.IsNullOrEmpty(win.PickedTarget))
                Session.TargetConn = win.PickedTarget!;

            Session.Save();
            SyncFromSession();
        }

        private async void OnTestSource(object sender, RoutedEventArgs e)
            => await TestConnAsync(SourceKind, SourceMode, SrcConn,
                $"소스 ({DbProvider.DisplayName(SourceKind)} · {DbProvider.ModeName(SourceMode)})");

        private async void OnTestTarget(object sender, RoutedEventArgs e)
            => await TestConnAsync(DbProviderKind.MsSql, DbConnectionMode.Direct, DstConn, "타겟 (NAIMS2 / MsSQL)");

        /// <summary>
        /// 지정한 DB에 실제로 접속해 버전 SQL을 실행하고 성공/실패를 MessageBox로 알린다(DB 접근·모달 UI).
        /// 예외는 삼켜서 실패 메시지로만 전시한다.
        /// </summary>
        /// <param name="kind">접속할 DB 종류(Oracle/MsSQL).</param>
        /// <param name="connStr">시험할 커넥션 문자열.</param>
        /// <param name="label">메시지에 표기할 대상 이름(예: "소스 (Oracle)").</param>
        private async Task TestConnAsync(DbProviderKind kind, DbConnectionMode mode, string connStr, string label)
        {
            try
            {
                await using var conn = DbProvider.CreateConnection(kind, mode, connStr);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = DbProvider.VersionSql(kind);
                var v = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
                var head = v.Split('\n').FirstOrDefault()?.Trim() ?? "";
                MessageBox.Show(Window.GetWindow(this), $"✅ {label} 연결 성공\n\n{head}",
                    "DB 연결 시험", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"❌ {label} 연결 실패\n\n{ex.Message}",
                    "DB 연결 시험", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════ 이관 실행 ══════════
        private System.Threading.CancellationTokenSource? _runCts;

        /// <summary>
        /// 화면 입력(커넥션·최대 행수·스니펫 루트 3곳·체크된 CLASS)을 모아 실행 옵션을 만든다.
        /// CLASS를 하나도 안 고르면 빈 필터가 되어 전체 이관(기존 동작)으로 동작한다.
        /// 최대 행수 입력이 비어있지 않은데 숫자가 아니면 경고 후 null 을 돌려 실행을 중단시킨다.
        /// </summary>
        /// <param name="mode">DryRun/Trial/Commit 실행 모드.</param>
        /// <returns>실행기·사전점검에 전달할 <see cref="DbToDbOptions"/>. 입력 오류면 null.</returns>
        private DbToDbOptions? BuildOptions(RunMode mode)
        {
            var maxRowsText = MaxRowsBox.Text.Trim();
            int maxRows = 0;
            if (maxRowsText.Length > 0 && !int.TryParse(maxRowsText, out maxRows))
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"테이블당 최대 행수 '{maxRowsText}' 를 숫자로 해석할 수 없습니다.\n" +
                    "비우거나 0(무제한) 또는 정수를 입력하세요.",
                    "실행 옵션", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            // 체크된 CLASS_ID 만 필터로 전달. 아무것도 안 고르면 빈 컬렉션 → 전체 이관(기존 동작).
            var classFilter = _classItems.Where(c => c.IsChecked).Select(c => c.ClassId).ToArray();

            return new DbToDbOptions
            {
                SourceKind = SourceKind,
                SourceMode = SourceMode,
                SourceConnStr = SrcConn,
                TargetConnStr = DstConn,
                Mode = mode,
                MaxRowsPerTable = maxRows,
                UseDecryption = UseDecryptionChk?.IsChecked == true,   // [찾기:복호토글]
                ClassificationRoot = NbRootBox.Text.Trim(),   // [NAIMS-ISSUE:SNIPPET-ROOTS] 단일 루트
                ClassFilter = classFilter,
                RebuildTargets = LoadPolicyCombo?.SelectedIndex == 1,
                RebuildConfirmDatabase = RebuildConfirmBox?.Text?.Trim() ?? "",
                EmptyOnMissingFile = EmptyOnMissingChk?.IsChecked == true,
                ManualFkMap = LoadManualFkMap(),
                // [찾기:파일부재해결] 저장된 것이 없으면 빈 표 — 종전과 똑같이 동작한다.
                MissingFileActions = MissingFileResolveWindow.LoadSaved(),
                // [찾기:충돌해결] 저장된 것이 없으면 빈 표 — 전부 '유지'(종전 동작).
                ConflictDecisions = ConflictResolveWindow.LoadSaved(),
            };
        }

        // [NAIMS-ISSUE:REBUILD-VS-KEY]
        /// <summary>
        /// 적재 방식이 바뀌면 그 뜻을 화면에 분명히 남긴다.
        /// '전량 재구축' 은 타겟을 지우는 방식이라, 고른 순간 무슨 일이 벌어지는지 알아야 한다.
        /// (실제 삭제 승인은 커밋 직전 게이트에서 서버·DB 를 확인하며 다시 받는다.)
        /// </summary>
        private void OnLoadPolicyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RunStatus == null) return;
            bool rebuild = LoadPolicyCombo?.SelectedIndex == 1;

            // [NAIMS-ISSUE:REBUILD-GUARD] 지울 DB 이름을 직접 타이핑해야 재구축이 열린다.
            //   체크박스는 습관적으로 눌리지만, 이름은 대상을 한 번 읽어야 칠 수 있다.
            //   비워 두면 실행기가 "확인 불일치" 로 거부한다 — UI 를 우회해도 마찬가지다.
            if (RebuildConfirmBox != null)
            {
                RebuildConfirmBox.IsEnabled = rebuild;
                if (!rebuild) RebuildConfirmBox.Text = "";
            }

            var db = DbToDbOptions.DatabaseOf(DstConn);
            RunStatus.Text = rebuild
                ? $"⚠ 전량 재구축 — [{db}] 의 이관 대상 STD_* 를 비우고 처음부터 넣습니다. "
                  + $"진행하려면 오른쪽 칸에 '{db}' 를 입력하세요. (리허설에서는 실제로 지우지 않습니다)"
                : "멱등 보충 — 기존 행은 재사용하고 없는 것만 추가합니다.";
        }

        // [찾기:사후조회]
        /// <summary>
        /// 지난 세션 폴더를 골라 결과 창을 다시 연다.
        ///
        /// 이관 결과를 "돌린 직후" 에만 볼 수 있으면, 확인이 필요할 때는 이미 창이 닫혀 있다.
        /// 필요한 기록은 세션 폴더에 이미 다 있으므로(06_mapping_records.json) 그것으로 되살린다.
        /// DB 를 다시 읽는 것이 아니라 그때 무슨 일이 있었는지를 읽는 것이라, 그 뒤 DB 가 바뀌어도
        /// 당시 짝이 그대로 나온다 — 대조는 지금 DB 를 조회해 하므로 차이가 있으면 그대로 보인다.
        /// </summary>
        private void OnOpenPastResult(object sender, RoutedEventArgs e)
        {
            var sessions = SessionResultLoader.List(SessionResultLoader.DefaultLogsRoot);
            var usable = sessions.Where(s => s.HasRecords).ToList();
            if (usable.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "되살릴 수 있는 세션이 없습니다.\n\n" +
                    "커밋까지 끝난 세션만 레코드 짝(06_mapping_records.json)을 남깁니다. " +
                    "리허설만 한 세션은 대조할 짝이 없어 결과 창을 열 수 없습니다.",
                    "지난 이관 결과", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "세션 폴더의 06_mapping_records.json 을 고르세요",
                Filter = "레코드 매핑 (06_mapping_records.json)|06_mapping_records.json|모든 파일 (*.*)|*.*",
                InitialDirectory = usable[0].Dir,
                FileName = "06_mapping_records.json",
            };
            if (dlg.ShowDialog() != true) return;

            var dir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            var rep = SessionResultLoader.Load(dir, out var err);
            if (rep == null)
            {
                MessageBox.Show(Window.GetWindow(this), err, "지난 이관 결과", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 대조는 '지금' 접속된 소스·타겟으로 한다 — 기록은 그때 것이지만, 확인하려는 것은 현재 DB 다.
                if (BuildOptions(RunMode.DryRun) is not { } opt)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "현재 접속 설정을 읽지 못했습니다 — 소스·타겟 연결을 먼저 확인하세요.");
                    return;
                }
                new MigrationResultWindow(rep, opt) { Owner = Window.GetWindow(this) }.Show();
                RunStatus.Text = $"지난 결과를 열었습니다 — {System.IO.Path.GetFileName(dir)} " +
                                 $"(표 {rep.Tables.Count}개 · 짝 {rep.Tables.Sum(t => t.IdPairs.Count):N0}건). " +
                                 "대조는 지금 접속된 소스·타겟으로 수행됩니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "결과 창을 열지 못했습니다: " + ex.Message);
            }
        }

        // [찾기:고아해결] [NAIMS-ISSUE:MANUAL-FK]
        /// <summary>
        /// 사람이 해결 창에서 정한 (행 → 부모) 지정을 읽어 실행에 싣는다.
        /// 파일이 없으면 빈 표 — 지금까지와 똑같이 동작한다(기능을 안 쓰면 아무것도 달라지지 않는다).
        /// </summary>
        private static IReadOnlyDictionary<string, long> LoadManualFkMap()
        {
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "config", "manual_fk.json");
                if (System.IO.File.Exists(path))
                    return System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, long>>(System.IO.File.ReadAllText(path))
                        ?? new Dictionary<string, long>();
            }
            catch { /* 읽기 실패는 '지정 없음' 과 같게 다룬다 — 이관을 막을 이유가 아니다 */ }
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        // ══════════ 복호화 규칙 표시·전/후 미리보기 ══════════

        /// <summary>현재 소스 종류·커넥션으로 뷰 복호화 컨텍스트를 확인(Probe)한다.</summary>
        private async Task EnsureViewCryptoAsync()
        {
            _viewCrypto.DecryptionEnabled = UseDecryptionChk?.IsChecked == true;   // [찾기:복호토글]
            try { await _viewCrypto.ProbeAsync(SourceKind, SourceMode, SrcConn); }
            catch { /* 규칙 표시는 부가 기능이라 실패해도 조회를 막지 않는다 */ }
        }

        /// <summary>
        /// [찾기:복호토글] '복호화 불러오기' 체크 변경 시: 꺼져 있으면 [Oracle 암호화 추출]을 비활성화하고
        /// 규칙 표시를 '원문 그대로'로 바꾼다. 이미 평문으로 dump 된 소스를 그대로 이관할 때 쓴다.
        /// </summary>
        private void OnUseDecryptionChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool on = UseDecryptionChk?.IsChecked == true;
            if (BtnOracleCrypto != null) BtnOracleCrypto.IsEnabled = on;
            if (CryptoRuleInfo != null)
                CryptoRuleInfo.Text = on
                    ? "복호화 사용 — 소스 종류 선택 후 [🔐 Oracle 암호화 추출] 또는 테이블 선택 시 규칙이 확인됩니다."
                    : "🔓 복호화 끔 — 이름 컬럼을 복호화하지 않고 원문 그대로 이관합니다(이미 평문으로 dump 된 소스용).";
        }

        /// <summary>복호화 규칙 문구를 화면에 갱신한다.</summary>
        private void UpdateCryptoRuleInfo()
        {
            CryptoRuleInfo.Text = $"[{DbProvider.DisplayName(_viewCrypto.SourceKind)}] "
                                + _viewCrypto.RuleDescription();
        }

        // ══════════ 이관 범위: CLASS(함정급) 단위 선택 ══════════

        /// <summary>선택된 소스 종류·커넥션으로 CLASS 목록을 읽어 체크박스 리스트로 표시한다.</summary>
        private async void OnLoadClassList(object sender, RoutedEventArgs e) => await LoadClassListAsync();

        /// <summary>CLASS 목록 조회 본체(버튼·자동 시작 공용).</summary>
        private async Task LoadClassListAsync()
        {
            BtnLoadClasses.IsEnabled = false;
            ClassScopeInfo.Text = "CLASS 목록 조회 중…";
            try
            {
                var resolver = new ClassScopeResolver(SourceKind, SourceMode, SrcConn);
                var list = await resolver.LoadClassListAsync();

                // 기존 체크 상태를 유지한 채 목록을 갱신한다.
                var prevChecked = _classItems.Where(c => c.IsChecked).Select(c => c.ClassId).ToHashSet();
                _classItems.Clear();
                foreach (var (id, label) in list)
                {
                    var item = new ClassScopeItem { ClassId = id, Label = label, IsChecked = prevChecked.Contains(id) };
                    item.PropertyChanged += (_, _) => UpdateClassScopeInfo();
                    _classItems.Add(item);
                }

                ClassListBorder.Visibility = _classItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                BtnClassAll.IsEnabled = BtnClassNone.IsEnabled = _classItems.Count > 0;
                UpdateClassScopeInfo();
            }
            catch (Exception ex)
            {
                ClassScopeInfo.Text = "CLASS 목록 조회 실패: " + ex.Message;
            }
            finally
            {
                BtnLoadClasses.IsEnabled = true;
            }
        }

        private void OnSelectAllClasses(object sender, RoutedEventArgs e)
        {
            foreach (var c in _classItems) c.IsChecked = true;
            UpdateClassScopeInfo();
        }

        private void OnClearClasses(object sender, RoutedEventArgs e)
        {
            foreach (var c in _classItems) c.IsChecked = false;
            UpdateClassScopeInfo();
        }

        /// <summary>선택 개수에 따라 안내 문구를 갱신한다.</summary>
        private void UpdateClassScopeInfo()
        {
            int n = _classItems.Count(c => c.IsChecked);
            ClassScopeInfo.Text = n == 0
                ? $"전체 {_classItems.Count}개 CLASS — 미선택이므로 전체 이관(기존 동작)."
                : $"{n}개 CLASS 선택 — 해당 함정급 서브트리 + 참조 공유데이터만 이관합니다.";
        }

        /// <summary>실행 로그 파일 기록기. 실행 1회당 하나, 화면 로그와 같은 내용을 남긴다.</summary>
        private System.IO.StreamWriter? _logWriter;

        // [찾기:로그배치] 줄마다 동기 Dispatcher.Invoke 하면 대량 로그에서 UI가 멎는다 —
        // 백그라운드는 큐에만 넣고(파일은 그 자리에서 기록), DispatcherTimer 가 배치로 화면에 반영한다.
        /// <summary>화면 반영 대기 중인 로그 줄 큐. <see cref="_logLock"/> 으로 보호한다.</summary>
        private readonly Queue<string> _logQueue = new();

        /// <summary>로그 큐·파일 기록기(_logWriter)를 함께 보호하는 락.</summary>
        private readonly object _logLock = new();

        /// <summary>큐를 200ms 주기로 비워 화면에 반영하는 타이머(UI 스레드).</summary>
        private readonly System.Windows.Threading.DispatcherTimer _logTimer;

        /// <summary>화면 로그 텍스트 상한(약 2MB). 초과 시 앞부분을 잘라낸다(파일 로그는 전체 보존).</summary>
        private const int RunLogMaxChars = 2 * 1024 * 1024;

        /// <summary>큐에 쌓인 로그 줄을 한 번에 화면 로그창으로 옮긴다(UI 스레드 전용).</summary>
        private void FlushRunLog()
        {
            string chunk;
            lock (_logLock)
            {
                if (_logQueue.Count == 0) return;
                var sb = new System.Text.StringBuilder();
                while (_logQueue.Count > 0) sb.Append(_logQueue.Dequeue()).Append("\r\n");
                chunk = sb.ToString();
            }

            RunLog.AppendText(chunk);
            // 화면 로그가 과도하게 길어지면 앞부분을 절반 잘라 UI 렌더 부하를 막는다.
            if (RunLog.Text.Length > RunLogMaxChars)
                RunLog.Text = "… (앞부분 생략 — 전체는 로그 파일 참조)\r\n" + RunLog.Text[^(RunLogMaxChars / 2)..];
            RunLog.ScrollToEnd();
        }

        /// <summary>화면 로그창과 대기 큐를 함께 비운다(실행 시작 시 이전 실행 잔여 줄 제거).</summary>
        private void ClearRunLog()
        {
            lock (_logLock) _logQueue.Clear();
            RunLog.Clear();
        }

        /// <summary>실행 로그를 담는 폴더 (exe 옆 logs\).</summary>
        private static string LogDir => System.IO.Path.Combine(AppContext.BaseDirectory, "logs");

        // [NAIMS-ISSUE:RUN-LOG-FILE] DB→DB 실행 로그 위치(exe\logs)
        /// <summary>가장 최근 실행 로그. 경로가 고정이라 매번 찾을 필요가 없다.</summary>
        public static string LastLogPath => System.IO.Path.Combine(LogDir, "dbtodb_last.log");

        /// <summary>
        /// 실행 시작 시 로그 파일을 연다. 고정 경로(dbtodb_last.log)는 매번 덮어쓰고,
        /// 시각이 박힌 사본을 함께 남겨 이전 실행 기록이 사라지지 않게 한다.
        /// </summary>
        private void StartFileLog(string label)
        {
            EndFileLog();
            try
            {
                System.IO.Directory.CreateDirectory(LogDir);
                var w = new System.IO.StreamWriter(
                    LastLogPath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                w.WriteLine($"# {label} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                lock (_logLock) _logWriter = w;   // 백그라운드 RunLogLine 과 경합 방지
                RunLog.AppendText($"[로그 파일] {LastLogPath}\r\n");
            }
            catch (Exception ex)
            {
                lock (_logLock) _logWriter = null;
                RunLog.AppendText($"[로그 파일] 기록 불가 — {ex.Message}\r\n");
            }
        }

        /// <summary>실행 종료 시 파일을 닫고, 시각이 박힌 사본을 보관한다.</summary>
        private void EndFileLog(string? label = null)
        {
            System.IO.StreamWriter? w;
            lock (_logLock)   // 백그라운드 RunLogLine 이 Dispose 된 writer 를 만지지 않게 한다
            {
                w = _logWriter;
                _logWriter = null;
            }
            if (w == null) return;
            try
            {
                w.Flush();
                w.Dispose();
                if (label != null)
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    System.IO.File.Copy(LastLogPath,
                        System.IO.Path.Combine(LogDir, $"dbtodb_{stamp}_{label}.log"), overwrite: true);
                }
            }
            catch { /* 로그 보관 실패가 이관을 막을 이유는 없다 */ }
        }

        /// <summary>
        /// 한 줄 로그를 기록한다. 파일은 호출 스레드(백그라운드)에서 즉시 기록하고,
        /// 화면 반영은 큐에만 넣어 <see cref="FlushRunLog"/>(200ms 배치)에 맡긴다.
        /// [찾기:로그배치] 줄마다 동기 Dispatcher.Invoke 하던 병목을 제거한 경로.
        /// </summary>
        private void RunLogLine(string s)
        {
            lock (_logLock)
            {
                _logQueue.Enqueue(s);
                try { _logWriter?.WriteLine(s); } catch { /* 파일 오류로 실행을 멈추지 않는다 */ }
            }
        }

        /// <summary>
        /// 사전 점검(Preflight)을 실행한다. 옵션을 만들어 백그라운드에서 <see cref="DbToDbPreflight"/>를 돌리며,
        /// 실행 중에는 버튼을 잠그고 취소 토큰·파일 로그를 관리한다. 전체/부분 이관 가능 여부를 상태줄에 표시한다.
        /// 이미 다른 실행이 진행 중이면 무시한다. (DB 접근·파일 로그·UI 상태 변경·취소 지원)
        /// </summary>
        private async void OnPreflight(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) return;
            if (BuildOptions(RunMode.DryRun) is not { } opt) return;   // 행수 입력 오류 — 경고 후 중단
            _runCts = new System.Threading.CancellationTokenSource();
            BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled = BtnRealRun.IsEnabled = false;
            BtnStop.IsEnabled = true;
            ClearRunLog();
            StartFileLog("환경 점검");
            RunStatus.Text = "환경 점검 중…";
            try
            {
                var pf = new DbToDbPreflight(opt, RunLogLine);
                var rep = await Task.Run(() => pf.RunAsync(_runCts.Token));
                RunStatus.Text = rep.FullyReady ? "점검 완료 — 전체 이관 준비 완료"
                                                : "점검 완료 — 부분 이관 가능 (로그 확인)";
            }
            catch (OperationCanceledException) { RunStatus.Text = "중지됨"; }
            catch (Exception ex) { RunStatus.Text = "오류"; RunLogLine("!! " + ex.Message); }
            finally
            {
                _runCts?.Dispose(); _runCts = null;
                BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled = BtnRealRun.IsEnabled = true;
                BtnStop.IsEnabled = false;
                EndFileLog("preflight");
            }
        }

        /// <summary>
        /// 현장 Oracle 접속 시: CLASSES/VESSEL 이름(RAW) 복호화 함수/식을 찾아 세션에 적용한다.
        /// 성공하면 이후 Trial/Commit 에서 Oracle 이름이 평문으로 이관된다.
        /// </summary>
        private async void OnOracleCryptoDiscover(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) return;
            if (BuildOptions(RunMode.DryRun) is not { } opt) return;   // 행수 입력 오류 — 경고 후 중단
            if (opt.SourceKind != DbProviderKind.Oracle)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "소스 종류를 Oracle 로 선택한 뒤 사용하세요.\n" +
                    "(CLASSES/VESSEL 이름 RAW 복호화는 Oracle 소스에만 해당)",
                    "Oracle 암호화 추출", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _runCts = new System.Threading.CancellationTokenSource();
            BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled =
                BtnRealRun.IsEnabled = BtnOracleCrypto.IsEnabled = false;
            BtnStop.IsEnabled = true;
            ClearRunLog();
            StartFileLog("Oracle 암호화 추출");
            RunStatus.Text = "Oracle 암호화 추출 중…";
            try
            {
                bool ok = await Task.Run(() =>
                    CryptoContext.DiscoverOracleCryptoAsync(opt.SourceConnStr, opt.SourceMode, RunLogLine, _runCts.Token));
                RunStatus.Text = ok ? "암호화 추출 성공 — 이관에 적용됨" : "암호화 추출 실패 (로그 확인)";
                await EnsureViewCryptoAsync();
                UpdateCryptoRuleInfo();
                MessageBox.Show(Window.GetWindow(this),
                    ok ? "✅ 복호화 식을 찾아 적용했습니다.\n이제 Oracle 소스로 CLASSES/VESSEL 이름이 평문으로 이관됩니다.\n(환경 점검·시험 실행으로 확인하세요)"
                       : "❌ 자동 추출에 실패했습니다.\n로그의 함수 소스 덤프를 확인하고, 필요하면 복호화 식을 수동 지정해야 합니다.",
                    "Oracle 암호화 추출", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (OperationCanceledException) { RunStatus.Text = "중지됨"; }
            catch (Exception ex) { RunStatus.Text = "오류"; RunLogLine("!! " + ex.Message); }
            finally
            {
                _runCts?.Dispose(); _runCts = null;
                BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled =
                    BtnRealRun.IsEnabled = BtnOracleCrypto.IsEnabled = true;
                BtnStop.IsEnabled = false;
                EndFileLog("oracle_crypto");
            }
        }

        private async void OnDryRunPreview(object sender, RoutedEventArgs e)
            => await RunMigrationAsync(RunMode.DryRun);

        /// <summary>시험 실행: 확인 후 실제 INSERT를 트랜잭션 안에서 수행하고 끝에 롤백한다. 타겟에 영구 반영되지 않으며 취소 시 아무 것도 하지 않는다.</summary>
        private async void OnTrialMigrate(object sender, RoutedEventArgs e)
        {
            var ans = MessageBox.Show(Window.GetWindow(this),
                "실제 INSERT 를 트랜잭션 안에서 실행한 뒤 마지막에 롤백합니다.\n" +
                "타겟에 영구 반영되지 않으며, 실 INSERT 경로(제약·형변환·IDENTITY)를 안전하게 검증합니다.\n\n계속할까요?",
                "시험 실행 (트랜잭션 롤백)", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ans != MessageBoxResult.Yes) return;
            await RunMigrationAsync(RunMode.Trial);
        }

        /// <summary>실제 이관: 경고 확인 후 NAIMS2에 INSERT하고 커밋한다(되돌릴 수 없음). 취소 시 실행하지 않는다.</summary>
        private async void OnRealMigrate(object sender, RoutedEventArgs e)
        {
            var ans = MessageBox.Show(Window.GetWindow(this),
                "실제로 NAIMS2 에 INSERT 하고 커밋합니다. 되돌릴 수 없습니다.\n\n" +
                "미리보기(DryRun)·시험 실행(Trial)으로 먼저 확인하셨습니까? 계속하시겠습니까?",
                "실제 이관 실행 (커밋)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
            await RunMigrationAsync(RunMode.Commit);
        }

        private void OnStopMigrate(object sender, RoutedEventArgs e) => _runCts?.Cancel();

        // ══════════ 덤프 복구 (오프라인 Oracle .dmp → 대상 스키마) [찾기:덤프복구] ══════════

        /// <summary>복구 로그 한 줄을 UI 스레드로 마샬링해 복구 로그창에 덧붙인다(자동 스크롤).</summary>
        private void RestoreLogLine(string s) =>
            Dispatcher.Invoke(() =>
            {
                RestoreLog.AppendText(s + "\r\n");
                RestoreLog.ScrollToEnd();
            });

        /// <summary>복구할 .dmp 파일을 고르고, 고르는 즉시 헤더로 종류를 판별해 상태줄에 표시한다.</summary>
        private void OnPickDump(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "복구할 Oracle 덤프(.dmp) 선택",
                Filter = "Oracle 덤프 (*.dmp)|*.dmp|모든 파일 (*.*)|*.*",
                InitialDirectory = System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(DumpPathBox.Text.Trim()) ?? "")
                    ? System.IO.Path.GetDirectoryName(DumpPathBox.Text.Trim())
                    : null
            };
            if (dlg.ShowDialog() == true)
            {
                DumpPathBox.Text = dlg.FileName;
                var kind = DumpRestore.DetectKind(dlg.FileName);
                RestoreStatus.Text = "판별: " + DumpRestore.KindLabel(kind);
            }
        }

        /// <summary>테이블별 .DMP 가 든 폴더를 골라 일괄 복구 대상으로 지정한다. 첫 .dmp 헤더로 종류를 판별한다.</summary>
        private void OnPickDumpFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "테이블별 .DMP 가 든 폴더 선택",
                InitialDirectory = System.IO.Directory.Exists(DumpPathBox.Text.Trim())
                    ? DumpPathBox.Text.Trim()
                    : (System.IO.Path.GetDirectoryName(DumpPathBox.Text.Trim()) ?? "")
            };
            if (dlg.ShowDialog() == true)
            {
                DumpPathBox.Text = dlg.FolderName;
                var files = DumpRestore.ResolveDumpFiles(dlg.FolderName);
                if (files.Count == 0)
                {
                    RestoreStatus.Text = "폴더에 .dmp 가 없습니다.";
                    return;
                }
                var kind = DumpRestore.DetectKind(files[0]);
                RestoreStatus.Text = $"판별: {DumpRestore.KindLabel(kind)}  ·  {files.Count}개 파일";
            }
        }

        /// <summary>현재 지정된 덤프 파일의 종류를 헤더로 판별해 상태줄에 표시한다.</summary>
        private void OnDetectDumpKind(object sender, RoutedEventArgs e)
        {
            var path = DumpPathBox.Text.Trim();
            var files = DumpRestore.ResolveDumpFiles(path);
            if (files.Count == 0)
            {
                RestoreStatus.Text = "덤프 파일(또는 .dmp 폴더)을 먼저 지정하세요.";
                return;
            }
            var kind = DumpRestore.DetectKind(files[0]);
            var home = DumpRestore.FindOracleHome();
            RestoreStatus.Text = $"판별: {DumpRestore.KindLabel(kind)}  ·  {files.Count}개 파일"
                               + (home != null ? "   ·   ORACLE_HOME ✓" : "   ·   ⚠ ORACLE_HOME 없음");
            RestoreLogLine($"── 종류 판별: {DumpRestore.KindLabel(kind)}  ({files.Count}개 파일)");
            RestoreLogLine(home != null ? "── ORACLE_HOME = " + home : "!! ORACLE_HOME 을 찾지 못했습니다.");
        }

        /// <summary>화면 콤보(0=없음/1=TRUNCATE/2=DROP)를 재실행 선정리 모드로 변환.</summary>
        private RestoreCleanMode SelectedCleanMode() => CleanModeCombo?.SelectedIndex switch
        {
            1 => RestoreCleanMode.TruncateTargetTables,
            2 => RestoreCleanMode.DropTargetTables,
            _ => RestoreCleanMode.None,
        };

        /// <summary>화면 콤보(0=자동/1=classic/2=DataPump)를 강제 종류로 변환.</summary>
        private OracleDumpKind ForcedDumpKind() => DumpKindCombo?.SelectedIndex switch
        {
            1 => OracleDumpKind.ClassicExp,
            2 => OracleDumpKind.DataPump,
            _ => OracleDumpKind.Unknown,
        };

        /// <summary>
        /// 덤프 복구를 실행한다. 화면 입력을 옵션으로 모아 백그라운드에서 imp/impdp 를 대리 실행하고
        /// 출력을 복구 로그창에 스트리밍한다. 다른 실행이 진행 중이면 무시한다(취소 지원).
        /// </summary>
        private async void OnRestoreDump(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) return;   // 이관/추출 등 다른 실행 중이면 무시

            var opt = new DumpRestoreOptions
            {
                DumpPath = DumpPathBox.Text.Trim(),
                DbaConnect = DbaConnBox.Text.Trim(),
                SourceSchema = SrcSchemaBox.Text.Trim(),
                TargetSchema = string.IsNullOrWhiteSpace(TgtSchemaBox.Text) ? "LEGACYNAIMS" : TgtSchemaBox.Text.Trim(),
                ForcedKind = ForcedDumpKind(),
                CleanMode = SelectedCleanMode(),
            };

            var files = DumpRestore.ResolveDumpFiles(opt.DumpPath);
            if (files.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "복구할 .dmp 파일(또는 .dmp 가 든 폴더)을 먼저 지정하세요.",
                    "덤프 복구", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string cleanText = opt.CleanMode switch
            {
                RestoreCleanMode.DropTargetTables => $"⚠ 선정리: '{opt.TargetSchema}' 스키마의 기존 테이블을 DROP 후 복구(dropdb.txt 대응).",
                RestoreCleanMode.TruncateTargetTables => $"선정리: '{opt.TargetSchema}' 스키마의 기존 테이블을 TRUNCATE 후 복구.",
                _ => "선정리 없음(classic 은 append — 기존 데이터가 있으면 중복될 수 있음).",
            };
            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"덤프를 '{opt.TargetSchema}' 스키마로 복구합니다.\n" +
                $"대상: {files.Count}개 파일" +
                (System.IO.Directory.Exists(opt.DumpPath) ? $" (폴더: {System.IO.Path.GetFileName(opt.DumpPath.TrimEnd('\\'))})\n" : $" ({System.IO.Path.GetFileName(files[0])})\n") +
                $"{cleanText}\n\n계속할까요?",
                "덤프 복구 실행", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _runCts = new System.Threading.CancellationTokenSource();
            BtnRestoreDump.IsEnabled = false;
            BtnRestoreStop.IsEnabled = true;
            BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled =
                BtnRealRun.IsEnabled = BtnOracleCrypto.IsEnabled = false;
            RestoreLog.Clear();
            RestoreStatus.Text = "덤프 복구 중…";
            try
            {
                bool ok = await Task.Run(() => DumpRestore.RunAsync(opt, RestoreLogLine, _runCts.Token));
                RestoreStatus.Text = ok ? "복구 완료 — 로그 확인 후 [연결 시험]"
                                        : "복구 실패/경고 — 로그 확인";
                MessageBox.Show(Window.GetWindow(this),
                    ok ? "✅ 덤프 복구가 완료되었습니다(경고가 있을 수 있음 — 로그 확인).\n[연결 시험] → [Oracle 암호화 추출] → 시험 실행 순으로 진행하세요."
                       : "⚠ 복구가 실패로 끝났습니다.\n복구 로그와 덤프 옆의 *_imp.log 를 확인하세요.",
                    "덤프 복구", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (OperationCanceledException) { RestoreStatus.Text = "중지됨"; RestoreLogLine("── 사용자 중지 ──"); }
            catch (Exception ex) { RestoreStatus.Text = "오류"; RestoreLogLine("!! " + ex.Message); }
            finally
            {
                _runCts?.Dispose(); _runCts = null;
                BtnRestoreDump.IsEnabled = true;
                BtnRestoreStop.IsEnabled = false;
                BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled =
                    BtnRealRun.IsEnabled = BtnOracleCrypto.IsEnabled = true;
            }
        }

        /// <summary>
        /// DryRun/Trial/Commit 공통 실행 루틴. 취소 토큰·파일 로그를 준비하고 버튼을 잠근 뒤
        /// 백그라운드에서 <see cref="DbToDbExecutor"/>를 돌리고, 결과(적재/재사용/건너뜀/오류)를 상태줄에 요약한다.
        /// 이미 실행 중이면 무시한다. (DB 접근·파일 로그·UI 상태 변경·취소 지원)
        /// </summary>
        /// <param name="mode">실행 모드. Commit만 타겟에 영구 반영된다.</param>
        private async Task RunMigrationAsync(RunMode mode)
        {
            if (_runCts != null) return;   // 이미 실행 중
            if (BuildOptions(mode) is not { } opt) return;   // 행수 입력 오류 — 경고 후 중단
            _runCts = new System.Threading.CancellationTokenSource();
            BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled = BtnRealRun.IsEnabled = false;
            BtnStop.IsEnabled = true;
            ClearRunLog();
            StartFileLog(mode switch
            {
                RunMode.DryRun => "미리보기(DryRun)",
                RunMode.Trial => "시험 실행(Trial, 롤백)",
                _ => "실제 이관(Commit)",
            });
            RunStatus.Text = mode switch
            {
                RunMode.DryRun => "미리보기 실행 중…",
                RunMode.Trial => "시험 실행 중 (롤백)…",
                _ => "실제 이관 실행 중…",
            };

            var exec = new DbToDbExecutor(opt, RunLogLine);
            try
            {
                var rep = await Task.Run(() => exec.RunAsync(_runCts.Token));
                string tag = mode switch
                {
                    RunMode.DryRun => "미리보기(계획)",
                    RunMode.Trial => "시험(롤백됨)",
                    _ => "이관 완료(커밋)",
                };
                RunStatus.Text = $"{tag} — 적재 {rep.TotalInserted} / 재사용 {rep.TotalReused} / 건너뜀 {rep.TotalSkipped} / 오류 {rep.TotalErrors}";

                // [찾기:이관결과창] 실제 이관(커밋) 성공 시 테이블별 매핑표 결과 창을 띄운다.
                if (mode == RunMode.Commit)
                {
                    var win = new MigrationResultWindow(rep, opt) { Owner = Window.GetWindow(this) };
                    win.Show();
                }
            }
            catch (OperationCanceledException)
            {
                RunStatus.Text = "중지됨";
                RunLogLine("── 사용자 중지 ──");
            }
            catch (Exception ex)
            {
                RunStatus.Text = "오류";
                RunLogLine("!! " + ex.Message);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled = BtnRealRun.IsEnabled = true;
                BtnStop.IsEnabled = false;
                EndFileLog(mode switch
                {
                    RunMode.DryRun => "dryrun",
                    RunMode.Trial => "trial",
                    _ => "commit",
                });
            }
        }

        // [찾기:자동스캔]
        /// <summary>
        /// 작업 화면 진입 직후 이 탭을 준비 상태로 만든다.
        ///   · CLASSIFICATION 루트 접근성 재점검
        ///   · Oracle 이름컬럼 복호화 식 자동 추출 (Oracle + 복호화 사용 시)
        ///   · CLASS(함정급) 목록 자동 조회 — 부분 이관 범위를 바로 고를 수 있게
        ///   · 테이블별 소스/타겟 행수 자동 조회
        /// 버튼을 눌러야만 채워지던 것들을, 열자마자 준비된 상태로 만든다.
        /// 전부 백그라운드·비모달이며 실패해도 화면을 막지 않는다(각 버튼으로 재시도 가능).
        /// </summary>
        public async Task AutoStartAsync()
        {
            if (_autoStarted) return;
            _autoStarted = true;

            SyncFromSession();
            ProbeRoot();

            // 복호화 식 추출 → 이름 컬럼이 평문으로 읽히는 전제를 먼저 만든다.
            try { await AutoDiscoverCryptoAsync(); } catch { }

            // CLASS 목록 → 범위 선택 UI 가 곧바로 쓸 수 있는 상태가 된다.
            try { await LoadClassListAsync(); } catch { }

            try { await LoadCountsAsync(); }
            catch { /* 자동 조회 실패는 [📊 전체 카운트 조회] 로 다시 시도할 수 있다 */ }
        }

        // [찾기:자동복호추출]
        /// <summary>
        /// Oracle 소스일 때 이름컬럼 복호화 식을 자동으로 찾아 적용한다(모달 없이 로그로만 보고).
        /// 수동 [🔐 Oracle 암호화 추출] 과 같은 경로이며, 실패해도 조용히 넘어간다 —
        /// 복호화 없이도 파일 이관은 되고, 필요하면 사용자가 버튼으로 다시 돌릴 수 있다.
        /// </summary>
        private async Task AutoDiscoverCryptoAsync()
        {
            if (SourceKind != DbProviderKind.Oracle) return;
            if (UseDecryptionChk?.IsChecked != true) return;
            if (string.IsNullOrWhiteSpace(SrcConn)) return;

            RunStatus.Text = "복호화 식 자동 추출 중…";
            bool ok = await Task.Run(() =>
                CryptoContext.DiscoverOracleCryptoAsync(SrcConn, SourceMode, RunLogLine, default));
            await EnsureViewCryptoAsync();
            UpdateCryptoRuleInfo();
            RunStatus.Text = ok ? "복호화 식 적용됨 (자동)" : "복호화 식 자동 추출 실패 — [🔐 Oracle 암호화 추출] 로 재시도";
        }

        private bool _autoStarted;

        // ══════════ [찾기:원버튼] 세션 실행 — 리허설(①~④) → 승인 → 실제 이관(⑤~⑥) ══════════

        /// <summary>진행 중인 세션. 리허설이 끝나면 승인 후 커밋에 그대로 재사용한다.</summary>
        private MigrationSession? _session;

        /// <summary>세션 옵션을 화면 체크에서 구성한다.</summary>
        private SessionOptions CurrentSessionOptions() => new()
        {
            RunDryRun = SkipDryRunChk?.IsChecked != true,
            VerifyIdempotencyByRerun = VerifyRerunChk?.IsChecked == true,
        };

        /// <summary>스텝바에 현재 단계를 표시한다.</summary>
        private void SetStep(Stage? stage, string message)
        {
            string[] names = { "①연결", "②매핑", "③DryRun", "④Trial", "⑤게이트", "⑥커밋", "⑦검증" };
            int idx = stage switch
            {
                Stage.Connect => 0, Stage.Mapping => 1, Stage.DryRun => 2, Stage.Trial => 3,
                Stage.Gate => 4, Stage.Commit => 5, Stage.Verify => 6, _ => -1,
            };
            var parts = names.Select((n, i) => i == idx ? $"【{n}】" : $" {n} ");
            StepText.Text = string.Join("  ", parts) + "   — " + message;
        }

        /// <summary>
        /// 리허설 전체 실행: 연결 점검 → 매핑 리스트 → DryRun → Trial → 게이트 채점까지 무인 수행한다.
        /// 타겟에 영구 반영되지 않는다(Trial 은 INSERT 후 롤백). 통과하면 [실제 이관] 버튼이 열린다.
        /// </summary>
        private async void OnRunRehearsal(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) return;
            if (BuildOptions(RunMode.Trial) is not { } opt) return;

            _runCts = new System.Threading.CancellationTokenSource();
            SetSessionButtons(false);
            BtnCommitSession.IsEnabled = false;
            ClearRunLog();
            StartFileLog("세션 리허설(①~④)");

            _session = new MigrationSession(opt, CurrentSessionOptions(), RunLogLine);
            var progress = new Progress<StageProgress>(p => SetStep(p.Stage, p.Message));

            try
            {
                RunStatus.Text = "리허설 실행 중…";
                bool pass = await Task.Run(() => _session.RunRehearsalAsync(progress, _runCts.Token));

                var trial = _session.TrialReport;
                RunStatus.Text = pass ? "리허설 통과 — 승인 대기" : "리허설 차단";
                SetStep(Stage.Gate, pass
                    ? "게이트 통과 — [승인 후 실제 이관] 진행 가능"
                    : "게이트 차단 — 아래 로그의 차단 항목 확인");

                // 게이트까지 도달했으면(통과 여부와 무관하게) 채점표를 보여준다.
                if (_session.RehearsalComplete && _session.G4 is { } g4)
                {
                    BtnCommitSession.IsEnabled = true;
                    // 검토 전용으로 연다 — 승인은 [승인 후 실제 이관] 버튼에서 한 번만 받는다.
                    var win = new GateReviewWindow(g4, opt, _session.Dir, _session.TrialReport, reviewOnly: true)
                    { Owner = Window.GetWindow(this) };
                    win.ShowDialog();
                }
                else
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "리허설이 게이트 이전 단계에서 중단되었습니다.\n\n진행 로그와 세션 폴더의 산출물을 확인하세요.\n\n"
                        + _session.Dir,
                        "리허설 중단", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                if (trial != null)
                    RunLogLine($"[리허설 요약] 적재예정 {trial.TotalInserted} / 재사용 {trial.TotalReused} / " +
                               $"건너뜀 {trial.TotalSkipped} / 오류 {trial.TotalErrors}");
            }
            catch (OperationCanceledException)
            {
                RunStatus.Text = "중지됨";
                SetStep(null, "중지됨");
                RunLogLine("── 사용자 중지 ──");
            }
            catch (Exception ex)
            {
                RunStatus.Text = "리허설 오류";
                SetStep(null, "오류");
                RunLogLine("!! " + ExtractRaw.ExceptionText.Describe(ex));
                MessageBox.Show(Window.GetWindow(this), ExtractRaw.ExceptionText.Describe(ex),
                    "리허설 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                SetSessionButtons(true);
                EndFileLog("rehearsal");
            }
        }

        /// <summary>
        /// 승인 창에서 타겟 확인·승인자 입력을 받은 뒤 실제 이관(커밋)과 사후 검증·레코드 매핑까지 수행한다.
        /// Commit 직전 소스 지문을 재대조해 Trial 이후 소스가 바뀌었으면 자동으로 중단된다.
        /// </summary>
        private async void OnCommitSession(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) return;
            if (_session is not { RehearsalComplete: true } s || s.G4 is not { } g4)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "먼저 [리허설 전체 실행]을 완료하세요.\n\n실제 이관은 Trial 결과를 근거로만 진행합니다.",
                    "실제 이관", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (BuildOptions(RunMode.Commit) is not { } opt) return;

            var gate = new GateReviewWindow(g4, opt, s.Dir, s.TrialReport) { Owner = Window.GetWindow(this) };
            if (gate.ShowDialog() != true || string.IsNullOrWhiteSpace(gate.Approver)) return;

            _runCts = new System.Threading.CancellationTokenSource();
            SetSessionButtons(false);
            StartFileLog("세션 실제 이관(⑤~⑥)");

            var progress = new Progress<StageProgress>(p => SetStep(p.Stage, p.Message));
            try
            {
                RunStatus.Text = "실제 이관 실행 중…";
                bool ok = await Task.Run(() => s.RunCommitAsync(gate.Approver!, progress, _runCts.Token));

                if (!ok)
                {
                    RunStatus.Text = "커밋 중단";
                    SetStep(Stage.Commit, "중단 — 소스 변경 감지 또는 전제 미충족");
                    MessageBox.Show(Window.GetWindow(this),
                        "커밋이 중단되었습니다.\n\nTrial 이후 소스가 변경되었을 수 있습니다.\n" +
                        "리허설을 다시 실행한 뒤 진행하세요.",
                        "커밋 중단", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var rep = s.CommitReport!;
                RunStatus.Text = $"이관 완료 — 적재 {rep.TotalInserted} / 재사용 {rep.TotalReused} / " +
                                 $"건너뜀 {rep.TotalSkipped} / 오류 {rep.TotalErrors}";
                SetStep(Stage.Verify, "완료 — 세션 폴더에 산출물 기록됨");

                var win = new MigrationResultWindow(rep, opt) { Owner = Window.GetWindow(this) };
                win.Show();

                MessageBox.Show(Window.GetWindow(this),
                    $"이관과 사후 검증이 끝났습니다.\n\n" +
                    $"적재 {rep.TotalInserted:N0} / 재사용 {rep.TotalReused:N0} / 건너뜀 {rep.TotalSkipped:N0}\n\n" +
                    $"산출물 폴더:\n{s.Dir}\n\n" +
                    "· 02_mapping_schema.tsv  (사전 컬럼 매핑)\n" +
                    "· 06_mapping_records.tsv (사후 레코드 매핑)\n" +
                    "· 00_summary.txt         (게이트·검증 종합)",
                    "이관 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                RunStatus.Text = "중지됨";
                RunLogLine("── 사용자 중지 ──");
            }
            catch (Exception ex)
            {
                RunStatus.Text = "이관 오류";
                RunLogLine("!! " + ExtractRaw.ExceptionText.Describe(ex));
                MessageBox.Show(Window.GetWindow(this), ExtractRaw.ExceptionText.Describe(ex),
                    "이관 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                SetSessionButtons(true);
                EndFileLog("commit");
            }
        }

        /// <summary>세션 실행 중에는 실행 계열 버튼을 잠그고 중지만 열어 둔다.</summary>
        private void SetSessionButtons(bool enabled)
        {
            BtnRehearsal.IsEnabled = enabled;
            BtnCommitSession.IsEnabled = enabled && _session is { RehearsalComplete: true };
            BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnTrial.IsEnabled = BtnRealRun.IsEnabled = enabled;
            BtnStop.IsEnabled = !enabled;
        }

        /// <summary>현재 세션(없으면 logs) 폴더를 탐색기로 연다.</summary>
        private void OnOpenSessionFolder(object sender, RoutedEventArgs e)
        {
            var dir = _session?.Dir ?? LogDir;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "폴더를 열 수 없습니다: " + ex.Message,
                    "세션 폴더", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>스니펫·영상 파일 루트(CLASSIFICATIONDB). 유일한 파일 출처다.</summary>
        private string FileRoot => NbRootBox.Text.Trim();

        /// <summary>테이블별 리졸버 캐시 — 파일명 인덱스를 재사용하기 위함.</summary>
        private readonly Dictionary<string, SnippetFileResolver> _resolvers = new();

        /// <summary>
        /// 테이블 종류에 맞는 파일 폴더를 만든다.
        ///   CLASSIFICATIONDB\{SNIPPET|IMAGE|CTRYIMAGE} 한 곳만 본다.
        ///   [NAIMS-ISSUE:SNIPPET-ROOTS] 예전의 1~3순위 폴백은 폐지됐다 — 파일 출처가 흐려지고
        ///   원음 폴더의 동명 파일을 오탐할 수 있었기 때문.
        /// </summary>
        private SnippetFileResolver ResolverFor(TableMapItem m)
        {
            var sub = m.FileSubFolder;
            var primary = string.IsNullOrEmpty(sub) ? FileRoot : Path.Combine(FileRoot, sub);

            if (_resolvers.TryGetValue(primary, out var cached)) return cached;

            // [NAIMS-ISSUE:SNIPPET-ROOTS] CLASSIFICATIONDB 아래 종류별 하위 폴더 한 곳만 본다.
            var r = new SnippetFileResolver(new[]
            {
                new SnippetFileResolver.RootSpec("CLASSIFICATION", primary),
            });
            _resolvers[primary] = r;
            return r;
        }

        private void OnPickNbRoot(object sender, RoutedEventArgs e)
            => PickRoot(NbRootBox, "CLASSIFICATIONDB 폴더 선택");

        /// <summary>
        /// 폴더 선택 대화상자를 열어 스니펫 루트 경로를 지정한다. 선택 시 리졸버 인덱스 캐시를 비우고 루트 접근성을 다시 점검한다.
        /// </summary>
        /// <param name="tb">경로를 반영할 대상 텍스트박스.</param>
        /// <param name="title">대화상자 제목.</param>
        private void PickRoot(TextBox tb, string title)
        {
            var dlg = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = Directory.Exists(tb.Text.Trim())
                    ? tb.Text.Trim()
                    : @"\\192.168.111.165\naims_share\MSU"
            };
            if (dlg.ShowDialog() == true)
                tb.Text = dlg.FolderName;   // TextChanged(OnRootTextChanged)가 캐시 폐기·재점검을 맡는다
        }

        /// <summary>
        /// CLASSIFICATION 루트를 직접 타이핑으로 고쳐도 리졸버 인덱스 캐시가 무효화되게 한다.
        /// 변경분은 AppSession(단일 원본)에도 즉시 반영하고 접근성을 다시 점검한다.
        /// </summary>
        private void OnRootTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _resolvers.Clear();   // 경로가 바뀌면 인덱스 캐시 폐기
            Session.ClassificationRoot = NbRootBox.Text.Trim();
            ProbeRoot();
        }

        /// <summary>ProbeRoot 재호출 시 이전 백그라운드 점검 결과를 무시하기 위한 세대 번호.</summary>
        private int _probeVersion;

        /// <summary>
        /// CLASSIFICATIONDB 하위 폴더(SNIPPET/IMAGE/CTRYIMAGE) 현황을 표시한다.
        /// NAS 재귀 파일 카운트는 UI를 멈추지 않도록 백그라운드에서 수행하고 결과만 UI에 반영한다.
        /// 진행 중 다시 호출되면(경로 재변경) 이전 점검 결과는 버린다.
        /// </summary>
        private async void ProbeRoot()
        {
            int ver = ++_probeVersion;   // 이 호출의 세대 — 뒤늦은 결과는 버린다

            // UI 스레드에서 경로만 캡처해 백그라운드로 넘긴다.
            var root = FileRoot;
            RootProbeInfo.Text = "CLASSIFICATIONDB 접근성 점검 중…";

            string text;
            try
            {
                text = await Task.Run(() =>
                {
                    var parts = new List<string>();

                    if (!Directory.Exists(root))
                    {
                        parts.Add("⚠ CLASSIFICATIONDB 접근 불가");
                    }
                    else
                    {
                        foreach (var sub in new[] { "SNIPPET", "IMAGE", "CTRYIMAGE" })
                        {
                            var p = Path.Combine(root, sub);
                            if (Directory.Exists(p))
                            {
                                int n = 0;
                                try { n = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Count(); } catch { }
                                parts.Add($"{sub} {n}개");
                            }
                            else parts.Add($"{sub}(없음)");
                        }
                    }

                    return string.Join("  ·  ", parts)
                        + "    (스니펫·영상 파일은 이 폴더에서만 찾습니다)";
                });
            }
            catch (Exception ex)
            {
                text = "루트 점검 실패: " + ex.Message;
            }

            if (ver == _probeVersion) RootProbeInfo.Text = text;
        }

        // ── 전체 카운트 조회 ──
        /// <summary>
        /// 매핑 목록 전체를 훑어 카운트 가능한 테이블마다 소스·타겟 행수를 조회하고(DB 접근),
        /// 성공/실패 개수와 소스·타겟 합계를 요약 문구로 표시한다. 타겟이 미확정("(...)")이면 타겟 카운트는 건너뛴다.
        /// </summary>
        private async void OnLoadCounts(object sender, RoutedEventArgs e) => await LoadCountsAsync();

        /// <summary>전체 테이블 행수 조회 본체(버튼·자동 시작 공용).</summary>
        private async Task LoadCountsAsync()
        {
            var kind = SourceKind;
            CountSummary.Text = "카운트 조회 중...";
            int ok = 0, miss = 0;

            foreach (var m in _map)
            {
                if (!m.IsCountable) { m.SourceCount = null; m.TargetCount = null; continue; }

                m.SourceCount = await TableInspector.TryGetRowCountAsync(kind, SourceMode, SrcConn, m.PrimarySourceTable);
                m.TargetCount = m.PrimaryTargetTable.StartsWith("(")
                    ? null
                    : await TableInspector.TryGetRowCountAsync(DbProviderKind.MsSql, DbConnectionMode.Direct, DstConn, m.PrimaryTargetTable);

                if (m.SourceCount.HasValue) ok++; else miss++;
            }

            long srcTotal = _map.Sum(m => m.SourceCount ?? 0);
            long tgtTotal = _map.Sum(m => m.TargetCount ?? 0);
            CountSummary.Text =
                $"{DbProvider.DisplayName(kind)} → MsSQL | 조회 성공 {ok} / 실패 {miss}\n" +
                $"소스 합계 {srcTotal:N0}건 · 타겟 합계 {tgtTotal:N0}건";
        }

        // ── 테이블 선택 ──
        /// <summary>
        /// 좌측 목록에서 테이블을 고르면 작업 패널을 열어 제목·매핑·소스/타겟 샘플을 바인딩한다.
        /// 해당 테이블의 상세를 아직 안 읽었으면(최초 1회) 컬럼 매핑·레코드를 비동기로 로드한다(DB 접근·UI 갱신).
        /// </summary>
        private async void OnTableSelected(object sender, SelectionChangedEventArgs e)
        {
            if (TableList.SelectedItem is not TableMapItem m) return;
            _current = m;

            NoSelectionHint.Visibility = Visibility.Collapsed;
            WorkspacePanel.Visibility = Visibility.Visible;

            WsTitle.Text = $"{m.SourceTable}   →   {m.TargetTable}";
            WsSubtitle.Text = $"[{m.Category}]  {m.Note}"
                            + (m.RequiresFiles ? $"   |  동반 파일: {m.FileKindText} ({m.FileColumns})" : "");

            MappingGrid.ItemsSource = m.ColumnMappings;
            SrcRowGrid.ItemsSource = m.SampleView;
            TgtRowGrid.ItemsSource = m.TargetSampleView;
            MappingSummaryText.Text = m.MappingSummary;
            _files.Clear();
            FileCheckInfo.Text = m.RequiresFiles
                ? "「파일 존재 확인」을 눌러 스니펫 루트에서 실제 파일을 조회합니다."
                : "이 테이블은 동반 파일이 없습니다.";

            if (!m.DetailLoaded)
            {
                m.DetailLoaded = true;
                await LoadMappingAsync(m);
            }
        }

        /// <summary>선택 테이블의 컬럼 매핑 + 소스 레코드를 로드한다.</summary>
        private async Task LoadMappingAsync(TableMapItem m)
        {
            m.DetailLoading = true;
            MappingSummaryText.Text = "컬럼 스키마 조회 중...";
            try
            {
                if (!m.IsCountable)
                {
                    m.MappingSummary = "단일 테이블이 아니어서 자동 매핑을 지원하지 않습니다.";
                    MappingSummaryText.Text = m.MappingSummary;
                    return;
                }

                var srcCols = await TableInspector.GetColumnsAsync(SourceKind, SourceMode, SrcConn, m.PrimarySourceTable);
                if (srcCols.Count == 0)
                {
                    m.MappingSummary = $"소스 테이블 '{m.PrimarySourceTable}' 을(를) 찾을 수 없거나 접근 불가.";
                    MappingSummaryText.Text = m.MappingSummary;
                    return;
                }

                var tgtCols = m.PrimaryTargetTable.StartsWith("(")
                    ? new List<ColumnInfo>()
                    : await TableInspector.GetColumnsAsync(DbProviderKind.MsSql, DbConnectionMode.Direct, DstConn, m.PrimaryTargetTable);

                m.Columns.Clear();
                foreach (var c in srcCols) m.Columns.Add(c);

                var mappings = ColumnMapper.Build(srcCols, tgtCols, m.PrimarySourceTable);
                m.ColumnMappings.Clear();
                foreach (var mp in mappings) m.ColumnMappings.Add(mp);

                int exact  = mappings.Count(x => x.Status == MappingStatus.Exact);
                int rename = mappings.Count(x => x.Status == MappingStatus.Renamed);
                int srcOnly = mappings.Count(x => x.Status == MappingStatus.SourceOnly);
                int tgtOnly = mappings.Count(x => x.Status == MappingStatus.TargetOnly);

                m.MappingSummary = tgtCols.Count == 0
                    ? $"소스 {srcCols.Count}컬럼 — 타겟 테이블 미확정이라 대응을 계산할 수 없습니다."
                    : $"소스 {srcCols.Count}컬럼 · 타겟 {tgtCols.Count}컬럼  |  "
                    + $"일치 {exact} · 이름변경 {rename} · 소스에만 {srcOnly} · 타겟에만 {tgtOnly}";

                MappingSummaryText.Text = m.MappingSummary;

                await LoadSourceRowsAsync(m);
            }
            catch (Exception ex)
            {
                m.MappingSummary = "매핑 로드 실패: " + ex.Message;
                MappingSummaryText.Text = m.MappingSummary;
            }
            finally
            {
                m.DetailLoading = false;
            }
        }

        // ── 레코드 열람 ──
        private int SelectedRowLimit()
        {
            var text = (SrcRowLimit.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return int.TryParse(text, out var n) ? n : 50;
        }

        private async void OnRowLimitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _current == null) return;
            await LoadSourceRowsAsync(_current);
        }

        private async void OnReloadSourceRows(object sender, RoutedEventArgs e)
        {
            if (_current != null) await LoadSourceRowsAsync(_current);
        }

        /// <summary>
        /// 선택 테이블의 소스 레코드 샘플을 조회해 그리드에 전시한다(DB 접근).
        /// 암호화 컬럼이 있는 테이블이면 복호화 규칙을 확인해 규칙이 있으면 (암호문)/(복호) 전·후를 나란히 보여주고,
        /// 규칙이 없으면 이름 컬럼을 숨긴 채 안내 문구를 덧붙인다. 현재 선택이 바뀌지 않았을 때만 그리드에 반영한다.
        /// </summary>
        private async Task LoadSourceRowsAsync(TableMapItem m)
        {
            if (!m.IsCountable)
            {
                if (ReferenceEquals(_current, m)) SrcRowInfo.Text = "조회 불가";
                return;
            }

            int limit = SelectedRowLimit();
            SrcRowInfo.Text = "조회 중...";

            try
            {
                // 암호화 컬럼이 있는 테이블이면, 복호화 규칙 확인 후 전/후를 나란히 전시한다.
                // [찾기:복호토글] '복호화 불러오기'가 꺼져 있으면 복호화하지 않고 원문 그대로 조회한다.
                bool wantsDecrypt = UseDecryptionChk?.IsChecked == true
                                    && CryptoContext.RequiresDecryption(SourceKind, m.PrimarySourceTable);
                DataTable dt;
                string suffix;
                if (wantsDecrypt)
                {
                    await EnsureViewCryptoAsync();
                    UpdateCryptoRuleInfo();
                    if (_viewCrypto.CanDecrypt)
                    {
                        dt = await TableInspector.GetDecryptedSampleAsync(
                            SourceKind, SourceMode, SrcConn, m.PrimarySourceTable, _viewCrypto, top: limit);
                        suffix = "  ·  암호화 컬럼: (암호문)/(복호) 전·후 전시";
                    }
                    else
                    {
                        dt = await TableInspector.GetSampleAsync(
                            SourceKind, SourceMode, SrcConn, m.PrimarySourceTable, top: limit, maxColumns: 0);
                        suffix = "  ·  ⚠ 복호화 규칙 없음 — 이름 컬럼 숨김. [🔐 Oracle 암호화 추출] 후 다시 조회하세요.";
                    }
                }
                else
                {
                    dt = await TableInspector.GetSampleAsync(
                        SourceKind, SourceMode, SrcConn, m.PrimarySourceTable, top: limit, maxColumns: 0);
                    suffix = " (바이너리 컬럼 제외)";
                }

                m.SampleView = dt.DefaultView;
                if (ReferenceEquals(_current, m))   // 조회 중 다른 테이블을 골랐으면 화면은 건드리지 않는다
                {
                    SrcRowGrid.ItemsSource = m.SampleView;
                    SrcRowInfo.Text = $"{dt.Rows.Count}행 × {dt.Columns.Count}컬럼 (상위 {limit}행)" + suffix;
                }
            }
            catch (Exception ex)
            {
                // async void 직접 경로(행수 변경·다시 조회)에서도 크래시하지 않도록 여기서 삼킨다.
                if (ReferenceEquals(_current, m)) SrcRowInfo.Text = "소스 레코드 조회 실패: " + ex.Message;
            }
        }

        /// <summary>선택 테이블의 타겟 레코드 샘플을 조회해 그리드에 전시한다(DB 접근). 타겟이 미확정("(...)")이면 조회하지 않는다.</summary>
        private async void OnReloadTargetRows(object sender, RoutedEventArgs e)
        {
            if (_current is not { } m) return;
            if (m.PrimaryTargetTable.StartsWith("("))
            {
                TgtRowInfo.Text = "타겟 테이블이 확정되지 않았습니다.";
                return;
            }

            TgtRowInfo.Text = "조회 중...";
            try
            {
                var dt = await TableInspector.GetSampleAsync(
                    DbProviderKind.MsSql, DbConnectionMode.Direct, DstConn, m.PrimaryTargetTable, top: SelectedRowLimit(), maxColumns: 0);

                m.TargetSampleView = dt.DefaultView;
                if (ReferenceEquals(_current, m))   // 조회 중 다른 테이블을 골랐으면 화면은 건드리지 않는다
                {
                    TgtRowGrid.ItemsSource = m.TargetSampleView;
                    TgtRowInfo.Text = $"{dt.Rows.Count}행 × {dt.Columns.Count}컬럼 (바이너리 컬럼 제외)";
                }
            }
            catch (Exception ex)
            {
                // async void 핸들러 — DB 오류(연결 끊김 등)로 앱이 죽지 않게 여기서 삼킨다.
                if (ReferenceEquals(_current, m)) TgtRowInfo.Text = "타겟 레코드 조회 실패: " + ex.Message;
            }
        }

        // ── 동반 파일 대조 (DB ↔ 디스크 양방향) ──
        /// <summary>
        /// 선택 테이블의 동반 파일을 DB↔디스크 양방향으로 대조한다(DB 접근·파일시스템 탐색).
        /// 스키마에서 *FILENAME 컬럼을 찾아 전체 레코드가 참조하는 파일을 우선순위 폴더 체인에서 확인하고,
        /// 옵션이 켜져 있으면 1순위 폴더에만 있고 DB가 참조하지 않는 고아 파일도 함께 나열한다.
        /// 존재/누락/고아 개수와 순위별 분포, 확보 용량을 요약 문구로 표시한다. 무거운 대조는 백그라운드에서 수행한다.
        /// </summary>
        private async void OnCheckFiles(object sender, RoutedEventArgs e)
        {
            if (_current is not { } m) return;
            if (!m.RequiresFiles)
            {
                FileCheckInfo.Text = "이 테이블은 동반 파일이 없습니다.";
                return;
            }

            var resolver = ResolverFor(m);
            FileCheckInfo.Text = "대조 중... (폴더가 크면 첫 조회에 시간이 걸립니다)";
            _files.Clear();
            BtnCheckFiles.IsEnabled = false;   // 대조 중 재클릭 방지

            try
            {
                // 파일명 컬럼은 테이블마다 다르므로 스키마에서 찾아낸다.
                var cols = await TableInspector.GetColumnsAsync(SourceKind, SourceMode, SrcConn, m.PrimarySourceTable);
                var idCol = cols.FirstOrDefault()?.Name ?? "";
                var nameCols = cols.Where(c => c.Name.EndsWith("FILENAME", StringComparison.OrdinalIgnoreCase))
                                   .Select(c => c.Name).ToList();
                if (nameCols.Count == 0)
                {
                    if (ReferenceEquals(_current, m))
                        FileCheckInfo.Text = "파일명 컬럼(*FILENAME)을 찾지 못했습니다.";
                    return;
                }

                // 전체 레코드를 대상으로 대조한다(행 수 제한과 무관).
                var dt = await TableInspector.GetSampleAsync(
                    SourceKind, SourceMode, SrcConn, m.PrimarySourceTable, top: 100000, maxColumns: 0);

                bool includeOrphans = ShowOrphans.IsChecked == true;

                var rows = await Task.Run(() =>
                {
                    var list = new List<SnippetFileRow>();
                    var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (DataRow r in dt.Rows)
                    {
                        var id = dt.Columns.Contains(idCol) ? r[idCol]?.ToString() ?? "" : "";
                        foreach (var nc in nameCols)
                        {
                            if (!dt.Columns.Contains(nc)) continue;
                            var raw = r[nc]?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(raw)) continue;

                            var kind = nc.Contains("GRAM", StringComparison.OrdinalIgnoreCase) ? "GRAM"
                                     : nc.Contains("FLAG", StringComparison.OrdinalIgnoreCase) ? "FLAG"
                                     : nc.Contains("IMG",  StringComparison.OrdinalIgnoreCase) ? "IMAGE"
                                     : "AUDIO";
                            referenced.Add(raw);
                            list.Add(SnippetFileRow.Probe(id, kind, raw, resolver));
                        }
                    }

                    // 디스크(1순위 폴더)에만 있고 DB가 참조하지 않는 파일
                    if (includeOrphans && resolver.PrimaryRoot is { } pr)
                    {
                        var idx = resolver.GetIndex(pr.Path);
                        foreach (var path in idx.PathsExcept(referenced))
                            list.Add(SnippetFileRow.Orphan(path, pr.Label));
                    }

                    return list;
                });

                // 대조 중 다른 테이블을 골랐으면 늦은 결과는 버린다(다른 테이블 목록에 섞임 방지).
                if (!ReferenceEquals(_current, m)) return;

                foreach (var f in rows) _files.Add(f);

                int found   = rows.Count(f => f.State == FileState.Found);
                int missing = rows.Count(f => f.State == FileState.Missing);
                int orphan  = rows.Count(f => f.State == FileState.Orphan);
                long total  = rows.Where(f => f.State == FileState.Found).Sum(f => f.SizeBytes);

                var msg = $"DB 참조 {found + missing}개 → 존재 {found} · 누락 {missing}";

                // 어느 순위에서 찾았는지 분포를 덧붙인다.
                var bySource = rows.Where(f => f.State == FileState.Found && !string.IsNullOrEmpty(f.SourceLabel))
                                   .GroupBy(f => f.SourceLabel)
                                   .OrderBy(g => g.Key)
                                   .Select(g => $"{g.Key} {g.Count()}")
                                   .ToList();
                if (bySource.Count > 0) msg += $" ({string.Join(", ", bySource)})";

                if (includeOrphans) msg += $" | 고아 {orphan}";
                msg += $" · 확보 용량 {total / 1024.0 / 1024:F1} MB";
                if (found == 0 && missing > 0) msg += "   ← 폴더 경로를 확인하세요.";
                FileCheckInfo.Text = msg;
            }
            catch (Exception ex)
            {
                // async void 핸들러 — DB·NAS 오류로 앱이 죽지 않게 여기서 삼킨다.
                if (ReferenceEquals(_current, m)) FileCheckInfo.Text = "파일 대조 실패: " + ex.Message;
            }
            finally
            {
                BtnCheckFiles.IsEnabled = true;
            }
        }

        /// <summary>그램 미리보기 창을 모달로 연다. 파일 목록에서 .grm을 고른 상태면 그 경로를 미리 채워준다.</summary>
        private void OnOpenGramPreview(object sender, RoutedEventArgs e)
        {
            var w = new GramPreviewWindow { Owner = Window.GetWindow(this) };
            // 파일 목록에서 .grm 을 고른 상태면 그 경로를 미리 채워준다.
            if (FileGrid.SelectedItem is SnippetFileRow f && f.Exists &&
                f.FileName.EndsWith(".grm", StringComparison.OrdinalIgnoreCase))
            {
                w.PathBox.Text = f.FullPath;
            }
            w.ShowDialog();
        }
    }
}
