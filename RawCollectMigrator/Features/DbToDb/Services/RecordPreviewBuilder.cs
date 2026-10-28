using System.Data;
using System.Windows;
using System.Windows.Media;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:미리보기]
    /// <summary>
    /// 레코드 한 건의 바이너리를 <b>소스·타겟 각각의 규약대로</b> 읽어 전시용 묶음으로 만든다.
    ///
    /// 창(팝업)과 표 안 인라인 전시가 같은 결과를 보여야 하므로, 생성 로직은 여기 한 곳에만 둔다.
    /// 두 화면이 각자 읽으면 언젠가 서로 다른 그림을 보여주게 되고, 그때는 어느 쪽이 맞는지
    /// 판단할 근거가 없어진다 — 검증 도구에서 그건 치명적이다.
    /// </summary>
    public static class RecordPreviewBuilder
    {
        /// <summary>전시 한 칸(제목 + 캡션 + 그림).</summary>
        public sealed class PanelItem
        {
            public string Title { get; init; } = "";
            public string Caption { get; init; } = "";
            public string Error { get; init; } = "";
            public ImageSource? Image { get; init; }
            public Visibility ImageVisibility => Image != null ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ErrorVisibility => Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <param name="Source">왼쪽(소스, 레거시 파싱).</param>
        /// <param name="Target">오른쪽(타겟, R8 파싱).</param>
        /// <param name="SourceTitle">왼쪽 패널 제목 — 무엇을 읽었는지 밝힌다.</param>
        /// <param name="Supported">이 표에 전시할 바이너리가 있는지.</param>
        public sealed record Result(
            List<PanelItem> Source, List<PanelItem> Target,
            string SourceTitle, string TargetTitle, bool Supported, string Note);

        public static bool Supports(string sourceTable) => Classify(sourceTable) != Kind.None;

        private enum Kind { None, Country, Image, Snippet }

        private static Kind Classify(string src) => src.ToUpperInvariant() switch
        {
            "COUNTRY_CODES" => Kind.Country,
            "IMAGE_DATA" => Kind.Image,
            "NB_SNIPPET" or "AI_SNIPPET" or "TR_SNIPPET" => Kind.Snippet,
            _ => Kind.None,
        };

        /// <summary>레코드 한 건의 전시 묶음을 만든다(소스·타겟 DB 읽기 전용).</summary>
        public static async Task<Result> BuildAsync(
            DbToDbOptions opt, string sourceTable, long sourcePk,
            string targetTable, long targetRecordId, CancellationToken ct = default)
        {
            var src = new List<PanelItem>();
            var tgt = new List<PanelItem>();
            var kind = Classify(sourceTable);

            if (kind == Kind.None)
                return new Result(src, tgt, "소스", "타겟", false,
                    $"{sourceTable} 은 전시할 바이너리 자료가 없는 표입니다.");

            DataRow? s = await ReadSourceRowAsync(opt, sourceTable, sourcePk, ct);
            DataRow? t = targetRecordId > 0 ? await ReadTargetRowAsync(opt, targetTable, targetRecordId, ct) : null;

            var resolver = new SnippetFileResolver(
                opt.SnippetRoots.Select((r, i) => new SnippetFileResolver.RootSpec($"우선{i + 1}", r)));
            Task<string?> Resolve(string name) => Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(name)) return (string?)null;
                try { var f = resolver.Resolve(name.Trim()); return f.Found ? f.FullPath : null; }
                catch { return null; }
            }, ct);

            string srcTitle, tgtTitle;

            switch (kind)
            {
                case Kind.Country:
                {
                    srcTitle = "소스 — CTRYIMAGE 원본 파일";
                    tgtTitle = "타겟 — STD_COUNTRY_CODES.FLAG_FILE";
                    string name = Str(s, "FLAG_FILENAME");
                    src.Add(name.Length == 0
                        ? Item("국기", new PreviewRenderer.Panel(null, "FLAG_FILENAME 이 비어 있습니다 — 국기가 없는 국가"))
                        : Item($"국기 — {name}", PreviewRenderer.FromImageFile(await Resolve(name), "원본 파일")));
                    tgt.Add(Item("국기 blob", PreviewRenderer.FromImageBytes(Bytes(t, "FLAG_FILE"), "FLAG_FILE")));
                    break;
                }
                case Kind.Image:
                {
                    srcTitle = "소스 — IMAGE 원본 파일";
                    tgtTitle = "타겟 — STD_REFERENCE_DATA.FILE_DATA";
                    string name = Str(s, "IMG_FILENAME");
                    src.Add(Item($"영상 — {(name.Length > 0 ? name : "(없음)")}",
                        PreviewRenderer.FromImageFile(await Resolve(name), "원본 파일")));
                    tgt.Add(Item("영상 blob", PreviewRenderer.FromImageBytes(Bytes(t, "FILE_DATA"), "FILE_DATA")));
                    break;
                }
                default:
                {
                    srcTitle = "소스 — 원본 파일 (레거시 파싱)";
                    tgtTitle = "타겟 — DB blob (R8 66B 파싱)";
                    bool isTr = sourceTable.Equals("TR_SNIPPET", StringComparison.OrdinalIgnoreCase);
                    bool isAi = sourceTable.Equals("AI_SNIPPET", StringComparison.OrdinalIgnoreCase);
                    string audio = Str(s, "AUDIO_FILENAME"), gram = Str(s, "GRAM_FILENAME");

                    // 파형과 그램이 <같은 시각 축>을 쓰게 한다 — 나란히 놓고 짚어 보라고 붙인 화면이다.
                    DateTime? wavStart = AbsStart(t);
                    src.Add(Item($"시계열 — {(audio.Length > 0 ? audio : "(없음)")}",
                        PreviewRenderer.FromWaveFile(await Resolve(audio), "원음 WAV", wavStart)));
                    tgt.Add(Item("시계열 — 원음 blob",
                        PreviewRenderer.FromWaveBytes(
                            Bytes(t, isTr || isAi ? "AUDIO_FILE" : "CLASSIFI_NB_AUDIO_SNIPPET_FILE"),
                            "원음 blob", wavStart)));

                    // [찾기:그램전시] 축 값은 blob 이 아니라 <행> 에 적혀 있다.
                    //   NB 협대역 그램은 순수 float(헤더 없음)이라 blob 만으로는 주파수도 시각도 모른다.
                    //   그렇다고 축을 빈·프레임 번호로 두면, 정작 좌우로 맞대 볼 값이 화면에서 사라진다.
                    //   R8 그램은 헤더가 정본이고 이 힌트는 헤더에 없는 칸만 메운다.
                    DateTime? ts = wavStart;
                    DateTime? te = AbsEnd(t);
                    var hint = new PreviewRenderer.GramAxes(
                        Dbl(t, isTr ? "SPECTROGRAM_START_FREQ" : "FREQ_MIN"),
                        Dbl(t, isTr ? "SPECTROGRAM_END_FREQ" : "FREQ_MAX"),
                        ts, te);

                    if (isTr)
                    {
                        src.Add(Item("스펙트로그램", new PreviewRenderer.Panel(null,
                            "소스에 그램 파일이 없습니다 — TR 은 원음에서 생성해 넣는 자료입니다.")));
                        tgt.Add(Item("스펙트로그램 — SPECTROGRAM_DATA (S0)",
                            PreviewRenderer.FromTargetGramBlob(
                                Bytes(t, "SPECTROGRAM_DATA"), Int(t, "BIN_NUM"), "S0 blob", hint)));
                    }
                    else
                    {
                        src.Add(Item($"스니펫그램 — {(gram.Length > 0 ? gram : "(없음)")}",
                            PreviewRenderer.FromLegacyGramFile(await Resolve(gram), "레거시 .grm")));
                        string col = isAi ? "GRAM_FILE" : "CLASSIFI_NB_GRAM_SNIPPET_FILE";
                        tgt.Add(Item($"스니펫그램 — {col}",
                            PreviewRenderer.FromTargetGramBlob(Bytes(t, col), Int(t, "BIN_NUM"),
                                isAi ? "AR 그램(A0 기대)" : "협대역 그램(R8 66B L0/D0/V 기대)", hint)));

                        // [찾기:미리보기] AI 도 TR 과 같은 격으로 <생성된 스펙트로그램>을 보여 준다.
                        //   AI 스펙트로그램은 원음에서 만들어 자식표에 넣는 자료라, 이걸 안 보면
                        //   "그램은 맞는데 스펙트로그램이 안 만들어졌다" 는 상태를 눈치챌 방법이 없다.
                        //   실제로 그런 일이 있었다 — 자식표 스키마를 안 읽어 한 건도 안 생겼는데
                        //   화면에는 아무 표시도 없었다.
                        if (isAi)
                        {
                            src.Add(Item("스펙트로그램",
                                new PreviewRenderer.Panel(null,
                                    "소스에는 없습니다 — 원음(wav)에서 생성해 STD_AI_SPECTROGRAM 에 넣는 자료입니다.")));

                            var sp = await ReadAiSpectrogramAsync(opt, targetRecordId, ct);
                            tgt.Add(sp == null
                                ? Item("스펙트로그램 — STD_AI_SPECTROGRAM (S0)",
                                    new PreviewRenderer.Panel(null,
                                        "자식행이 없습니다",
                                        $"REF_STD_AI_SNIPPET_ID={targetRecordId} 인 행을 찾지 못했습니다 — " +
                                        "원음이 없었거나 스펙트로그램 생성이 건너뛰어졌습니다."))
                                : Item($"스펙트로그램 — STD_AI_SPECTROGRAM (S0) · {sp.FrameNum}프레임 × {sp.BinNum}빈",
                                    // 주파수는 자식행 제원, 시각은 부모 스니펫 것을 쓴다(자식표에 시각 칸이 없다).
                                    PreviewRenderer.FromTargetGramBlob(sp.Data, sp.BinNum, "S0 blob",
                                        new PreviewRenderer.GramAxes(sp.StartFreq, sp.EndFreq, ts, te))));
                        }
                    }
                    break;
                }
            }

            string note = targetRecordId > 0
                ? "왼쪽·오른쪽이 같은 그림이어야 정상입니다. 다르면 캡션의 '어떻게 읽었는지' 를 비교하세요."
                : "타겟 RECORD_ID 를 몰라 오른쪽은 비어 있습니다(짝이 기록되지 않은 행).";
            return new Result(src, tgt, srcTitle, tgtTitle, true, note);
        }

        // ── 읽기 ──────────────────────────────────────────────────────

        private static async Task<DataRow?> ReadSourceRowAsync(
            DbToDbOptions opt, string table, long pk, CancellationToken ct)
        {
            var plan = MigrationPlan.Defaults()
                .FirstOrDefault(p => p.SourceTable.Equals(table, StringComparison.OrdinalIgnoreCase));
            if (plan == null) return null;
            var cols = await TableInspector.GetColumnsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, table, ct);
            var dt = await TableInspector.GetRowsByIdsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, table, cols, plan.SourcePk, new[] { pk }, null, ct);
            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        /// <summary>
        /// 타겟 행을 blob 까지 통째로 읽는다. TableInspector 는 바이너리를 제외하는데,
        /// 미리보기의 목적이 바로 그 바이너리를 보는 것이라 여기서는 직접 조회한다.
        /// </summary>
        private static async Task<DataRow?> ReadTargetRowAsync(
            DbToDbOptions opt, string table, long id, CancellationToken ct)
        {
            await using var cn = new Microsoft.Data.SqlClient.SqlConnection(opt.TargetConnStr);
            await cn.OpenAsync(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM [{table}] WHERE RECORD_ID = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.CommandTimeout = 120;
            var dt = new DataTable();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            dt.Load(r);
            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        /// <summary>AI 스펙트로그램 자식행 한 건(제원 포함).</summary>
        private sealed record AiSpectro(byte[]? Data, int FrameNum, int BinNum, double StartFreq, double EndFreq);

        // [찾기:미리보기] 빈 수를 상수로 박아 두지 않는다 —
        //   틀린 빈 수로 그리면 그램이 사선으로 밀려 "깨진 자료" 처럼 보이는데,
        //   실제로는 그리는 쪽이 틀린 것이다. 제원은 그 행에 적혀 있으니 그걸 읽는다.
        private static async Task<AiSpectro?> ReadAiSpectrogramAsync(
            DbToDbOptions opt, long parentId, CancellationToken ct)
        {
            if (parentId <= 0) return null;
            try
            {
                await using var cn = new Microsoft.Data.SqlClient.SqlConnection(opt.TargetConnStr);
                await cn.OpenAsync(ct);
                await using var cmd = cn.CreateCommand();
                cmd.CommandText =
                    "SELECT TOP 1 GRAM_DATA, FRAME_NUM, START_FREQ, END_FREQ, BIN_RES " +
                    "FROM [STD_AI_SPECTROGRAM] WHERE REF_STD_AI_SNIPPET_ID = @id";
                cmd.Parameters.AddWithValue("@id", parentId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;

                byte[]? data = r["GRAM_DATA"] as byte[];
                int frames = r["FRAME_NUM"] is int fi ? fi : 0;
                double f0 = ToD(r["START_FREQ"]), f1 = ToD(r["END_FREQ"]), res = ToD(r["BIN_RES"]);
                // 빈 수는 제원에서 유도한다. 제원이 비면 blob 길이와 프레임 수로 되짚는다.
                int bins = res > 0 && f1 > f0 ? (int)Math.Round((f1 - f0) / res) + 1 : 0;
                if (bins <= 0 && frames > 0 && data is { Length: > GramHeader66.TotalSize })
                    bins = (data.Length - GramHeader66.TotalSize) / frames / sizeof(float);
                return new AiSpectro(data, frames, Math.Max(0, bins), f0, f1);
            }
            catch { return null; }
        }

        private static double ToD(object? v) =>
            v is null or DBNull ? 0 : Convert.ToDouble(v);

        // ── 도우미 ────────────────────────────────────────────────────
        private static PanelItem Item(string title, PreviewRenderer.Panel p) =>
            new() { Title = title, Caption = p.Caption, Error = p.Error ?? "", Image = p.Image };

        private static string Str(DataRow? r, string col) =>
            r != null && r.Table.Columns.Contains(col) && r[col] is not DBNull
                ? (Convert.ToString(r[col]) ?? "").Trim() : "";

        private static byte[]? Bytes(DataRow? r, string col) =>
            r != null && r.Table.Columns.Contains(col) ? r[col] as byte[] : null;

        // [NAIMS-ISSUE:SNIPPET-ABS-TIME]
        /// <summary>
        /// 타겟 행에서 <b>절대 시작 시각</b>을 만든다 = <c>DET_DATETIME 의 날짜</c> + <c>A_START_*</c>.
        ///
        /// DET_DATETIME 만 읽으면 안 된다 — 레거시 탐지일자에는 시·분·초가 없어 늘 자정이다.
        /// 하루 중 언제인지는 A_START_MILLISEC / A_START_MICROSEC 에 따로 들어 있고,
        /// <b>어느 컬럼인지는 표마다 다르다</b>(NB=밀리초, AI·TR=마이크로초).
        /// 실측 2026-08-20: STD_TR_SNIPPET DET_DATETIME=2008-09-11 00:00:00 인데
        /// A_START_MICROSEC=44,035,000,000 → 실제 12:13:55. 값은 있었고 안 읽고 있었다.
        ///
        /// SNIPPET_START_DATETIME 이 있고 자정이 아니면 그것을 먼저 쓴다 —
        /// 그 컬럼은 그램 헤더의 실측 시각까지 반영해 채워지므로 더 정확하다.
        /// (STD_TR_SNIPPET 에는 이 컬럼 자체가 없다.)
        /// </summary>
        private static DateTime? AbsStart(DataRow? r) =>
            Absolute(r, "SNIPPET_START_DATETIME", "A_START_MILLISEC", "A_START_MICROSEC");

        private static DateTime? AbsEnd(DataRow? r) =>
            Absolute(r, "SNIPPET_END_DATETIME", "A_END_MILLISEC", "A_END_MICROSEC");

        private static DateTime? Absolute(DataRow? r, string exactCol, string msCol, string usCol)
        {
            if (r == null) return null;

            // ① 시:분:초가 실린 전용 컬럼이 있으면 그것이 정본이다.
            if (Time(r, exactCol) is { } exact && exact.TimeOfDay != TimeSpan.Zero) return exact;

            var day = (Time(r, "DET_DATETIME") ?? Time(r, exactCol))?.Date;
            if (day == null) return null;

            // ② 자정 이후 오프셋. 단위는 컬럼 이름이 정한다.
            if (Dbl(r, usCol) is { } us && us > 0) return day.Value.AddSeconds(us / 1_000_000d);
            if (Dbl(r, msCol) is { } ms && ms > 0) return day.Value.AddSeconds(ms / 1_000d);

            // ③ 오프셋이 없으면(값 없음) 날짜만. 자정으로 보이는 것이 사실이다 — 지어내지 않는다.
            return Time(r, exactCol) ?? day;
        }

        private static double? Dbl(DataRow? r, string col) =>
            r != null && r.Table.Columns.Contains(col) && r[col] is not DBNull
                && double.TryParse(Convert.ToString(r[col]), out var v) ? v : null;

        private static DateTime? Time(DataRow? r, string col) =>
            r != null && r.Table.Columns.Contains(col) && r[col] is DateTime d && d > DateTime.MinValue
                ? d : null;

        private static int Int(DataRow? r, string col) =>
            r != null && r.Table.Columns.Contains(col) && r[col] is not DBNull
                && int.TryParse(Convert.ToString(r[col]), out var v) ? v : 0;
    }
}
