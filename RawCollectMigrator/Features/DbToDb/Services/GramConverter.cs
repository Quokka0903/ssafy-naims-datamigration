using System.IO;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 레거시 .grm → 신규 R8 66B 포맷 변환.
    ///
    /// 출력 레이아웃 (GramHeader66 와 정합):
    ///   [0..1]   GramType   char[2]    "L0" / "D0" / "V1"~"V4" / "S0"
    ///   [2..17]  StartTime  TIMESTAMP  8 × UInt16
    ///   [18..33] EndTime    TIMESTAMP
    ///   [34..37] FrameCount int32
    ///   [38..41] IntegralTime float
    ///   [42..49] BinResolution double
    ///   [50..57] StartFreq  double
    ///   [58..65] EndFreq    double
    ///   이후 프레임마다: TIMESTAMP(16) + float[BinCount]
    ///
    /// 레거시에는 프레임별 시각이 없으므로 Start~End 를 프레임 수로 균등 분할해 채운다.
    /// 진폭은 LegacyGrmReader 가 역산해 둔 값을 그대로 쓴다
    /// (NB = 선형, AI = log 영역 — 규약이 다르다. LegacyGrmReader 주석 참조).
    /// </summary>
    public static class GramConverter
    {
        /// <summary>레거시 그램 태그(L/V/D)를 R8 2바이트 태그로 변환.</summary>
        // [NAIMS-ISSUE:AI-GRAM-A0] ai_*.grm 은 AR 그램이므로 태그가 "A0" 다("S0" 아님).
        //   S0 = STFT 스펙트로그램(SpectrogramS0Codec), A0 = AR 그램(ArGramCodec) 으로 서로 다른 자료다.
        //   정본: 260128_svn/AIATUSW/Model/STFT/ArGramCodec.cs (A0Tag0='A', A0Tag1='0').
        //   전시 측 TryDeserialize 는 선두 2B 가 "A0" 가 아니면 legacy 213B 로 간주해 "거부하지 않고
        //   잘못 읽는다" — 태그가 틀리면 오류 없이 엉뚱한 그램이 그려진다.
        public static string ToR8Tag(LegacyGrm g)
        {
            if (g.Kind == LegacyGrmKind.AiSnippet) return "A0";   // 능동식별 스니펫 AR 그램
            if (g.Kind == LegacyGrmKind.RawFloat)  return "L0";   // 정밀분석 협대역 스니펫
            var t = (g.GramTag ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) return "L0";
            return t[0] switch
            {
                'L' => "L0",
                'D' => "D0",
                // VERNIER 는 대역 번호를 유지한다 (V1~V4). 번호가 없으면 V1.
                'V' => t.Length >= 2 && t[1] is >= '1' and <= '4' ? $"V{t[1]}" : "V1",
                _ => "L0"
            };
        }

        public sealed record ConvertResult(
            bool Ok, byte[] Data, string Tag, int FrameCount, int BinCount, string Message);

        /// <summary>
        /// 레거시 파싱 결과를 R8 66B 바이너리로 변환한다.
        /// </summary>
        /// <param name="baseDate">
        /// 프레임 시각의 기준 날짜. 레거시 NB 헤더의 탐지일자를 우선 쓰고,
        /// 없으면 이 값(보통 DB 의 DET_DATE)을 쓴다.
        /// </param>
        /// <param name="forceTag">
        /// 2바이트 태그를 강제한다(예: STD_AI_SNIPPET.GRAM_FILE 은 원본 종류와 무관하게 항상 "A0").
        /// 타겟 컬럼이 요구하는 규약이 원본 파일 종류보다 우선하기 때문이다. null 이면 원본에서 산출.
        /// </param>
        /// <param name="fallbackStart">
        /// 레거시 헤더에 <b>시각이 없을 때</b> 쓸 절대 시작 시각(보통 DB 행의 DET_DATE + A_START_TIME).
        ///
        /// [NAIMS-ISSUE:SNIPPET-ABS-TIME] 헤더가 비었다고 자정을 적어 넣으면, 그 blob 을 읽는 쪽은
        /// "자정에 잡힌 신호" 로 읽는다 — 없는 값을 없는 채로 두는 것이 아니라 <b>틀린 값을 새로 만드는</b> 것이다.
        /// 자료 안에 시각이 남아 있으면(행의 A_START_TIME) 그것을 쓰는 편이 언제나 낫다.
        /// </param>
        public static ConvertResult ToR8(LegacyGrm g, DateTime? baseDate = null, string? forceTag = null,
                                         DateTime? fallbackStart = null, DateTime? fallbackEnd = null)
        {
            if (!g.Ok)
                return new ConvertResult(false, Array.Empty<byte>(), "", 0, 0, g.Error ?? "파싱 실패");

            if (g.Kind == LegacyGrmKind.R8)
                return new ConvertResult(false, Array.Empty<byte>(), g.GramTag, g.FrameCount, g.BinCount,
                    "이미 R8 포맷 — 변환 불필요");

            int frames = g.FrameCount, bins = g.BinCount;
            if (frames <= 0 || bins <= 0)
                return new ConvertResult(false, Array.Empty<byte>(), "", 0, 0, "프레임/빈 수가 비정상");

            var day = g.DetDate ?? baseDate ?? DateTime.Today;
            var start = day.AddMilliseconds(Math.Max(0, g.StartTimeMs));
            var end   = day.AddMilliseconds(Math.Max(0, g.EndTimeMs));

            // 헤더에 쓸 만한 시간 범위가 없으면(둘 다 0 이거나 끝<시작) 넘겨받은 값으로 대신한다.
            bool headerHasTime = g.EndTimeMs > g.StartTimeMs && g.StartTimeMs > 0;
            if (!headerHasTime && fallbackStart is { } fs)
            {
                start = fs;
                end = fallbackEnd is { } fe && fe > fs ? fe : fs;
            }
            if (end < start) end = start;

            // 레거시 Resolution 이 비어 있으면 주파수 범위에서 산출한다.
            double binRes = g.Resolution > 0
                ? g.Resolution
                : (bins > 1 ? (g.EndFreq - g.StartFreq) / (bins - 1) : 0.0);

            string tag = forceTag is { Length: 2 } ft ? ft : ToR8Tag(g);

            int frameBytes = GramHeader66.TimestampSize + bins * sizeof(float);
            var output = new byte[GramHeader66.TotalSize + (long)frames * frameBytes];

            int pos = 0;
            output[pos++] = (byte)tag[0];
            output[pos++] = (byte)tag[1];
            WriteTimestamp(output, ref pos, start);
            WriteTimestamp(output, ref pos, end);
            WriteInt32(output, ref pos, frames);
            WriteSingle(output, ref pos, g.IntegralTime);
            WriteDouble(output, ref pos, binRes);
            WriteDouble(output, ref pos, g.StartFreq);
            WriteDouble(output, ref pos, g.EndFreq);
            // pos == 66

            // 프레임 시각을 균등 분할
            double stepMs = frames > 1 ? (end - start).TotalMilliseconds / (frames - 1) : 0.0;

            var amp = g.Amplitude;
            for (int f = 0; f < frames; f++)
            {
                WriteTimestamp(output, ref pos, start.AddMilliseconds(stepMs * f));

                int srcOffset = f * bins;
                int copy = Math.Min(bins, Math.Max(0, amp.Length - srcOffset));
                if (copy > 0)
                    Buffer.BlockCopy(amp, srcOffset * sizeof(float), output, pos, copy * sizeof(float));
                pos += bins * sizeof(float);   // 부족분은 0 으로 남긴다
            }

            var msg = $"{g.KindText} → R8 '{tag}'  |  {frames}프레임 × {bins}빈  |  {output.Length:N0} B";
            if (g.Kind == LegacyGrmKind.RawFloat)
                msg += "   ※ 헤더가 없어 주파수·시각 정보는 DB(FREQ_MIN/MAX, DET_DATE)에서 채워야 함";
            return new ConvertResult(true, output, tag, frames, bins, msg);
        }

        /// <summary>파일을 읽어 곧바로 R8 바이너리로 변환한다.</summary>
        public static ConvertResult ConvertFile(string path, DateTime? baseDate = null)
            => ToR8(LegacyGrmReader.Read(path), baseDate);

        // ── 진단용 요약 ──────────────────────────────────────────────

        public sealed class GramInspection
        {
            public string FilePath { get; init; } = "";
            public long FileSize { get; init; }
            public string DetectedTag { get; init; } = "";
            public bool IsR8Header { get; init; }
            public GramHeader66? Header { get; init; }
            public LegacyGrm? Legacy { get; init; }
            public int EstimatedBinCount { get; init; }
            public string Diagnostic { get; init; } = "";
        }

        /// <summary>
        /// .grm 을 판별해 요약한다. R8 이면 66B 헤더를, 레거시면 NB/AI 헤더를 해석한다.
        /// </summary>
        public static GramInspection Inspect(string filePath)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists)
                return new GramInspection { FilePath = filePath, Diagnostic = "파일 없음" };

            var legacy = LegacyGrmReader.Read(filePath);

            if (legacy.Kind == LegacyGrmKind.R8)
            {
                byte[] head;
                try { head = File.ReadAllBytes(filePath); }
                catch (Exception ex)
                {
                    return new GramInspection { FilePath = filePath, FileSize = fi.Length, Diagnostic = "읽기 실패: " + ex.Message };
                }
                var hdr = GramHeader66.TryRead(head);
                return new GramInspection
                {
                    FilePath = filePath,
                    FileSize = fi.Length,
                    DetectedTag = legacy.GramTag,
                    IsR8Header = true,
                    Header = hdr,
                    Legacy = legacy,
                    EstimatedBinCount = legacy.BinCount,
                    Diagnostic = "신규 R8 66B 헤더 — 변환 불필요."
                };
            }

            return new GramInspection
            {
                FilePath = filePath,
                FileSize = fi.Length,
                DetectedTag = legacy.GramTag,
                IsR8Header = false,
                Legacy = legacy,
                EstimatedBinCount = legacy.BinCount,
                Diagnostic = legacy.Ok
                    ? $"{legacy.KindText} 인식 — R8 변환 대상."
                    : $"판별 실패: {legacy.Error}"
            };
        }

        // ── 바이너리 쓰기 ────────────────────────────────────────────

        /// <summary>TIMESTAMP(16B) = 8 × UInt16 (year, month, dayOfWeek, day, hour, min, sec, ms)</summary>
        private static void WriteTimestamp(byte[] buf, ref int pos, DateTime dt)
        {
            WriteUInt16(buf, ref pos, (ushort)dt.Year);
            WriteUInt16(buf, ref pos, (ushort)dt.Month);
            WriteUInt16(buf, ref pos, (ushort)dt.DayOfWeek);
            WriteUInt16(buf, ref pos, (ushort)dt.Day);
            WriteUInt16(buf, ref pos, (ushort)dt.Hour);
            WriteUInt16(buf, ref pos, (ushort)dt.Minute);
            WriteUInt16(buf, ref pos, (ushort)dt.Second);
            WriteUInt16(buf, ref pos, (ushort)dt.Millisecond);
        }

        private static void WriteUInt16(byte[] b, ref int pos, ushort v)
        {
            b[pos++] = (byte)(v & 0xFF);
            b[pos++] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteInt32(byte[] b, ref int pos, int v)
        {
            BitConverter.TryWriteBytes(b.AsSpan(pos, 4), v);
            pos += 4;
        }

        private static void WriteSingle(byte[] b, ref int pos, float v)
        {
            BitConverter.TryWriteBytes(b.AsSpan(pos, 4), v);
            pos += 4;
        }

        private static void WriteDouble(byte[] b, ref int pos, double v)
        {
            BitConverter.TryWriteBytes(b.AsSpan(pos, 8), v);
            pos += 8;
        }
    }
}
