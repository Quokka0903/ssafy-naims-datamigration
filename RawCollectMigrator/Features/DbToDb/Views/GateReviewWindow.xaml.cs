using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:게이트승인창]
    /// <summary>
    /// G4(Trial) 채점표를 보여주고 실제 이관(Commit)을 승인받는 창.
    ///
    /// 승인 조건(모두 충족해야 [승인] 이 열린다):
    ///   ① 적재 대상 서버를 눈으로 확인했다는 체크 — 실서버 오지정 사고를 막는 마지막 관문
    ///   ② 리허설 결과를 확인했다는 체크
    ///   ③ 승인자 이름 입력 (보고서에 감사 추적으로 기록)
    ///   ④ 차단 항목이 있으면 "알고도 진행" 체크를 추가로 요구한다
    /// </summary>
    public partial class GateReviewWindow : Window
    {
        private readonly GateScore _gate;
        private readonly bool _hasBlocking;

        /// <summary>승인되었으면 승인자 이름, 취소면 null.</summary>
        public string? Approver { get; private set; }

        /// <param name="gate">G4 채점 결과.</param>
        /// <param name="opt">실행 옵션(소스·타겟 표시용).</param>
        /// <param name="sessionDir">세션 산출물 폴더(열기 버튼용).</param>
        /// <summary>접속문자열에서 데이터베이스 이름만 뽑는다(승인 문구용). 못 찾으면 "(미지정)".</summary>
        private static string DbOf(string conn)
        {
            foreach (var part in (conn ?? "").Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                if (k is "database" or "initial catalog") return kv[1].Trim();
            }
            return "(미지정)";
        }

        private readonly DbToDbOptions _opt;
        private MigrationReport? _report;

        // [찾기:고아해결] 리허설 리포트를 함께 받으면 [고아 레코드 해결] 버튼이 열린다.
        /// <param name="reviewOnly">
        /// true 면 <b>검토 전용</b> — 승인 입력을 숨기고, 창을 닫는 것 외에는 아무 일도 하지 않는다.
        ///
        /// 리허설 직후에도 이 창을 띄우는데 예전에는 승인 UI 를 그대로 보여주면서 결과를 무시했다.
        /// 사용자는 체크를 채우고 승인자를 넣고 [승인하고 실제 이관] 을 눌렀지만 아무 일도 없었다.
        /// <b>승인하지 않는 승인 창은 안 띄우는 것만 못하다</b> — 눌러도 되는 버튼과 안 되는 버튼이
        /// 똑같이 생겼으면 사람은 도구를 믿지 않게 된다. 그래서 겉모습부터 다르게 나눈다.
        /// </param>
        public GateReviewWindow(GateScore gate, DbToDbOptions opt, string sessionDir,
                                MigrationReport? report, bool reviewOnly = false)
            : this(gate, opt, sessionDir)
        {
            _report = report;

            if (reviewOnly)
            {
                Title = "🔍 리허설(Trial) 결과 검토 — 실제 이관은 아직 하지 않습니다";
                ApproveBox.Visibility = Visibility.Collapsed;   // 승인 입력 자체를 없앤다
                TargetBox.Visibility = Visibility.Collapsed;    // '되돌릴 수 없다' 는 이 단계에선 사실이 아니다
                BtnApprove.Visibility = Visibility.Collapsed;
                BtnClose.Content = "닫기";
                ReviewHint.Visibility = Visibility.Visible;
                ReviewHint.Text = gate.Pass
                    ? "리허설 결과입니다. 타겟에는 아무것도 반영되지 않았습니다(Trial 은 INSERT 후 롤백). "
                      + "실제 이관은 창을 닫고 작업 화면의 [승인 후 실제 이관] 에서 진행합니다."
                    : "차단 항목이 있습니다. 해소한 뒤 리허설을 다시 실행하세요 — "
                      + "타겟에는 아무것도 반영되지 않았습니다.";
            }

            int orphans = report?.Tables.Sum(t => t.UnresolvedFks.Count) ?? 0;
            if (orphans > 0)
            {
                BtnOrphans.Visibility = Visibility.Visible;
                BtnOrphans.Content = $"🔗 고아 레코드 해결 ({orphans})";
            }

            // [찾기:파일부재해결] 파일 부재도 <사람이 판단할 것> 이다 — 리허설 자리에서 정하게 한다.
            //   예전에는 "파일 부재 → 빈 값 적재 22" 라고 결과만 통보했다. 통보는 판단이 아니다.
            int missing = report?.Tables
                .SelectMany(t => t.MissingFileRefs)
                .Select(m => m.FileName)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
            if (missing > 0)
            {
                BtnMissingFiles.Visibility = Visibility.Visible;
                BtnMissingFiles.Content = $"📁 파일 부재 해결 ({missing})";
            }

            // [찾기:충돌해결] 멱등 재실행에서 값이 달라지는 행 — 실제 이관 전에 정해야 한다.
            int conflicts = report?.Tables.Sum(t => t.Conflicts.Count) ?? 0;
            if (conflicts > 0)
            {
                BtnConflicts.Visibility = Visibility.Visible;
                BtnConflicts.Content = $"♻ 멱등 충돌 해결 ({conflicts})";
            }
        }

        // [찾기:충돌해결]
        /// <summary>
        /// 멱등 충돌(타겟에 이미 있고 값이 다른 행)을 사람이 정하게 한다.
        /// 다른 해결 창과 마찬가지로 결정은 저장되지만 <b>이번 채점표에는 반영되지 않는다</b>.
        /// </summary>
        private void OnResolveConflicts(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var w = new ConflictResolveWindow(_report) { Owner = this };
            if (w.ShowDialog() != true || w.Result == null) return;

            int ow = w.Result.Count(kv => kv.Value.Action == DbToDbOptions.ConflictDecision.Overwrite);
            int nv = w.Result.Count(kv => kv.Value.Action == DbToDbOptions.ConflictDecision.NewValue);
            MessageBox.Show(this,
                $"저장했습니다 — 덮어쓰기 {ow}건 / 새 값 {nv}건.\n(나머지는 종전대로 유지합니다.)\n\n" +
                "이 결정은 다음 실행부터 반영됩니다. 창을 닫고 [▶ 리허설 전체 실행] 을 다시 눌러 확인하세요.",
                "멱등 충돌 해결", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 참조 파일이 없는 건을 사람이 처리하게 한다. 고아 해결과 마찬가지로 결정은 저장되지만
        /// <b>이번 채점표에는 반영되지 않는다</b> — 정한 뒤 리허설을 다시 돌려야 그 결과로 판단할 수 있다.
        /// </summary>
        private void OnResolveMissingFiles(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var w = new MissingFileResolveWindow(_report) { Owner = this };
            if (w.ShowDialog() != true || w.Result == null) return;

            int discard = w.Result.Count(kv => kv.Value.Action == DbToDbOptions.MissingFileDecision.Discard);
            int sub = w.Result.Count(kv => kv.Value.Action == DbToDbOptions.MissingFileDecision.Substitute);
            MessageBox.Show(this,
                $"저장했습니다 — 행 버림 {discard}종 / 다른 파일 지정 {sub}종.\n" +
                "(나머지는 종전대로 빈 값으로 적재합니다.)\n\n" +
                "이 결정은 다음 리허설부터 반영됩니다. 창을 닫고 [▶ 리허설 전체 실행] 을 다시 눌러 주세요.",
                "파일 부재 해결", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 부모를 못 찾은 행을 사람이 처리하게 한다. 결정은 저장되지만 <b>이번 채점표에는 반영되지 않는다</b> —
        /// 정한 뒤 리허설을 다시 돌려야 그 결과로 판단할 수 있기 때문이다. 그래서 창을 닫으며 그 사실을 알린다.
        /// </summary>
        private void OnResolveOrphans(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            var w = new OrphanResolveWindow(_report, _opt) { Owner = this };
            if (w.ShowDialog() != true || w.Result == null) return;

            int keep = w.Result.Count(kv => kv.Value > 0);
            MessageBox.Show(this,
                $"저장했습니다 — 연결 {keep}건 / 버림 {w.Result.Count - keep}건.\n\n" +
                "이 결정은 다음 리허설부터 반영됩니다. 창을 닫고 [▶ 리허설 전체 실행] 을 다시 눌러 주세요.",
                "고아 레코드 해결", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public GateReviewWindow(GateScore gate, DbToDbOptions opt, string sessionDir)
        {
            InitializeComponent();
            _opt = opt;
            _gate = gate;
            _sessionDir = sessionDir;
            _hasBlocking = gate.Blocked > 0;

            HeaderText.Text = gate.Pass
                ? $"✅ 리허설 통과 — 차단 0 / 경고 {gate.Warnings}"
                : $"❌ 차단 {gate.Blocked}건 / 경고 {gate.Warnings}건 — 해소 전 진행을 권하지 않습니다";

            TargetText.Text = "적재 대상(타겟) : " + Describe(opt.TargetConnStr);
            SourceText.Text = "읽는 곳(소스)   : " +
                $"[{DbProvider.DisplayName(opt.SourceKind)} · {DbProvider.ModeName(opt.SourceMode)}] " +
                Describe(opt.SourceConnStr);

            ConfirmTargetText.Text =
                $" 적재 대상이 {ServerOf(opt.TargetConnStr)} 인 것을 확인했습니다. " +
                "(시험기/실운영을 반드시 구분하세요)";

            // [NAIMS-ISSUE:REBUILD-VS-KEY] 전량 재구축은 "넣기 전에 지우는" 작업이다.
            //   승인 창에서 이 사실이 눈에 띄지 않으면, 사람은 자기가 무엇을 승인하는지 모른 채 누른다.
            //   대상 서버·DB 를 문구에 그대로 박아 둔다 — 시험기와 실운영을 눈으로 구분하게.
            if (opt.RebuildTargets)
            {
                RebuildWarnBox.Visibility = Visibility.Visible;
                RebuildWarnText.Text =
                    $"⚠ 전량 재구축 모드입니다. 승인하면 {ServerOf(opt.TargetConnStr)} 의 " +
                    $"[{DbOf(opt.TargetConnStr)}] 에서 이관 대상 STD_* 테이블의 " +
                    "기존 행을 모두 삭제한 뒤 처음부터 적재합니다.\n" +
                    "이관 계획 밖의 테이블은 삭제하지 않으며, 삭제와 적재는 하나의 트랜잭션이라 " +
                    "도중 실패 시 전부 되돌아갑니다.";
                ConfirmTargetText.Text =
                    $" {ServerOf(opt.TargetConnStr)} / [{DbOf(opt.TargetConnStr)}] 의 기존 STD_* 데이터를 " +
                    "삭제하고 재구축하는 것에 동의합니다. (시험기/실운영을 반드시 구분하세요)";
            }

            CheckGrid.ItemsSource = gate.Checks;

            if (_hasBlocking)
            {
                AcceptBlockedChk.Visibility = Visibility.Visible;
                HintText.Text = "차단 항목이 있어 추가 확인이 필요합니다.";
            }
            else HintText.Text = "세 항목을 모두 확인하면 승인 버튼이 열립니다.";
        }

        private readonly string _sessionDir;

        /// <summary>접속문자열에서 서버·DB만 뽑아 보여준다(비밀번호는 제외).</summary>
        private static string Describe(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return "(미지정)";
            var masked = Regex.Replace(conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
            return masked.Length > 200 ? masked[..200] + "…" : masked;
        }

        /// <summary>접속문자열에서 서버/DB 부분만 짧게 추출한다(확인 문구용).</summary>
        private static string ServerOf(string conn)
        {
            var server = Regex.Match(conn, @"(?i)\b(?:server|data source)\s*=\s*([^;]+)").Groups[1].Value.Trim();
            var db = Regex.Match(conn, @"(?i)\b(?:database|initial catalog)\s*=\s*([^;]+)").Groups[1].Value.Trim();
            if (server.Length == 0 && db.Length == 0)
            {
                var dsn = Regex.Match(conn, @"(?i)\bDSN\s*=\s*([^;]+)").Groups[1].Value.Trim();
                return dsn.Length > 0 ? $"DSN={dsn}" : "(확인 불가)";
            }
            return server + (db.Length > 0 ? " / " + db : "");
        }

        private void OnConfirmChanged(object sender, RoutedEventArgs e) => Refresh();
        private void OnApproverChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

        /// <summary>승인 조건을 모두 만족했을 때만 승인 버튼을 연다.</summary>
        private void Refresh()
        {
            bool ok = ConfirmTargetChk.IsChecked == true
                   && ConfirmResultChk.IsChecked == true
                   && ApproverBox.Text.Trim().Length > 0
                   && (!_hasBlocking || AcceptBlockedChk.IsChecked == true);
            BtnApprove.IsEnabled = ok;
        }

        private void OnApprove(object sender, RoutedEventArgs e)
        {
            var blocked = _gate.Checks.Where(c => !c.Pass && c.Blocking).Select(c => c.Name).ToList();
            var msg = "실제 이관을 시작합니다.\n\n" +
                      TargetText.Text + "\n\n" +
                      (blocked.Count > 0
                          ? "⚠ 차단 항목: " + string.Join(", ", blocked) + "\n\n"
                          : "") +
                      "Commit 직전에 소스 지문을 다시 대조하며, 변경이 감지되면 자동으로 중단됩니다.\n\n계속할까요?";
            if (MessageBox.Show(this, msg, "최종 확인",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            Approver = ApproverBox.Text.Trim();
            DialogResult = true;
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_sessionDir}\"") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "폴더를 열 수 없습니다: " + ex.Message, "세션 폴더",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
