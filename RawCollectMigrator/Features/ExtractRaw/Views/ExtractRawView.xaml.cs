using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.ExtractRaw
{
    public partial class ExtractRawView : UserControl
    {
        /// <summary>협대역(NarrowBand) 스캔 결과 원본 컬렉션. 그리드는 이 컬렉션의 뷰(_nbView)를 바인딩한다.</summary>
        private readonly ObservableCollection<ExtractItem> _nb = new();
        /// <summary>단주기(ShortPeriod, *.sd) 스캔 결과 원본 컬렉션. 그리드는 이 컬렉션의 뷰(_sdView)를 바인딩한다.</summary>
        private readonly ObservableCollection<ExtractItem> _sd = new();
        /// <summary>_nb에 WAV 유무 필터를 적용한 표시용 뷰.</summary>
        private readonly ICollectionView _nbView;
        /// <summary>_sd에 WAV 유무 필터를 적용한 표시용 뷰.</summary>
        private readonly ICollectionView _sdView;

        /// <summary>CP949(EUC-KR) 등 레거시 코드페이지를 쓰려면 인코딩 공급자를 먼저 등록해야 하므로 정적 생성자에서 1회 등록한다.</summary>
        static ExtractRawView()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>뷰를 초기화하고 두 컬렉션에 WAV 필터를 건 뒤 각 그리드의 ItemsSource로 연결한다.</summary>
        public ExtractRawView()
        {
            InitializeComponent();
            _nbView = CollectionViewSource.GetDefaultView(_nb);
            _sdView = CollectionViewSource.GetDefaultView(_sd);
            _nbView.Filter = FilterNb;
            _sdView.Filter = FilterSd;
            NbGrid.ItemsSource = _nbView;
            SdGrid.ItemsSource = _sdView;

            // [찾기:접속설정] 접속·경로의 단일 원본은 AppSession(시작 화면)이다.
            Loaded += (_, _) =>
            {
                NbPathBox.Text = Session.NarrowBandRoot;
                SdPathBox.Text = Session.ShortPeriodRoot;
                TargetInfoText.Text = "적재 대상 — " + Infrastructure.AppSession.StripPassword(Session.TargetConn)
                    + "     (변경은 상단 [⚙ 연결 설정])";
            };
            Session.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                TargetInfoText.Text = "적재 대상 — " + Infrastructure.AppSession.StripPassword(Session.TargetConn)
                    + "     (변경은 상단 [⚙ 연결 설정])"));
        }

        /// <summary>접속·경로의 단일 원본.</summary>
        private static Infrastructure.AppSession Session => Infrastructure.AppSession.Current;

        // ── 필터: WAV 없는 폴더는 기본 숨김. 체크박스 켜면 표시. ──
        /// <summary>협대역 그리드 필터. WAV가 있거나 '빈 폴더 포함' 체크 시 표시.</summary>
        /// <param name="o">필터 대상(ExtractItem 기대).</param>
        /// <returns>표시하면 true.</returns>
        private bool FilterNb(object o) => o is ExtractItem it && (NbIncludeEmpty.IsChecked == true || it.WavCount > 0);
        /// <summary>단주기 그리드 필터. WAV가 있거나 '빈 폴더 포함' 체크 시 표시.</summary>
        /// <param name="o">필터 대상(ExtractItem 기대).</param>
        /// <returns>표시하면 true.</returns>
        private bool FilterSd(object o) => o is ExtractItem it && (SdIncludeEmpty.IsChecked == true || it.WavCount > 0);

        /// <summary>협대역 '빈 폴더 포함' 체크 변경 시 뷰를 갱신하고 요약을 다시 계산한다.</summary>
        private void OnNbIncludeEmptyChanged(object sender, RoutedEventArgs e) { _nbView.Refresh(); UpdateNbSummary(); }
        /// <summary>단주기 '빈 폴더 포함' 체크 변경 시 뷰를 갱신하고 요약을 다시 계산한다.</summary>
        private void OnSdIncludeEmptyChanged(object sender, RoutedEventArgs e) { _sdView.Refresh(); UpdateSdSummary(); }

        // [찾기:자동스캔]
        /// <summary>
        /// 작업 화면 진입 직후 협대역·단주기 폴더를 자동으로 스캔한다.
        /// 사용자가 탭마다 [스캔]을 눌러야 목록이 생기던 것을, 준비된 상태로 열리게 한다.
        /// 경로가 접근 불가면 조용히 건너뛴다(해당 작업만 못 할 뿐 화면은 정상).
        /// </summary>
        public async Task AutoScanAsync()
        {
            if (_autoScanned) return;
            _autoScanned = true;

            NbPathBox.Text = Session.NarrowBandRoot;
            SdPathBox.Text = Session.ShortPeriodRoot;

            if (System.IO.Directory.Exists(NbPathBox.Text.Trim())) await ScanNbAsync();
            if (System.IO.Directory.Exists(SdPathBox.Text.Trim())) await ScanSdAsync();
        }

        private bool _autoScanned;

        // ── 협대역 ──
        /// <summary>폴더 선택 대화상자를 열어 협대역원음자료 루트 경로를 NbPathBox에 채운다.</summary>
        private void OnSelectNbFolder(object sender, RoutedEventArgs e) => PickFolder(NbPathBox, "협대역원음자료 루트");
        /// <summary>지정 경로를 백그라운드로 스캔해 협대역 항목을 채우고, 선택 알림 연결·뷰 갱신·요약/대상 카운트를 갱신한다(비동기, 파일 IO, _nb 컬렉션 교체).</summary>
        private async void OnScanNb(object sender, RoutedEventArgs e) => await ScanNbAsync();

        /// <summary>협대역 스캔 본체(버튼·자동 스캔 공용).</summary>
        private async Task ScanNbAsync()
        {
            var path = NbPathBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                MessageBox.Show(Window.GetWindow(this), "경로가 존재하지 않습니다:\n" + path, "스캔",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Session.NarrowBandRoot = path;   // 이 화면에서 바꾼 경로도 단일 원본에 반영
            NbSummary.Text = "스캔 중...";
            _nb.Clear();
            try
            {
                var items = await Task.Run(() => ExtractScanner.ScanNarrowBand(path));
                foreach (var it in items) _nb.Add(it);
                HookSelection(items);
                _nbView.Refresh();
                UpdateNbSummary();
                UpdateMigTargetInfo();
            }
            catch (Exception ex)
            {
                NbSummary.Text = "스캔 오류: " + ex.Message;
                MessageBox.Show(Window.GetWindow(this), ex.Message, "협대역 스캔 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>협대역 스캔 결과를 집계해(전체/원음 있음/표시/alg 유효 수 + 원음 총용량) 요약 텍스트를 갱신한다.</summary>
        private void UpdateNbSummary()
        {
            int total = _nb.Count;
            int nonEmpty = _nb.Count(i => i.WavCount > 0);
            int shown = _nb.Count(FilterNb);
            long size = _nb.Where(i => i.WavCount > 0).Sum(i => i.WavTotalBytes);
            int algOk = _nb.Count(i => i.HasAlg);
            NbSummary.Text = $"총 {total}개 (원음 있음 {nonEmpty}, 표시 {shown}) / alg {algOk} / 원음 {size / 1024.0 / 1024 / 1024:F1} GB";
        }

        // ── 단주기 ──
        /// <summary>폴더 선택 대화상자를 열어 단주기원음자료 루트 경로를 SdPathBox에 채운다.</summary>
        private void OnSelectSdFolder(object sender, RoutedEventArgs e) => PickFolder(SdPathBox, "단주기원음자료 루트");
        /// <summary>지정 경로에서 *.sd를 백그라운드로 스캔해 단주기 항목을 채우고, 선택 알림 연결·뷰 갱신·요약/대상 카운트를 갱신한다(비동기, 파일 IO, _sd 컬렉션 교체).</summary>
        private async void OnScanSd(object sender, RoutedEventArgs e) => await ScanSdAsync();

        /// <summary>단주기 스캔 본체(버튼·자동 스캔 공용).</summary>
        private async Task ScanSdAsync()
        {
            var path = SdPathBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                MessageBox.Show(Window.GetWindow(this), "경로가 존재하지 않습니다:\n" + path, "스캔",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Session.ShortPeriodRoot = path;
            SdSummary.Text = "*.sd 스캔 중...";
            _sd.Clear();
            try
            {
                var items = await Task.Run(() => ExtractScanner.ScanShortPeriod(path));
                foreach (var it in items) _sd.Add(it);
                HookSelection(items);
                _sdView.Refresh();
                UpdateSdSummary();
                UpdateMigTargetInfo();
            }
            catch (Exception ex)
            {
                SdSummary.Text = "스캔 오류: " + ex.Message;
                MessageBox.Show(Window.GetWindow(this), ex.Message, "단주기 스캔 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>단주기 스캔 결과를 집계해(전체/원음/표시 수 + SPS·이벤트·alg 유효 수 + 이벤트·세그먼트 합계 + 원음 총용량) 요약 텍스트를 갱신한다.</summary>
        private void UpdateSdSummary()
        {
            int total = _sd.Count;
            int nonEmpty = _sd.Count(i => i.WavCount > 0);
            int shown = _sd.Count(FilterSd);
            long size = _sd.Where(i => i.WavCount > 0).Sum(i => i.WavTotalBytes);
            int spsOk = _sd.Count(i => i.HasSpsList);
            int evtOk = _sd.Count(i => i.HasEventList);
            int algOk = _sd.Count(i => i.HasAlg);
            int totalEvt = _sd.Sum(i => i.LstEventCount);
            int totalSeg = _sd.Sum(i => i.LstSegmentCount);
            SdSummary.Text =
                $"총 {total}개 *.sd (원음 있음 {nonEmpty}, 표시 {shown}) / SPS {spsOk} / 이벤트 {evtOk} / alg {algOk}" +
                $" / 이벤트 합 {totalEvt} / 세그먼트 합 {totalSeg} / 원음 {size / 1024.0 / 1024 / 1024:F1} GB";
        }

        /// <summary>폴더 선택 대화상자를 띄워 선택 경로를 텍스트박스에 반영한다. 기존 경로가 유효하면 그곳을, 아니면 NAS 공유(MSU)를 초기 위치로 연다.</summary>
        /// <param name="tb">선택 결과를 채울 대상 텍스트박스.</param>
        /// <param name="title">대화상자 제목.</param>
        private void PickFolder(TextBox tb, string title)
        {
            var dlg = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = Directory.Exists(tb.Text) ? tb.Text : @"\\192.168.111.165\naims_share\MSU"
            };
            if (dlg.ShowDialog() == true) tb.Text = dlg.FolderName;
        }

        // ── 행 더블클릭 → 상세 확장/축소 ──
        /// <summary>협대역 행 더블클릭 시 상세 영역을 토글한다(비동기).</summary>
        private async void OnRowDoubleClickNb(object sender, MouseButtonEventArgs e) => await HandleRowDoubleClickAsync(e);
        /// <summary>단주기 행 더블클릭 시 상세 영역을 토글한다(비동기).</summary>
        private async void OnRowDoubleClickSd(object sender, MouseButtonEventArgs e) => await HandleRowDoubleClickAsync(e);

        /// <summary>더블클릭한 DataGridRow의 항목을 확장/축소하고, 처음 펼칠 때만 상세를 지연 로드한다(비동기, 항목 상태·행 표시 변경).</summary>
        /// <param name="e">더블클릭 이벤트 인자(원본 소스에서 DataGridRow를 역추적).</param>
        private static async Task HandleRowDoubleClickAsync(MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;
            var row = FindAncestor<DataGridRow>(src);
            if (row?.Item is not ExtractItem it) return;

            it.IsExpanded = !it.IsExpanded;
            row.DetailsVisibility = it.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;

            if (it.IsExpanded && !it.DetailLoaded)
            {
                it.DetailLoaded = true;
                await LoadDetailAsync(it);
            }
        }

        /// <summary>
        /// 항목의 상세를 백그라운드에서 파싱해 채운다: alg 헤더·전체 구조(Supplement), 단주기면 LST 이벤트/SPS 리스트.
        /// 파싱 결과로 LstEvents·SpsEvents 컬렉션과 요약 문자열을 갱신한다(비동기, 파일 IO, 항목 컬렉션/상태 변경).
        /// </summary>
        /// <param name="it">상세를 채울 대상 항목.</param>
        private static async Task LoadDetailAsync(ExtractItem it)
        {
            it.DetailLoading = true;
            try
            {
                // 1) alg 전체 스키마 파싱 (Main 프로젝트 AlgReader 이식본)
                if (it.HasAlg && !string.IsNullOrEmpty(it.AlgPath))
                {
                    await Task.Run(() =>
                    {
                        it.ParsedAlg   = AlgParser.TryParse(it.AlgPath!);                        // 헤더 요약
                        it.Supplement  = Features.Supplement.SupplementView.TryLoad(it.AlgPath!); // 전체 구조
                    });
                }

                // 2) 단주기: SPS/LST 파싱 결과
                if (it.Kind == ExtractKind.ShortPeriod)
                {
                    (LstHeader hdr, List<SdEvent> lstEvts, List<SdEvent> spsEvts,
                     string? spsLayout, string? error) result = default;
                    await Task.Run(() =>
                    {
                        var lst = it.HasEventList && !string.IsNullOrEmpty(it.EventListPath)
                            ? EventListReader.Read(it.EventListPath!)
                            : (header: new LstHeader(), events: new List<SdEvent>(), error: (string?)null);
                        var sps = it.HasSpsList && !string.IsNullOrEmpty(it.SpsListPath)
                            ? SpsListReader.Read(it.SpsListPath!)
                            : (events: new List<SdEvent>(), layout: (string?)null, error: (string?)null);
                        // 파싱 실패 사유는 합쳐서 화면에 드러낸다(침묵 부분-결과 금지).
                        var err = string.Join(" / ", new[] { lst.error, sps.error }.Where(s => !string.IsNullOrEmpty(s)));
                        result = (lst.header, lst.events, sps.events, sps.layout, err.Length == 0 ? null : err);
                    });

                    it.LstHeader = result.hdr;

                    it.LstEvents.Clear();
                    foreach (var ev in result.lstEvts) it.LstEvents.Add(ev);
                    int lstSegTotal = result.lstEvts.Sum(e => e.SegmentCount);
                    it.LstSummary = $"(이벤트 {result.lstEvts.Count} / 세그먼트 합 {lstSegTotal})";

                    it.SpsEvents.Clear();
                    foreach (var ev in result.spsEvts) it.SpsEvents.Add(ev);
                    int spsSegTotal = result.spsEvts.Sum(e => e.SegmentCount);
                    int aiTotal = result.spsEvts.Sum(e => e.AiSegmentCount);
                    int trTotal = result.spsEvts.Sum(e => e.TrSegmentCount);
                    it.SpsSummary = $"(이벤트 {result.spsEvts.Count} / 세그먼트 {spsSegTotal} = AI {aiTotal}/TR {trTotal})";
                    // 어느 레거시 포맷으로 읽혔는지 드러낸다 — AAS(packed) 인지 CDMU(구조체 통째) 인지가
                    // 값 해석·재현에 필요하고, 이관 로그의 원천 표기와 대조할 수 있어야 한다.
                    if (result.spsLayout != null)
                        it.SpsSummary += $"   [{SpsListReader.DescribeLayout(result.spsLayout)}]";

                    // 파싱이 도중에 끊겼으면 요약에 명시 — 부분 결과가 정상으로 보이지 않게 한다.
                    if (result.error != null)
                        it.LstSummary += $"   ⚠ {result.error}";
                }
            }
            finally
            {
                it.DetailLoading = false;
            }
        }

        /// <summary>비주얼 트리를 위로 거슬러 올라가며 지정 타입 T의 조상 요소를 찾는다(더블클릭 원본에서 DataGridRow 탐색용).</summary>
        /// <typeparam name="T">찾을 조상 타입.</typeparam>
        /// <param name="start">탐색 시작 요소.</param>
        /// <returns>찾은 조상, 없으면 null.</returns>
        private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T t) return t;
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  이관 (PREC_SRC_* 계열)
        // ══════════════════════════════════════════════════════════════

        /// <summary>진행 중인 이관/시험 실행을 취소하기 위한 토큰 소스. 실행 중이 아니면 null.</summary>
        private CancellationTokenSource? _migCts;

        /// <summary>현재 화면에 보이는 두 목록에서 선택된 항목 전부.</summary>
        private List<ExtractItem> SelectedItems()
            => _nb.Where(i => i.IsSelected).Concat(_sd.Where(i => i.IsSelected)).ToList();

        // ── 전체 선택 / 해제 ──
        // 필터로 가려진 항목은 건드리지 않고, 이관 가능한(IsReady) 항목만 선택한다.
        /// <summary>화면에 표시된 협대역 항목 중 이관 가능한(IsReady) 것을 모두 선택한다.</summary>
        private void OnNbSelectAll(object sender, RoutedEventArgs e)  => SetSelection(_nb, FilterNb, true);
        /// <summary>화면에 표시된 협대역 항목의 선택을 모두 해제한다.</summary>
        private void OnNbSelectNone(object sender, RoutedEventArgs e) => SetSelection(_nb, FilterNb, false);
        /// <summary>화면에 표시된 단주기 항목 중 이관 가능한(IsReady) 것을 모두 선택한다.</summary>
        private void OnSdSelectAll(object sender, RoutedEventArgs e)  => SetSelection(_sd, FilterSd, true);
        /// <summary>화면에 표시된 단주기 항목의 선택을 모두 해제한다.</summary>
        private void OnSdSelectNone(object sender, RoutedEventArgs e) => SetSelection(_sd, FilterSd, false);

        /// <summary>필터에 걸린(화면에 보이는) 항목만 대상으로 선택 상태를 일괄 변경한다. 선택 시에는 IsReady 항목만 켜며, 이후 대상 카운트를 갱신한다(항목 IsSelected 변경).</summary>
        /// <param name="src">대상 컬렉션(_nb 또는 _sd).</param>
        /// <param name="visible">화면 표시 여부 판정 필터.</param>
        /// <param name="value">설정할 선택 값(true=선택, false=해제).</param>
        private void SetSelection(
            ObservableCollection<ExtractItem> src, Func<object, bool> visible, bool value)
        {
            foreach (var it in src)
            {
                if (!visible(it)) continue;          // 화면에 없는 항목은 제외
                if (value && !it.IsReady) continue;  // 필수 파일이 없으면 선택 불가
                it.IsSelected = value;
            }
            UpdateMigTargetInfo();
        }

        /// <summary>스캔 결과에 선택 변경 알림을 연결해 상단 카운트를 실시간 갱신한다.</summary>
        private void HookSelection(IEnumerable<ExtractItem> items)
        {
            foreach (var it in items)
            {
                it.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(ExtractItem.IsSelected))
                        Dispatcher.BeginInvoke(new Action(UpdateMigTargetInfo));
                };
            }
        }

        /// <summary>현재 UI 체크 상태로 이관 옵션을 구성한다(시험 실행 여부, 중복 건너뛰기).</summary>
        /// <param name="dryRun">true면 INSERT 후 롤백하는 시험 실행.</param>
        /// <returns>구성된 이관 옵션.</returns>
        private ExtractOptions MigOptions(bool dryRun) => new()
        {
            DryRun = dryRun,
            SkipDuplicates = MigSkipDup.IsChecked == true,
        };

        /// <summary>선택 항목을 반환하되, 하나도 없으면 경고 메시지를 띄우고 null을 반환한다.</summary>
        /// <returns>선택 항목 목록, 선택이 없으면 null.</returns>
        private List<ExtractItem>? GetTargetsOrWarn()
        {
            var sel = SelectedItems();
            if (sel.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "선택된 항목이 없습니다.\n\n협대역·단주기 탭에서 [대상] 열을 체크하세요.\n" +
                    "(필수 파일이 없는 항목은 선택할 수 없습니다)",
                    "이관", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return sel;
        }

        /// <summary>선택/이관 가능 건수와 선택 원음 총용량을 집계해 이관 대상 정보 텍스트를 갱신한다.</summary>
        private void UpdateMigTargetInfo()
        {
            int nbSel = _nb.Count(i => i.IsSelected);
            int sdSel = _sd.Count(i => i.IsSelected);
            int nbReady = _nb.Count(i => i.IsReady);
            int sdReady = _sd.Count(i => i.IsReady);
            long bytes = _nb.Concat(_sd).Where(i => i.IsSelected).Sum(i => i.WavTotalBytes);

            MigTargetInfo.Text =
                $"협대역 {nbSel}/{nbReady} · 단주기 {sdSel}/{sdReady} 선택"
                + (nbSel + sdSel > 0 ? $"  ({bytes / 1024.0 / 1024:F1} MB)" : "");
        }

        /// <summary>선택 항목에 대해 사전 점검(중복·필수 파일·DB 상태 등)을 수행하고 결과를 메시지박스로 보여준다(비동기, DB 읽기).</summary>
        private async void OnMigPreflight(object sender, RoutedEventArgs e)
        {
            if (GetTargetsOrWarn() is not { } sel) return;

            MigProgressText.Text = "사전 점검 중...";
            try
            {
                var rep = await ExtractMigrationExecutor.PreflightAsync(
                    Session.TargetConn.Trim(), sel, MigOptions(false));

                MigProgressText.Text = rep.CanProceed ? "사전 점검 통과" : "사전 점검 실패";
                MessageBox.Show(Window.GetWindow(this),
                    (rep.CanProceed ? "✅ 진행 가능합니다.\n\n" : "❌ 문제가 있어 진행할 수 없습니다.\n\n") + rep.ToText(),
                    "사전 점검 결과", MessageBoxButton.OK,
                    rep.CanProceed
                        ? (rep.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information)
                        : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MigProgressText.Text = "사전 점검 오류";
                MessageBox.Show(Window.GetWindow(this), ExceptionText.Describe(ex), "사전 점검 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>선택 항목으로 시험 실행을 확인받은 뒤 실제 INSERT 후 롤백하는 방식으로 이관을 예행한다(비동기, DB 쓰기 후 롤백).</summary>
        private async void OnMigDryRun(object sender, RoutedEventArgs e)
        {
            if (GetTargetsOrWarn() is not { } sel) return;

            var msg = $"선택된 {sel.Count}건으로 시험 실행합니다.\n\n" +
                      "실제 INSERT 를 수행한 뒤 롤백하므로 DB에는 반영되지 않습니다.\n\n계속하시겠습니까?";
            if (MessageBox.Show(Window.GetWindow(this), msg, "시험 실행",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            await RunMigrationAsync(sel, dryRun: true);
        }

        /// <summary>사전 점검을 먼저 수행해 통과 시에만, 대상 요약을 확인받은 뒤 실제 이관(DB 반영)을 실행한다(비동기, DB 쓰기/커밋).</summary>
        private async void OnMigRun(object sender, RoutedEventArgs e)
        {
            if (GetTargetsOrWarn() is not { } sel) return;

            MigProgressText.Text = "사전 점검 중...";
            ExtractPreflight rep;
            try
            {
                rep = await ExtractMigrationExecutor.PreflightAsync(
                    Session.TargetConn.Trim(), sel, MigOptions(false));
            }
            catch (Exception ex)
            {
                MigProgressText.Text = "사전 점검 오류";
                MessageBox.Show(Window.GetWindow(this), ExceptionText.Describe(ex), "사전 점검 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!rep.CanProceed)
            {
                MigProgressText.Text = "사전 점검 실패";
                MessageBox.Show(Window.GetWindow(this),
                    "❌ 사전 점검을 통과하지 못해 중단합니다.\n\n" + rep.ToText(),
                    "중단", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int nb = sel.Count(i => i.Kind == ExtractKind.NarrowBand);
            int sd = sel.Count(i => i.Kind == ExtractKind.ShortPeriod);
            var msg = $"선택된 {sel.Count}건을 실제로 이관합니다.\n\n" +
                      $"  • 협대역 {nb}건 → PREC_SRC_SIG + PREC_SRC_NB_SIG\n" +
                      $"  • 단주기 {sd}건 → PREC_SRC_SIG + PREC_SRC_SS_SIG_EVENT/SEGMENT\n" +
                      $"  • WAV → EXTRACT_DATA_STORE\n\n" +
                      "── 사전 점검 ──\n" + rep.ToText() +
                      "\n이 작업은 DB에 실제로 반영됩니다.\n계속하시겠습니까?";
            if (MessageBox.Show(Window.GetWindow(this), msg, "실행 확인",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                MigProgressText.Text = "취소됨";
                return;
            }

            await RunMigrationAsync(sel, dryRun: false);
        }

        /// <summary>
        /// 진행 중인 이관/시험 실행에 취소를 요청한다.
        /// finally 의 Dispose 와 겹칠 수 있어 ObjectDisposedException 을 흡수한다.
        /// </summary>
        private void OnMigCancel(object sender, RoutedEventArgs e)
        {
            try { _migCts?.Cancel(); }
            catch (ObjectDisposedException) { /* 이미 끝난 실행 — 무시 */ }
        }

        /// <summary>
        /// 이관/시험 실행의 공통 실행부. 각 항목 이관 상태를 초기화하고 진행률을 UI에 반영하며 실행기를 호출한 뒤,
        /// 성공/실패/건너뜀 집계와 결과 로그 기록, 결과 메시지박스 표시, 취소·오류 처리, 토큰 정리까지 담당한다
        /// (비동기, 취소 가능, dryRun이 아니면 DB 쓰기, 파일 로그 기록, 항목/진행률 UI 상태 변경).
        /// </summary>
        /// <param name="sel">이관 대상 항목 목록.</param>
        /// <param name="dryRun">true면 롤백되는 시험 실행, false면 실제 반영.</param>
        private async Task RunMigrationAsync(List<ExtractItem> sel, bool dryRun)
        {
            // [찾기:재진입가드] 실행 중 재클릭 방지. 없으면 같은 항목이 동시에 두 번 이관돼
            // 양쪽 다 중복검사를 통과한 뒤 중복 INSERT 되고, _migCts 가 덮어써져 첫 실행은 취소도 안 된다.
            if (_migCts != null) return;
            _migCts = new CancellationTokenSource();
            SetMigButtonsEnabled(false);
            MigProgressBar.Value = 0;
            MigProgressPct.Text = "0 %";
            MigProgressText.Text = dryRun ? "시험 실행 시작..." : "이관 시작...";

            foreach (var it in sel)
            {
                it.MigStatus = "대기";
                it.MigError = "";
                it.MigSigId = null;
                it.MigSupplementCount = 0;
                it.MigDataStoreCount = 0;
                it.MigDetailCount = 0;
            }

            var progress = new Progress<ExtractProgress>(p =>
            {
                if (p.Total > 0)
                {
                    double pct = 100.0 * p.Done / p.Total;
                    MigProgressBar.Value = pct;
                    MigProgressPct.Text = $"{pct:F0} %";
                }
                MigProgressText.Text = $"[{p.Done}/{p.Total}] {p.CurrentItem} — {p.Phase}";
            });

            try
            {
                await ExtractMigrationExecutor.RunAsync(
                    Session.TargetConn.Trim(), sel, progress, _migCts.Token, MigOptions(dryRun));

                int ok      = sel.Count(i => i.MigStatus is "완료" or "시험완료");
                int fail    = sel.Count(i => i.MigStatus == "실패");
                int skipped = sel.Count(i => i.MigStatus == "건너뜀");

                var label = dryRun ? "시험 실행" : "이관";
                WriteExtractLog(label, sel, ok, fail, skipped);
                MigProgressBar.Value = 100;
                MigProgressPct.Text = "100 %";
                MigProgressText.Text = $"{label} 완료 — 성공 {ok} / 실패 {fail} / 건너뜀 {skipped}";

                var body = $"{label} 완료\n\n성공: {ok}건\n실패: {fail}건\n건너뜀: {skipped}건";
                if (dryRun) body += "\n\n※ 모두 롤백되어 DB에는 반영되지 않았습니다.";
                if (fail > 0) body += "\n\n실패 사유는 그리드의 [이관 결과] 열을 확인하세요.";

                MessageBox.Show(Window.GetWindow(this), body, "결과", MessageBoxButton.OK,
                    fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                MigProgressText.Text = "취소됨";
            }
            catch (Exception ex)
            {
                MigProgressText.Text = "오류: " + ex.Message;
                MessageBox.Show(Window.GetWindow(this), ExceptionText.Describe(ex), "이관 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                var cts = _migCts;
                _migCts = null;          // 먼저 비워 취소 핸들러가 폐기된 CTS 를 건드리지 않게 한다
                cts?.Dispose();
                SetMigButtonsEnabled(true);
                UpdateMigTargetInfo();
            }
        }

        /// <summary>이관 실행 중에는 실행 계열 버튼을 잠그고, 취소 버튼만 열어 둔다.</summary>
        private void SetMigButtonsEnabled(bool enabled)
        {
            foreach (var b in new[] { BtnMigrate, BtnMigTrial, BtnMigPreflight })
                if (b != null) b.IsEnabled = enabled;
            if (BtnMigCancel != null) BtnMigCancel.IsEnabled = !enabled;
        }

        /// <summary>
        /// 발췌 이관 결과를 파일로 남긴다(항목별 상태·건수·사유 + 합계).
        /// 고정 경로 logs\extract_last.log 는 매번 덮어쓰고, 시각 박힌 사본을 함께 보관한다.
        /// (DbToDb 로그와 동일한 방식)
        /// </summary>
        private static void WriteExtractLog(
            string label, System.Collections.Generic.IReadOnlyList<ExtractItem> items,
            int ok, int fail, int skipped)
        {
            try
            {
                var dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "logs");
                System.IO.Directory.CreateDirectory(dir);
                var last = System.IO.Path.Combine(dir, "extract_last.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# 발췌 원음 {label} — {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"대상 {items.Count}건  (협대역 {items.Count(i => i.Kind == ExtractKind.NarrowBand)} / " +
                              $"단주기 {items.Count(i => i.Kind == ExtractKind.ShortPeriod)})");
                foreach (var it in items)
                {
                    sb.Append($"  [{it.MigStatus}] {it.KindText} {it.ItemName}");
                    if (it.MigSigId is { } sid) sb.Append($" → SIG_ID={sid}");
                    if (it.MigDataStoreCount > 0 || it.MigSupplementCount > 0 || it.MigDetailCount > 0)
                        sb.Append($" (store {it.MigDataStoreCount} / supp {it.MigSupplementCount} / detail {it.MigDetailCount})");
                    // 단주기는 "어느 인덱스 파일을 실제로 썼는지"를 남긴다 — SPSlist 우선 규칙이
                    // 적용됐는지, .lst 로 내려갔다면 왜인지를 로그만 보고 확인할 수 있어야 한다.
                    if (!string.IsNullOrEmpty(it.MigEventSource)) sb.Append($"  ↳ 원천 {it.MigEventSource}");
                    // RECORDING_* 를 DMF 로 채웠는지 .alg/폴더명으로 채웠는지 — 있는 DMF 를 흘리지 않았음을
                    // 로그만 보고 확인할 수 있어야 한다([NAIMS-ISSUE:DMF-METADATA]).
                    if (!string.IsNullOrEmpty(it.MigMetaSource)) sb.Append($"  ↳ 메타 {it.MigMetaSource}");
                    if (!string.IsNullOrEmpty(it.MigError)) sb.Append($"  ↳ {it.MigError}");
                    sb.AppendLine();
                }
                sb.AppendLine($"완료 — 성공 {ok} / 실패 {fail} / 건너뜀 {skipped}  [{label}]");
                System.IO.File.WriteAllText(last, sb.ToString(), new System.Text.UTF8Encoding(false));

                var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var tag = label.Contains("시험") ? "dryrun" : "commit";
                System.IO.File.Copy(last, System.IO.Path.Combine(dir, $"extract_{stamp}_{tag}.log"), true);
            }
            catch { /* 로그 실패가 이관을 막지 않는다 */ }
        }
    }
}
