using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.ExtractRaw
{
    public sealed class ExtractProgress
    {
        public int Total { get; set; }
        public int Done { get; set; }
        public string CurrentItem { get; set; } = "";
        public string Phase { get; set; } = "";
    }

    public sealed class ExtractOptions
    {
        /// <summary>실제 INSERT 후 롤백한다.</summary>
        public bool DryRun { get; set; }

        /// <summary>PREC_SRC_SIG_NAME 이 이미 있으면 건너뛴다.</summary>
        public bool SkipDuplicates { get; set; } = true;

        /// <summary>이 크기를 넘는 WAV 는 제외(0이면 무제한).</summary>
        public long MaxWavBytes { get; set; } = 512L * 1024 * 1024;
    }

    /// <summary>
    /// 예외를 InnerException 까지 펼쳐 문자열로 만든다.
    /// TypeInitializationException 처럼 바깥 메시지만으로는 원인을 알 수 없는 경우가 있어
    /// 전 계층을 이어 붙여 진단 정보를 잃지 않게 한다.
    /// </summary>
    public static class ExceptionText
    {
        /// <summary>
        /// 예외 체인을 바깥→안쪽(InnerException) 순으로 순회하며 "[타입] 메시지" 를 이어 붙인다.
        /// 중복 라인은 한 번만 넣어 같은 메시지가 반복되는 것을 막는다.
        /// </summary>
        /// <param name="ex">펼칠 최상위 예외.</param>
        /// <returns>' ↳ ' 로 연결된 전 계층 진단 문자열.</returns>
        public static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                var line = $"[{e.GetType().Name}] {e.Message}";
                if (!parts.Contains(line)) parts.Add(line);
            }
            return string.Join("\n  ↳ ", parts);
        }
    }

    public sealed class ExtractPreflight
    {
        public bool CanProceed { get; set; }
        public List<string> Ok { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        /// <summary>Ok(✓)/Warnings(⚠)/Errors(✗) 항목을 사람이 읽을 수 있는 여러 줄 리포트로 만든다.</summary>
        /// <returns>기호 접두어가 붙은 줄바꿈 구분 텍스트.</returns>
        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Ok)       sb.AppendLine("  ✓ " + s);
            foreach (var s in Warnings) sb.AppendLine("  ⚠ " + s);
            foreach (var s in Errors)   sb.AppendLine("  ✗ " + s);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 발췌 원음(협대역/단주기) → NAIMS2 PREC_SRC_* 계열 이관.
    ///
    /// ── 구조 (2026-07-21 타겟 DB 실측으로 확정) ──
    ///   PREC_SRC_SIG                     REF_EXT_SIG_ID = -1 (FK 아님), IS_LEGACY = 1
    ///     ├─ PREC_SRC_COLLECT_SUPPLEMENT 보조정보.alg 바이너리
    ///     ├─ [협대역] PREC_SRC_NB_SIG    → EXTRACT_DATA_STORE (DATA_TYPE='wav_nb')
    ///     └─ [단주기] PREC_SRC_SS_SIG_EVENT → EXTRACT_DATA_STORE (DATA_TYPE='wav_ss_event')
    ///            └─ PREC_SRC_SS_SIG_SEGMENT
    ///
    ///   ※ EXT_SIG 경로를 쓰지 않는 이유: 클라이언트가 EXT_SIG → PREC_SRC_SIG 로 복제한 뒤
    ///     EXT_SIG 를 삭제하므로, 이관은 최종 상태인 PREC_SRC_SIG 에 직접 넣는다.
    ///     덕분에 EXT_SIG.REF_RAW_COLLECT_SIG_ID(NOT NULL FK) 의존이 사라져
    ///     수집 원음 없이도 독립 이관이 가능하다.
    ///
    /// ── 시간 규약 (260626_데이터 시간 값 예시.pptx) ──
    ///   *_DATETIME              : KST wall-clock 그대로 (UTC 변환 금지)
    ///   SS EVENT/SEGMENT의
    ///   START/END_MICROSEC      : KST 자정 기준 offset microseconds
    ///                             예) 12:34:56.789 → 45,296,789,000
    ///   (SPECTROGRAM 계열은 epoch μs 라 기준이 다르므로 혼동 주의)
    ///
    /// ── 주파수 규약 (기존 PREC_SRC_NB_SIG 실측) ──
    ///   LOFAR/DEMON_START_FREQ = 0, *_END_FREQ = 샘플레이트/2 (나이퀴스트)
    ///   예) sr 32768 → 16384,  sr 6144 → 3072
    /// </summary>
    public static class ExtractMigrationExecutor
    {
        public const string DataTypeNb = "wav_nb";
        public const string DataTypeSs = "wav_ss_event";

        /// <summary>폴더명에 박힌 14자리 타임스탬프(yyyyMMddHHmmss).</summary>
        private static readonly Regex StampRx = new(@"_(\d{14})(?:_|\.|$)", RegexOptions.Compiled);

        // ── 시간 유틸 ────────────────────────────────────────────────

        /// <summary>KST 자정 기준 offset microseconds. SS EVENT/SEGMENT 전용 규약.</summary>
        public static long ToDayOffsetMicros(DateTime kst)
            => (long)Math.Round(kst.TimeOfDay.TotalMilliseconds * 1000.0);

        /// <summary>
        /// 레거시 .lst 시간값을 자정 offset μs 로 정규화한다.
        /// 레거시 값은 이미 μs 단위(FrameCount = Length/15625 → 15.625ms = 64fps)이므로
        /// 하루 범위 안이면 그대로 쓰고, 벗어나면 하루로 접는다.
        /// </summary>
        public static long NormalizeDayOffsetMicros(long raw)
        {
            const long day = 86_400_000_000L;
            if (raw < 0) return 0;
            return raw < day ? raw : raw % day;
        }

        /// <summary>폴더명에서 녹음 시작 시각을 뽑는다. 없으면 폴더 수정시각으로 대체.</summary>
        public static DateTime ResolveRecordingTime(ExtractItem item)
        {
            var m = StampRx.Match(item.ItemName);
            if (m.Success && DateTime.TryParseExact(
                    m.Groups[1].Value, "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;

            return item.LastWriteTime == DateTime.MinValue ? DateTime.Now : item.LastWriteTime;
        }

        /// <summary>PREC_SRC_SIG 의 RECORDING_* 3필드와 그 출처 표기.</summary>
        /// <param name="System">RECORDING_SYSTEM(정본 표기, 식별 실패 시 "ETC").</param>
        /// <param name="DateTime">RECORDING_DATETIME.</param>
        /// <param name="Vessel">RECORDING_VESSEL(없으면 빈 문자열).</param>
        /// <param name="Source">각 값을 어느 파일에서 가져왔는지의 사람이 읽는 요약(로그용).</param>
        public readonly record struct RecordingMeta(string System, DateTime DateTime, string Vessel, string Source);

        /// <summary>
        /// [NAIMS-ISSUE:DMF-METADATA] 자료관리파일.DMF 는 "있으면 채우고 없으면 넘어가는" 메타데이터다
        ///   (사용자 확정 2026-08-19). 다만 있는데 흘리면 안 되므로 출처를 여기서 명시적으로 고르고
        ///   무엇을 썼는지 문자열로 남긴다.
        ///   · RECORDING_SYSTEM — DMF [LIST]수집체계 가 유일하게 신뢰할 수 있는 출처다.
        ///     .alg szDetVesselClass 는 AAS 자료에서만 체계명이고, CDMU 자료에서는 함정급("214")이나
        ///     시험 오염값("aaa"/"test"/"OT_옥포")이라 전부 ETC 로 떨어진다(실측 대조 2026-08-19).
        ///     그래서 DMF 로 식별되면 DMF 를 쓰고, 아니면 .alg 로 내려간다.
        ///   · RECORDING_DATETIME — DMF [LIST]수집일시 는 시분초까지 있고, .alg 는 날짜(YYYYMMDD)뿐이며,
        ///     폴더명 스탬프는 애초에 수집이 아니라 *수신* 일시다
        ///     (실측: 폴더 ISUS90_20191126151223 ↔ DMF 수집일시 20191017001900). 정밀한 것부터 쓴다.
        ///   · RECORDING_VESSEL — 원천은 .alg szDetVesselName 이고 DMF [LIST]수집함 은 그 전사본이므로
        ///     .alg 를 먼저 보고 비었을 때만 DMF 로 보완한다.
        /// </summary>
        /// <param name="it">대상 항목(스캔 시 파싱해 둔 DMF 를 들고 있다).</param>
        /// <param name="alg">파싱된 보조정보.alg(없으면 null).</param>
        /// <param name="fallback">두 출처 모두 시각을 주지 못할 때 쓸 값(폴더명 스탬프 = 수신일시).</param>
        /// <returns>결정된 세 값과 출처 표기.</returns>
        public static RecordingMeta ResolveRecordingMeta(ExtractItem it, AlgInfo? alg, DateTime fallback)
        {
            var dmf = it.Dmf;
            var from = new List<string>();

            string system;
            if (dmf != null && RecordingSystem.IsIdentified(dmf.RecordingSystem))
            { system = RecordingSystem.Normalize(dmf.RecordingSystem); from.Add($"체계=DMF({dmf.RecordingSystem})"); }
            else if (alg != null && RecordingSystem.IsIdentified(alg.DetVesselClass))
            { system = alg.RecordingSystemMapped; from.Add($"체계=alg({alg.DetVesselClass})"); }
            else
            { system = "ETC"; from.Add("체계=ETC(식별불가)"); }

            DateTime when;
            if (dmf?.CollectDateTime is { } dmfDt)      { when = dmfDt;   from.Add("수집일시=DMF"); }
            else if (alg?.RecordingDateTime is { } aDt) { when = aDt;     from.Add("수집일시=alg(날짜만)"); }
            else                                        { when = fallback; from.Add("수집일시=폴더명(수신일시)"); }

            string vessel = (alg?.DetVesselName ?? "").Trim();
            if (vessel.Length > 0) from.Add("함정=alg");
            else if (!string.IsNullOrEmpty(dmf?.RecordingVessel)) { vessel = dmf!.RecordingVessel; from.Add("함정=DMF"); }

            return new RecordingMeta(system, when, vessel, string.Join(" · ", from));
        }

        // ── 사전 점검 ────────────────────────────────────────────────

        /// <summary>
        /// 실제 이관 전 안전 점검을 수행한다(읽기 위주, DB 상태를 바꾸지 않음).
        /// 점검 항목: DB 연결·대상 6개 테이블 건수 조회, 시험 INSERT 후 롤백으로 쓰기 권한 확인,
        /// PREC_SRC_SIG_NAME 중복 조회, WAV 존재/크기·보조정보(alg)·SPS/이벤트 리스트 유무.
        /// 시험 삽입은 트랜잭션 롤백으로 되돌리므로 영구 반영은 없다.
        /// </summary>
        /// <param name="connStr">타겟 MsSQL 연결 문자열.</param>
        /// <param name="items">점검할 발췌 원음 항목들.</param>
        /// <param name="opt">중복 스킵·최대 WAV 크기 등 실행 옵션(경고 문구 판정에 사용).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <returns>Ok/Warnings/Errors 와 진행 가능 여부(CanProceed = 오류 0건)를 담은 리포트.</returns>
        public static async Task<ExtractPreflight> PreflightAsync(
            string connStr, IEnumerable<ExtractItem> items, ExtractOptions opt, CancellationToken ct = default)
        {
            var rep = new ExtractPreflight();
            var list = items.ToList();

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(ct);
                rep.Ok.Add($"DB 연결 성공 ({conn.DataSource} / {conn.Database})");

                foreach (var t in new[]
                {
                    "PREC_SRC_SIG", "PREC_SRC_COLLECT_SUPPLEMENT", "EXTRACT_DATA_STORE",
                    "PREC_SRC_NB_SIG", "PREC_SRC_SS_SIG_EVENT", "PREC_SRC_SS_SIG_SEGMENT"
                })
                {
                    using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM [{t}]", conn);
                    var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                    rep.Ok.Add($"{t} — 현재 {n:N0}건");
                }

                // 쓰기 권한 확인 (롤백)
                try
                {
                    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                    using (var cmd = new SqlCommand(
                        "INSERT INTO PREC_SRC_SIG (REF_EXT_SIG_ID,PREC_SRC_SIG_NAME,PREC_SRC_SIG_TYPE,SAMPLING_RATE," +
                        "EXTRACT_DATETIME,EXTRACT_OPERATOR,PROCESSING_STATE,CHNG_DATETIME,DELETED,IS_TRAINING," +
                        "ACCESS_LEVEL,IS_LEGACY) " +
                        "VALUES (-1,'__preflight__',0,0,SYSDATETIME(),'MIGRATION',0,SYSDATETIME(),0,0,0,1)", conn, tx))
                    {
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await tx.RollbackAsync(ct);
                    rep.Ok.Add("INSERT 권한 확인 (시험 삽입 후 롤백)");
                }
                catch (Exception ex)
                {
                    rep.Errors.Add("INSERT 권한 없음 또는 스키마 불일치:\n  " + ExceptionText.Describe(ex));
                }

                // 중복
                var dup = new List<string>();
                foreach (var it in list)
                {
                    ct.ThrowIfCancellationRequested();
                    using var cmd = new SqlCommand(
                        "SELECT COUNT(*) FROM PREC_SRC_SIG WHERE PREC_SRC_SIG_NAME = @n", conn);
                    cmd.Parameters.AddWithValue("@n", Trunc(it.ItemName, 150));
                    if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0) dup.Add(it.ItemName);
                }
                if (dup.Count > 0)
                {
                    var head = string.Join(", ", dup.Take(3));
                    var more = dup.Count > 3 ? $" 외 {dup.Count - 3}건" : "";
                    rep.Warnings.Add(opt.SkipDuplicates
                        ? $"이미 이관된 항목 {dup.Count}건 — 건너뜁니다 ({head}{more})"
                        : $"이미 이관된 항목 {dup.Count}건 — 중복 삽입됩니다 ({head}{more})");
                }
                else rep.Ok.Add("중복 없음 — 모두 신규");
            }
            catch (Exception ex)
            {
                rep.Errors.Add("DB 점검 실패:\n  " + ExceptionText.Describe(ex));
            }

            // 파일 점검
            long total = 0; int wavN = 0, miss = 0, over = 0, noAlg = 0, noIdx = 0;
            int noDmf = 0, dmfNoSys = 0, sysUnknown = 0;
            var dmfUnmapped = new List<string>();
            foreach (var it in list)
            {
                if (!it.HasAlg) noAlg++;

                // [NAIMS-ISSUE:DMF-METADATA] DMF 는 없어도 되지만, 있는데 못 쓰면 반드시 드러나야 한다.
                if (!it.HasDmf) noDmf++;
                else
                {
                    if (!RecordingSystem.IsIdentified(it.Dmf!.RecordingSystem)) dmfNoSys++;
                    foreach (var u in it.Dmf!.PendingKeys())
                        if (!dmfUnmapped.Contains(u)) dmfUnmapped.Add(u);
                }
                // 두 출처 모두로 수집체계를 못 정하면 ETC 로 들어간다 — 미리 알려 준다.
                var algCls = it.HasAlg && it.AlgPath != null ? AlgParser.TryParse(it.AlgPath)?.DetVesselClass : null;
                if (!(it.HasDmf && RecordingSystem.IsIdentified(it.Dmf!.RecordingSystem))
                    && !RecordingSystem.IsIdentified(algCls)) sysUnknown++;
                if (it.Kind == ExtractKind.ShortPeriod && !it.HasSpsList && !it.HasEventList) noIdx++;

                foreach (var w in it.WavPaths)
                {
                    wavN++;
                    try
                    {
                        var fi = new FileInfo(w);
                        if (!fi.Exists) { miss++; continue; }
                        total += fi.Length;
                        if (opt.MaxWavBytes > 0 && fi.Length > opt.MaxWavBytes) over++;
                    }
                    catch { miss++; }
                }
            }
            rep.Ok.Add($"대상 {list.Count}건 · WAV {wavN}개 · {total / 1024.0 / 1024:F1} MB");
            if (miss  > 0) rep.Errors.Add($"접근 불가 WAV {miss}개");
            if (over  > 0) rep.Warnings.Add($"{opt.MaxWavBytes / 1024 / 1024}MB 초과 WAV {over}개 — 제외됩니다");
            if (noAlg > 0) rep.Warnings.Add($"보조정보.alg 없는 항목 {noAlg}건 — SUPPLEMENT 생략");
            rep.Ok.Add(noDmf == 0
                ? $"자료관리파일.DMF {list.Count}건 모두 확인 — RECORDING_* 를 DMF 로 채웁니다"
                : $"자료관리파일.DMF 없는 항목 {noDmf}건 — 해당 항목은 .alg/폴더명으로 채웁니다(정상)");
            if (dmfNoSys > 0)
                rep.Warnings.Add($"DMF 는 있으나 [LIST]수집체계 를 식별할 수 없는 항목 {dmfNoSys}건 — .alg 로 대체합니다");
            if (sysUnknown > 0)
                rep.Warnings.Add($"수집체계를 어느 출처로도 정할 수 없는 항목 {sysUnknown}건 — RECORDING_SYSTEM 이 ETC 로 들어갑니다");
            if (dmfUnmapped.Count > 0)
            {
                var head = string.Join(", ", dmfUnmapped.Take(6));
                var more = dmfUnmapped.Count > 6 ? $" 외 {dmfUnmapped.Count - 6}종" : "";
                rep.Warnings.Add($"DMF 에 값이 있으나 이관 대상 컬럼이 정해지지 않은 키 {dmfUnmapped.Count}종 — 이번 이관에서 DB 에 들어가지 않습니다 ({head}{more})");
            }
            if (noIdx > 0) rep.Warnings.Add($"SPSlist·이벤트리스트 둘 다 없는 단주기 {noIdx}건 — 이벤트/세그먼트 생략");

            rep.CanProceed = rep.Errors.Count == 0;
            return rep;
        }

        // ── 실행 ─────────────────────────────────────────────────────

        /// <summary>
        /// 발췌 원음 항목들을 순회하며 실제 이관을 수행한다(DB 다중 INSERT · 항목별 트랜잭션).
        ///
        /// 트랜잭션 경계: 항목 1건 = 트랜잭션 1개. 그 안에서 PREC_SRC_SIG → (보조정보) →
        /// EXTRACT_DATA_STORE → 협대역 NB_SIG / 단주기 SS_EVENT·SS_SEGMENT 를 부모→자식 순으로 넣는다.
        /// 정상이면 Commit, DryRun 이면 Rollback(시험완료), 예외면 Rollback(실패)하고 다음 항목으로 넘어간다.
        /// 취소(OperationCanceledException) 시 현재 항목을 롤백한 뒤 예외를 다시 던진다.
        /// SkipDuplicates 옵션이 켜져 있고 PREC_SRC_SIG_NAME 이 이미 있으면 해당 항목은 건너뛴다.
        /// 진행 상황·각 항목의 MigStatus/MigError/집계값은 부작용으로 갱신된다.
        /// </summary>
        /// <param name="connStr">타겟 MsSQL 연결 문자열.</param>
        /// <param name="items">이관할 발췌 원음 항목들.</param>
        /// <param name="progress">단계·진행 건수 보고용(없으면 무시).</param>
        /// <param name="ct">비동기 취소 토큰(취소 시 현재 항목 롤백 후 예외 전파).</param>
        /// <param name="options">DryRun·중복 스킵·최대 WAV 크기 옵션(null 이면 기본값).</param>
        public static async Task RunAsync(
            string connStr,
            IEnumerable<ExtractItem> items,
            IProgress<ExtractProgress>? progress,
            CancellationToken ct,
            ExtractOptions? options = null)
        {
            var opt = options ?? new ExtractOptions();

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            var list = items.ToList();
            var prog = new ExtractProgress { Total = list.Count };

            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = list[i];

                prog.Done = i;
                prog.CurrentItem = it.ItemName;
                prog.Phase = "시작";
                progress?.Report(prog);

                it.MigStatus = "진행중";
                it.MigError = "";
                it.MigEventSource = "";   // 재실행 시 지난 회차의 원천 표기가 남지 않게 초기화
                it.MigMetaSource = "";

                if (opt.SkipDuplicates && await ExistsAsync(conn, it.ItemName, ct))
                {
                    it.MigStatus = "건너뜀";
                    it.MigError = "이미 이관된 항목 (PREC_SRC_SIG_NAME 중복)";
                    continue;
                }

                using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                try
                {
                    // 0) 보조정보 파싱 → RECORDING_* 채우기
                    prog.Phase = "보조정보.alg 파싱"; progress?.Report(prog);
                    AlgInfo? alg = it.HasAlg && it.AlgPath != null ? AlgParser.TryParse(it.AlgPath) : null;

                    // 대표 WAV 헤더에서 샘플레이트/길이 확보
                    var firstWav = it.WavPaths.FirstOrDefault();
                    var wh = firstWav != null ? WavHeader.TryRead(firstWav) : default;
                    int sampleRate = wh.SampleRate > 0 ? wh.SampleRate : 0;

                    var recTime = ResolveRecordingTime(it);

                    // 1) PREC_SRC_SIG
                    prog.Phase = "PREC_SRC_SIG"; progress?.Report(prog);
                    int sigId = await InsertSigAsync(conn, tx, it, alg, sampleRate, recTime, ct);
                    it.MigSigId = sigId;

                    // 2) 보조정보
                    int suppN = 0;
                    if (it.HasAlg && it.AlgPath != null)
                    {
                        prog.Phase = "PREC_SRC_COLLECT_SUPPLEMENT"; progress?.Report(prog);
                        suppN = await InsertSupplementAsync(conn, tx, sigId, it.AlgPath, ct);
                    }
                    it.MigSupplementCount = suppN;

                    int dataN = 0, detailN = 0, skipped = 0;

                    if (it.Kind == ExtractKind.NarrowBand)
                    {
                        // 3-A) 협대역: WAV 1개 → EXTRACT_DATA_STORE + PREC_SRC_NB_SIG
                        foreach (var wav in it.WavPaths)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (TooBig(wav, opt)) { skipped++; continue; }

                            prog.Phase = "EXTRACT_DATA_STORE (협대역)"; progress?.Report(prog);
                            int storeId = await InsertDataStoreAsync(conn, tx, wav, DataTypeNb, ct);
                            dataN++;

                            var h = WavHeader.TryRead(wav);
                            var start = recTime;
                            var end = h.AudioLengthSec > 0 ? recTime.AddSeconds(h.AudioLengthSec) : recTime;

                            prog.Phase = "PREC_SRC_NB_SIG"; progress?.Report(prog);
                            await InsertNbSigAsync(conn, tx, sigId, storeId, start, end, h.SampleRate, ct);
                            detailN++;
                        }
                    }
                    else
                    {
                        // 3-B) 단주기: 이벤트가 주체다. 정렬된 WAV 를 이벤트에 min 개수만큼 붙인다.
                        //   · 참조 SdLoader: events[i] ↔ 파일명 정렬된 Signal_i.wav, n = min(wav, event)
                        //   · REF_EXTRACT_DATA_STORE_ID 는 NOT NULL 이라 이벤트마다 WAV 가 반드시 필요 → min 이 상한
                        //   · 이벤트/세그먼트 START_MICROSEC 은 (부모, START) 유니크 → 실제 .lst 시각(정밀)을 쓰고
                        //     같은 시작시각이 겹치면 건너뛴다(레거시 중복 방어).
                        // [NAIMS-ISSUE:SPSLIST-PRIORITY] 이벤트 원천 우선순위 = SPSlist.txt 우선(사용자 확정).
                        //   ※ 레거시는 정반대다 — AAS(ModelPreciseAnalysis.cpp:4492)도 CDMU
                        //     (ShortSignalProcessMainDlg.cpp:1486)도 이벤트리스트.lst 가 있으면 그걸 읽고
                        //     없을 때만 SPSlist.txt 로 내려간다. 우리는 뒤집는다: SPSlist 가 신호처리기가
                        //     직접 쓴 원본(이벤트·세그먼트 수치가 온전)이고, .lst 는 정밀분석 단계에서
                        //     사람이 손댄 뒤 다시 쓴 파생물이라 없는 폴더도 많기 때문이다.
                        //   순서: ① SPSlist 로 읽는다 → ② 실패하거나 0건이면 이벤트리스트.lst 로 내려간다
                        //         → ③ 둘 다 실패해야 항목 실패. ①이 성공하면 .lst 는 아예 보지 않는다.
                        //   · SPSlist 레이아웃(AAS packed / CDMU 구조체 통째)은 SpsListReader 가 자동 판별한다.
                        //   · 오류는 "실제로 사용한 원천" 것만 남긴다 — 예전엔 .lst 오류가 남아 있으면
                        //     SPSlist 로 정상 파싱해도 항목이 실패로 처리되는 버그가 있었다.
                        prog.Phase = "이벤트 목록 파싱"; progress?.Report(prog);
                        var events = new List<SdEvent>();
                        string? parseError = null;
                        string source = "";            // 로그에 남길 "실제로 쓴 원천"
                        string? spsError = null;

                        if (it.HasSpsList && it.SpsListPath != null)
                        {
                            var sps = SpsListReader.Read(it.SpsListPath);
                            spsError = sps.error;
                            if (sps.error == null && sps.events.Count > 0)
                            {
                                events = sps.events;
                                parseError = null;
                                source = $"SPSlist.txt[{SpsListReader.DescribeLayout(sps.layout)}] 이벤트 {sps.events.Count}";
                            }
                        }

                        // SPSlist 가 못 읽혔거나 0건일 때만 .lst 로 내려간다(우선권 유지).
                        if (events.Count == 0 && it.HasEventList && it.EventListPath != null)
                        {
                            var lst = EventListReader.Read(it.EventListPath);
                            if (lst.events.Count > 0 || lst.error != null)
                            {
                                events = lst.events;
                                parseError = lst.error;   // 실제 사용한 원천의 오류로 교체
                                source = $"이벤트리스트.lst 이벤트 {lst.events.Count}";
                                // SPSlist 가 있는데도 .lst 로 내려왔으면 왜 그랬는지 반드시 남긴다.
                                if (spsError != null)
                                    source += $" (SPSlist 파싱 실패로 대체 — {spsError})";
                                else if (it.HasSpsList)
                                    source += " (SPSlist 이벤트 0건이라 대체)";
                            }
                        }

                        // SPSlist 만 있고 그마저 실패했다면 그 오류가 이 항목의 실패 사유다.
                        if (events.Count == 0 && parseError == null && spsError != null)
                            parseError = spsError;

                        // [NAIMS-ISSUE:PARSE-SILENT-PARTIAL] 손상 파일의 부분 파싱을 "완료" 로 위장하지 않는다.
                        //   파싱이 도중에 끊겼으면 이 항목은 이관하지 않고 오류로 남긴다 — 일부 이벤트만
                        //   들어간 채 완료로 표기되면 나중에 누락을 발견할 방법이 없다.
                        if (parseError != null)
                            throw new InvalidOperationException(
                                $"이벤트 목록 파싱 실패 — {parseError}. 원본 파일을 확인한 뒤 다시 이관하세요.");

                        if (source.Length == 0)
                            source = it.HasSpsList || it.HasEventList
                                ? "이벤트 원천 0건 — WAV 단위 최소 이벤트로 생성"
                                : "인덱스 파일 없음 — WAV 단위 최소 이벤트로 생성";
                        it.MigEventSource = source;

                        var wavs = it.WavPaths
                            .Where(w => !TooBig(w, opt))
                            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        skipped += it.WavPaths.Length - wavs.Count;

                        if (events.Count == 0)
                        {
                            // 이벤트 정보가 없으면 WAV 마다 최소 이벤트를 만들되 순번으로 시작시각을 어긋낸다.
                            for (int k = 0; k < wavs.Count; k++)
                            {
                                ct.ThrowIfCancellationRequested();
                                prog.Phase = $"EXTRACT_DATA_STORE ({k + 1}/{wavs.Count})"; progress?.Report(prog);
                                int storeId = await InsertDataStoreAsync(conn, tx, wavs[k], DataTypeSs, ct); dataN++;
                                var h = WavHeader.TryRead(wavs[k]);
                                prog.Phase = "PREC_SRC_SS_SIG_EVENT"; progress?.Report(prog);
                                await InsertSsEventAsync(conn, tx, sigId, storeId, null, recTime, h, ct, seqForFallback: k);
                                detailN++;
                            }
                        }
                        else
                        {
                            // 각 Signal_N.wav 는 파일명 숫자 N = 이벤트의 EventID 로 짝지어진다 (AAS 실측:
                            // ModelPreciseAnalysis.cpp:5023 "Signal_%d.wav" ← m_lEventID). 위치가 아니라
                            // EventID 로 매칭하므로 WAV/이벤트 개수·정렬이 달라도 어긋나지 않는다.
                            var evById = new Dictionary<long, SdEvent>();
                            foreach (var ev in events) evById[ev.EventID] = ev;

                            var seenEvStart = new HashSet<long>();
                            int idx = 0;
                            foreach (var wav in wavs)
                            {
                                ct.ThrowIfCancellationRequested();
                                idx++;
                                long sigNo = ParseSignalNumber(wav);
                                if (sigNo < 0 || !evById.TryGetValue(sigNo, out var ev))
                                {
                                    skipped++;   // 짝 이벤트 없는 WAV → 이관 대상 아님
                                    continue;
                                }
                                long evStart = NormalizeDayOffsetMicros(ev.StartTime);
                                if (!seenEvStart.Add(evStart)) { skipped++; continue; }  // 같은 시작시각 이벤트 중복

                                prog.Phase = $"EXTRACT_DATA_STORE ({idx}/{wavs.Count})"; progress?.Report(prog);
                                int storeId = await InsertDataStoreAsync(conn, tx, wav, DataTypeSs, ct); dataN++;
                                var h = WavHeader.TryRead(wav);

                                prog.Phase = "PREC_SRC_SS_SIG_EVENT"; progress?.Report(prog);
                                int eventId = await InsertSsEventAsync(conn, tx, sigId, storeId, ev, recTime, h, ct);
                                detailN++;

                                if (ev.Segments.Count > 0)
                                {
                                    prog.Phase = $"PREC_SRC_SS_SIG_SEGMENT ({ev.Segments.Count})"; progress?.Report(prog);
                                    var seenSegStart = new HashSet<long>();
                                    foreach (var seg in ev.Segments)
                                    {
                                        if (!seenSegStart.Add(NormalizeDayOffsetMicros(seg.StartTime))) continue; // 세그먼트 시작 중복 제거
                                        await InsertSsSegmentAsync(conn, tx, eventId, seg, ct);
                                    }
                                }
                            }
                        }
                    }

                    it.MigDataStoreCount = dataN;
                    it.MigDetailCount = detailN;

                    if (opt.DryRun)
                    {
                        await tx.RollbackAsync(ct);
                        it.MigStatus = "시험완료";
                        it.MigError = $"DRY RUN — 롤백됨 (SIG 1, SUPP {suppN}, STORE {dataN}, 상세 {detailN}"
                                    + (skipped > 0 ? $", 크기초과 {skipped} 제외" : "") + ")";
                    }
                    else
                    {
                        await tx.CommitAsync(ct);
                        it.MigStatus = "완료";
                        if (skipped > 0) it.MigError = $"WAV {skipped}건은 크기 초과로 제외";
                    }
                }
                catch (OperationCanceledException)
                {
                    try { await tx.RollbackAsync(CancellationToken.None); } catch { }
                    it.MigStatus = "취소";
                    throw;
                }
                catch (Exception ex)
                {
                    try { await tx.RollbackAsync(CancellationToken.None); } catch { }
                    it.MigStatus = "실패";
                    it.MigError = ExceptionText.Describe(ex);
                }
            }

            prog.Done = list.Count;
            prog.Phase = "완료";
            prog.CurrentItem = "";
            progress?.Report(prog);
        }

        // ── INSERT 구현 ──────────────────────────────────────────────

        /// <summary>
        /// 이관 트리의 루트인 PREC_SRC_SIG 1행을 넣고(DB write) 생성된 RECORD_ID 를 돌려준다.
        /// REF_EXT_SIG_ID = -1, IS_LEGACY = 1 로 이관 데이터임을 표시하고(EXT_SIG 미참조),
        /// RECORDING_* 은 보조정보(alg)에서, 없으면 폴더 추정 시각으로 채운다.
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="it">대상 항목(이름·경로·종류).</param>
        /// <param name="alg">보조정보 파싱 결과(없으면 null).</param>
        /// <param name="sampleRate">대표 WAV 헤더 샘플레이트(없으면 0).</param>
        /// <param name="recTime">녹음 시작 추정 시각(KST wall-clock).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <returns>삽입된 PREC_SRC_SIG 의 RECORD_ID.</returns>
        private static async Task<int> InsertSigAsync(
            SqlConnection conn, SqlTransaction tx, ExtractItem it,
            AlgInfo? alg, int sampleRate, DateTime recTime, CancellationToken ct)
        {
            // PREC_SRC_SIG_TYPE : 협대역 0 / 단주기 1 (기존 데이터 관례)
            int sigType = it.Kind == ExtractKind.NarrowBand ? 0 : 1;

            const string sql = @"
INSERT INTO PREC_SRC_SIG
( REF_EXT_SIG_ID, PREC_SRC_SIG_NAME, PREC_SRC_SIG_TYPE, SAMPLING_RATE,
  EXTRACT_DATETIME, EXTRACT_OPERATOR, PROCESSING_STATE, CHNG_DATETIME,
  COMMENT, DELETED, IS_TRAINING,
  RECORDING_DATETIME, RECORDING_SYSTEM, RECORDING_VESSEL, ACCESS_LEVEL, IS_LEGACY )
OUTPUT INSERTED.RECORD_ID
VALUES
( @refExt, @name, @type, @sr,
  @extDt, @op, 0, @chngDt,
  @comment, 0, 0,
  @recDt, @recSys, @recVes, 0, 1 );";

            using var cmd = new SqlCommand(sql, conn, tx);
            var now = DateTime.Now;

            // EXT_SIG 는 클라이언트가 복제 후 삭제하므로 이관 데이터는 -1 로 표시한다.
            cmd.Parameters.AddWithValue("@refExt", -1);
            cmd.Parameters.AddWithValue("@name", Trunc(it.ItemName, 150));
            cmd.Parameters.AddWithValue("@type", sigType);
            cmd.Parameters.AddWithValue("@sr", sampleRate);
            cmd.Parameters.AddWithValue("@extDt", recTime);
            cmd.Parameters.AddWithValue("@op", "MIGRATION");
            cmd.Parameters.AddWithValue("@chngDt", now);
            cmd.Parameters.AddWithValue("@comment", (object?)Trunc(it.ItemPath, 1000) ?? DBNull.Value);
            var meta = ResolveRecordingMeta(it, alg, recTime);
            it.MigMetaSource = meta.Source;
            var (recSys, recDt, recVes) = (meta.System, meta.DateTime, meta.Vessel);

            cmd.Parameters.AddWithValue("@recDt", recDt);
            cmd.Parameters.AddWithValue("@recSys", (object?)Trunc(recSys, 30) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@recVes", (object?)Trunc(recVes, 45) ?? DBNull.Value);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        /// <summary>
        /// 보조정보.alg 파일 전체를 바이너리로 읽어 PREC_SRC_COLLECT_SUPPLEMENT 1행으로 넣는다(파일 IO + DB write).
        /// 파일 읽기에 실패하면 삽입을 건너뛰고 0 을 돌려준다(보조정보는 선택 항목).
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="sigId">부모 PREC_SRC_SIG 의 RECORD_ID.</param>
        /// <param name="algPath">읽어 저장할 .alg 파일 경로.</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <returns>삽입한 행 수(성공 1, 파일 읽기 실패 0).</returns>
        private static async Task<int> InsertSupplementAsync(
            SqlConnection conn, SqlTransaction tx, int sigId, string algPath, CancellationToken ct)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(algPath, ct); }
            catch { return 0; }

            const string sql = @"
INSERT INTO PREC_SRC_COLLECT_SUPPLEMENT
( REF_PREC_SRC_SIG_ID, DET_SONAR, MISSION_SEA_AREA, SUPPLEMENT_DATA,
  IS_TRAINING, CHNG_DATETIME, DELETED )
VALUES ( @sig, 1, 1, @data, 0, @chngDt, 0 );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("@sig", sigId);
            var p = cmd.Parameters.Add("@data", SqlDbType.VarBinary, -1);
            p.Value = data;
            cmd.Parameters.AddWithValue("@chngDt", DateTime.Now);

            await cmd.ExecuteNonQueryAsync(ct);
            return 1;
        }

        /// <summary>WAV 바이너리를 공용 저장소에 넣고 RECORD_ID 를 돌려준다.</summary>
        /// <remarks>
        /// 파일 IO + DB write. IS_BLOB 은 DATA_LENGTH 기반 계산 컬럼이라 INSERT 목록에서 제외한다.
        /// 대용량(FILESTREAM) 대비로 CommandTimeout 을 900초로 늘린다.
        /// WAV 읽기 실패 시 IOException 을 던져 호출부 트랜잭션이 롤백되게 한다.
        /// </remarks>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="wavPath">읽어 저장할 WAV 파일 경로.</param>
        /// <param name="dataType">저장소 구분값(협대역 wav_nb / 단주기 wav_ss_event).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <returns>삽입된 EXTRACT_DATA_STORE 의 RECORD_ID.</returns>
        private static async Task<int> InsertDataStoreAsync(
            SqlConnection conn, SqlTransaction tx, string wavPath, string dataType, CancellationToken ct)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(wavPath, ct); }
            catch (Exception ex) { throw new IOException($"WAV 읽기 실패: {wavPath}", ex); }

            // IS_BLOB 은 계산 컬럼이므로 INSERT 목록에서 제외한다.
            //   IS_BLOB = (DATA_LENGTH >= 10,485,760) → 10MB 이상이면 자동으로 1
            // DATA_LENGTH 만 정확히 넣으면 DB 가 산출한다.
            const string sql = @"
