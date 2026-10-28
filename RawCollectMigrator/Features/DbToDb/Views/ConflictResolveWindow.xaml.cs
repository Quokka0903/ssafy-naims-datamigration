using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:충돌해결] [NAIMS-ISSUE:IDEMPOTENT-CONFLICT]
    /// <summary>
    /// 멱등 재실행에서 <b>타겟에 이미 부합하는 행이 있고 값이 다른</b> 건을 사람이 정하게 한다.
    ///
    /// 왜 도구가 정하면 안 되나 — 두 가지가 다 말이 된다.
    ///   · 소스를 바로잡고 다시 돌리는 중이면 → <b>덮어쓰기</b>가 맞다.
    ///   · 타겟에서 손봐 둔 것이 있으면      → <b>유지</b>가 맞다. 덮어쓰면 그 손질이 조용히 사라진다.
    /// 겉으로는 구별되지 않는다. 지금까지는 도구가 말없이 '유지'를 골라 왔고, 그래서
    /// 소스를 고쳐도 재이관이 반영하지 않는다는 사실이 드러나지 않았다.
    ///
    /// 그래서 리허설(Trial)에서 <b>무엇이 달라지는지 먼저 보여 주고</b>, 실제 이관 전에 정하게 한다.
    /// 고아 해결·파일 부재 해결과 같은 자리, 같은 방식이다.
    /// <b>기본은 '유지'</b> — 아무것도 안 고르면 지금까지와 똑같이 동작한다.
    ///
    /// 결정은 config/manual_conflicts.json 에 남아 재실행에도 이어진다.
    /// </summary>
    public partial class ConflictResolveWindow : Window
    {
        public sealed class ActionChoice
        {
            public string Action { get; init; } = "";
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        private static readonly ActionChoice[] AllChoices =
        {
            new() { Action = DbToDbOptions.ConflictDecision.Keep,      Label = "유지 (기본)" },
            new() { Action = DbToDbOptions.ConflictDecision.Overwrite, Label = "덮어쓰기" },
            new() { Action = DbToDbOptions.ConflictDecision.NewValue,  Label = "새 값 입력" },
        };

        /// <summary>달라지는 컬럼 한 줄. 사람이 직접 값을 적어 넣을 수 있다.</summary>
        public sealed class DiffRow : INotifyPropertyChanged
        {
            public string Column { get; init; } = "";
            public string TargetValue { get; init; } = "";
            public string SourceValue { get; init; } = "";

            private string _newValue = "";
            public string NewValue
            {
                get => _newValue;
                set { _newValue = value ?? ""; Notify(nameof(NewValue)); Changed?.Invoke(); }
            }

            internal Action? Changed;
            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        /// <summary>충돌 한 건.</summary>
        public sealed class ConflictRow : INotifyPropertyChanged
        {
            public string SourceTable { get; init; } = "";
            public long SourcePk { get; init; }
            public long TargetRecordId { get; init; }
            public List<DiffRow> Diffs { get; } = new();

            public string DiffSummary => Diffs.Count == 0 ? ""
                : Diffs.Count <= 3
                    ? string.Join(", ", Diffs.Select(d => d.Column))
                    : $"{Diffs[0].Column} 외 {Diffs.Count - 1}개";

            public IReadOnlyList<ActionChoice> Choices => AllChoices;

            private string _action = DbToDbOptions.ConflictDecision.Keep;
            public string Action
            {
                get => _action;
                set
                {
                    if (_action == value) return;
                    _action = value;
                    // '새 값' 을 풀면 적어 둔 값도 지운다 — 안 쓰는 값이 남으면 무엇이 유효한지 알 수 없다.
                    if (value != DbToDbOptions.ConflictDecision.NewValue)
                        foreach (var d in Diffs) d.NewValue = "";
                    Notify(nameof(Action));
                }
            }

            public string Key => DbToDbOptions.ConflictKey(SourceTable, SourcePk);

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly List<ConflictRow> _all = new();
        private readonly ObservableCollection<ConflictRow> _view = new();
        private readonly ObservableCollection<DiffRow> _diffs = new();
        private bool _ready;

        /// <summary>저장한 결정. 취소하면 null.</summary>
        public IReadOnlyDictionary<string, DbToDbOptions.ConflictDecision>? Result { get; private set; }

        public ConflictResolveWindow(MigrationReport report)
        {
            InitializeComponent();

            foreach (var t in report.Tables)
                foreach (var c in t.Conflicts)
                {
                    var row = new ConflictRow
                    {
                        SourceTable = c.SourceTable,
                        SourcePk = c.SourcePk,
                        TargetRecordId = c.TargetRecordId,
                    };
                    foreach (var (col, tv, sv) in c.Diffs)
                    {
                        var d = new DiffRow { Column = col, TargetValue = tv, SourceValue = sv };
                        // 값을 적으면 그 행의 결정이 자동으로 '새 값 입력' 이 된다 —
                        // 적어 놓고 결정을 안 바꿔서 아무 일도 안 일어나는 실수를 막는다.
                        d.Changed = () =>
                        {
                            if (d.NewValue.Length > 0) row.Action = DbToDbOptions.ConflictDecision.NewValue;
                            Refresh();
                        };
                        row.Diffs.Add(d);
                    }
                    _all.Add(row);
                }

            var saved = LoadSaved();
            foreach (var r in _all)
                if (saved.TryGetValue(r.Key, out var d))
                {
                    r.Action = d.Action;
                    if (d.Values is { } vals)
                        foreach (var diff in r.Diffs)
                            if (vals.TryGetValue(diff.Column, out var v)) diff.NewValue = v;
                }

            TableCombo.Items.Add("전체");
            foreach (var n in _all.Select(r => r.SourceTable).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                TableCombo.Items.Add(n);
            TableCombo.SelectedIndex = 0;

            HeaderText.Text = $"타겟에 이미 있고 값이 다른 행 {_all.Count:N0}건";
            Grid.ItemsSource = _view;
            DiffGrid.ItemsSource = _diffs;

            _ready = true;
            Refresh();
        }

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (!_ready) return;

            string table = TableCombo.SelectedIndex > 0 ? (string)TableCombo.SelectedItem : "";
            int filter = FilterCombo.SelectedIndex;

            _view.Clear();
            foreach (var r in _all)
            {
                if (table.Length > 0 && r.SourceTable != table) continue;
                bool decided = r.Action != DbToDbOptions.ConflictDecision.Keep;
                if (filter == 1 && decided) continue;
                if (filter == 2 && !decided) continue;
                _view.Add(r);
            }

            int ow = _all.Count(r => r.Action == DbToDbOptions.ConflictDecision.Overwrite);
            int nv = _all.Count(r => r.Action == DbToDbOptions.ConflictDecision.NewValue);
            SummaryText.Text =
                $"유지 {_all.Count - ow - nv} · 덮어쓰기 {ow} · 새 값 {nv}" +
                "   |   저장한 결정은 다음 실행부터 반영됩니다.";
        }

        private void OnRowSelected(object sender, SelectionChangedEventArgs e)
        {
            _diffs.Clear();
            if (Grid.SelectedItem is not ConflictRow r) { DetailHead.Text = "선택한 행의 달라지는 값"; return; }
            DetailHead.Text = $"{r.SourceTable} #{r.SourcePk} → 타겟 #{r.TargetRecordId} — 달라지는 값 {r.Diffs.Count}개";
            foreach (var d in r.Diffs) _diffs.Add(d);
        }

        private IEnumerable<ConflictRow> Targets() =>
            Grid.SelectedItems.Count > 0 ? Grid.SelectedItems.Cast<ConflictRow>().ToList() : _view.ToList();

        private void OnBulkKeep(object sender, RoutedEventArgs e)
        {
            foreach (var r in Targets()) r.Action = DbToDbOptions.ConflictDecision.Keep;
            Refresh();
        }

        private void OnBulkOverwrite(object sender, RoutedEventArgs e)
        {
            foreach (var r in Targets()) r.Action = DbToDbOptions.ConflictDecision.Overwrite;
            Refresh();
        }

        // ── 저장 ──────────────────────────────────────────────────────
        private static string SavePath =>
            Path.Combine(AppContext.BaseDirectory, "config", "manual_conflicts.json");

        public static Dictionary<string, DbToDbOptions.ConflictDecision> LoadSaved()
        {
            try
            {
                if (File.Exists(SavePath))
                    return JsonSerializer
                        .Deserialize<Dictionary<string, DbToDbOptions.ConflictDecision>>(File.ReadAllText(SavePath))
                        ?? new Dictionary<string, DbToDbOptions.ConflictDecision>(StringComparer.OrdinalIgnoreCase);
            }
            catch { /* 읽기 실패는 '지정 없음' 과 같게 다룬다 — 이관을 막을 이유가 아니다 */ }
            return new Dictionary<string, DbToDbOptions.ConflictDecision>(StringComparer.OrdinalIgnoreCase);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            // 기본값(유지)은 저장하지 않는다 — 파일에는 '사람이 정한 것' 만 남는다.
            var map = new Dictionary<string, DbToDbOptions.ConflictDecision>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
            {
                if (r.Action == DbToDbOptions.ConflictDecision.Keep) continue;

                Dictionary<string, string>? vals = null;
                if (r.Action == DbToDbOptions.ConflictDecision.NewValue)
                {
                    vals = r.Diffs.Where(d => d.NewValue.Length > 0)
                                  .ToDictionary(d => d.Column, d => d.NewValue, StringComparer.OrdinalIgnoreCase);
                    if (vals.Count == 0)
                    {
                        MessageBox.Show(this,
                            $"{r.SourceTable} #{r.SourcePk} 은 '새 값 입력' 인데 적어 넣은 값이 없습니다.\n" +
                            "오른쪽 표에 값을 적거나 다른 결정으로 바꿔 주세요.",
                            "멱등 충돌 해결", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                map[r.Key] = new DbToDbOptions.ConflictDecision(r.Action, vals);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                File.WriteAllText(SavePath,
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장 실패: " + ex.Message,
                    "멱등 충돌 해결", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Result = map;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
