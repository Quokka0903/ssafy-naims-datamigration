using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:이관결과창] 실제 이관(Commit) 성공 직후 뜨는 소스↔타겟 레코드 대조 창
    /// <summary>
    /// 실제 이관(커밋) 성공 직후 뜨는 결과 창. 이관된 테이블마다 탭 하나 —
    /// 좌측(소스 레코드)과 우측(이관 완료된 타겟 레코드)을 나란히 전시한다.
    /// 행 짝은 이번 실행이 기록한 (소스 PK → 타겟 RECORD_ID) 매핑으로 맞추므로
    /// 같은 인덱스의 좌우 행이 "무엇이 무엇으로 옮겨졌는지"다.
    /// 셀 선택·호버는 컬럼 매핑 규칙(ColumnMapper)으로 상대편 셀과 동기화된다.
    /// </summary>
    public partial class MigrationResultWindow : Window
    {
        private readonly DbToDbOptions _opt;
        private readonly ObservableCollection<ResultTabItem> _tabs = new();

        // [찾기:레코드매칭] 레코드 단위 대조 탭 상태
        private readonly MigrationReport _report;
        private DataView? _matchView;
        private string _matchTitle = "";

        /// <summary>소스 이름컬럼 복호화용 컨텍스트(최초 필요 시 1회 Probe).</summary>
        private readonly CryptoContext _crypto = new();

        /// <summary>Probe 를 Task 로 캐시해 동시 호출(탭 로드 경합)이 같은 Probe 를 await 하게 한다.</summary>
        private Task? _cryptoProbeTask;

        // ContentTemplate 는 탭 전환 시 재사용되는 단일 인스턴스라 그리드도 각 1개다.
        private DataGrid? _srcGrid, _tgtGrid;
        private bool _syncing;

        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0x3f, 0x5f, 0x3f));

        // [찾기:트리정렬]
        /// <summary>
        /// 테이블을 <b>계통 트리 순</b>으로 늘어놓는다: 뿌리부터 깊이 우선, 부모 바로 뒤에 그 자식.
        /// 형제 사이 순서는 <see cref="MigrationPlan"/> 에 적힌 순서를 따른다 — 그게 사람이 정한 순서다.
        /// 계획에 없는 표는 뒤에 원래 순서대로 붙인다(빠뜨리지 않는 것이 정렬보다 중요하다).
        /// </summary>
        private static List<TableResult> OrderByTree(IReadOnlyList<TableResult> tables)
        {
            var plans = MigrationPlan.Defaults();
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plans.Count; i++) rank[plans[i].SourceTable] = i;

            var byName = new Dictionary<string, TableResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) byName[t.Source] = t;

            // 부모 → 자식들. 부모는 첫 번째 FK 로 정한다(다중 FK 표는 한 계통에만 매단다 —
            // 같은 표가 두 번 나오면 그게 더 헷갈린다).
            var childrenOf = new Dictionary<string, List<TablePlan>>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<TablePlan>();
            foreach (var p in plans)
            {
                if (!byName.ContainsKey(p.SourceTable)) continue;
                var parent = p.ForeignKeys
                    .Select(f => f.ParentSourceTable)
                    .FirstOrDefault(n => byName.ContainsKey(n)
                                         && !n.Equals(p.SourceTable, StringComparison.OrdinalIgnoreCase));
                if (parent == null) { roots.Add(p); continue; }
                if (!childrenOf.TryGetValue(parent, out var list))
                    childrenOf[parent] = list = new List<TablePlan>();
                list.Add(p);
            }

            var outp = new List<TableResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(TablePlan p)
            {
                if (!seen.Add(p.SourceTable)) return;      // 순환·중복 방어
                if (byName.TryGetValue(p.SourceTable, out var t)) outp.Add(t);
                if (!childrenOf.TryGetValue(p.SourceTable, out var kids)) return;
                foreach (var k in kids.OrderBy(k => rank.GetValueOrDefault(k.SourceTable, int.MaxValue)))
                    Walk(k);
            }
            foreach (var r in roots.OrderBy(r => rank.GetValueOrDefault(r.SourceTable, int.MaxValue)))
                Walk(r);

            foreach (var t in tables)          // 계획 밖의 표도 빠뜨리지 않는다
                if (seen.Add(t.Source)) outp.Add(t);
            return outp;
        }

        /// <param name="report">방금 끝난 이관의 테이블별 집계(ID 짝 포함).</param>
        /// <param name="opt">실행에 쓴 옵션 — 레코드·행수 재조회용 접속 정보를 재사용한다.</param>
        public MigrationResultWindow(MigrationReport report, DbToDbOptions opt)
        {
            InitializeComponent();
            _opt = opt;
            _report = report;

            // [찾기:트리정렬] 탭은 <테이블 트리 순>으로 늘어놓는다 —
            //   CLASSES → VESSEL_DATA → CHARACTERISTICS … 처럼 부모 바로 뒤에 그 자식이 온다.
            //   실행 순서(위상 정렬)는 "넣을 수 있는 순서" 라 사람이 훑기엔 흩어져 보인다.
            //   사람은 자료를 계통으로 기억하므로, 찾는 순서와 늘어놓는 순서가 같아야 한다.
            foreach (var t in OrderByTree(report.Tables))
                _tabs.Add(new ResultTabItem(t));

            Tabs.ItemsSource = _tabs;
            HeaderTotals.Text =
                $"대상 {report.Tables.Count}개 테이블  ·  적재 {report.TotalInserted:N0} / 재사용 {report.TotalReused:N0}" +
                $" / 건너뜀 {report.TotalSkipped:N0} / 오류 {report.TotalErrors:N0}   ({DateTime.Now:yyyy-MM-dd HH:mm} 커밋)";

            if (_tabs.Count > 0) Tabs.SelectedIndex = 0;

            BuildOverview(report);
            OverviewGrid.ItemsSource = _overview;
            _ = RefreshOverviewTargetCountsAsync();

            // 레코드 매칭 탭: 짝이 기록된 테이블만 고를 수 있게 한다(빈 표를 보여주지 않으려고).
            MatchTableCombo.ItemsSource = _tabs.Where(t => t.Result.IdPairs.Count > 0).ToList();
            if (MatchTableCombo.Items.Count > 0) MatchTableCombo.SelectedIndex = 0;
            else MatchSummary.Text =
                "(소스 PK → 타겟 RECORD_ID) 짝이 기록된 테이블이 없습니다 — 대조할 레코드가 없습니다.";
        }

        // ══════════ 전체 요약(Overview) ══════════

        private readonly ObservableCollection<OverviewRow> _overview = new();

        /// <summary>리포트에서 테이블별 요약 행과 총계를 만든다(타겟 현재 행수는 이후 비동기로 채움).</summary>
        private void BuildOverview(MigrationReport report)
        {
            _overview.Clear();
            foreach (var t in report.Tables) _overview.Add(new OverviewRow(t));

            int okCnt = _overview.Count(r => r.StateKey == "Ok");
            int misCnt = _overview.Count(r => r.StateKey == "Mismatch");
            int errCnt = _overview.Count(r => r.StateKey == "Error");
            int blkCnt = _overview.Count(r => r.StateKey == "Blocked");
            int srcTotal = report.Tables.Sum(t => t.SourceRows);
            int derived = report.Tables.Sum(t => t.DerivedChildren);

            OverviewTotals.Text =
                $"대상 {report.Tables.Count}개 테이블  ·  소스 {srcTotal:N0}행  →  " +
                $"적재 {report.TotalInserted:N0} / 재사용 {report.TotalReused:N0} / 건너뜀 {report.TotalSkipped:N0}" +
                (derived > 0 ? $" / 산출자식 {derived:N0}" : "") +
                $" / 오류 {report.TotalErrors:N0}";

            OverviewCheck.Text =
                $"검산 일치 {okCnt} · 불일치 {misCnt} · 오류 {errCnt} · 차단 {blkCnt}" +
                (misCnt + errCnt + blkCnt == 0
                    ? "   ✅ 모든 테이블이 검산을 통과했습니다."
                    : "   ⚠ 색이 표시된 행을 확인하세요(불일치=주황, 오류=빨강).");
        }

        /// <summary>각 타겟 테이블의 현재 행수를 조회해 요약에 채운다(DB 접근).</summary>
        private async Task RefreshOverviewTargetCountsAsync()
        {
            foreach (var r in _overview)
            {
                if (r.StateKey == "Blocked") continue;
                try
                {
                    var n = await TableInspector.TryGetRowCountAsync(
                        DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr, r.Target);
                    r.SetTargetNow(n);
                }
                catch { /* 요약 보조 정보 — 실패해도 창을 막지 않는다 */ }
            }
        }

        private async void OnRefreshOverview(object sender, RoutedEventArgs e)
            => await RefreshOverviewTargetCountsAsync();

        /// <summary>요약표를 CSV 로 내보낸다(검증 산출물 제출용).</summary>
        private void OnExportOverviewCsv(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "이관 요약 CSV 저장",
                    Filter = "CSV 파일 (*.csv)|*.csv",
                    FileName = $"이관요약_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                };
                if (dlg.ShowDialog() != true) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("상태,소스 테이블,타겟 테이블,소스,적재,재사용,건너뜀,산출자식,오류,이관 후 타겟,검산");
                foreach (var r in _overview)
                    sb.AppendLine(string.Join(",", new[]
                    {
                        r.StateText, r.Source, r.Target, r.SourceRows.ToString(), r.Inserted.ToString(),
                        r.Reused.ToString(), r.Skipped.ToString(), r.Derived.ToString(), r.Errors.ToString(),
                        r.TargetNow, r.CheckText,
                    }.Select(Csv)));

                // 엑셀이 한글을 깨뜨리지 않도록 UTF-8 BOM 으로 저장한다.
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(this, "저장했습니다.\n\n" + dlg.FileName, "CSV 내보내기",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "CSV 저장 실패\n\n" + ex.Message, "CSV 내보내기",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>CSV 한 칸 이스케이프(쉼표·따옴표·줄바꿈 포함 시 따옴표로 감싼다).</summary>
        private static string Csv(string? v)
        {
            v ??= "";
            return v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;
        }

        // ══════════ 탭 로드 ══════════

        /// <summary>
        /// 탭 선택 시 해당 테이블의 좌우 레코드를 최초 1회 로드한다.
        /// 내부 컨트롤(콤보·그리드)의 SelectionChanged 도 버블링되어 들어오므로 원본이 TabControl 일 때만 처리한다.
        /// </summary>
        private async void OnTabSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
            if (Tabs.SelectedItem is not ResultTabItem tab || tab.Loaded) return;
            tab.Loaded = true;
            await LoadTabAsync(tab);
        }

        private int CurrentLimit()
        {
            var t = (RowLimitCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return int.TryParse(t, out var n) ? n : 100;
        }

        /// <summary>표시 행수 변경 시 모든 탭을 미로드 상태로 되돌리고 현재 탭만 다시 읽는다.</summary>
        private async void OnRowLimitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            foreach (var t in _tabs) t.Loaded = false;
            if (Tabs.SelectedItem is ResultTabItem cur)
            {
                cur.Loaded = true;
                await LoadTabAsync(cur);
            }
        }

        /// <summary>
        /// 선택 탭의 소스/타겟 레코드를 ID 짝 순서로 로드한다(DB 접근).
        /// 좌우 DataTable 은 짝 순서로 재정렬해 같은 행 인덱스 = 같은 이관 건이 되게 하고,
        /// 셀 동기화용 컬럼 대응(ColumnMapper)도 함께 만든다.
        /// </summary>
        private async Task LoadTabAsync(ResultTabItem tab)
        {
            var r = tab.Result;
            int limit = CurrentLimit();
            tab.MappingSummary = "레코드 대조 조회 중…";
            tab.SourceView = null;
            tab.TargetView = null;
            try
            {
                var srcCols = await TableInspector.GetColumnsAsync(
                    _opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, r.Source);
                var tgtCols = await TableInspector.GetColumnsAsync(
                    DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr, r.Target);

                if (srcCols.Count == 0 || tgtCols.Count == 0)
                {
                    tab.MappingSummary = srcCols.Count == 0
                        ? $"소스 테이블 '{r.Source}' 스키마를 읽지 못했습니다 (연결 끊김 또는 접근 불가)."
                        : $"타겟 테이블 '{r.Target}' 스키마를 읽지 못했습니다.";
                    return;
                }

                var maps = ColumnMapper.Build(srcCols, tgtCols, r.Source);

                var pairs = r.IdPairs.Take(limit).ToList();
                if (pairs.Count == 0 || string.IsNullOrEmpty(r.SourcePk))
                {
                    tab.MappingSummary =
                        "이번 실행에서 (소스 PK → 타겟 RECORD_ID) 짝이 기록된 행이 없어 대조할 레코드가 없습니다. " +
                        "(부모단위 스킵·링크 재사용 행은 타겟 RECORD_ID 를 몰라 짝을 기록하지 않습니다)";
                }
                else
                {
                    var crypto = await CryptoForAsync(r.Source);
                    var srcIds = pairs.Select(p => p.Src).ToList();
                    var tgtIds = pairs.Select(p => p.Tgt).ToList();

                    var srcDt = await TableInspector.GetRowsByIdsAsync(
                        _opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr,
                        r.Source, srcCols, r.SourcePk, srcIds, crypto);
                    var tgtDt = await TableInspector.GetRowsByIdsAsync(
                        DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr,
                        r.Target, OrderTargetCols(srcCols, tgtCols, maps), "RECORD_ID", tgtIds);

                    var srcAligned = AlignByIds(srcDt, r.SourcePk, srcIds);
                    var tgtAligned = AlignByIds(tgtDt, "RECORD_ID", tgtIds);

                    tab.BuildColumnMaps(maps, srcAligned, tgtAligned);
                    tab.SourceView = srcAligned.DefaultView;
                    tab.TargetView = tgtAligned.DefaultView;

                    int exact   = maps.Count(x => x.Status == MappingStatus.Exact);
                    int rename  = maps.Count(x => x.Status == MappingStatus.Renamed);
                    int srcOnly = maps.Count(x => x.Status == MappingStatus.SourceOnly);
                    int tgtOnly = maps.Count(x => x.Status == MappingStatus.TargetOnly);
                    tab.MappingSummary =
                        $"컬럼 대응 — 일치 {exact} · 이름변경 {rename} · 소스에만 {srcOnly} · 타겟에만 {tgtOnly}" +
                        $"   |   대조 {pairs.Count:N0}행 표시 (짝 확인 {r.IdPairs.Count:N0}건, 상한 {limit})" +
                        (crypto != null ? "   |   암호화 컬럼은 복호값으로 전시" : "");
                }

                var tgtNow = await TableInspector.TryGetRowCountAsync(
                    DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr, r.Target);
                tab.RefreshStats(tgtNow);
            }
            catch (Exception ex)
            {
                tab.MappingSummary = "레코드 대조 로드 실패: " + ex.Message;
            }
        }

        /// <summary>
        /// 소스 이름컬럼 복호화가 필요한 테이블이면 복호화 컨텍스트를 준비해 돌려준다(최초 1회 Probe).
        /// 복호화 규칙을 못 찾으면 null — 암호화 컬럼은 숨겨진다.
        /// </summary>
        private async Task<CryptoContext?> CryptoForAsync(string sourceTable)
        {
            if (!_opt.UseDecryption) return null;
            if (!CryptoContext.RequiresDecryption(_opt.SourceKind, sourceTable)) return null;
            // Probe 를 Task 로 캐시한다. 탭이 거의 동시에 로드돼도 두 번째 호출이 같은 Probe 를
            // await 하므로, 완료 전 CanDecrypt=false 를 보고 암호화 컬럼을 숨기는 일이 없다.
            _cryptoProbeTask ??= ProbeCryptoAsync();
            await _cryptoProbeTask;
            return _crypto.CanDecrypt ? _crypto : null;
        }

        private async Task ProbeCryptoAsync()
        {
            _crypto.DecryptionEnabled = true;
            try { await _crypto.ProbeAsync(_opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr); }
            catch { /* 복호화는 부가 전시 — 실패해도 대조를 막지 않는다 */ }
        }

        /// <summary>
        /// 타겟 컬럼을 "소스 컬럼 순서에 대응되는 것 먼저, 나머지는 뒤에"로 정렬한다.
        /// 좌우 그리드의 컬럼 진행 방향이 같아야 눈으로 나란히 비교할 수 있다.
        /// </summary>
        private static List<ColumnInfo> OrderTargetCols(
            IReadOnlyList<ColumnInfo> srcCols, IReadOnlyList<ColumnInfo> tgtCols, List<ColumnMapping> maps)
        {
            var srcToTgt = maps
                .Where(m => m.Status is MappingStatus.Exact or MappingStatus.Renamed)
                .GroupBy(m => m.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().TargetName, StringComparer.OrdinalIgnoreCase);
            var tgtByName = tgtCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            var ordered = new List<ColumnInfo>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in srcCols)
                if (srcToTgt.TryGetValue(s.Name, out var tn)
                    && tgtByName.TryGetValue(tn, out var tc) && used.Add(tc.Name))
                    ordered.Add(tc);
            foreach (var t in tgtCols)
                if (used.Add(t.Name)) ordered.Add(t);
            return ordered;
        }

        /// <summary>
        /// 조회 결과를 ID 순서(=이관 짝 순서)로 재정렬한 새 DataTable 을 만든다.
        /// 조회에서 빠진 ID(삭제됨 등)는 ID 만 채운 빈 행으로 남겨 좌우 행 인덱스를 유지한다.
        /// </summary>
        private static DataTable AlignByIds(DataTable dt, string idCol, IReadOnlyList<long> order)
        {
            var res = dt.Clone();
            // dt.Load 가 스키마의 NOT NULL 을 그대로 가져오므로, 빈 행을 넣을 수 있게 제약을 푼다.
            res.Constraints.Clear();
            foreach (DataColumn c in res.Columns)
            {
                c.AllowDBNull = true;
                c.AutoIncrement = false;
                c.ReadOnly = false;
            }

            var byId = new Dictionary<long, DataRow>();
            if (dt.Columns.Contains(idCol))
                foreach (DataRow row in dt.Rows)
                    if (row[idCol] != DBNull.Value && long.TryParse(Convert.ToString(row[idCol]), out var id))
                        byId[id] = row;

            foreach (var id in order)
            {
                if (byId.TryGetValue(id, out var row))
                {
                    res.ImportRow(row);
                }
                else
                {
                    var blank = res.NewRow();
                    if (res.Columns.Contains(idCol)) blank[idCol] = id;
                    res.Rows.Add(blank);
                }
            }
            return res;
        }

        // ══════════ 셀 선택·호버 동기화 ══════════

        private void OnGridLoaded(object sender, RoutedEventArgs e)
        {
            var g = (DataGrid)sender;
            if (g.Name == "SrcGrid") _srcGrid = g; else _tgtGrid = g;
        }

        /// <summary>셀 선택 시 상대편 그리드의 (같은 행 인덱스, 매핑 컬럼) 셀을 선택하고 화면에 보이게 한다.</summary>
        private void OnCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (_syncing) return;
            var grid = (DataGrid)sender;
            var other = ReferenceEquals(grid, _srcGrid) ? _tgtGrid : _srcGrid;
            if (other == null || Tabs.SelectedItem is not ResultTabItem tab) return;
            if (grid.SelectedCells.Count == 0) return;

            var cell = grid.SelectedCells[0];
            if (cell.Column == null || cell.Item == null) return;

            int rowIdx = grid.Items.IndexOf(cell.Item);
            _syncing = true;
            try
            {
                other.SelectedCells.Clear();
                if (rowIdx < 0 || rowIdx >= other.Items.Count) return;

                var otherItem = other.Items[rowIdx];
                var otherCol = MapColumn(tab, grid, other, cell.Column);
                if (otherCol != null)
                {
                    var info = new DataGridCellInfo(otherItem, otherCol);
                    other.SelectedCells.Add(info);
                    other.CurrentCell = info;
                    other.ScrollIntoView(otherItem, otherCol);
                }
                else
                {
                    // 대응 컬럼이 없는(소스에만/타겟에만) 셀 — 행 위치만 맞춰준다.
                    other.ScrollIntoView(otherItem);
                }
            }
            finally { _syncing = false; }
        }

        // ══════════ [찾기:호버싱크] 좌우 강조 — 두 탭이 같은 규칙을 쓴다 ══════════
        //   '테이블별 대조' 에만 있던 셀 호버 짝맞춤을 '레코드 매칭' 까지 넓히고, 셀뿐 아니라
        //   <b>행</b>도 함께 강조한다. 좌우로 눈이 오갈 때 "지금 어느 줄을 보고 있나" 가
        //   양쪽에서 동시에 보여야, 나란히 놓인 두 값이 정말 짝인지 확신할 수 있다.
        //
        //   되돌리기는 ClearValue 로 한다 — 로컬값을 지워야 스타일(불일치 색·판정 색)이 되살아난다.
        //   Background 에 원래 색을 되써 넣으면 불일치 표시가 지워진 채로 남는다.

        /// <summary>지금 칠해 둔 것들. 무엇을 칠했든 같은 방법으로 되돌린다.</summary>
        private readonly List<Control> _painted = new();

        private static readonly Brush RowHoverBrush = new SolidColorBrush(Color.FromRgb(0x2c, 0x3a, 0x2c));

        /// <summary>좌우 짝 그리드. 두 탭 어느 쪽이든 알아본다.</summary>
        private DataGrid? PeerOf(DataGrid? g) =>
            g == null ? null
            : ReferenceEquals(g, _srcGrid) ? _tgtGrid
            : ReferenceEquals(g, _tgtGrid) ? _srcGrid
            : ReferenceEquals(g, MatchGrid) ? MatchTgtGrid
            : ReferenceEquals(g, MatchTgtGrid) ? MatchGrid
            : null;

        /// <summary>짝 컬럼. 대조 탭은 매핑으로, 매칭 탭은 (소스)/(타겟) 순번으로 찾는다.</summary>
        private DataGridColumn? PeerColumn(DataGrid from, DataGrid to, DataGridColumn col)
        {
            bool isMatchTab = ReferenceEquals(from, MatchGrid) || ReferenceEquals(from, MatchTgtGrid);
            if (isMatchTab) return MatchPeerColumn(from, to, col);
            return Tabs.SelectedItem is ResultTabItem tab ? MapColumn(tab, from, to, col) : null;
        }

        private void Paint(Control? c, Brush brush)
        {
            if (c == null) return;
            c.Background = brush;
            _painted.Add(c);
        }

        private void ClearHover()
        {
            foreach (var c in _painted) c.ClearValue(BackgroundProperty);
            _painted.Clear();
        }

        // [NAIMS-ISSUE:HOVER-EVENTSETTER] 스타일 EventSetter(MouseEnter) 로는 못 잡는다.
        //   MouseEnter/Leave 는 <b>버블링되지 않는 direct 이벤트</b>라, 셀 스타일에 건 EventSetter 가
        //   상황에 따라 걸리지 않는다 — 실측: 대조 탭은 되는데 매칭 탭은 같은 배선인데도 안 됐다.
        //   그리드에서 MouseMove(버블링) 를 받아 <마우스 밑에 무엇이 있는지> 직접 찾으면
        //   스타일·템플릿 사정과 무관하게 항상 같은 결과가 나온다.
        private DataGridCell? _lastHoverCell;
        private DataGridRow? _lastHoverRow;

        private void OnGridHoverLeave(object sender, MouseEventArgs e)
        {
            _lastHoverCell = null; _lastHoverRow = null;
            ClearHover();
        }

        private void OnGridHoverMove(object sender, MouseEventArgs e)
        {
            if (sender is not DataGrid grid) return;

            var src = e.OriginalSource as DependencyObject;
            var cell = FindParent<DataGridCell>(src);
            var row = cell != null ? FindParent<DataGridRow>(cell) : FindParent<DataGridRow>(src);

            // 같은 자리면 다시 칠하지 않는다 — MouseMove 는 픽셀마다 오므로 그냥 두면 깜빡인다.
            if (ReferenceEquals(cell, _lastHoverCell) && ReferenceEquals(row, _lastHoverRow)) return;
            _lastHoverCell = cell; _lastHoverRow = row;

            ClearHover();
            if (row == null) return;

            // 이쪽: 행을 옅게, 그 위에 셀을 진하게.
            Paint(row, RowHoverBrush);
            Paint(cell, HoverBrush);

            var other = PeerOf(grid);
            if (other == null) return;

            int rowIdx = grid.Items.IndexOf(row.Item);
            if (rowIdx < 0 || rowIdx >= other.Items.Count) return;
            if (other.ItemContainerGenerator.ContainerFromIndex(rowIdx) is not DataGridRow peerRow) return;

            Paint(peerRow, RowHoverBrush);

            // 짝 컬럼이 없으면(소스에만/타겟에만) 행까지만 맞춘다 — 없는 짝을 지어내지 않는다.
            if (cell?.Column == null) return;
            var otherCol = PeerColumn(grid, other, cell.Column);
            if (otherCol?.GetCellContent(peerRow)?.Parent is DataGridCell peerCell)
                Paint(peerCell, HoverBrush);
        }


        /// <summary>컬럼 매핑 사전으로 상대편 그리드의 대응 컬럼을 찾는다(자동 생성 컬럼의 Header=컬럼명).</summary>
        private static DataGridColumn? MapColumn(
            ResultTabItem tab, DataGrid from, DataGrid to, DataGridColumn col)
        {
            if (col?.Header is not string name) return null;
            var map = from.Name == "SrcGrid" ? tab.SrcToTgtCol : tab.TgtToSrcCol;
            if (!map.TryGetValue(name, out var otherName)) return null;
            return to.Columns.FirstOrDefault(c => c.Header as string == otherName);
        }

        private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        // ══════════ 레코드 매칭 탭 ══════════
        // [찾기:레코드매칭]

        private async void OnMatchTableChanged(object sender, SelectionChangedEventArgs e)
            => await LoadMatchAsync();

        private async void OnMatchReload(object sender, RoutedEventArgs e)
            => await LoadMatchAsync();

        private void OnMatchFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyMatchFilter();

        private async void OnMatchModeChanged(object sender, SelectionChangedEventArgs e)
            => await LoadMatchAsync();

        /// <summary>
        /// 고른 테이블의 레코드 매칭표를 만들어 그리드에 올린다.
        /// 짝(소스 PK ↔ 타겟 RECORD_ID)은 이번 실행 기록이라 추정이 없고,
        /// 여기서 확인하는 것은 "짝지어진 두 행의 값이 실제로 같은가"다.
        /// </summary>
        private async Task LoadMatchAsync()
        {
            if (MatchTableCombo?.SelectedItem is not ResultTabItem tab) return;
            var r = tab.Result;

            MatchGrid.ItemsSource = null;
            MatchTgtGrid.ItemsSource = null;
            _matchView = null;
            MatchSummary.Text = $"{r.Source} → {r.Target} 레코드 대조 중…";

            try
            {
                var plan = MigrationPlan.Defaults()
                    .FirstOrDefault(p => p.SourceTable.Equals(r.Source, StringComparison.OrdinalIgnoreCase));
                var crypto = await CryptoForAsync(r.Source);

                bool byKey = MatchModeCombo?.SelectedIndex == 1;
                var res = byKey
                    ? await RecordMatchBuilder.BuildByKeyAsync(r, plan, _opt, crypto, CurrentLimit())
                    : await RecordMatchBuilder.BuildAsync(
                          r, plan, _opt, TranslateFk, crypto, CurrentLimit());

                if (res.Table.Columns.Count == 0)
                {
                    MatchSummary.Text = res.Summary;
                    return;
                }

                _matchTitle = $"{r.Source}__{r.Target}";
                _matchView = res.Table.DefaultView;
                // 같은 DataView 를 두 표가 함께 본다 — 그래야 n번째 줄끼리 반드시 짝이 된다.
                // (각자 따로 조회하면 필터·정렬이 어긋나는 순간 조용히 다른 행을 견주게 된다.)
                MatchGrid.ItemsSource = _matchView;
                MatchTgtGrid.ItemsSource = _matchView;
                MatchSrcHead.Text = $"📤 소스 — {r.Source}";
                MatchTgtHead.Text = $"📥 타겟 — {r.Target}";
                ApplyMatchFilter();

                MatchSummary.Text = $"{r.Source} → {r.Target}   |   {res.Summary}" +
                    (res.Mismatched > 0
                        ? "   ⚠ '차이' 열에 어긋난 필드가 적혀 있습니다."
                        : res.Unknown > 0 ? "   · 확인불가는 부모 짝이 없거나 한쪽 행이 사라진 경우입니다." : "");
            }
            catch (Exception ex)
            {
                MatchSummary.Text = "레코드 매칭 실패: " + ex.Message;
            }
        }

        /// <summary>
        /// (부모 소스테이블, 소스 PK) → 타겟 RECORD_ID. FK 가 제대로 치환됐는지 대조하는 기준이다.
        /// 이번 실행 리포트의 모든 테이블 짝에서 찾는다.
        /// </summary>
        private long? TranslateFk(string parentSourceTable, long sourceId)
        {
            foreach (var t in _report.Tables)
            {
                if (!t.Source.Equals(parentSourceTable, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (src, tgt) in t.IdPairs)
                    if (src == sourceId) return tgt;
            }
            return null;
        }

        // [찾기:불일치셀]
        /// <summary>
        /// 셀 색을 칠하려고 표에 실어 둔 표시 컬럼은 화면에서 감춘다.
        /// 사람이 읽을 열이 아니라 화면이 읽을 열이다.
        /// </summary>
        private void OnMatchColumnGenerating(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == RecordMatchBuilder.ColDiffCells) { e.Cancel = true; return; }

            // [찾기:레코드매칭] 한 표를 좌·우로 가른다. 열 이름이 곧 소속이다.
            //   타겟 쪽 : "…(타겟)" · "타겟 RECORD_ID" · "차이"(어긋난 내용을 적는 칸)
            //   소스 쪽 : 나머지(판정·구분·소스 PK·자연키·"…(소스)")
            bool isTargetColumn =
                e.PropertyName == "차이"
                || e.PropertyName.StartsWith("타겟 ", StringComparison.Ordinal)
                || e.PropertyName.EndsWith("(타겟)", StringComparison.Ordinal);

            bool wantTarget = ReferenceEquals(sender, MatchTgtGrid);
            if (isTargetColumn != wantTarget) e.Cancel = true;
        }

        // [찾기:레코드매칭] 두 표는 같은 행 목록을 보므로 세로 위치가 어긋나면 안 된다.
        //   어긋난 채로 나란히 놓이면 <다른 레코드끼리 견주면서 맞다고 믿게 된다> — 대조 도구에서
        //   가장 나쁜 실패다. 그래서 한쪽이 구르면 다른 쪽도 같은 만큼 구른다.
        private bool _matchSyncing;

        private void OnMatchScroll(object sender, ScrollChangedEventArgs e)
        {
            if (_matchSyncing) return;
            SyncScroll(e, ReferenceEquals(sender, MatchGrid) ? MatchTgtGrid : MatchGrid);
        }

        // [찾기:스크롤동기] 좌우 대조 표는 <가로도> 함께 움직여야 한다.
        //   세로만 맞추면 같은 행을 보면서 서로 다른 컬럼을 보게 된다. 컬럼 순서를 같게 맞춰 놓았는데
        //   한쪽만 옆으로 밀리면, 사람은 나란히 놓인 두 값이 짝이라고 읽는다 — 아니어도 그렇게 읽는다.
        private void OnCompareScroll(object sender, ScrollChangedEventArgs e)
        {
            if (_matchSyncing) return;
            SyncScroll(e, ReferenceEquals(sender, _srcGrid) ? _tgtGrid : _srcGrid);
        }

        private void SyncScroll(ScrollChangedEventArgs e, DataGrid? other)
        {
            if (other == null) return;
            var sv = FindScroll(other);
            if (sv == null) return;

            _matchSyncing = true;
            try
            {
                if (e.VerticalChange != 0) sv.ScrollToVerticalOffset(e.VerticalOffset);
                if (e.HorizontalChange != 0) sv.ScrollToHorizontalOffset(e.HorizontalOffset);
            }
            finally { _matchSyncing = false; }
        }

        // [찾기:레코드매칭] 좌우 <셀>까지 함께 잡는다.
        //   두 표는 같은 행 목록을 공유하고, 열도 (소스)/(타겟) 짝으로 <같은 순서>로 만들어진다.
        //   그래서 짝 열은 "접미사가 같은 열들 중 몇 번째냐" 로 정확히 찾을 수 있다 —
        //   이름으로 맞추려 들면 이름이 바뀐 컬럼(RENAMED)에서 어긋난다.
        //   행만 맞추고 열을 안 맞추면, 사람은 눈으로 가로 위치를 세어 가며 봐야 한다.
        // [찾기:레코드매칭] 동작하는 '테이블별 대조'(OnCellsChanged)와 <같은 순서>로 맞춘다.
        //   앞서 안 먹던 이유 세 가지:
        //     ① CurrentCell 을 읽었다 — SelectedCellsChanged 시점엔 아직 옮겨지지 않아 빈 값일 때가 있다.
        //        선택 사건의 정본은 e/SelectedCells 다.
        //     ② CurrentCell 을 먼저 <설정>했다 — 단일선택 표에서는 그 뒤의 SelectedCells 조작과 부딪친다.
        //        Add → CurrentCell → ScrollIntoView 순서여야 한다.
        //     ③ 스크롤 동기와 <같은 플래그>를 썼다 — 한쪽이 구르는 중이면 셀 동기가 통째로 무시됐다.
        //        막으려는 되먹임이 다르므로 플래그도 따로 둔다.
        private bool _matchCellSyncing;

        private void OnMatchCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (_matchCellSyncing || sender is not DataGrid grid) return;
            var other = ReferenceEquals(grid, MatchGrid) ? MatchTgtGrid : MatchGrid;
            if (other == null || grid.SelectedCells.Count == 0) return;

            var cell = grid.SelectedCells[0];
            if (cell.Column == null || cell.Item == null) return;

            int rowIdx = grid.Items.IndexOf(cell.Item);
            _matchCellSyncing = true;
            try
            {
                other.SelectedCells.Clear();
                if (rowIdx < 0 || rowIdx >= other.Items.Count) return;

                var otherItem = other.Items[rowIdx];
                var otherCol = MatchPeerColumn(grid, other, cell.Column);
                if (otherCol != null)
                {
                    var info = new DataGridCellInfo(otherItem, otherCol);
                    other.SelectedCells.Add(info);
                    other.CurrentCell = info;
                    other.ScrollIntoView(otherItem, otherCol);
                }
                else
                {
                    // 판정·구분·차이처럼 짝이 없는 열이면 행만 맞춘다.
                    other.ScrollIntoView(otherItem);
                }
            }
            finally { _matchCellSyncing = false; }
        }

        private const string SrcSuffix = "(소스)", TgtSuffix = "(타겟)";

        /// <summary>같은 짝 순번의 반대편 열을 찾는다. 짝이 없는 열이면 null.</summary>
        private static DataGridColumn? MatchPeerColumn(DataGrid from, DataGrid to, DataGridColumn? col)
        {
            if (col == null) return null;
            string fromSfx = Suffix(col);
            if (fromSfx.Length == 0) return null;
            string toSfx = fromSfx == SrcSuffix ? TgtSuffix : SrcSuffix;

            int idx = 0;
            foreach (var c in from.Columns.OrderBy(c => c.DisplayIndex))
            {
                if (Suffix(c) != fromSfx) continue;
                if (ReferenceEquals(c, col)) break;
                idx++;
            }

            int seen = 0;
            foreach (var c in to.Columns.OrderBy(c => c.DisplayIndex))
            {
                if (Suffix(c) != toSfx) continue;
                if (seen++ == idx) return c;
            }
            return null;
        }

        private static string Suffix(DataGridColumn c)
        {
            var h = c.Header as string ?? "";
            if (h.EndsWith(SrcSuffix, StringComparison.Ordinal)) return SrcSuffix;
            if (h.EndsWith(TgtSuffix, StringComparison.Ordinal)) return TgtSuffix;
            return "";
        }

        private static ScrollViewer? FindScroll(DependencyObject? d)
        {
            if (d == null) return null;
            if (d is ScrollViewer sv) return sv;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var r = FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                if (r != null) return r;
            }
            return null;
        }

        private void ApplyMatchFilter()
        {
            if (_matchView == null) return;
            // '문제만' 은 특정 판정값 하나가 아니라 "일치가 아닌 전부" 다 —
            // 판정 종류는 방식(실행기록/자연키)마다 다른데, 사람이 보고 싶은 건 언제나 "일치 아닌 것"이다.
            var col = RecordMatchBuilder.ColVerdict;
            _matchView.RowFilter = (MatchFilterCombo?.SelectedIndex) switch
            {
                1 => $"[{col}] <> '{RecordMatchBuilder.VerdictOk}'",
                2 => $"[{col}] =  '{RecordMatchBuilder.VerdictOk}'",
                _ => "",
            };
        }

        /// <summary>지금 보이는 매칭표를 TSV 로 저장한다(필터·정렬 상태 그대로 — 보이는 것이 곧 산출물).</summary>
        private void OnMatchExport(object sender, RoutedEventArgs e)
        {
            if (_matchView == null) { MessageBox.Show(this, "저장할 매칭표가 없습니다."); return; }
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "레코드 매칭표 저장",
                    Filter = "TSV 파일 (*.tsv)|*.tsv",
                    FileName = $"07_record_match_{_matchTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.tsv",
                };
                if (dlg.ShowDialog() != true) return;

                var dt = _matchView.Table!;
                // 셀 색칠용 숨은 표시 컬럼은 저장하지 않는다 — 화면 사정이지 자료가 아니다.
                var cols = dt.Columns.Cast<DataColumn>()
                    .Where(c => c.ColumnName != RecordMatchBuilder.ColDiffCells).ToList();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Join("\t", cols.Select(c => c.ColumnName)));
                foreach (DataRowView v in _matchView)
                    sb.AppendLine(string.Join("\t", cols.Select(c => Tsv(v[c.ColumnName]))));

                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                MessageBox.Show(this, $"저장했습니다 ({_matchView.Count:N0}행)\n{dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장 실패: " + ex.Message);
            }
        }

        /// <summary>탭·개행이 섞여 TSV 열이 깨지지 않게 치환한다.</summary>
        private static string Tsv(object? v) =>
            (Convert.ToString(v) ?? "").Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');

        // ══════════ 레코드 미리보기 ══════════
        // [찾기:미리보기]

        /// <summary>
        /// 레코드 매칭 표에서 행을 더블클릭하면 그 레코드의 바이너리를 양쪽 규약으로 그려 본다.
        /// 표의 판정은 "값이 같은가" 까지만 본다 — blob 안이 온전한 그램인지는 그려 봐야 알 수 있다.
        /// </summary>
        private void OnMatchRowActivated(object sender, MouseButtonEventArgs e)
        {
            var picked = MatchGrid.SelectedItem
                         ?? (MatchGrid.CurrentCell.IsValid ? MatchGrid.CurrentCell.Item : null)
                         ?? MatchGrid.CurrentItem
                         ?? MatchTgtGrid.SelectedItem
                         ?? (MatchTgtGrid.CurrentCell.IsValid ? MatchTgtGrid.CurrentCell.Item : null)
                         ?? MatchTgtGrid.CurrentItem;
            if (picked is not DataRowView v)
            { MessageBox.Show(this, "미리보기할 행의 셀을 먼저 클릭하세요."); return; }

            long srcPk = FindLong(v, "소스 ");
            long tgtId = FindLong(v, "타겟 RECORD_ID");
            var tab = MatchTableCombo?.SelectedItem as ResultTabItem;
            if (tab == null) { MessageBox.Show(this, "테이블을 먼저 고르세요."); return; }
            if (srcPk <= 0)
            { MessageBox.Show(this, "이 행에서 소스 PK 를 읽지 못했습니다(자연키 대조 모드일 수 있습니다)."); return; }
            OpenPreview(tab.Result.Source, srcPk, tab.Result.Target, tgtId);
        }

        /// <summary>
        /// 테이블별 대조 표에서 더블클릭 — 좌우 어느 쪽을 눌러도 같은 짝을 연다.
        ///
        /// PreviewMouseDoubleClick 을 쓰는 이유: DataGridCell 이 마우스 입력을 먼저 처리해
        /// 버블링 MouseDoubleClick 이 DataGrid 까지 올라오지 않는 경우가 있다(실측: 아무 반응 없음).
        /// 터널링 이벤트는 셀보다 먼저 도달하므로 확실히 잡힌다.
        /// </summary>
        private void OnCompareRowActivated(object sender, MouseButtonEventArgs e)
            => PreviewFromCompare(sender as DataGrid);

        /// <summary>표 머리의 [미리보기] 버튼 — 큰 창으로 연다.</summary>
        private void OnPreviewSelected(object sender, RoutedEventArgs e)
            => PreviewFromCompare(RowIndexOf(_srcGrid) >= 0 ? _srcGrid : _tgtGrid);

        // ══════════ 행 단위 클릭 → 그 행 아래 인라인 전시 ══════════
        // [찾기:미리보기]
        //   셀 선택(SelectionUnit="Cell")은 좌우 매핑 동기화에 필요하므로 그대로 둔다.
        //   그 위에 '행 단위 선택' 을 얹어, 클릭한 행만 파란 테두리 + 바로 아래에 그림을 펼친다.
        //   매번 팝업을 띄우지 않는 이유 — 표를 훑으며 하나씩 확인하는 작업인데 창이 뜨고 닫히면
        //   흐름이 끊긴다. 큰 화면이 필요할 때만 더블클릭으로 창을 연다.

        /// <summary>지금 펼쳐 둔 행(좌·우 각 1개). 다른 행을 누르면 접는다.</summary>
        private DataGridRow? _openSrcRow, _openTgtRow;

        public ObservableCollection<RecordPreviewBuilder.PanelItem> SrcPreview { get; } = new();
        public ObservableCollection<RecordPreviewBuilder.PanelItem> TgtPreview { get; } = new();

        private void OnRowPicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid) return;
            var row = FindRow(e.OriginalSource as DependencyObject);
            if (row == null) return;

            var current = ReferenceEquals(grid, _srcGrid) ? _openSrcRow : _openTgtRow;
            bool sameRowAgain = ReferenceEquals(row, current) && row.DetailsVisibility == Visibility.Visible;

            CollapseAll();
            if (sameRowAgain) return;   // 토글 — 화면을 되돌릴 방법이 있어야 한다

            int idx = grid.Items.IndexOf(row.Item);
            if (idx < 0) return;

            // 좌우 같은 순번 행을 함께 펼친다 — 나란히 비교하는 것이 목적이므로.
            OpenRow(_srcGrid, idx, r => _openSrcRow = r);
            OpenRow(_tgtGrid, idx, r => _openTgtRow = r);
            _ = LoadInlinePreviewAsync(idx);
        }

        private static void OpenRow(DataGrid? grid, int idx, Action<DataGridRow?> assign)
        {
            assign(null);
            if (grid == null || idx < 0 || idx >= grid.Items.Count) return;
            grid.ScrollIntoView(grid.Items[idx]);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromIndex(idx) is DataGridRow r)
            {
                r.DetailsVisibility = Visibility.Visible;
                assign(r);
            }
        }

        private void CollapseAll()
        {
            if (_openSrcRow != null) { _openSrcRow.DetailsVisibility = Visibility.Collapsed; _openSrcRow = null; }
            if (_openTgtRow != null) { _openTgtRow.DetailsVisibility = Visibility.Collapsed; _openTgtRow = null; }
            SrcPreview.Clear(); TgtPreview.Clear();
        }

        private CancellationTokenSource? _previewCts;

        private async Task LoadInlinePreviewAsync(int idx)
        {
            if (Tabs.SelectedItem is not ResultTabItem tab) return;
            var pairs = tab.Result.IdPairs;

            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;

            SrcPreview.Clear(); TgtPreview.Clear();

            if (!RecordPreviewBuilder.Supports(tab.Result.Source))
            {
                SrcPreview.Add(new RecordPreviewBuilder.PanelItem
                { Title = "미리보기 없음", Caption = $"{tab.Result.Source} 은 전시할 바이너리 자료가 없는 표입니다." });
                return;
            }
            if (idx >= pairs.Count)
            {
                SrcPreview.Add(new RecordPreviewBuilder.PanelItem
                { Title = "짝 없음", Caption = $"이 행에는 (소스 PK → 타겟 RECORD_ID) 짝이 기록되지 않았습니다 (짝 {pairs.Count}건)." });
                return;
            }

            var (srcPk, tgtId) = pairs[idx];
            SrcPreview.Add(new RecordPreviewBuilder.PanelItem
            { Title = "읽는 중…", Caption = $"소스 #{srcPk} → 타겟 #{tgtId}" });

            try
            {
                var res = await RecordPreviewBuilder.BuildAsync(
                    _opt, tab.Result.Source, srcPk, tab.Result.Target, tgtId, ct);
                if (ct.IsCancellationRequested) return;

                SrcPreview.Clear();
                foreach (var p in res.Source) SrcPreview.Add(p);
                foreach (var p in res.Target) TgtPreview.Add(p);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SrcPreview.Clear();
                SrcPreview.Add(new RecordPreviewBuilder.PanelItem
                { Title = "미리보기 실패", Caption = "", Error = ex.Message });
            }
        }

        /// <summary>클릭 지점에서 위로 올라가며 DataGridRow 를 찾는다.</summary>
        private static DataGridRow? FindRow(DependencyObject? d)
        {
            while (d != null && d is not DataGridRow)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return d as DataGridRow;
        }

        // [NAIMS-ISSUE:CELL-SELECTION] 이 표들은 SelectionUnit="Cell" 이다(좌우 셀 동기화 때문).
        //   셀 선택 모드에서는 SelectedItem/SelectedIndex 가 채워지지 않아 항상 -1 이 나온다 —
        //   실측: 어느 행을 눌러도 "행을 먼저 선택하세요" 만 떴다.
        //   현재 셀이 가리키는 항목을 Items 에서 찾아 행 번호를 얻는다.
        /// <summary>셀 선택 모드에서도 동작하는 행 번호 찾기. 없으면 -1.</summary>
        private static int RowIndexOf(DataGrid? grid)
        {
            if (grid == null) return -1;
            if (grid.SelectedIndex >= 0) return grid.SelectedIndex;          // 행 선택 모드면 그대로

            var item = grid.CurrentCell.IsValid ? grid.CurrentCell.Item : null;
            if (item == null && grid.SelectedCells.Count > 0) item = grid.SelectedCells[0].Item;
            if (item == null) item = grid.CurrentItem;
            return item == null ? -1 : grid.Items.IndexOf(item);
        }

        private void PreviewFromCompare(DataGrid? grid)
        {
            if (Tabs.SelectedItem is not ResultTabItem tab)
            { MessageBox.Show(this, "테이블을 먼저 고르세요."); return; }

            int idx = RowIndexOf(grid);
            if (idx < 0) { MessageBox.Show(this, "미리보기할 행의 셀을 먼저 클릭하세요."); return; }

            var pairs = tab.Result.IdPairs;
            if (idx >= pairs.Count)
            {
                MessageBox.Show(this,
                    $"이 행에는 (소스 PK → 타겟 RECORD_ID) 짝이 기록되지 않았습니다.\n" +
                    $"표시 {idx + 1}번째 / 짝 {pairs.Count}건");
                return;
            }
            var (src, tgt) = pairs[idx];
            OpenPreview(tab.Result.Source, src, tab.Result.Target, tgt);
        }

        /// <summary>레코드 매칭 표의 [미리보기] 버튼.</summary>
        private void OnPreviewSelectedMatch(object sender, RoutedEventArgs e)
        {
            OnMatchRowActivated(MatchGrid, null!);
        }

        private void OpenPreview(string sourceTable, long sourcePk, string targetTable, long targetId)
        {
            try
            {
                new RecordPreviewWindow(_opt, sourceTable, sourcePk, targetTable, targetId)
                { Owner = this }.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "미리보기를 열지 못했습니다: " + ex.Message);
            }
        }

        /// <summary>컬럼명이 표마다 달라(소스 PK 컬럼명이 섞임) 앞부분이 일치하는 열에서 값을 찾는다.</summary>
        private static long FindLong(DataRowView v, string columnPrefix)
        {
            foreach (System.Data.DataColumn c in v.Row.Table.Columns)
            {
                if (!c.ColumnName.StartsWith(columnPrefix, StringComparison.Ordinal)) continue;
                var raw = v[c.ColumnName];
                if (raw is null or DBNull) return 0;
                return long.TryParse(Convert.ToString(raw), out var n) ? n : 0;
            }
            return 0;
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }

    // [찾기:이관결과창] 전체 요약 표의 한 행
    /// <summary>
    /// 테이블 하나가 "몇 건이 어떻게 옮겨졌는지" 한 줄 요약.
    /// 검산식은 <c>소스 = 적재 + 재사용 + 건너뜀</c> 이다 — 멱등 재실행에서는 단순
    /// 소스/타겟 행수 비교보다 이 식이 정확하다(재사용 분이 타겟에 새로 늘지 않기 때문).
    /// </summary>
    public sealed class OverviewRow : INotifyPropertyChanged
    {
        private readonly TableResult _r;

        public OverviewRow(TableResult r) => _r = r;

        public string Source     => _r.Source;
        public string Target     => _r.Target;
        public int    SourceRows => _r.SourceRows;
        public int    Inserted   => _r.Inserted;
        public int    Reused     => _r.Reused;
        public int    Skipped    => _r.Skipped;
        public int    Derived    => _r.DerivedChildren;
        public int    Errors     => _r.Errors.Count;

        private long? _targetNow;
        public string TargetNow => _targetNow?.ToString("N0") ?? "-";

        /// <summary>이관 후 타겟 실제 행수를 채운다(비동기 조회 결과).</summary>
        public void SetTargetNow(long? n)
        {
            _targetNow = n;
            OnChanged(nameof(TargetNow));
        }

        private int Handled => _r.Inserted + _r.Reused + _r.Skipped;

        /// <summary>행 색 트리거용 키.</summary>
        public string StateKey =>
            _r.Blocked                 ? "Blocked" :
            _r.Errors.Count > 0        ? "Error"   :
            _r.SourceRows != Handled   ? "Mismatch" : "Ok";

        public string StateText => StateKey switch
        {
            "Blocked"  => "⛔ 차단",
            "Error"    => "⚠ 오류",
            "Mismatch" => "△ 불일치",
            _          => "✅ 일치",
        };

        public string CheckText => _r.Blocked
            ? "차단됨 — " + (_r.Errors.FirstOrDefault() ?? "")
            : _r.SourceRows == Handled
                ? "소스 = 적재+재사용+건너뜀"
                : $"소스 {_r.SourceRows:N0} ≠ 처리 {Handled:N0} (차이 {_r.SourceRows - Handled:N0})";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>결과 창의 탭 하나 = 이관된 테이블 하나. 좌우 레코드는 탭 선택 시 채워진다.</summary>
    public sealed class ResultTabItem : INotifyPropertyChanged
    {
        public TableResult Result { get; }

        public ResultTabItem(TableResult result)
        {
            Result = result;
            _statsText = BuildStats(null);
        }

        public string Header      => $"{Result.Source}  →  {Result.Target}";

        // 콤보 상자가 DisplayMemberPath 를 못 살릴 때가 있어(테마 템플릿) 형식 이름이 그대로 떴다.
        // 표시 이름은 객체 자신이 알고 있어야 한다 — 화면 설정에 기대지 않는다.
        public override string ToString() => Header;
        public string TargetLabel => $"→ {Result.Target}";
        public string StatusIcon  => Result.Blocked ? "⛔" : Result.Errors.Count > 0 ? "⚠" : "✅";
        public bool   HasErrors   => Result.Errors.Count > 0;

        public string ErrorsText =>
            string.Join(Environment.NewLine, Result.Errors.Take(20))
            + (Result.Errors.Count > 20 ? $"{Environment.NewLine}… 외 {Result.Errors.Count - 20}건" : "");

        /// <summary>레코드를 이미 조회했는지 (탭 최초 선택 시 1회만 로드).</summary>
        public bool Loaded { get; set; }

        /// <summary>셀 동기화용 컬럼 대응(소스→타겟 / 타겟→소스). 실제 그리드에 있는 컬럼만 담는다.</summary>
        public Dictionary<string, string> SrcToTgtCol { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> TgtToSrcCol { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>ColumnMapper 결과에서 좌우 DataTable 에 실존하는 컬럼 짝만 골라 동기화 사전을 만든다.</summary>
        public void BuildColumnMaps(List<ColumnMapping> maps, DataTable src, DataTable tgt)
        {
            SrcToTgtCol.Clear();
            TgtToSrcCol.Clear();
            foreach (var m in maps)
            {
                if (m.Status is not (MappingStatus.Exact or MappingStatus.Renamed)) continue;
                if (!src.Columns.Contains(m.SourceName) || !tgt.Columns.Contains(m.TargetName)) continue;
                if (!SrcToTgtCol.ContainsKey(m.SourceName)) SrcToTgtCol[m.SourceName] = m.TargetName;
                if (!TgtToSrcCol.ContainsKey(m.TargetName)) TgtToSrcCol[m.TargetName] = m.SourceName;
            }
        }

        private DataView? _sourceView;
        public DataView? SourceView { get => _sourceView; set => Set(ref _sourceView, value); }

        private DataView? _targetView;
        public DataView? TargetView { get => _targetView; set => Set(ref _targetView, value); }

        private string _mappingSummary = "탭을 선택하면 좌우 레코드를 조회합니다.";
        public string MappingSummary { get => _mappingSummary; set => Set(ref _mappingSummary, value); }

        private string _statsText;
        public string StatsText { get => _statsText; set => Set(ref _statsText, value); }

        /// <summary>이관 후 타겟 행수를 반영해 건수 요약(검산 포함)을 갱신한다.</summary>
        public void RefreshStats(long? targetRowsNow) => StatsText = BuildStats(targetRowsNow);

        private string BuildStats(long? targetRowsNow)
        {
            var r = Result;
            if (r.Blocked) return "⛔ 차단됨 — " + (r.Errors.FirstOrDefault() ?? "");

            int handled = r.Inserted + r.Reused + r.Skipped;
            string check = r.SourceRows == handled
                ? "검산 일치 (소스 = 적재+재사용+건너뜀)"
                : $"검산 불일치 — 소스 {r.SourceRows:N0} ≠ 처리 {handled:N0}";

            var s = $"소스 {r.SourceRows:N0}  ·  적재 {r.Inserted:N0}  ·  재사용 {r.Reused:N0}  ·  건너뜀 {r.Skipped:N0}"
                  + (r.Errors.Count > 0 ? $"  ·  오류 {r.Errors.Count:N0}" : "")
                  + $"   |   {check}";
            if (targetRowsNow.HasValue) s += $"   |   이관 후 타겟 {targetRowsNow.Value:N0}행";
            return s;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
