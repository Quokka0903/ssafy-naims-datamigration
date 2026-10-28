using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:고아해결] [NAIMS-ISSUE:MANUAL-FK]
    /// <summary>
    /// 부모를 못 찾은 행에 대해 <b>사람이 붙일 부모를 고르거나 버리기로 결정</b>하는 창.
    ///
    /// 왜 필요한가 — 레거시에는 FK 가 0("참조 없음")이거나 부모가 지워진 채 자식만 남은 행이 있다.
    /// 도구가 할 수 있는 선택지는 둘뿐이었다: 조용히 버리거나, "미상" 부모를 지어내거나.
    /// 앞은 실제 자료를 잃고(실측: CHARACTERISTICS 8행이 42컬럼 전부 채워진 채 버려짐),
    /// 뒤는 타겟에 없던 데이터를 만든다. 어느 쪽도 도구가 정할 일이 아니다.
    ///
    /// 그래서 결정을 사람에게 넘기되, <b>기본값은 '버림'</b> 으로 둔다 —
    /// 아무 조작도 하지 않으면 지금까지와 똑같이 동작하고, 살릴 것만 고르면 된다.
    /// 잘못 눌러서 엉뚱한 부모에 붙는 것보다 그대로 빠지는 편이 되돌리기 쉽다.
    ///
    /// 결정은 config/manual_fk.json 에 남아 재실행에도 이어진다 — 매번 다시 고르게 하면
    /// 결국 아무도 안 고르게 되기 때문이다.
    /// </summary>
    public partial class OrphanResolveWindow : Window
    {
        /// <summary>부모 후보 하나(콤보 항목).</summary>
        public sealed class ParentChoice
        {
            public long RecordId { get; init; }          // 0 = 버림
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        /// <summary>해결 대상 행 하나.</summary>
        public sealed class OrphanRow : INotifyPropertyChanged
        {
            public string SourceTable { get; init; } = "";
            public string TargetTable { get; init; } = "";
            public long SourcePk { get; init; }
            public string FkColumn { get; init; } = "";
            public string ParentTable { get; init; } = "";
            public long RawFk { get; init; }
            public string Reason { get; init; } = "";
            public string Preview { get; set; } = "";
            public IReadOnlyList<ParentChoice> Candidates { get; init; } = Array.Empty<ParentChoice>();

            private ParentChoice? _chosen;
            public ParentChoice? Chosen
            {
                get => _chosen;
                set { _chosen = value; OnChanged(); OnChanged(nameof(IsDiscarded)); }
            }

            public bool IsDiscarded => (Chosen?.RecordId ?? 0) == 0;
            public string Key => DbToDbOptions.ManualFkKey(SourceTable, SourcePk, FkColumn);

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly DbToDbOptions _opt;
        private readonly List<OrphanRow> _all = new();
        private readonly ObservableCollection<OrphanRow> _view = new();

        /// <summary>저장 눌렀을 때의 결과(키 → 부모 RECORD_ID, 0=버림). 취소면 null.</summary>
        public Dictionary<string, long>? Result { get; private set; }

        public OrphanResolveWindow(MigrationReport report, DbToDbOptions opt)
        {
            InitializeComponent();
            _opt = opt;
            Grid.ItemsSource = _view;
            Loaded += async (_, _) => await LoadAsync(report);
        }

        private async Task LoadAsync(MigrationReport report)
        {
            HeaderText.Text = "고아 레코드를 읽는 중…";
            try
            {
                // 부모 테이블별 후보 목록은 한 번만 만든다(같은 부모를 여러 테이블이 참조한다).
                var candidateCache = new Dictionary<string, List<ParentChoice>>(StringComparer.OrdinalIgnoreCase);
                var saved = LoadSaved();

                foreach (var t in report.Tables)
                {
                    foreach (var (srcPk, fkCol, parentTable, rawFk, reason) in t.UnresolvedFks)
                    {
                        var plan = MigrationPlan.Defaults()
                            .FirstOrDefault(p => p.SourceTable.Equals(parentTable, StringComparison.OrdinalIgnoreCase));
                        string parentTarget = plan?.TargetTable ?? "";

                        if (!candidateCache.TryGetValue(parentTarget, out var cands))
                        {
                            cands = await BuildCandidatesAsync(parentTarget);
                            candidateCache[parentTarget] = cands;
                        }

                        var row = new OrphanRow
                        {
                            SourceTable = t.Source, TargetTable = t.Target, SourcePk = srcPk,
                            FkColumn = fkCol, ParentTable = parentTable, RawFk = rawFk, Reason = reason,
                            Candidates = cands,
                        };
                        // 저장된 결정이 있으면 되살린다. 없으면 '버림'(첫 항목).
                        long want = saved.TryGetValue(row.Key, out var v) ? v : 0;
                        row.Chosen = cands.FirstOrDefault(c => c.RecordId == want) ?? cands[0];
                        _all.Add(row);
                    }
                }

                await FillPreviewsAsync();

                TableCombo.ItemsSource = new[] { "(전체)" }
                    .Concat(_all.Select(r => r.SourceTable).Distinct().OrderBy(x => x)).ToList();
                TableCombo.SelectedIndex = 0;
                Refresh();
            }
            catch (Exception ex)
            {
                HeaderText.Text = "고아 목록 로드 실패: " + ex.Message;
            }
        }

        /// <summary>
        /// 부모 후보 목록을 만든다. 맨 앞은 항상 '버림'(RECORD_ID=0) — 기본값이 되도록.
        /// 이름 컬럼이 있으면 "이름 (#RECORD_ID)" 로 보여 사람이 고를 수 있게 한다.
        /// </summary>
        private async Task<List<ParentChoice>> BuildCandidatesAsync(string parentTargetTable)
        {
            var list = new List<ParentChoice> { new() { RecordId = 0, Label = "— 버림 (이관하지 않음) —" } };
            if (parentTargetTable.Length == 0) return list;

            try
            {
                var cols = await TableInspector.GetColumnsAsync(
                    DbProviderKind.MsSql, DbConnectionMode.Direct, _opt.TargetConnStr, parentTargetTable);
                // 사람이 알아볼 이름 컬럼을 고른다(NAME 우선, 없으면 첫 문자열 컬럼).
                var nameCol = cols.FirstOrDefault(c => c.Name.EndsWith("NAME", StringComparison.OrdinalIgnoreCase))
                           ?? cols.FirstOrDefault(c => c.DataType.Contains("char", StringComparison.OrdinalIgnoreCase));

                string select = nameCol != null
                    ? $"SELECT RECORD_ID, CAST([{nameCol.Name}] AS nvarchar(200)) AS LBL FROM [{parentTargetTable}] ORDER BY LBL"
                    : $"SELECT RECORD_ID, CAST(RECORD_ID AS nvarchar(20)) AS LBL FROM [{parentTargetTable}] ORDER BY RECORD_ID";

                await using var cn = new Microsoft.Data.SqlClient.SqlConnection(_opt.TargetConnStr);
                await cn.OpenAsync();
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = select;
                cmd.CommandTimeout = 60;
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    long id = Convert.ToInt64(r.GetValue(0));
                    string lbl = r.IsDBNull(1) ? "" : (r.GetValue(1)?.ToString() ?? "");
                    list.Add(new ParentChoice { RecordId = id, Label = $"{lbl}  (#{id})" });
                }
            }
            catch { /* 후보를 못 읽어도 '버림' 만으로 창은 동작해야 한다 */ }
            return list;
        }

        /// <summary>각 고아 행의 소스 내용을 몇 컬럼만 요약해 보여 준다(무엇을 버리는지 알고 정하도록).</summary>
        private async Task FillPreviewsAsync()
        {
            foreach (var g in _all.GroupBy(r => r.SourceTable))
            {
                try
                {
                    var plan = MigrationPlan.Defaults()
                        .FirstOrDefault(p => p.SourceTable.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
                    if (plan == null) continue;

                    var cols = await TableInspector.GetColumnsAsync(
                        _opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, g.Key);
                    // PK + 앞쪽 비-FK 컬럼 몇 개만 — 전체를 보여주면 오히려 안 읽는다.
                    var pick = new List<ColumnInfo>();
                    var pk = cols.FirstOrDefault(c => c.Name.Equals(plan.SourcePk, StringComparison.OrdinalIgnoreCase));
                    if (pk != null) pick.Add(pk);
                    pick.AddRange(cols.Where(c => c != pk && !c.Name.EndsWith("_ID", StringComparison.OrdinalIgnoreCase)).Take(4));

                    var dt = await TableInspector.GetRowsByIdsAsync(
                        _opt.SourceKind, _opt.SourceMode, _opt.SourceConnStr, g.Key,
                        pick, plan.SourcePk, g.Select(r => r.SourcePk).Distinct().ToList());

                    var byId = new Dictionary<long, DataRow>();
                    foreach (DataRow dr in dt.Rows)
                        if (dt.Columns.Contains(plan.SourcePk) && long.TryParse(Convert.ToString(dr[plan.SourcePk]), out var id))
                            byId[id] = dr;

                    foreach (var r in g)
                        if (byId.TryGetValue(r.SourcePk, out var dr))
                            r.Preview = string.Join(" · ", dt.Columns.Cast<DataColumn>()
                                .Where(c => !c.ColumnName.Equals(plan.SourcePk, StringComparison.OrdinalIgnoreCase))
                                .Select(c => $"{c.ColumnName}={Trim(dr[c])}"));
                }
                catch { /* 미리보기는 보조 정보 — 실패해도 결정은 할 수 있어야 한다 */ }
            }
        }

        private static string Trim(object? v)
        {
            var s = (Convert.ToString(v) ?? "").Trim();
            return s.Length > 28 ? s[..28] + "…" : s;
        }

        // ── 화면 ──────────────────────────────────────────────────────
        private void Refresh()
        {
            string tbl = TableCombo.SelectedItem as string ?? "(전체)";
            int filter = FilterCombo?.SelectedIndex ?? 0;

            _view.Clear();
            foreach (var r in _all)
            {
                if (tbl != "(전체)" && r.SourceTable != tbl) continue;
                if (filter == 1 && !r.IsDiscarded) continue;
                if (filter == 2 && r.IsDiscarded) continue;
                _view.Add(r);
            }

            int keep = _all.Count(r => !r.IsDiscarded);
            HeaderText.Text = $"고아 레코드 {_all.Count:N0}건 — 연결 {keep:N0} / 버림 {_all.Count - keep:N0}";
            SummaryText.Text = _all.GroupBy(r => r.SourceTable)
                .Select(g => $"{g.Key} {g.Count()}건(연결 {g.Count(x => !x.IsDiscarded)})")
                .Aggregate("", (a, b) => a.Length == 0 ? b : a + "  ·  " + b);

            // 일괄 콤보는 지금 보이는 테이블의 후보로 맞춘다(부모가 테이블마다 다르므로).
            BulkParentCombo.ItemsSource = _view.Count > 0 ? _view[0].Candidates : null;
        }

        private void OnTableChanged(object sender, SelectionChangedEventArgs e) => Refresh();
        private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

        private void OnBulkAssign(object sender, RoutedEventArgs e)
        {
            if (BulkParentCombo.SelectedItem is not ParentChoice pick) { MessageBox.Show(this, "연결할 부모를 먼저 고르세요."); return; }
            var targets = Grid.SelectedItems.Cast<OrphanRow>().ToList();
            if (targets.Count == 0) { MessageBox.Show(this, "행을 선택하세요(여러 행 선택 가능)."); return; }
            foreach (var r in targets)
                r.Chosen = r.Candidates.FirstOrDefault(c => c.RecordId == pick.RecordId) ?? r.Candidates[0];
            Refresh();
        }

        private void OnBulkDiscard(object sender, RoutedEventArgs e)
        {
            var targets = Grid.SelectedItems.Cast<OrphanRow>().ToList();
            if (targets.Count == 0) targets = _view.ToList();   // 선택이 없으면 보이는 전체
            foreach (var r in targets) r.Chosen = r.Candidates[0];
            Refresh();
        }

        // ── 저장 ──────────────────────────────────────────────────────
        private static string SavePath =>
            Path.Combine(AppContext.BaseDirectory, "config", "manual_fk.json");

        private static Dictionary<string, long> LoadSaved()
        {
            try
            {
                if (File.Exists(SavePath))
                    return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(SavePath))
                           ?? new Dictionary<string, long>();
            }
            catch { }
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            // 버림(0)도 함께 저장한다 — "아직 안 정함" 과 "버리기로 정함" 을 구분해야
            // 다음 실행에서 다시 물어볼지 판단할 수 있다.
            var map = _all.ToDictionary(r => r.Key, r => r.Chosen?.RecordId ?? 0);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath,
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장 실패: " + ex.Message);
                return;
            }
            Result = map;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; Close(); }
    }
}
