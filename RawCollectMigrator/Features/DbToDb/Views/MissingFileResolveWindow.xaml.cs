using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:파일부재해결] [NAIMS-ISSUE:MISSING-FILE-DECISION]
    /// <summary>
    /// 참조 파일이 없는 건들을 <b>리허설 단계에서 사람이 판단</b>하게 한다.
    ///
    /// 왜 도구가 정하면 안 되나 — "파일이 없다" 의 뜻은 자료마다 다르다.
    ///   · 아직 안 받아온 파일   → 빈 값으로 싣고 나중에 채우는 게 맞다.
    ///   · 폐기된 자료           → 그 행은 버리는 게 맞다.
    ///   · 이름만 바뀐 파일       → 실제 파일을 지정해 주는 게 맞다.
    /// 셋은 겉보기가 완전히 같아서 도구가 구별할 방법이 없다. 그런데 지금까지는 도구가
    /// 말없이 첫 번째를 골라 왔다 — 그 결과가 "파일 부재 → 빈 값 적재 22건" 이라는
    /// <b>통보</b>였다. 통보는 판단이 아니다.
    ///
    /// 그래서 고아 레코드 해결과 같은 자리(리허설 결과 창)에 같은 방식으로 붙인다.
    /// <b>기본은 '빈 값으로 적재'</b> — 아무것도 안 고르면 지금까지와 똑같이 동작한다.
    ///
    /// 판단 단위가 <b>파일</b>인 이유 — 사람은 "이 파일이 없다" 로 판단하지 "PK 137번 행" 으로
    /// 판단하지 않는다. 한 파일을 여러 행이 참조하면 결정도 함께 가는 것이 자연스럽다.
    /// 어느 행이 걸려 있는지는 '참조한 레코드' 열에 그대로 보여 준다.
    ///
    /// 결정은 config/manual_files.json 에 남아 재실행에도 이어진다 — 매번 다시 고르게 하면
    /// 결국 아무도 안 고르게 된다.
    /// </summary>
    public partial class MissingFileResolveWindow : Window
    {
        /// <summary>결정 콤보의 항목.</summary>
        public sealed class ActionChoice
        {
            public string Action { get; init; } = "";
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        private static readonly ActionChoice[] AllChoices =
        {
            new() { Action = DbToDbOptions.MissingFileDecision.Empty,      Label = "빈 값으로 적재 (기본)" },
            new() { Action = DbToDbOptions.MissingFileDecision.Discard,    Label = "행 버림" },
            new() { Action = DbToDbOptions.MissingFileDecision.Substitute, Label = "다른 파일 지정" },
        };

        /// <summary>해결 대상 파일 하나.</summary>
        public sealed class FileRow : INotifyPropertyChanged
        {
            public string Kind { get; init; } = "";
            public string FileName { get; init; } = "";

            /// <summary>이 파일을 참조하는 (소스표, PK) 목록 — 사람이 무게를 가늠할 근거.</summary>
            public List<(string Table, long Pk)> Refs { get; } = new();

            public string RefText => Refs.Count == 0
                ? ""
                : Refs.Count <= 3
                    ? string.Join(", ", Refs.Select(r => $"{r.Table}#{r.Pk}"))
                    : $"{Refs[0].Table}#{Refs[0].Pk} 외 {Refs.Count - 1}건";

            public IReadOnlyList<ActionChoice> Choices => AllChoices;

            private string _action = DbToDbOptions.MissingFileDecision.Empty;
            public string Action
            {
                get => _action;
                set
                {
                    if (_action == value) return;
                    _action = value;
                    // '다른 파일 지정' 을 풀면 경로도 함께 지운다 —
                    // 안 쓰는 경로가 남아 있으면 나중에 무엇이 유효한지 알 수 없다.
                    if (value != DbToDbOptions.MissingFileDecision.Substitute) SubstitutePath = "";
                    Notify(nameof(Action));
                }
            }

            private string _substitutePath = "";
            public string SubstitutePath
            {
                get => _substitutePath;
                set { _substitutePath = value ?? ""; Notify(nameof(SubstitutePath)); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly List<FileRow> _all = new();
        private readonly ObservableCollection<FileRow> _view = new();

        // FilterCombo.SelectedIndex="0" 이 InitializeComponent 중에 발화하는데
        // 그때는 SummaryText 가 아직 null 이다. 준비되기 전 갱신을 막는다.
        private bool _ready;

        /// <summary>사용자가 저장한 결정(파일명 → 결정). 취소하면 null.</summary>
        public IReadOnlyDictionary<string, DbToDbOptions.MissingFileDecision>? Result { get; private set; }

        public MissingFileResolveWindow(MigrationReport report)
        {
            InitializeComponent();

            var byFile = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in report.Tables)
                foreach (var (pk, kind, file) in t.MissingFileRefs)
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    if (!byFile.TryGetValue(file, out var row))
                    {
                        row = new FileRow { Kind = kind, FileName = file };
                        byFile[file] = row;
                        _all.Add(row);
                    }
                    if (!row.Refs.Any(r => r.Table == t.Source && r.Pk == pk))
                        row.Refs.Add((t.Source, pk));
                }

            // 저장해 둔 결정을 되살린다.
            var saved = LoadSaved();
            foreach (var r in _all)
                if (saved.TryGetValue(r.FileName, out var d))
                {
                    r.Action = d.Action;
                    r.SubstitutePath = d.SubstitutePath ?? "";
                }

            _all.Sort((a, b) =>
            {
                int k = string.Compare(a.Kind, b.Kind, StringComparison.Ordinal);
                return k != 0 ? k : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            KindCombo.Items.Add("전체");
            foreach (var k in _all.Select(r => r.Kind).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                KindCombo.Items.Add(k);
            KindCombo.SelectedIndex = 0;

            HeaderText.Text = $"참조 파일을 찾지 못한 파일 {_all.Count:N0}종 " +
                              $"(관련 레코드 {_all.Sum(r => r.Refs.Count):N0}건)";
            Grid.ItemsSource = _view;

            _ready = true;
            Refresh();
        }

        // ── 보기 ──────────────────────────────────────────────────────
        private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (!_ready) return;

            string kind = KindCombo.SelectedIndex > 0 ? (string)KindCombo.SelectedItem : "";
            int filter = FilterCombo.SelectedIndex;

            _view.Clear();
            foreach (var r in _all)
            {
                if (kind.Length > 0 && r.Kind != kind) continue;
                bool decided = r.Action != DbToDbOptions.MissingFileDecision.Empty;
                if (filter == 1 && decided) continue;
                if (filter == 2 && !decided) continue;
                _view.Add(r);
            }

            int discard = _all.Count(r => r.Action == DbToDbOptions.MissingFileDecision.Discard);
            int sub = _all.Count(r => r.Action == DbToDbOptions.MissingFileDecision.Substitute);
            int empty = _all.Count - discard - sub;
            int discardRows = _all.Where(r => r.Action == DbToDbOptions.MissingFileDecision.Discard)
                                  .Sum(r => r.Refs.Count);

            SummaryText.Text =
                $"빈 값 {empty}종 · 버림 {discard}종(레코드 {discardRows}건) · 대체 지정 {sub}종" +
                "   |   저장한 결정은 다음 리허설부터 반영됩니다.";
        }

        // ── 일괄 ──────────────────────────────────────────────────────
        private IEnumerable<FileRow> Targets() =>
            Grid.SelectedItems.Count > 0 ? Grid.SelectedItems.Cast<FileRow>().ToList() : _view.ToList();

        private void OnBulkEmpty(object sender, RoutedEventArgs e)
        {
            foreach (var r in Targets()) r.Action = DbToDbOptions.MissingFileDecision.Empty;
            Refresh();
        }

        private void OnBulkDiscard(object sender, RoutedEventArgs e)
        {
            foreach (var r in Targets()) r.Action = DbToDbOptions.MissingFileDecision.Discard;
            Refresh();
        }

        /// <summary>대체 파일을 고른다. 고르면 결정도 '다른 파일 지정' 으로 함께 바뀐다.</summary>
        private void OnPickFile(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FileRow row) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"'{row.FileName}' 대신 쓸 파일 선택",
                Filter = "모든 파일 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            row.SubstitutePath = dlg.FileName;
            row.Action = DbToDbOptions.MissingFileDecision.Substitute;
            Refresh();
        }

        // ── 저장 ──────────────────────────────────────────────────────
        private static string SavePath =>
            Path.Combine(AppContext.BaseDirectory, "config", "manual_files.json");

        /// <summary>저장된 결정을 읽는다. 읽기 실패는 '지정 없음' 과 같게 다룬다.</summary>
        public static Dictionary<string, DbToDbOptions.MissingFileDecision> LoadSaved()
        {
            try
            {
                if (File.Exists(SavePath))
                    return JsonSerializer
                        .Deserialize<Dictionary<string, DbToDbOptions.MissingFileDecision>>(
                            File.ReadAllText(SavePath))
                        ?? new Dictionary<string, DbToDbOptions.MissingFileDecision>(StringComparer.OrdinalIgnoreCase);
            }
            catch { /* 이관을 막을 이유가 아니다 */ }
            return new Dictionary<string, DbToDbOptions.MissingFileDecision>(StringComparer.OrdinalIgnoreCase);
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            // '다른 파일 지정' 인데 경로가 비어 있으면 아무 일도 안 하는 결정이 된다 — 미리 막는다.
            var bad = _all.FirstOrDefault(r =>
                r.Action == DbToDbOptions.MissingFileDecision.Substitute
                && string.IsNullOrWhiteSpace(r.SubstitutePath));
            if (bad != null)
            {
                MessageBox.Show(this,
                    $"'{bad.FileName}' 은 '다른 파일 지정' 인데 파일을 고르지 않았습니다.\n" +
                    "[찾기…] 로 파일을 고르거나 다른 결정으로 바꿔 주세요.",
                    "파일 부재 해결", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 기본값(빈 값 적재)은 저장하지 않는다 — 파일에는 '사람이 정한 것' 만 남는다.
            var map = new Dictionary<string, DbToDbOptions.MissingFileDecision>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
            {
                if (r.Action == DbToDbOptions.MissingFileDecision.Empty) continue;
                map[r.FileName] = new DbToDbOptions.MissingFileDecision(
                    r.Action,
                    r.Action == DbToDbOptions.MissingFileDecision.Substitute ? r.SubstitutePath : null);
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
                    "파일 부재 해결", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Result = map;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
