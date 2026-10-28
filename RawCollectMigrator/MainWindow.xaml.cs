using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using RawCollectMigrator.Infrastructure;
using RawCollectMigrator.Features.Collection;

namespace RawCollectMigrator
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<CollectionRow> _rows = new();
        private readonly ICollectionView _rowsView;
        /// <summary>실행/시험 실행 중 취소 요청을 전달하는 토큰 소스. 실행 중이 아니면 null.</summary>
        private CancellationTokenSource? _cts;

        /// <summary>폴더 스캔 진행 중 여부 — 스캔 중 재클릭에 의한 중복 스캔을 막는다.</summary>
        private bool _scanning;

        /// <summary>
        /// 메인 창을 초기화하고, 스캔 결과 컬렉션(<see cref="_rows"/>)에 대한 필터링 뷰를 구성해
        /// 그리드에 바인딩한다. 필터는 <see cref="FilterRow"/>(빈 폴더 표시 여부).
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            _rowsView = CollectionViewSource.GetDefaultView(_rows);
            _rowsView.Filter = FilterRow;
            Grid.ItemsSource = _rowsView;

            // [찾기:시작화면] 접속·경로 설정을 먼저 받고, 확인된 뒤에야 작업 화면을 연다.
            Setup.StartRequested += (_, _) => EnterWorkspace();
            PathBox.Text = Session.CollectRawRoot;
        }

        /// <summary>접속·경로의 단일 원본.</summary>
        private static Infrastructure.AppSession Session => Infrastructure.AppSession.Current;

        /// <summary>시작 화면을 닫고 작업 탭으로 전환한다. 상단 접속 바를 현재 세션 값으로 채운다.</summary>
        private void EnterWorkspace()
        {
            PathBox.Text = Session.CollectRawRoot;
            ConnBar.Text = Session.Summary;
            Setup.Visibility = Visibility.Collapsed;
            WorkRoot.Visibility = Visibility.Visible;

            // [찾기:자동스캔] 진입 직후 각 탭을 준비 상태로 만든다.
            //   버튼을 하나씩 누르지 않아도 목록·카운트가 채워져 있어야 바로 일을 시작할 수 있다.
            //   전부 백그라운드이고 실패해도 화면을 막지 않는다(개별 [스캔]으로 다시 돌릴 수 있음).
            _ = AutoStartTabsAsync();
        }

        /// <summary>진입 직후 세 탭을 자동으로 준비시킨다(수집 폴더 스캔 · 발췌 스캔 · DB→DB 카운트).</summary>
        private async Task AutoStartTabsAsync()
        {
            try
            {
                if (System.IO.Directory.Exists(PathBox.Text.Trim()))
                    await ScanAsync();
            }
            catch { /* 자동 준비 실패가 작업 화면을 막지 않는다 */ }

            try { await ExtractTab.AutoScanAsync(); } catch { }
            try { await DbToDbTab.AutoStartAsync(); } catch { }
        }

        /// <summary>[⚙ 연결 설정] — 작업을 멈추고 시작 화면으로 되돌아간다.</summary>
        private void OnOpenSetup(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                MessageBox.Show(this, "실행 중에는 연결 설정을 바꿀 수 없습니다. 먼저 중지하세요.",
                    "연결 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            WorkRoot.Visibility = Visibility.Collapsed;
            Setup.Visibility = Visibility.Visible;
        }

        /// <summary>수집원음 경로를 세션에 반영한다(이 탭에서 바꿔도 단일 원본이 갱신되도록).</summary>
        private void OnCollectPathChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Session.CollectRawRoot = PathBox.Text.Trim();
        }

        /// <summary>
        /// 그리드 표시 필터. '빈 폴더 포함' 체크가 꺼져 있으면 WAV(SCHANNEL_INFO)가 하나도 없는 임무폴더는 숨긴다.
        /// </summary>
        /// <param name="o">행 후보(<see cref="CollectionRow"/> 기대).</param>
        /// <returns>표시 대상이면 true.</returns>
        private bool FilterRow(object o)
            => o is CollectionRow r && (IncludeEmpty.IsChecked == true || r.WavCount > 0);

        /// <summary>'빈 폴더 포함' 체크 변경 시 뷰를 다시 필터링하고 요약 통계를 갱신한다.</summary>
        private void OnIncludeEmptyChanged(object sender, RoutedEventArgs e)
        {
            _rowsView.Refresh();
            UpdateSummary();
        }

        // ── 폴더 선택 ──
        /// <summary>
        /// 수집원음자료 루트 폴더 선택 대화상자를 띄우고, 선택 시 경로 입력란(PathBox)에 반영한다.
        /// 기본 시작 위치는 현재 입력 경로가 유효하면 그곳, 아니면 NAS 공유 경로(MSU).
        /// </summary>
        private void OnSelectFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "수집원음자료 루트 폴더 선택",
                InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : @"\\192.168.111.165\naims_share\MSU"
            };
            if (dlg.ShowDialog() == true)
            {
                PathBox.Text = dlg.FolderName;
            }
        }

        // ── 폴더 스캔 ──
        /// <summary>
        /// 입력 경로를 백그라운드 스레드에서 스캔(<see cref="CollectionScanner.Scan"/>)해
        /// 임무폴더 목록을 그리드(<see cref="_rows"/>)에 채우고 요약을 갱신한다.
        /// 파일 IO만 수행하며 DB 변경은 없다. 경로가 없거나 스캔 실패 시 메시지 박스로 안내한다.
        /// </summary>
        private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

        // [찾기:자동스캔]
        /// <summary>수집원음 폴더 스캔 본체(버튼·자동 스캔 공용).</summary>
        private async Task ScanAsync()
        {
            if (_scanning) return;   // 스캔 중 재클릭 방지 (중복 스캔·행 이중 적재 방지)

            var path = PathBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "경로가 존재하지 않습니다:\n" + path, "스캔", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _scanning = true;
            BtnScan.IsEnabled = false;
            ProgressText.Text = "스캔 중...";
            SummaryText.Text = "스캔 중";
            _rows.Clear();

            try
            {
                var rows = await Task.Run(() => CollectionScanner.Scan(path));
                foreach (var r in rows) _rows.Add(r);
                _rowsView.Refresh();
                UpdateSummary();
                ProgressText.Text = $"스캔 완료 — {rows.Count}개 임무폴더";
            }
            catch (Exception ex)
            {
                ProgressText.Text = $"스캔 오류: {ex.Message}";
                MessageBox.Show(this, ex.Message, "스캔 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _scanning = false;
                BtnScan.IsEnabled = true;
            }
        }

        /// <summary>선택 가능한(<see cref="CollectionRow.CanBeSelected"/>) 모든 행을 선택하고 요약을 갱신한다.</summary>
        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows.Where(r => r.CanBeSelected)) r.IsSelected = true;
            UpdateSummary();
        }

        /// <summary>모든 행의 선택을 해제하고 요약을 갱신한다.</summary>
        private void OnDeselectAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.IsSelected = false;
            UpdateSummary();
        }

        /// <summary>
        /// 하단 요약줄을 갱신한다. 전체/표시/선택 건수와, 선택분의 SUPPLEMENT(보조정보.alg 보유)·
        /// SCHANNEL_INFO(WAV) 건수 및 총 용량(GB)을 집계한다.
        /// </summary>
        private void UpdateSummary()
        {
            int total = _rows.Count;
            int shown = _rows.Count(FilterRow);
            int sel = _rows.Count(r => r.IsSelected);
            int withAlg = _rows.Count(r => r.IsSelected && r.HasSupplementAlg);
            int wavTotal = _rows.Where(r => r.IsSelected).Sum(r => r.WavCount);
            long sizeTotal = _rows.Where(r => r.IsSelected).Sum(r => r.TotalSizeBytes);
            SummaryText.Text = $"전체 {total} / 표시 {shown} / 선택 {sel} / SUPPLEMENT {withAlg} / SCHANNEL_INFO {wavTotal} / {sizeTotal / 1024.0 / 1024 / 1024:F1} GB";
        }

        /// <summary>
        /// 현재 선택된 행 목록을 반환한다. 선택이 하나도 없으면 경고 메시지 박스를 띄우고 null을 반환한다.
        /// </summary>
        /// <returns>선택된 행 목록. 선택이 없으면 null.</returns>
        private List<CollectionRow>? GetSelectedOrWarn()
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "선택된 폴더가 없습니다.", "마이그레이션",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return selected;
        }

        /// <summary>현재 UI 상태(중복 건너뛰기 옵션)와 지정한 시험 실행 여부로 마이그레이션 옵션을 구성한다.</summary>
        /// <param name="dryRun">true면 INSERT 후 롤백하는 시험 실행 모드.</param>
        private MigrationOptions CurrentOptions(bool dryRun) => new()
        {
            DryRun = dryRun,
            SkipDuplicates = SkipDup.IsChecked == true,
        };

        // ── 사전 점검 (DB 변경 없음) ──
        /// <summary>
        /// 선택된 폴더에 대해 사전 점검(<see cref="MigrationExecutor.PreflightAsync"/>)만 실행한다.
        /// DB를 변경하지 않으며, 진행 가능 여부와 경고/오류 목록을 메시지 박스로 보여준다.
        /// </summary>
        private async void OnPreflight(object sender, RoutedEventArgs e)
        {
            if (GetSelectedOrWarn() is not { } selected) return;

            ProgressText.Text = "사전 점검 중...";
            try
            {
                var rep = await MigrationExecutor.PreflightAsync(
                    Session.TargetConn, selected, CurrentOptions(false));

                ProgressText.Text = rep.CanProceed ? "사전 점검 통과" : "사전 점검 실패";
                MessageBox.Show(this,
                    (rep.CanProceed ? "✅ 진행 가능합니다.\n\n" : "❌ 문제가 있어 진행할 수 없습니다.\n\n") + rep.ToText(),
                    "사전 점검 결과", MessageBoxButton.OK,
                    rep.CanProceed
                        ? (rep.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information)
                        : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                ProgressText.Text = "사전 점검 오류";
                MessageBox.Show(this, ex.Message, "사전 점검 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 시험 실행 (INSERT 후 롤백) ──
        /// <summary>
        /// 시험 실행. 실제 INSERT를 수행한 뒤 트랜잭션을 롤백하므로 DB에는 반영되지 않는다.
        /// 스키마 불일치·제약 위반을 사전에 잡는 용도. 사용자 확인 후 <see cref="ExecuteAsync"/>(dryRun:true)로 진행한다.
        /// </summary>
        private async void OnDryRun(object sender, RoutedEventArgs e)
        {
            if (GetSelectedOrWarn() is not { } selected) return;

            var msg = $"선택된 {selected.Count}개 폴더로 시험 실행합니다.\n\n" +
                      "실제 INSERT 를 수행한 뒤 트랜잭션을 롤백하므로 DB에는 반영되지 않습니다.\n" +
                      "스키마 불일치·제약 위반을 미리 잡을 수 있습니다.\n\n계속하시겠습니까?";
            if (MessageBox.Show(this, msg, "시험 실행", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                != MessageBoxResult.OK) return;

            await ExecuteAsync(selected, dryRun: true);
        }

        // ── 실제 마이그레이션 ──
        /// <summary>
        /// 실제 이관을 수행한다. 반드시 사전 점검을 먼저 거치고, 통과하지 못하면 중단한다.
        /// 통과 시 대상 건수(RAW_COLLECT_SIG / RAW_COLLECT_SUPPLEMENT / RAW_SCHANNEL_INFO)를 요약해
        /// 최종 확인을 받은 뒤 <see cref="ExecuteAsync"/>(dryRun:false)로 DB에 커밋한다. (되돌리려면 수동 삭제 필요)
        /// </summary>
        private async void OnRunMigration(object sender, RoutedEventArgs e)
        {
            if (GetSelectedOrWarn() is not { } selected) return;

            // 실제 반영 전에 항상 사전 점검을 거친다.
            ProgressText.Text = "사전 점검 중...";
            PreflightReport rep;
            try
            {
                rep = await MigrationExecutor.PreflightAsync(Session.TargetConn, selected, CurrentOptions(false));
            }
            catch (Exception ex)
            {
                ProgressText.Text = "사전 점검 오류";
                MessageBox.Show(this, ex.Message, "사전 점검 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!rep.CanProceed)
            {
                ProgressText.Text = "사전 점검 실패";
                MessageBox.Show(this, "❌ 사전 점검을 통과하지 못해 중단합니다.\n\n" + rep.ToText(),
                    "중단", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var msg = $"선택된 {selected.Count}개 폴더를 실제로 이관합니다.\n\n" +
                      $"  • RAW_COLLECT_SIG: {selected.Count}건\n" +
                      $"  • RAW_COLLECT_SUPPLEMENT: {selected.Count(r => r.HasSupplementAlg)}건\n" +
                      $"  • RAW_SCHANNEL_INFO: {selected.Sum(r => r.WavCount)}건\n\n" +
                      "── 사전 점검 ──\n" + rep.ToText() +
                      "\n이 작업은 DB에 실제로 반영되며 되돌리려면 수동 삭제가 필요합니다.\n계속하시겠습니까?";
            if (MessageBox.Show(this, msg, "실행 확인", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK)
            {
                ProgressText.Text = "취소됨";
                return;
            }

            await ExecuteAsync(selected, dryRun: false);
        }

        /// <summary>
        /// 마이그레이션 실행 공통 루틴. 대상 행의 상태를 초기화하고 새 취소 토큰을 만든 뒤
        /// <see cref="MigrationExecutor.RunAsync"/>를 호출하며, 진행률/현재 폴더/단계를 UI에 반영한다.
        /// 완료 시 성공/실패/건너뜀 건수를 집계해 안내하고, 취소·오류를 처리한 뒤 토큰을 정리한다.
        /// dryRun=false면 DB에 실제 반영된다.
        /// </summary>
        /// <param name="selected">이관 대상 행 목록.</param>
        /// <param name="dryRun">true면 INSERT 후 롤백(시험 실행), false면 실제 커밋.</param>
        private async Task ExecuteAsync(List<CollectionRow> selected, bool dryRun)
        {
            if (_cts != null) return;   // 재진입 방지 — 이미 실행 중이면 _cts 를 덮어쓰지 않는다
            _cts = new CancellationTokenSource();
            BtnScan.IsEnabled = BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnRun.IsEnabled = false;
            ProgressBar.Value = 0;
            ProgressText.Text = dryRun ? "시험 실행 시작..." : "시작...";
            ProgressPctText.Text = "0 %";

            foreach (var r in selected)
            {
                r.Status = "대기";
                r.ErrorMessage = "";
                r.InsertedCollectSigId = null;
                r.InsertedSupplementCount = 0;
                r.InsertedSchannelCount = 0;
            }

            var progress = new Progress<MigrationProgress>(p =>
            {
                if (p.TotalCollections > 0)
                {
                    double pct = 100.0 * p.DoneCollections / p.TotalCollections;
                    ProgressBar.Value = pct;
                    ProgressPctText.Text = $"{pct:F0} %";
                }
                ProgressText.Text = $"[{p.DoneCollections}/{p.TotalCollections}] {p.CurrentFolder} — {p.Phase}";
            });

            try
            {
                await MigrationExecutor.RunAsync(
                    Session.TargetConn, selected, progress, _cts.Token, CurrentOptions(dryRun));

                int ok      = selected.Count(r => r.Status is "완료" or "시험완료");
                int fail    = selected.Count(r => r.Status == "실패");
                int skipped = selected.Count(r => r.Status == "건너뜀");

                var label = dryRun ? "시험 실행" : "마이그레이션";
                ProgressText.Text = $"{label} 완료 — 성공 {ok} / 실패 {fail} / 건너뜀 {skipped}";
                ProgressBar.Value = 100;
                ProgressPctText.Text = "100 %";

                var body = $"{label} 완료\n\n성공: {ok}건\n실패: {fail}건\n건너뜀: {skipped}건";
                if (dryRun) body += "\n\n※ 모두 롤백되었으므로 DB에는 반영되지 않았습니다.";
                if (fail > 0) body += "\n\n실패 사유는 그리드의 [결과 / 오류] 열을 확인하세요.";

                MessageBox.Show(this, body, "결과", MessageBoxButton.OK,
                    fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "취소됨";
            }
            catch (Exception ex)
            {
                ProgressText.Text = $"오류: {ex.Message}";
                MessageBox.Show(this, ex.Message, "마이그레이션 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // 취소 핸들러가 Dispose 된 토큰을 만지지 않도록 null 로 먼저 바꾼 뒤 Dispose 한다.
                var cts = _cts;
                _cts = null;
                cts?.Dispose();
                BtnScan.IsEnabled = BtnPreflight.IsEnabled = BtnDryRun.IsEnabled = BtnRun.IsEnabled = true;
                UpdateSummary();
            }
        }

        /// <summary>진행 중인 실행/시험 실행에 취소를 요청한다. 실행 중이 아니면 무시된다.</summary>
        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* 실행 종료 직후 눌린 취소 — 무시 */ }
        }

        // ── 행 더블클릭 → 상세 확장/축소 ──
        // NOTE: RowDetailsVisibilityMode(Collapsed) 는 각 행의 DetailsVisibility 를 강제로 덮어쓰므로,
        //       Style Setter 바인딩 대신 code-behind 에서 로컬 값(DependencyObject.SetValue)으로 직접 토글한다.
        //       또한 CheckBox 열의 편집 진입 로직에 MouseDoubleClick 이 흡수될 수 있어 Preview 로 잡는다.
        /// <summary>
        /// 행 더블클릭 시 상세 영역을 확장/축소한다. 체크박스 열 위 더블클릭은 체크 토글로 두고 확장은 무시한다.
        /// 처음 펼칠 때 한 번만 상세 데이터를 지연 로딩(<see cref="LoadDetailAsync"/>)한다.
        /// </summary>
        private async void OnGridRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;

            // 체크박스 컬럼 위에서의 더블클릭은 체크 토글로 취급하고 확장은 무시(원치 않는 확장 방지)
            if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(src) != null) return;

            var row = FindAncestor<DataGridRow>(src);
            if (row?.Item is not CollectionRow cr) return;

            cr.IsExpanded = !cr.IsExpanded;
            row.DetailsVisibility = cr.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;

            if (cr.IsExpanded && !cr.DetailLoaded)
            {
                cr.DetailLoaded = true;
                await LoadDetailAsync(cr);
            }
        }

        /// <summary>
        /// 확장된 행의 상세 정보를 백그라운드에서 로딩한다.
        /// (1) 보조정보.alg를 파싱해 헤더 요약과 전체 스키마를 채우고,
        /// (2) 각 WAV 헤더(샘플레이트·채널·비트·길이)를 SMB 대역폭을 고려해 순차 파싱한다(병렬 회피).
        /// 파일 IO만 수행하며 DB 접근은 없다. 진행 중에는 <see cref="CollectionRow.DetailLoading"/>를 세운다.
        /// </summary>
        private static async Task LoadDetailAsync(CollectionRow row)
        {
            row.DetailLoading = true;
            try
            {
                // 1) 보조정보.alg 파싱 — 헤더 요약 + 전체 스키마(발췌 원음 탭과 공용)
                if (row.HasSupplementAlg && !string.IsNullOrEmpty(row.SupplementAlgPath))
                {
                    await Task.Run(() =>
                    {
                        row.ParsedAlg  = AlgParser.TryParse(row.SupplementAlgPath!);
                        row.Supplement = Features.Supplement.SupplementView.TryLoad(row.SupplementAlgPath!);
                    });
                }

                // 2) WAV 헤더 순차 파싱 (SMB 대역폭 고려해 병렬 회피)
                var items = await Task.Run(() =>
                {
                    var list = new List<WavDetailRow>(row.WavFiles.Length);
                    foreach (var wav in row.WavFiles)
                    {
                        long size = 0;
                        try { size = new FileInfo(wav).Length; } catch { }
                        var h = WavHeader.TryRead(wav);
                        list.Add(new WavDetailRow
                        {
                            FileName = Path.GetFileName(wav),
                            FullPath = wav,
                            SizeBytes = size,
                            SampleRate = h.SampleRate,
                            Channels = h.Channels,
                            BitsPerSample = h.BitsPerSample,
                            AudioLengthSec = h.AudioLengthSec,
                        });
                    }
                    return list;
                });

                row.WavDetails.Clear();
                foreach (var it in items) row.WavDetails.Add(it);
            }
            finally
            {
                row.DetailLoading = false;
            }
        }

        /// <summary>비주얼 트리를 위로 거슬러 올라가며 지정한 형식 <typeparamref name="T"/>의 첫 조상 요소를 찾는다.</summary>
        /// <returns>찾으면 해당 요소, 없으면 null.</returns>
        private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T t) return t;
            return null;
        }
    }
}