INSERT INTO EXTRACT_DATA_STORE ( DATA_TYPE, DATA, DATA_LENGTH )
OUTPUT INSERTED.RECORD_ID
VALUES ( @type, @data, @len );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.CommandTimeout = 900;   // FILESTREAM 대용량 대비
            cmd.Parameters.AddWithValue("@type", dataType);
            var p = cmd.Parameters.Add("@data", SqlDbType.VarBinary, -1);
            p.Value = data;
            cmd.Parameters.AddWithValue("@len", (long)data.Length);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        /// <summary>
        /// 협대역 상세행 PREC_SRC_NB_SIG 1행을 넣는다(DB write). SIG 와 DATA_STORE 를 연결하고
        /// 추출 시작/끝 시각을 기록한다. LOFAR/DEMON 주파수는 참고용으로 START=0, END=나이퀴스트(fs/2).
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="sigId">부모 PREC_SRC_SIG 의 RECORD_ID.</param>
        /// <param name="storeId">연결할 EXTRACT_DATA_STORE 의 RECORD_ID.</param>
        /// <param name="start">추출 시작 시각(KST wall-clock).</param>
        /// <param name="end">추출 종료 시각(WAV 길이가 없으면 start 와 동일).</param>
        /// <param name="sampleRate">샘플레이트(나이퀴스트 산출용, 0 이면 주파수 0).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        private static async Task InsertNbSigAsync(
            SqlConnection conn, SqlTransaction tx, int sigId, int storeId,
            DateTime start, DateTime end, int sampleRate, CancellationToken ct)
        {
            // 주파수는 참고용 — 나이퀴스트(fs/2) 로 채운다(기존 데이터 관례).
            float nyquist = sampleRate > 0 ? sampleRate / 2f : 0f;

            const string sql = @"
INSERT INTO PREC_SRC_NB_SIG
( REF_PREC_SRC_SIG_ID, REF_EXTRACT_DATA_STORE_ID,
  EXTRACT_START_DATETIME, EXTRACT_END_DATETIME,
  LOFAR_START_FREQ, LOFAR_END_FREQ, DEMON_START_FREQ, DEMON_END_FREQ,
  IS_TRAINING, CHNG_DATETIME, DELETED )
VALUES ( @sig, @store, @st, @ed, 0, @nyq, 0, @nyq, 0, @chngDt, 0 );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@sig", sigId);
            cmd.Parameters.AddWithValue("@store", storeId);
            cmd.Parameters.AddWithValue("@st", start);
            cmd.Parameters.AddWithValue("@ed", end);
            cmd.Parameters.AddWithValue("@nyq", nyquist);
            cmd.Parameters.AddWithValue("@chngDt", DateTime.Now);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// 단주기 이벤트행 PREC_SRC_SS_SIG_EVENT 1행을 넣고(DB write) 생성된 RECORD_ID 를 돌려준다.
        /// START/END_MICROSEC 은 KST 자정 기준 offset μs.
        /// ev 가 있으면 .lst 실측 시각·검출타입·수동/자동을, 없으면(폴백) WAV 길이로 최소 정보만 구성한다.
        /// 폴백 시 여러 WAV 가 같은 recTime 을 공유해 (SIG_ID, START_MICROSEC) 유니크를 위반하지 않도록
        /// seqForFallback 만큼 시작 μs 를 어긋낸다.
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="sigId">부모 PREC_SRC_SIG 의 RECORD_ID.</param>
        /// <param name="storeId">연결할 EXTRACT_DATA_STORE 의 RECORD_ID(NOT NULL).</param>
        /// <param name="ev">이벤트 메타(없으면 null → 폴백 경로).</param>
        /// <param name="recTime">녹음 시작 추정 시각(자정 기준 계산의 날짜 기준).</param>
        /// <param name="wh">WAV 헤더(폴백 길이·샘플레이트).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <param name="seqForFallback">폴백 시 시작 μs 충돌 방지용 순번 오프셋.</param>
        /// <returns>삽입된 이벤트행의 RECORD_ID(세그먼트 부모 키).</returns>
        private static async Task<int> InsertSsEventAsync(
            SqlConnection conn, SqlTransaction tx, int sigId, int storeId,
            SdEvent? ev, DateTime recTime, WavHeaderInfo wh, CancellationToken ct,
            int seqForFallback = 0)
        {
            // START/END_MICROSEC 은 KST 자정 기준 offset μs.
            long startUs, endUs;
            float lenUs;
            int detType, manual;
            DateTime sigStart;

            if (ev != null)
            {
                startUs = NormalizeDayOffsetMicros(ev.StartTime);
                endUs   = NormalizeDayOffsetMicros(ev.EndTime);
                if (endUs < startUs) endUs = startUs;
                lenUs   = ev.EventLength > 0 ? ev.EventLength : (endUs - startUs);
                detType = ParseDetType(ev.DetectingType);
                manual  = ev.ManualOrAuto ? 1 : 0;
                sigStart = recTime.Date.AddTicks(startUs * 10);   // μs → tick(100ns)
            }
            else
            {
                // 이벤트 정보가 없으면 WAV 길이로 최소 정보만 구성한다.
                // 같은 recTime 이 여러 WAV 에 중복되면 (SIG_ID, START_MICROSEC) 유니크 위반이 되므로
                // WAV 순번(seqForFallback)만큼 μs 를 어긋내어 충돌을 피한다.
                startUs = ToDayOffsetMicros(recTime) + seqForFallback;
                long durUs = (long)Math.Round(Math.Max(0, wh.AudioLengthSec) * 1_000_000.0);
                endUs = startUs + durUs;
                lenUs = durUs;
                detType = 0;
                manual = 0;
                sigStart = recTime;
            }

            const string sql = @"
INSERT INTO PREC_SRC_SS_SIG_EVENT
( REF_PREC_SRC_SIG_ID, START_MICROSEC, END_MICROSEC, EVENT_LENGTH_MICROSEC,
  DETECTING_TYPE, MANUAL_AUTO, SIG_START_DATETIME, CHNG_DATETIME,
  DELETED, IS_TRAINING, REF_EXTRACT_DATA_STORE_ID, SAMPLING_RATE )
OUTPUT INSERTED.RECORD_ID
VALUES ( @sig, @st, @ed, @len, @dt, @man, @sigSt, @chngDt, 0, 0, @store, @sr );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@sig", sigId);
            cmd.Parameters.AddWithValue("@st", startUs);
            cmd.Parameters.AddWithValue("@ed", endUs);
            cmd.Parameters.AddWithValue("@len", lenUs);
            cmd.Parameters.AddWithValue("@dt", detType);
            cmd.Parameters.AddWithValue("@man", manual);
            cmd.Parameters.AddWithValue("@sigSt", sigStart);
            cmd.Parameters.AddWithValue("@chngDt", DateTime.Now);
            cmd.Parameters.AddWithValue("@store", storeId);
            cmd.Parameters.AddWithValue("@sr", wh.SampleRate);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        /// <summary>
        /// 이벤트 하위 세그먼트행 PREC_SRC_SS_SIG_SEGMENT 1행을 넣는다(DB write).
        /// START/END_MICROSEC 은 KST 자정 기준 offset μs 로 정규화하며, 끝이 시작보다 앞서면 시작으로 보정한다.
        /// LDA 좌표·검출타입·주파수(START/END/CENTER/BANDWIDTH)·프레임수·변조모드 등 상세 특성을 함께 저장한다.
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="tx">호출부 항목 트랜잭션.</param>
        /// <param name="eventId">부모 PREC_SRC_SS_SIG_EVENT 의 RECORD_ID.</param>
        /// <param name="seg">저장할 세그먼트 메타.</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        private static async Task InsertSsSegmentAsync(
            SqlConnection conn, SqlTransaction tx, int eventId, SdSegment seg, CancellationToken ct)
        {
            long st = NormalizeDayOffsetMicros(seg.StartTime);
            long ed = NormalizeDayOffsetMicros(seg.EndTime);
            if (ed < st) ed = st;
            float len = seg.Length > 0 ? seg.Length : (ed - st);

            const string sql = @"
INSERT INTO PREC_SRC_SS_SIG_SEGMENT
( REF_PREC_SRC_SS_SIG_EVENT_ID, START_MICROSEC, END_MICROSEC, SEGMENT_LENGTH_MICROSEC,
  LDA_X, LDA_Y, DETECT_TYPE, FRAME_COUNT, START_FREQ, END_FREQ,
  MODE, CENTER_FREQ, BANDWIDTH, IS_TRAINING )
VALUES ( @ev, @st, @ed, @len, @x, @y, @dt, @fc, @sf, @ef, @mode, @cf, @bw, 0 );";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@ev", eventId);
            cmd.Parameters.AddWithValue("@st", st);
            cmd.Parameters.AddWithValue("@ed", ed);
            cmd.Parameters.AddWithValue("@len", len);
            cmd.Parameters.AddWithValue("@x", (float)seg.LDAx);
            cmd.Parameters.AddWithValue("@y", (float)seg.LDAy);
            cmd.Parameters.AddWithValue("@dt", seg.DetectingType);
            cmd.Parameters.AddWithValue("@fc", seg.FrameCount);
            cmd.Parameters.AddWithValue("@sf", (float)seg.StartFreq);
            cmd.Parameters.AddWithValue("@ef", (float)seg.EndFreq);
            cmd.Parameters.AddWithValue("@mode", seg.Modulation);
            cmd.Parameters.AddWithValue("@cf", (float)seg.CenterFreq);
            cmd.Parameters.AddWithValue("@bw", (float)seg.BandWidth);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── 보조 ─────────────────────────────────────────────────────

        /// <summary>
        /// PREC_SRC_SIG_NAME 중복 여부를 조회한다(읽기 전용). 중복 스킵 판정에 쓰인다.
        /// 예외를 삼키지 않고 호출부로 올려 "중복 아님" 오인에 의한 중복 INSERT 를 막는다.
        /// </summary>
        /// <param name="conn">열린 연결.</param>
        /// <param name="name">조회할 항목 이름(150자로 잘라 비교).</param>
        /// <param name="ct">비동기 취소 토큰.</param>
        /// <returns>같은 이름의 행이 있으면 true.</returns>
        private static async Task<bool> ExistsAsync(SqlConnection conn, string name, CancellationToken ct)
        {
            // 오류를 삼켜 false 를 돌려주면 "중복 아님"으로 오인해 중복 INSERT 가 된다.
            // 예외는 호출부(항목별 try/catch)로 그대로 올려 실패를 드러낸다.
            using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM PREC_SRC_SIG WHERE PREC_SRC_SIG_NAME = @n", conn);
            cmd.Parameters.AddWithValue("@n", Trunc(name, 150));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
        }

        /// <summary>WAV 가 옵션의 최대 크기(MaxWavBytes)를 넘는지 판정한다. 0(무제한)이거나 접근 실패 시 false.</summary>
        /// <param name="path">검사할 파일 경로.</param>
        /// <param name="opt">최대 크기 기준을 담은 옵션.</param>
        /// <returns>제한을 초과하면 true(제외 대상).</returns>
        private static bool TooBig(string path, ExtractOptions opt)
        {
            if (opt.MaxWavBytes <= 0) return false;
            try { return new FileInfo(path).Length > opt.MaxWavBytes; }
            catch { return false; }
        }

        /// <summary>"Signal_123.wav" → 123 (파일명 접미 숫자 = 이벤트 EventID). 파싱 실패 시 -1.</summary>
        private static long ParseSignalNumber(string wavPath)
        {
            var name = Path.GetFileNameWithoutExtension(wavPath);   // "Signal_123"
            int us = name.LastIndexOf('_');
            if (us < 0 || us + 1 >= name.Length) return -1;
            return long.TryParse(name.AsSpan(us + 1), out var n) ? n : -1;
        }

        /// <summary>검출타입 문자열("AI"→1, "TR"→2, 그 외/공백→0)을 DB 코드값으로 변환한다.</summary>
        private static int ParseDetType(string s) => s?.Trim().ToUpperInvariant() switch
        {
            "AI" => 1,
            "TR" => 2,
            _ => 0
        };

        /// <summary>DB 컬럼 길이에 맞춰 문자열을 max 자로 자른다(초과 시 앞에서 자름, 이하면 원본 그대로).</summary>
        // [NAIMS-ISSUE:CHAR-PADDING] 이 경로의 문자열은 파일 트리·고정폭 바이너리 레코드에서 온다.
        //   각 리더가 이미 Trim 하지만, DB 로 나가는 문이 여기 하나이므로 여기서도 같은 규칙을 태운다
        //   (리더가 하나 늘 때마다 오염 가능성이 하나 느는 구조를 만들지 않기 위해서다).
        //   자르기 <b>전에</b> 떼야 폭을 패딩으로 낭비하지 않는다.
        private static string Trunc(string s, int max)
        {
            var t = LegacyText.Normalize(s);
            return t.Length > max ? t.Substring(0, max) : t;
        }
    }
}
