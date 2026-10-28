using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:세션] [GUIDE:MIGRATION-SESSION] [찾기:세션] [찾기:게이트] [찾기:원버튼]
    //  ▣ 이 파일 = 이관 전 과정을 하나의 "세션"으로 묶는 오케스트레이터.
    //
    //    [1] 연결 점검 ─G1→ [2] 매핑 리스트 ─G2→ [3] DryRun ─G3→ [4] Trial ─G4(사람)→
    //    [5] Commit → [6] 사후 검증 + 레코드 매핑 리스트
    //
    //  ▶ 원버튼은 [1]~[4] 까지만 무인 실행한다(RunRehearsalAsync). [5] 앞에서 반드시 멈춘다.
    //    되돌릴 수 없는 단계이고, Trial 결과를 사람이 보고 판단해야 "피드백"이 성립하기 때문이다.
    //  ▶ G1~G3 은 자동 판정, G4 는 자동 채점 + 사람 승인. 승인 후 [5][6]은 다시 무인으로 끝까지 간다.
    //  ▶ ★DryRun 집계를 합격 기준으로 쓰지 않는다★ — DryRun 은 멱등 대조가 꺼져 있어
    //    적재/재사용/건너뜀이 실전과 다르다(DbToDbExecutor 주석 참조). G3 은 오류·차단만 본다.
    //  ▶ Trial 과 Commit 사이에 소스가 바뀌면 Trial 결과가 무효다 → 소스 지문(SourceFingerprint)을
    //    Trial 직후 찍어두고 Commit 직전 재확인해 불일치면 커밋을 거부한다. [찾기:소스지문]
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>세션 단계.</summary>
    public enum Stage
    {
        Connect,
        Mapping,
        DryRun,
        Trial,
        Gate,
        Commit,
        Verify,
    }

    /// <summary>단계 진행 보고.</summary>
    /// <param name="Stage">현재 단계.</param>
    /// <param name="Index">단계 번호(1부터).</param>
    /// <param name="Total">전체 단계 수.</param>
    /// <param name="Message">화면에 띄울 문구.</param>
    public sealed record StageProgress(Stage Stage, int Index, int Total, string Message);

    /// <summary>게이트 판정 항목 하나.</summary>
    /// <param name="Name">지표 이름.</param>
    /// <param name="Value">관측값(사람이 읽는 형태).</param>
    /// <param name="Expected">합격 기준.</param>
    /// <param name="Pass">통과 여부.</param>
    /// <param name="Blocking">불합격 시 진행을 막는 항목인지(false 면 경고).</param>
    /// <param name="Detail">근거·조치 안내.</param>
    public sealed record GateCheck(
        string Name, string Value, string Expected, bool Pass, bool Blocking, string Detail = "")
    {
        public string Icon => Pass ? "✓" : (Blocking ? "✗" : "⚠");
        public string Level => Pass ? "통과" : (Blocking ? "차단" : "경고");
    }

    /// <summary>한 게이트의 채점 결과.</summary>
    public sealed class GateScore
    {
        public string Name { get; init; } = "";
        public List<GateCheck> Checks { get; } = new();

        /// <summary>차단 항목이 하나도 불합격하지 않았는지.</summary>
        public bool Pass => Checks.All(c => c.Pass || !c.Blocking);
        public int Blocked => Checks.Count(c => !c.Pass && c.Blocking);
        public int Warnings => Checks.Count(c => !c.Pass && !c.Blocking);

        public void Add(string name, string value, string expected, bool pass, bool blocking, string detail = "")
            => Checks.Add(new GateCheck(name, value, expected, pass, blocking, detail));

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── {Name} ── {(Pass ? "통과" : "차단")} (차단 {Blocked} / 경고 {Warnings})");
            foreach (var c in Checks)
                sb.AppendLine($"  {c.Icon} {c.Name}: {c.Value}   (기준 {c.Expected})"
                            + (c.Detail.Length > 0 ? $"\n      ↳ {c.Detail}" : ""));
            return sb.ToString();
        }
    }

    /// <summary>소스 상태 지문 — Trial 과 Commit 사이에 소스가 바뀌지 않았음을 확인한다.</summary>
    public sealed class SourceFingerprint
    {
        /// <summary>테이블 → "행수/최소PK/최대PK".</summary>
        public Dictionary<string, string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime TakenAt { get; init; } = DateTime.Now;

        /// <summary>다른 지문과 다른 테이블 목록을 돌려준다(같으면 빈 목록).</summary>
        public List<string> Diff(SourceFingerprint other)
        {
            var diff = new List<string>();
            foreach (var (k, v) in Tables)
            {
                if (!other.Tables.TryGetValue(k, out var w))
                    diff.Add($"{k}: 이후 조회 실패");
                else if (v != w)
                    diff.Add($"{k}: {v} → {w}");
            }
            foreach (var k in other.Tables.Keys.Where(k => !Tables.ContainsKey(k)))
                diff.Add($"{k}: 새로 나타남");
            return diff;
        }
    }

    /// <summary>세션 실행 옵션(원버튼 동작 조정).</summary>
    public sealed class SessionOptions
    {
        /// <summary>[3] DryRun 단계를 수행할지. 끄면 Trial 로 바로 간다(읽기 검증은 Trial 이 포함).</summary>
        public bool RunDryRun { get; init; } = true;

        /// <summary>스킵률 경고 임계(0.01 = 1%).</summary>
        public double SkipRateWarnThreshold { get; init; } = 0.01;

        // [찾기:멱등재검증]
        /// <summary>
        /// Commit 후 Trial 을 한 번 더 돌려 "적재 0 / 재사용 전량"인지 확인할지.
        /// 멱등이 실제로 걸렸는지 증명하는 가장 확실한 방법이지만, INSERT 후 롤백이라
        /// 타겟 IDENTITY 시퀀스를 한 번 더 소비한다. 기본은 끔.
        /// </summary>
        public bool VerifyIdempotencyByRerun { get; init; }
    }

    /// <summary>
    /// 연결 점검 → 매핑 리스트 → DryRun → Trial → (사람 게이트) → Commit → 사후 검증까지
    /// 한 세션으로 묶어 실행하고, 모든 산출물을 하나의 세션 폴더에 남긴다.
    /// </summary>
    public sealed class MigrationSession
    {
        private readonly DbToDbOptions _base;
        private readonly SessionOptions _sopt;
        private readonly Action<string> _log;

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>세션 식별자(폴더명에 쓰인다).</summary>
        public string SessionId { get; }

        /// <summary>이 세션의 산출물 폴더.</summary>
        public string Dir { get; }

        public PreflightReport? Preflight { get; private set; }
        public SchemaMapResult? SchemaMap { get; private set; }
        public MigrationReport? DryRunReport { get; private set; }
        public MigrationReport? TrialReport { get; private set; }
        public MigrationReport? CommitReport { get; private set; }
        public SourceFingerprint? Fingerprint { get; private set; }

        /// <summary>dedup 키 고유성 검사 결과(2단계 산출).</summary>
        public List<DedupKeyAudit.KeyAudit> KeyAudits { get; private set; } = new();

        public GateScore? G1 { get; private set; }
        public GateScore? G2 { get; private set; }
        public GateScore? G3 { get; private set; }
        public GateScore? G4 { get; private set; }

        /// <summary>리허설([1]~[4])이 전부 끝나고 G4 까지 채점됐는지 — Commit 진입 전제.</summary>
        public bool RehearsalComplete { get; private set; }

        private const int TotalStages = 7;

        /// <param name="options">이관 옵션(실행 방식은 단계마다 세션이 바꿔 쓴다).</param>
        /// <param name="sessionOptions">세션 동작 옵션(없으면 기본값).</param>
        /// <param name="log">진행 로그 싱크.</param>
        public MigrationSession(DbToDbOptions options, SessionOptions? sessionOptions = null, Action<string>? log = null)
        {
            _base = options;
            _sopt = sessionOptions ?? new SessionOptions();
            _log = log ?? (_ => { });
            SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            Dir = Path.Combine(AppContext.BaseDirectory, "logs", "session_" + SessionId);
            Directory.CreateDirectory(Dir);
        }

        // ════════════════════════════════════════════════════════════
        //  리허설 — [1] 연결 → [2] 매핑 → [3] DryRun → [4] Trial → G4 채점
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Commit 직전까지를 무인으로 실행한다. 게이트에서 막히면 그 단계에서 멈추고 false 를 돌려준다.
        /// 부작용: [4] Trial 은 실제 INSERT 후 롤백이므로 타겟 IDENTITY 시퀀스를 소비한다.
        /// </summary>
        /// <returns>G4 까지 도달해 통과했으면 true(=Commit 승인을 물을 수 있는 상태).</returns>
        public async Task<bool> RunRehearsalAsync(
            IProgress<StageProgress>? progress = null, CancellationToken ct = default)
        {
            RehearsalComplete = false;
            WriteSessionHeader();

            // ── [1] 연결 점검 ──
            progress?.Report(new StageProgress(Stage.Connect, 1, TotalStages, "연결·복호화·파일망 점검"));
            _log("═══ [1/7] 연결 점검 ═══");
            Preflight = await new DbToDbPreflight(_base, _log).RunAsync(ct);
            G1 = ScoreG1(Preflight);
            WriteJson("01_preflight.json", new
            {
                sourceOk = Preflight.SourceOk, sourceMsg = Preflight.SourceMsg,
                targetOk = Preflight.TargetOk, targetMsg = Preflight.TargetMsg,
                decryptMode = Preflight.DecryptMode.ToString(),
                canDecrypt = Preflight.CanDecrypt, decryptMsg = Preflight.DecryptMsg,
                blockedTables = Preflight.EncryptedBlocked,
                roots = Preflight.Roots.Select(r => new { root = r.Root, ok = r.Ok, files = r.Files }),
                snippets = Preflight.Snippets.Select(s => new { table = s.Table, referenced = s.Referenced, found = s.Found }),
                gate = Describe(G1),
            });
            _log(G1.ToText());
            if (!G1.Pass) { _log("‼ G1 차단 — 연결 문제를 해결한 뒤 다시 실행하세요."); return false; }

            // ── [2] 매핑 리스트 ──
            progress?.Report(new StageProgress(Stage.Mapping, 2, TotalStages, "전 테이블 컬럼 매핑 리스트 생성"));
            _log("═══ [2/7] 매핑 리스트 생성 ═══");
            SchemaMap = await MappingExporter.BuildSchemaMapAsync(_base, _log, ct);
            foreach (var p in MappingExporter.WriteSchemaMap(SchemaMap, Dir)) _log("  [매핑] " + p);

            // [찾기:dedup감사] 키가 고유하지 않으면 dedup 은 중복 방지가 아니라 행 삭제가 된다.
            //   그 손실은 스킵도 오류도 아닌 "재사용" 으로 집계돼 뒤 게이트에 전혀 걸리지 않는다.
            //   그래서 실행 전에, 소스에서 직접 세어 여기서 막는다.
            KeyAudits = await DedupKeyAudit.RunAsync(_base, SchemaMap, _log, ct);
            WriteText("02_dedup_keys.tsv", BuildKeyAuditTsv(KeyAudits));

            G2 = ScoreG2(SchemaMap, KeyAudits);
            WriteJson("02_gate_mapping.json", Describe(G2));
            _log(G2.ToText());
            if (!G2.Pass) { _log("‼ G2 차단 — 매핑 문제를 해결한 뒤 다시 실행하세요."); return false; }

            // ── [3] DryRun ──
            if (_sopt.RunDryRun)
            {
                progress?.Report(new StageProgress(Stage.DryRun, 3, TotalStages, "DryRun — 읽기·복호·FK 치환 검증 (쓰기 없음)"));
                _log("═══ [3/7] DryRun ═══");
                DryRunReport = await new DbToDbExecutor(_base.CloneWith(RunMode.DryRun), _log).RunAsync(ct);
                G3 = ScoreG3(DryRunReport);
                WriteJson("03_dryrun.json", Summarize(DryRunReport, G3));
                _log(G3.ToText());
                if (!G3.Pass) { _log("‼ G3 차단 — DryRun 오류를 해결한 뒤 다시 실행하세요."); return false; }
            }
            else _log("═══ [3/7] DryRun 생략 (옵션) ═══");

            // ── [4] Trial ──
            progress?.Report(new StageProgress(Stage.Trial, 4, TotalStages, "Trial — 실제 INSERT 후 롤백 (리허설)"));
            _log("═══ [4/7] Trial (INSERT 후 롤백) ═══");
            TrialReport = await new DbToDbExecutor(_base.CloneWith(RunMode.Trial), _log).RunAsync(ct);

            // [찾기:소스지문] Trial 시점의 소스 상태를 찍어 둔다 — Commit 직전에 대조한다.
            Fingerprint = await TakeFingerprintAsync(ct);

            // ── G4 채점 ──
            progress?.Report(new StageProgress(Stage.Gate, 5, TotalStages, "게이트 채점 — 사람 승인 대기"));
            G4 = ScoreG4(TrialReport);
            WriteJson("04_trial.json", Summarize(TrialReport, G4));
            // [찾기:빈파일목록] 리허설 단계에서도 남긴다 — 승인 전에 "무엇이 비는지" 를 보고 판단해야 한다.
            foreach (var f in MappingExporter.WriteMissingFileList(TrialReport, Dir, "04_missing_files"))
                _log("  [빈파일] " + f);
            WriteText("04_gate.txt", G4.ToText());
            _log(G4.ToText());

            RehearsalComplete = true;
            _log(G4.Pass
                ? "★ 리허설 통과 — [실제 이관] 승인 단계로 진행할 수 있습니다."
                : "‼ G4 차단 — 아래 차단 항목을 해소하기 전에는 실제 이관을 권하지 않습니다.");
            return G4.Pass;
        }

        // ════════════════════════════════════════════════════════════
        //  실제 이관 — [5] Commit → [6] 검증 + 레코드 매핑
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 승인된 리허설 결과를 근거로 실제 이관을 수행하고, 사후 검증과 레코드 매핑 리스트까지 만든다.
        ///
        /// 안전장치: Commit 직전에 소스 지문을 다시 찍어 Trial 시점과 다르면 <b>커밋하지 않는다</b>.
        /// 리허설이 검증한 것과 다른 데이터를 넣는 일을 막기 위함이다.
        /// </summary>
        /// <param name="approver">승인자 표기(보고서·감사 추적용).</param>
        /// <returns>커밋까지 마쳤으면 true.</returns>
        public async Task<bool> RunCommitAsync(
            string approver, IProgress<StageProgress>? progress = null, CancellationToken ct = default)
        {
            if (!RehearsalComplete)
            {
                _log("‼ 리허설을 먼저 완료해야 합니다(Commit 은 Trial 결과를 근거로만 진행).");
                return false;
            }

            // ── 소스 지문 재확인 ──
            progress?.Report(new StageProgress(Stage.Commit, 6, TotalStages, "소스 변경 여부 재확인"));
            _log("═══ [5/7] Commit 전 소스 지문 대조 ═══");
            var now = await TakeFingerprintAsync(ct);
            var diff = Fingerprint?.Diff(now) ?? new List<string>();
            if (diff.Count > 0)
            {
                _log("‼ 소스가 Trial 이후 변경되었습니다 — 커밋을 중단합니다:");
                foreach (var d in diff.Take(20)) _log("    · " + d);
                WriteText("05_aborted_fingerprint.txt",
                    "Trial 이후 소스 변경으로 커밋 중단\n\n" + string.Join("\n", diff));
                return false;
            }
            _log($"  ✓ 소스 지문 일치 ({Fingerprint?.Tables.Count ?? 0}개 테이블)");

            // ── [5] Commit ──
            progress?.Report(new StageProgress(Stage.Commit, 6, TotalStages, "실제 이관 (커밋)"));
            _log("═══ [5/7] Commit (실제 적재) ═══");
            _log($"  승인자: {approver}");
            CommitReport = await new DbToDbExecutor(_base.CloneWith(RunMode.Commit), _log).RunAsync(ct);
            WriteJson("05_commit.json", Summarize(CommitReport, null));

            // 승인 기록 — 감사 추적
            WriteJson("04_gate_approval.json", new
            {
                approver,
                approvedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                gatePassed = G4?.Pass ?? false,
                blockedChecks = G4?.Checks.Where(c => !c.Pass && c.Blocking).Select(c => c.Name).ToList()
                                ?? new List<string>(),
                warnedChecks = G4?.Checks.Where(c => !c.Pass && !c.Blocking).Select(c => c.Name).ToList()
                               ?? new List<string>(),
                target = MaskConn(_base.TargetConnStr),
            });

            // ── [6] 검증 + 레코드 매핑 ──
            progress?.Report(new StageProgress(Stage.Verify, 7, TotalStages, "사후 검증 + 레코드 매핑 리스트"));
            _log("═══ [6/7] 사후 검증 ═══");
            var verify = await VerifyAsync(CommitReport, ct);
            WriteJson("06_verify.json", verify);
            foreach (var line in verify.Lines) _log("  " + line);

            _log("═══ [7/7] 레코드 매핑 리스트 ═══");
            foreach (var p in MappingExporter.WriteRecordMap(CommitReport, Dir)) _log("  [매핑] " + p);
            foreach (var f in MappingExporter.WriteMissingFileList(CommitReport, Dir))
                _log("  [빈파일] " + f + "  ← 파일 확보 후 이 행들을 다시 채우세요");

            WriteText("00_summary.txt", BuildFinalSummary(verify, approver));
            _log($"★ 세션 완료 — 산출물 폴더: {Dir}");
            return true;
        }

        // ════════════════════════════════════════════════════════════
        //  게이트 채점
        // ════════════════════════════════════════════════════════════

        /// <summary>G1 — 연결·복호화·파일망. 하나라도 막히면 이후 단계가 무의미하다.</summary>
        private static GateScore ScoreG1(PreflightReport p)
        {
            var g = new GateScore { Name = "G1 연결 점검" };
            g.Add("소스 연결", p.SourceOk ? "성공" : "실패", "성공", p.SourceOk, true, p.SourceMsg);
            g.Add("타겟 연결", p.TargetOk ? "성공" : "실패", "성공", p.TargetOk, true, p.TargetMsg);
            g.Add("이름 복호화", p.CanDecrypt ? $"가능({p.DecryptMode})" : $"불가({p.DecryptMode})", "가능",
                  p.CanDecrypt, true,
                  p.CanDecrypt ? p.DecryptMsg
                               : "복호화 불가 시 암호화 테이블이 전량 차단됩니다. " +
                                 "이미 평문으로 dump 된 소스라면 [복호화 불러오기]를 꺼서 원문 그대로 이관하세요.");

            int badRoots = p.Roots.Count(r => !r.Ok);
            g.Add("파일 루트 접근", badRoots == 0 ? $"{p.Roots.Count}곳 정상" : $"{badRoots}곳 실패", "전부 정상",
                  badRoots == 0, true,
                  badRoots == 0 ? "" : "스니펫·영상 파일을 읽을 수 없으면 blob 컬럼이 비어 이관됩니다.");

            int failedProbe = p.Snippets.Count(s => s.Referenced < 0);
            g.Add("스니펫 참조 조회", failedProbe == 0 ? "정상" : $"{failedProbe}개 테이블 조회 실패", "정상",
                  failedProbe == 0, true,
                  failedProbe == 0 ? "" : "조회 실패를 '참조 0건 통과'로 오판하지 않도록 차단합니다.");

            foreach (var s in p.Snippets.Where(s => s.Referenced > 0))
            {
                double rate = (double)s.Found / s.Referenced;
                g.Add($"파일 확보율 {s.Table}", $"{s.Found}/{s.Referenced} ({rate:P0})", "> 0",
                      s.Found > 0, false,
                      s.Found == 0 ? "참조 파일을 하나도 못 찾았습니다 — 루트 경로를 확인하세요." : "");
            }
            return g;
        }

        /// <summary>dedup 키 검사 결과를 표로 남긴다(검증 산출물).</summary>
        private static string BuildKeyAuditTsv(List<DedupKeyAudit.KeyAudit> keys)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join("\t", "소스테이블", "방식", "대조행", "고유조합", "유실예상", "제외행", "판정", "키컬럼", "비고"));
            foreach (var k in keys)
                sb.AppendLine(string.Join("\t", k.SourceTable, k.Kind, k.Rows, k.Combos, k.Collapse,
                                          k.Skipped, k.Verdict, string.Join(", ", k.Columns), k.Error ?? ""));
            return sb.ToString();
        }

        /// <summary>G2 — 매핑. 타겟 미확정·플랜 오류·dedup 키 붕괴는 차단, 미결 규칙은 경고.</summary>
        private static GateScore ScoreG2(SchemaMapResult m, List<DedupKeyAudit.KeyAudit> keys)
        {
            var g = new GateScore { Name = "G2 매핑 리스트" };

            // [NAIMS-ISSUE:DEDUP-KEY-VERIFY] 키가 고유하지 않으면 그 차이만큼 행이 사라진다.
            //   실측 TR_FRAME 495행 → 3행(492 유실)이 "재사용 492건" 으로 위장됐다.
            //   이건 자료 문제가 아니라 계획 오류라 반드시 막는다.
            var collapsing = keys.Where(k => k.Collapse > 0).ToList();
            long lost = collapsing.Sum(k => k.Collapse);
            g.Add("dedup 키 붕괴", collapsing.Count == 0 ? "0" : $"{collapsing.Count}개 표 / {lost:N0}행",
                  "0", collapsing.Count == 0, true,
                  collapsing.Count == 0 ? ""
                  : "키가 고유하지 않아 그만큼 행이 사라집니다(스킵이 아니라 '재사용' 으로 집계돼 뒤 게이트가 못 잡습니다). "
                    + string.Join(" / ", collapsing.Take(4).Select(k =>
                        $"{k.SourceTable} {k.Rows:N0}→{k.Combos:N0} [{string.Join(",", k.Columns)}]"))
                    + " — MigrationPlan 의 DedupKeyColumns 를 고치거나 비우세요.");

            var keyErrors = keys.Where(k => k.Error != null).ToList();
            if (keyErrors.Count > 0)
                g.Add("dedup 키 검사 실패", keyErrors.Count.ToString(), "0", false, true,
                      string.Join(" / ", keyErrors.Take(3).Select(k => $"{k.SourceTable}: {k.Error}")));

            g.Add("dedup 키 대조 제외행", keys.Sum(k => k.Skipped).ToString("N0"), "참고", true, false,
                  "키 값이 비어 대조에서 빠지는 행 수입니다 — 이 행들은 항상 INSERT 됩니다(재실행 시 중복 가능).");

            g.Add("타겟 미확정 테이블", m.UndecidedTargets.ToString(), "0",
                  m.UndecidedTargets == 0, true, "타겟이 정해지지 않은 테이블은 이관할 수 없습니다.");

            // dedup 키 부재는 재실행 중복 위험이라 사람이 반드시 봐야 한다.
            int dedupProblems = m.Problems.Count(s => s.Contains("dedup 키"));
            g.Add("dedup 키 부재", dedupProblems.ToString(), "0",
                  dedupProblems == 0, true,
                  dedupProblems == 0 ? "" : "복합키 멱등이 꺼져 재실행 시 중복 INSERT 가 발생합니다.");

            int planProblems = m.Problems.Count(s => s.Contains("플랜"));
            g.Add("플랜 컬럼 오류", planProblems.ToString(), "0",
                  planProblems == 0, true,
                  planProblems == 0 ? "" : "PK/FK 컬럼명이 실제 소스 스키마와 다릅니다 — MigrationPlan 을 교정하세요.");

            int other = m.Problems.Count - dedupProblems - planProblems;
            g.Add("기타 매핑 문제", other.ToString(), "0", other == 0, false);
            g.Add("미결 판정 컬럼", m.Pending.ToString(), "0", m.Pending == 0, false,
                  "규칙이 '미결'인 컬럼은 NULL 이거나 행 스킵으로 처리됩니다.");
            g.Add("소스에만 있는 컬럼", m.Unmapped.ToString(), "참고", true, false,
                  "타겟 대응이 없어 버려지는 컬럼 수입니다(정상일 수 있음).");
            g.Add("매핑 컬럼 총수", m.TotalRows.ToString("N0"), "> 0", m.TotalRows > 0, true);
            return g;
        }

        /// <summary>
        /// G3 — DryRun. <b>집계(적재/재사용/건너뜀)는 보지 않는다</b>: DryRun 은 멱등 대조가 꺼져 있어
        /// 실전과 다르다. 오류와 차단만 판정한다.
        /// </summary>
        private static GateScore ScoreG3(MigrationReport r)
        {
            var g = new GateScore { Name = "G3 DryRun" };
            int blocked = r.Tables.Count(t => t.Blocked);
            g.Add("차단 테이블", blocked.ToString(), "0", blocked == 0, true,
                  blocked == 0 ? "" : string.Join(", ", r.Tables.Where(t => t.Blocked).Select(t => t.Source)));

            // [NAIMS-ISSUE:GATE-SEVERITY] 오류를 성격으로 나눈다.
            //   · 코드·매핑 결함(타입변환·미선언FK·규칙없음·정체불명 예외) → 우리가 고쳐야 한다. 차단.
            //   · 자료 문제(참조 파일 부재·소스 고아 FK) → 우리가 고칠 수 없다. 사람이 판단할 경고.
            //   예전에는 둘을 합쳐 "오류 0" 을 차단 기준으로 썼는데, 파일 몇 건 없다고 700행 이관이
            //   통째로 막혔다. 안전장치가 일을 막기만 하면 결국 꺼버리게 된다.
            int Sum(string kind) => r.Tables.Sum(t => t.SkipReasons.TryGetValue(kind, out var n) ? n : 0);

            int codeErrors = Sum(DbToDbExecutor.SkipKind.TypeCast)
                           + Sum(DbToDbExecutor.SkipKind.FkUndeclared)
                           + Sum(DbToDbExecutor.SkipKind.NoRule)
                           + Sum(DbToDbExecutor.SkipKind.RuleNotImplemented)
                           + Sum(DbToDbExecutor.SkipKind.Exception);
            g.Add("코드·매핑 결함", codeErrors.ToString(), "0", codeErrors == 0, true,
                  codeErrors == 0 ? "" : "타입 변환·미선언 FK·규칙 없음·정체불명 예외 — 매핑 계획을 고쳐야 합니다.");

            int missing = Sum(DbToDbExecutor.SkipKind.MissingFile);
            if (missing > 0)
                g.Add("참조 파일 부재", missing.ToString(), "0", false, false,
                      "스니펫·영상 파일이 없어 해당 행을 건너뜁니다. 자료를 구해 오거나, " +
                      "빠진 채로 진행할지 승인 단계에서 판단하세요.");

            int fkMissing = Sum(DbToDbExecutor.SkipKind.FkParentMissing);
            if (fkMissing > 0)
                g.Add("FK 부모 미이관", fkMissing.ToString(), "0", false, false,
                      "부모가 없어 자식이 빠집니다. 부모가 파일 부재로 스킵된 연쇄인지, " +
                      "소스 자체의 고아 FK 인지 구분해 판단하세요(후자는 소스 데이터 문제).");

            g.Add("소스 읽기", $"{r.Tables.Sum(t => t.SourceRows):N0}행", "> 0",
                  r.Tables.Sum(t => t.SourceRows) > 0, true);
            g.Add("주의", "집계는 실전과 다름", "참고", true, false,
                  "DryRun 은 멱등 대조가 꺼져 있어 적재/재사용/건너뜀 수치를 신뢰하면 안 됩니다.");
            return g;
        }

        /// <summary>G4 — Trial. 실제 INSERT 경로를 통과한 결과라 이것이 진짜 판정이다.</summary>
        private GateScore ScoreG4(MigrationReport r)
        {
            var g = new GateScore { Name = "G4 Trial (실제 이관 승인 판정)" };

            int blocked = r.Tables.Count(t => t.Blocked);
            g.Add("차단 테이블", blocked.ToString(), "0", blocked == 0, true,
                  blocked == 0 ? "" : string.Join(", ", r.Tables.Where(t => t.Blocked).Select(t => t.Source)));

            int Sum(string kind) => r.Tables.Sum(t => t.SkipReasons.TryGetValue(kind, out var n) ? n : 0);

            // [NAIMS-ISSUE:GATE-ERROR-DOUBLECOUNT] r.TotalErrors 는 '독립된 오류 수'가 아니다.
            //   행이 스킵될 때마다 그 사유가 Errors 에 표본으로도 쌓인다(테이블당 20건 상한).
            //   그래서 예전에는 같은 사건을 아래에서 '경고'로 세고 여기서 다시 '차단'으로 셌다 —
            //   실측 로그에서 "오류 58 → 차단" 인데 그 58 이 전부 파일부재·FK 스킵의 표본이었다.
            //   안전장치가 자기 경고를 차단 사유로 되먹임하면 사람은 결국 그것을 꺼 버린다.
            //   차단은 '우리가 고쳐야 하는 결함' 에만 건다. 총 오류 표본 수는 참고로만 보여 준다.
            int codeErrors = Sum(DbToDbExecutor.SkipKind.TypeCast)
                           + Sum(DbToDbExecutor.SkipKind.FkUndeclared)
                           + Sum(DbToDbExecutor.SkipKind.NoRule)
                           + Sum(DbToDbExecutor.SkipKind.RuleNotImplemented)
                           + Sum(DbToDbExecutor.SkipKind.Exception);
            g.Add("코드·매핑 결함", codeErrors.ToString(), "0", codeErrors == 0, true,
                  codeErrors == 0 ? "" : "타입변환·미선언FK·규칙없음·정체불명 예외 — 도구를 고쳐야 합니다.");
            g.Add("오류 표본 수", r.TotalErrors.ToString(), "참고", true, false,
                  r.TotalErrors == 0 ? "" : "스킵 사유 표본을 포함한 수치입니다(테이블당 20건 상한) — 아래 사유별 건수를 보세요.");

            // [NAIMS-ISSUE:GATE-SEVERITY] 자료 문제는 경고 — 우리가 코드로 고칠 수 없고,
            //   "빠진 채로 갈지" 는 사람이 정할 문제다. 대신 건수를 정확히 보여 준다.
            // [찾기:파일부재해결] 건수만 통보하지 않는다 — 어떻게 할지 <여기서 정할 수 있다> 고 말해 준다.
            //   "파일이 없다" 는 아직 못 받아온 것일 수도, 폐기된 것일 수도 있는데 도구는 구별하지 못한다.
            //   그래서 기본값(빈 값 적재)만 알리고 끝내면, 사람은 정할 수 있다는 사실조차 모른 채 넘어간다.
            int emptyFilled = r.Tables.Sum(t => t.MissingFileRefs.Count);
            int emptyFiles = r.Tables.SelectMany(t => t.MissingFileRefs)
                                     .Select(m => m.FileName)
                                     .Where(f => !string.IsNullOrWhiteSpace(f))
                                     .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (emptyFilled > 0)
                g.Add("파일 부재 → 빈 값 적재", emptyFilled.ToString(), "참고", true, false,
                      $"파일 {emptyFiles}종이 없어 그램·원음·영상 blob 이 빈 채로 들어갑니다(행은 적재). " +
                      "아직 못 받아온 파일이면 그대로 두고 나중에 전량 재구축하면 채워집니다. " +
                      "폐기된 자료라 행째로 빼야 한다면 아래 [📁 파일 부재 해결] 에서 지금 정하세요.");

            // 사람이 이미 판단한 것은 '고칠 것' 과 같은 칸에 세지 않는다.
            // [찾기:충돌해결] 정할 것이 남아 있으면 알린다 — 차단은 아니다(기본 '유지' 도 유효한 선택이다).
            int conflicts = r.Tables.Sum(t => t.Conflicts.Count);
            if (conflicts > 0)
                g.Add("멱등 충돌(값 다름)", conflicts.ToString(), "참고", true, false,
                      "타겟에 이미 있고 값이 다른 행입니다. 그대로 두면 <유지>(종전 동작)이고, " +
                      "소스에서 값을 바로잡았다면 아래 [♻ 멱등 충돌 해결] 에서 덮어쓰기를 고르세요.");

            int overwritten = r.Tables.Sum(t => t.Overwritten);
            if (overwritten > 0)
                g.Add("사용자 결정: 덮어쓰기", overwritten.ToString(), "참고", true, false,
                      "해결 창에서 정한 대로 기존 타겟 행을 갱신했습니다.");

            int userDiscard = Sum(DbToDbExecutor.SkipKind.ManualDiscard);
            if (userDiscard > 0)
                g.Add("사용자 결정: 버림", userDiscard.ToString(), "참고", true, false,
                      "해결 창에서 '버림' 으로 정한 행입니다 — 사고가 아니라 결정이라 차단하지 않습니다.");

            int missing = Sum(DbToDbExecutor.SkipKind.MissingFile);
            g.Add("참조 파일 부재 스킵", missing.ToString(), "0", missing == 0, false,
                  missing == 0 ? "" : "스니펫·영상 파일이 없어 그 행은 이관되지 않습니다. " +
                                      "지금 진행하면 나중에 파일을 구해 재실행해야 합니다(멱등이라 안전).");

            int fkMissing = Sum(DbToDbExecutor.SkipKind.FkParentMissing);
            g.Add("FK 부모 미이관 스킵", fkMissing.ToString(), "0", fkMissing == 0, false,
                  fkMissing == 0 ? "" : "부모가 없어 자식이 빠집니다. 부모가 파일 부재로 스킵된 연쇄면 " +
                                        "파일 확보 후 재실행으로 채워지고, 소스 자체의 고아 FK 면 채울 수 없습니다.");

            // [NAIMS-ISSUE:REUSED-PARENT-NO-ID] 이건 소스 탓이 아니라 도구가 못 푼 것이다 → 차단.
            int pidUnknown = Sum(DbToDbExecutor.SkipKind.FkParentIdUnknown);
            g.Add("FK 부모ID 미상 스킵", pidUnknown.ToString(), "0", pidUnknown == 0, true,
                  pidUnknown == 0 ? "" : "부모는 타겟에 있는데 RECORD_ID 를 되찾지 못해 자식이 빠집니다. " +
                                         "해당 부모 테이블에 DedupKeyColumns 를 지정하거나 ForceReimport 로 재구축하세요.");

            int cast = Sum(DbToDbExecutor.SkipKind.TypeCast);
            g.Add("타입 변환 실패 스킵", cast.ToString(), "0", cast == 0, true,
                  cast == 0 ? "" : "Oracle 소스에서 자주 발생합니다 — 값 형식을 확인하세요.");

            int undecl = Sum(DbToDbExecutor.SkipKind.FkUndeclared);
            g.Add("미선언 FK 스킵", undecl.ToString(), "0", undecl == 0, true,
                  undecl == 0 ? "" : "MigrationPlan.ForeignKeys 에 FK 선언을 추가해야 합니다.");

            int partial = r.Tables.Sum(t => t.PartialSkipWarnings);
            g.Add("부분 누락 의심", partial.ToString(), "0", partial == 0, true,
                  partial == 0 ? "" : "부모단위 스킵이 자식 일부를 영구 누락시킬 수 있습니다 — ForceReimport 재구축을 검토하세요.");

            int dedupOff = r.Tables.Sum(t => t.DedupDisabledColumns);
            g.Add("dedup 비활성 컬럼", dedupOff.ToString(), "0", dedupOff == 0, true,
                  dedupOff == 0 ? "" : "재실행 시 중복 INSERT 위험이 있습니다.");

            int dedupBad = r.Tables.Sum(t => t.DedupUnusableWarnings);
            g.Add("dedup 대조 불가", dedupBad.ToString(), "0", dedupBad == 0, false,
                  dedupBad == 0 ? "" : "키 값이 비어 대조를 못 한 행이 있습니다 — 재실행 시 중복 가능.");

            int zero = Sum(DbToDbExecutor.SkipKind.FkZeroSentinel);
            int notnull = Sum(DbToDbExecutor.SkipKind.NotNull);
            int norule = Sum(DbToDbExecutor.SkipKind.NoRule) + Sum(DbToDbExecutor.SkipKind.RuleNotImplemented);
            if (zero > 0) g.Add("0 센티넬 스킵", zero.ToString(), "0", false, false,
                                "타겟 부모에 RECORD_ID=0 행이 없어 '참조 없음'을 표현할 수 없습니다.");
            if (notnull > 0) g.Add("NOT NULL 값없음 스킵", notnull.ToString(), "0", false, false);
            if (norule > 0) g.Add("규칙 없음/미구현 스킵", norule.ToString(), "0", false, false);

            long srcTotal = r.Tables.Sum(t => (long)t.SourceRows);
            double skipRate = srcTotal > 0 ? (double)r.TotalSkipped / srcTotal : 0;
            g.Add("스킵률", $"{r.TotalSkipped:N0}/{srcTotal:N0} ({skipRate:P2})",
                  $"≤ {_sopt.SkipRateWarnThreshold:P0}",
                  skipRate <= _sopt.SkipRateWarnThreshold, false,
                  "임계를 넘으면 사유별 내역을 확인한 뒤 승인하세요.");

            g.Add("적재 예정", $"{r.TotalInserted:N0}건", "> 0", r.TotalInserted > 0, false,
                  r.TotalInserted == 0 ? "새로 넣을 것이 없습니다(이미 전부 이관됐거나 범위가 비어 있음)." : "");
            g.Add("재사용", $"{r.TotalReused:N0}건", "참고", true, false);

            var breakdown = r.Tables.Where(t => t.SkipReasons.Count > 0)
                .Select(t => $"{t.Source}[{t.SkipBreakdown}]");
            if (breakdown.Any())
                g.Add("스킵 내역", string.Join(" ", breakdown.Take(8)), "참고", true, false);

            return g;
        }

        // ════════════════════════════════════════════════════════════
        //  소스 지문
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// [찾기:소스지문] 대상 테이블마다 (행수, 최소 PK, 최대 PK)를 읽어 지문을 만든다.
        /// 값싸면서도 "그 사이 데이터가 바뀌었는가"를 실용적으로 잡는다.
        /// </summary>
        private async Task<SourceFingerprint> TakeFingerprintAsync(CancellationToken ct)
        {
            var fp = new SourceFingerprint();
            var plans = MigrationPlan.BuildOrder(MigrationPlan.Defaults());

            try
            {
                await using var cn = DbProvider.CreateConnection(_base.SourceKind, _base.SourceMode, _base.SourceConnStr);
                await cn.OpenAsync(ct);

                foreach (var p in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var q = DbProvider.QuoteIdent(_base.SourceKind, p.SourceTable);
                        var pk = DbProvider.QuoteColumn(_base.SourceKind, p.SourcePk);
                        await using var cmd = cn.CreateCommand();
                        cmd.CommandText = $"SELECT COUNT(*), MIN({pk}), MAX({pk}) FROM {q}";
                        cmd.CommandTimeout = 60;
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            var n = Convert.ToString(r.GetValue(0));
                            var lo = r.IsDBNull(1) ? "-" : Convert.ToString(r.GetValue(1));
                            var hi = r.IsDBNull(2) ? "-" : Convert.ToString(r.GetValue(2));
                            fp.Tables[p.SourceTable] = $"{n}/{lo}/{hi}";
                        }
                    }
                    catch
                    {
                        // 개별 테이블 실패는 지문에서 제외한다(대조 시 "조회 실패"로 드러난다).
                    }
                }
            }
            catch (Exception ex)
            {
                _log("  ! 소스 지문 채취 실패 — " + ex.Message);
            }
            return fp;
        }

        // ════════════════════════════════════════════════════════════
        //  사후 검증
        // ════════════════════════════════════════════════════════════

        /// <summary>사후 검증 결과.</summary>
        public sealed class VerifyResult
        {
            public List<string> Lines { get; } = new();
            public List<string> Problems { get; } = new();
            public bool Ok => Problems.Count == 0;
        }

        /// <summary>
        /// 커밋 결과가 실제로 성립하는지 타겟에서 확인한다.
        ///   ① 행수 항등식   : 소스행 = 적재 + 재사용 + 스킵
        ///   ② ID 짝 완전성  : IdPairs 수 vs 적재+재사용
        ///   ③ FK 무결성     : 자식의 REF_*_ID 가 부모에 실재하는가
        ///   ④ 이름 복호     : 타겟 이름 컬럼이 읽을 수 있는 문자열인가(값은 기록하지 않는다)
        ///   ⑤ 파생 자식     : STD_AI_SNIPPET 대비 STD_AI_SPECTROGRAM 수
        /// </summary>
        private async Task<VerifyResult> VerifyAsync(MigrationReport rep, CancellationToken ct)
        {
            var v = new VerifyResult();
            var plans = MigrationPlan.BuildOrder(MigrationPlan.Defaults());
            var tgtBySrc = plans.ToDictionary(p => p.SourceTable, p => p.TargetTable, StringComparer.OrdinalIgnoreCase);

            // ① 행수 항등식
            foreach (var t in rep.Tables.Where(t => !t.Blocked))
            {
                int accounted = t.Inserted + t.Reused + t.Skipped;
                if (t.SourceRows != accounted)
                {
                    var msg = $"행수 불일치 {t.Source}: 소스 {t.SourceRows} ≠ 적재{t.Inserted}+재사용{t.Reused}+스킵{t.Skipped}={accounted}";
                    v.Problems.Add(msg); v.Lines.Add("✗ " + msg);
                }
            }
            if (v.Problems.Count == 0) v.Lines.Add($"✓ 행수 항등식 — {rep.Tables.Count}개 테이블 전부 일치");

            // ② ID 짝 완전성 (부모단위 스킵은 RECORD_ID 를 모르므로 부족할 수 있다 — 정보로만)
            foreach (var t in rep.Tables.Where(t => !t.Blocked))
            {
                int expect = t.Inserted + t.Reused;
                if (t.IdPairs.Count < expect)
                    v.Lines.Add($"· ID 짝 {t.Source}: {t.IdPairs.Count}/{expect} " +
                                "(부모단위 스킵·링크 재사용은 타겟 RECORD_ID 를 모릅니다 — 정상)");
            }

            // ③ FK 무결성
            try
            {
                await using var cn = new SqlConnection(_base.TargetConnStr);
                await cn.OpenAsync(ct);

                int fkChecked = 0, fkBad = 0;
                foreach (var p in plans)
                {
                    if (p.TargetTable.StartsWith('(')) continue;
                    foreach (var fk in p.ForeignKeys)
                    {
                        if (!tgtBySrc.TryGetValue(fk.ParentSourceTable, out var parentTgt)) continue;
                        if (parentTgt.StartsWith('(')) continue;
                        try
                        {
                            await using var cmd = cn.CreateCommand();
                            cmd.CommandText =
                                $"SELECT COUNT(*) FROM [{p.TargetTable}] c " +
                                $"WHERE c.[{fk.TargetColumn}] IS NOT NULL AND c.[{fk.TargetColumn}] <> 0 " +
                                $"AND NOT EXISTS (SELECT 1 FROM [{parentTgt}] pp WHERE pp.RECORD_ID = c.[{fk.TargetColumn}])";
                            cmd.CommandTimeout = 120;
                            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                            fkChecked++;
                            if (n > 0)
                            {
                                fkBad++;
                                var msg = $"FK 고아 {p.TargetTable}.{fk.TargetColumn} → {parentTgt}: {n}건";
                                v.Problems.Add(msg); v.Lines.Add("✗ " + msg);
                            }
                        }
                        catch { /* 컬럼/테이블이 없는 조합은 건너뛴다 */ }
                    }
                }
                if (fkBad == 0) v.Lines.Add($"✓ FK 무결성 — {fkChecked}개 관계 검사, 고아 0건");

                // ④ 이름 복호 판독 (값은 기록하지 않는다 — 보고서 파일에 실명이 남지 않게)
                foreach (var (tbl, col) in new[]
                {
                    ("STD_CLASSES", "CLASS_NAME"),
                    ("STD_VESSEL_DATA", "VESSEL_NAME"),
                    ("STD_ACTV_SONAR", "SONAR_NAME"),
                    ("STD_TRANSIENT", "TR_NAME"),
                })
                {
                    try
                    {
                        await using var cmd = cn.CreateCommand();
                        cmd.CommandText = $"SELECT TOP 50 [{col}] FROM [{tbl}] WHERE [{col}] IS NOT NULL ORDER BY RECORD_ID DESC";
                        cmd.CommandTimeout = 60;
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        int total = 0, readable = 0;
                        while (await r.ReadAsync(ct))
                        {
                            total++;
                            if (IsReadable(Convert.ToString(r.GetValue(0)))) readable++;
                        }
                        if (total == 0) { v.Lines.Add($"· 이름 판독 {tbl}.{col}: 표본 없음"); continue; }
                        double rate = (double)readable / total;
                        if (rate < 0.9)
                        {
                            var msg = $"이름 판독 {tbl}.{col}: {readable}/{total} ({rate:P0}) — 복호 실패 의심";
                            v.Problems.Add(msg); v.Lines.Add("✗ " + msg);
                        }
                        else v.Lines.Add($"✓ 이름 판독 {tbl}.{col}: {readable}/{total} ({rate:P0})");
                    }
                    catch { /* 테이블·컬럼이 없으면 건너뛴다 */ }
                }

                // ⑤ 파생 자식 (AI 스니펫 : 스펙트로그램)
                try
                {
                    await using var c1 = cn.CreateCommand();
                    c1.CommandText = "SELECT (SELECT COUNT(*) FROM STD_AI_SNIPPET), (SELECT COUNT(*) FROM STD_AI_SPECTROGRAM)";
                    await using var r1 = await c1.ExecuteReaderAsync(ct);
                    if (await r1.ReadAsync(ct))
                    {
                        long snip = Convert.ToInt64(r1.GetValue(0)), spec = Convert.ToInt64(r1.GetValue(1));
                        if (spec < snip)
                            v.Lines.Add($"⚠ 파생 자식 — AI 스니펫 {snip} / 스펙트로그램 {spec} " +
                                        "(기준식별은 스펙트로그램이 있어야 스니펫을 전시합니다)");
                        else v.Lines.Add($"✓ 파생 자식 — AI 스니펫 {snip} / 스펙트로그램 {spec}");
                    }
                }
                catch { /* 테이블 없으면 건너뛴다 */ }
            }
            catch (Exception ex)
            {
                var msg = "타겟 검증 조회 실패 — " + ExtractRaw.ExceptionText.Describe(ex);
                v.Problems.Add(msg); v.Lines.Add("✗ " + msg);
            }

            // ⑥ 멱등 재검증 (옵션) — Trial 을 한 번 더 돌려 "적재 0 / 재사용 전량" 인지 본다.
            if (_sopt.VerifyIdempotencyByRerun)
            {
                _log("  · 멱등 재검증 — Trial 재실행(INSERT 후 롤백)");
                var again = await new DbToDbExecutor(_base.CloneWith(RunMode.Trial), _ => { }).RunAsync(ct);
                if (again.TotalInserted > 0)
                {
                    var msg = $"멱등 재검증 실패 — 재실행에서 {again.TotalInserted}건이 다시 적재 대상으로 잡힙니다(중복 위험)";
                    v.Problems.Add(msg); v.Lines.Add("✗ " + msg);
                }
                else v.Lines.Add($"✓ 멱등 재검증 — 재실행 적재 0 / 재사용 {again.TotalReused:N0}");
            }

            v.Lines.Add(v.Ok ? "★ 사후 검증 통과" : $"‼ 사후 검증 문제 {v.Problems.Count}건 — 위 항목을 확인하세요.");
            return v;
        }

        /// <summary>문자열이 사람이 읽을 수 있는 형태인지(제어문자·치환문자 비율로 판정).</summary>
        private static bool IsReadable(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            int bad = s.Count(ch => char.IsControl(ch) || ch == '�');
            return bad == 0 && s.Trim().Length > 0;
        }

        // ════════════════════════════════════════════════════════════
        //  산출물
        // ════════════════════════════════════════════════════════════

        private void WriteSessionHeader()
        {
            WriteJson("00_session.json", new
            {
                sessionId = SessionId,
                startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                source = new
                {
                    kind = DbProvider.DisplayName(_base.SourceKind),
                    mode = DbProvider.ModeName(_base.SourceMode),
                    conn = MaskConn(_base.SourceConnStr),
                },
                target = new { conn = MaskConn(_base.TargetConnStr) },
                options = new
                {
                    excludeCategories = _base.ExcludeCategories.ToArray(),
                    maxRowsPerTable = _base.MaxRowsPerTable,
                    useDecryption = _base.UseDecryption,
                    classFilter = _base.ClassFilter.ToArray(),
                    forceReimport = _base.ForceReimportTables.ToArray(),
                    classificationRoot = _base.ClassificationRoot,
                    runDryRun = _sopt.RunDryRun,
                    verifyIdempotencyByRerun = _sopt.VerifyIdempotencyByRerun,
                },
            });
            _log($"세션 시작 — {SessionId}");
            _log($"산출물 폴더: {Dir}");
        }

        private object Summarize(MigrationReport r, GateScore? gate) => new
        {
            dryRun = r.DryRun,
            totals = new
            {
                inserted = r.TotalInserted,
                reused = r.TotalReused,
                skipped = r.TotalSkipped,
                errors = r.TotalErrors,
            },
            tables = r.Tables.Select(t => new
            {
                source = t.Source,
                target = t.Target,
                sourceRows = t.SourceRows,
                inserted = t.Inserted,
                reused = t.Reused,
                skipped = t.Skipped,
                blocked = t.Blocked,
                derivedChildren = t.DerivedChildren,
                skipReasons = t.SkipReasons,
                partialSkipWarnings = t.PartialSkipWarnings,
                dedupDisabledColumns = t.DedupDisabledColumns,
                dedupUnusableWarnings = t.DedupUnusableWarnings,
                errors = t.Errors,
            }),
            gate = gate == null ? null : Describe(gate),
        };

        private static object Describe(GateScore g) => new
        {
            name = g.Name,
            pass = g.Pass,
            blocked = g.Blocked,
            warnings = g.Warnings,
            checks = g.Checks.Select(c => new
            {
                name = c.Name, value = c.Value, expected = c.Expected,
                pass = c.Pass, blocking = c.Blocking, level = c.Level, detail = c.Detail,
            }),
        };

        private string BuildFinalSummary(VerifyResult v, string approver)
        {
            var sb = new StringBuilder();
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  NAIMS → NAIMS2 이관 세션 요약   [{SessionId}]");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  소스   : {DbProvider.DisplayName(_base.SourceKind)} · {DbProvider.ModeName(_base.SourceMode)}  {MaskConn(_base.SourceConnStr)}");
            sb.AppendLine($"  타겟   : {MaskConn(_base.TargetConnStr)}");
            sb.AppendLine($"  승인자 : {approver}");
            sb.AppendLine();

            foreach (var g in new[] { G1, G2, G3, G4 })
                if (g != null) sb.AppendLine(g.ToText());

            if (CommitReport is { } c)
            {
                sb.AppendLine("── 커밋 결과 ──");
                foreach (var t in c.Tables)
                    sb.AppendLine("  " + t.Summary);
                sb.AppendLine($"  합계 — 적재 {c.TotalInserted:N0} / 재사용 {c.TotalReused:N0} / " +
                              $"건너뜀 {c.TotalSkipped:N0} / 오류 {c.TotalErrors}");
                sb.AppendLine();
            }

            sb.AppendLine("── 사후 검증 ──");
            foreach (var line in v.Lines) sb.AppendLine("  " + line);
            sb.AppendLine();
            sb.AppendLine("── 산출물 ──");
            foreach (var f in Directory.GetFiles(Dir).OrderBy(f => f))
                sb.AppendLine("  " + Path.GetFileName(f));
            return sb.ToString();
        }

        private void WriteJson(string name, object payload)
        {
            try { File.WriteAllText(Path.Combine(Dir, name), JsonSerializer.Serialize(payload, Json), new UTF8Encoding(false)); }
            catch (Exception ex) { _log($"  ! 산출물 기록 실패({name}) — {ex.Message}"); }
        }

        private void WriteText(string name, string text)
        {
            try { File.WriteAllText(Path.Combine(Dir, name), text, new UTF8Encoding(false)); }
            catch (Exception ex) { _log($"  ! 산출물 기록 실패({name}) — {ex.Message}"); }
        }

        /// <summary>접속문자열에서 비밀번호만 가린다(산출물 노출 방지).</summary>
        private static string MaskConn(string conn) =>
            string.IsNullOrEmpty(conn) ? "" :
            System.Text.RegularExpressions.Regex.Replace(
                conn, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
    }
}
