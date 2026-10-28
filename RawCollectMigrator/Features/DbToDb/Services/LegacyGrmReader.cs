using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.DbToDb
{
    public enum LegacyGrmKind
    {
        Unknown,
        NbSnippet,   // nb_*.grm — 128B 헤더 + BYTE 본문
        AiSnippet,   // ai_*.grm — 216B 헤더 + BYTE 본문
        RawFloat,    // 헤더 없는 순수 float[] (정밀분석 스니펫)
        R8           // 이미 신규 66B 포맷
    }

    /// <summary>레거시 .grm 파싱 결과.</summary>
    public sealed class LegacyGrm
    {
        public LegacyGrmKind Kind { get; init; }
        public string FilePath { get; init; } = "";
        public long FileSize { get; init; }
        public string? Error { get; init; }

        /// <summary>"L"/"V"/"D" — LOFAR / VERNIER / DEMON. AI 스니펫은 빈 값.</summary>
        public string GramTag { get; init; } = "";

        public DateTime? DetDate { get; init; }
        public long StartTimeMs { get; init; }
        public long EndTimeMs { get; init; }
        public float StartFreq { get; init; }
        public float EndFreq { get; init; }
        public float Resolution { get; init; }
        public int FrameCount { get; init; }
        public int BinCount { get; init; }
        public float IntegralTime { get; init; }
        public float UpdateTime { get; init; }
        public string Comment { get; init; } = "";

        // AI 스니펫 전용
        public string AudioSnippetFileName { get; init; } = "";
        public int EventId { get; init; }
        public string DetSonar { get; init; } = "";

        public int HeaderSize { get; init; }

        /// <summary>
        /// 역산된 진폭 float[FrameCount × BinCount].
        ///
        /// NB(L/V/D) : <b>선형(로그 적용 전)</b> 값 — 레거시 저장 3단(선형→log10→×A+B)을 모두 되돌린다.
        /// AI(A0)    : dB 영역 값 — AR 그램은 전시식이 <c>(val+12)/18×255</c> 라 로그를 되돌리지 않는다.
        /// RawFloat  : 원본 그대로(변환 없음).
        /// </summary>
        public float[] Amplitude { get; init; } = Array.Empty<float>();

        /// <summary>역산에 실제로 쓴 A/B 스케일과 그 출처(.set 경로 또는 "기본값").</summary>
        public float UsedAScale { get; init; }
        public float UsedBScale { get; init; }
        public string ScaleSource { get; init; } = "";

        /// <summary>원본 BYTE 값 보존 (전시 검증용).</summary>
        public byte[] RawBody { get; init; } = Array.Empty<byte>();

        /// <summary>LOFAR 전용 ALI 블록 (없으면 null).</summary>
        public byte[]? Ali { get; init; }

        public bool Ok => Error == null && Kind != LegacyGrmKind.Unknown;

        public string KindText => Kind switch
        {
            LegacyGrmKind.NbSnippet => "레거시 NB 스니펫 (128B 헤더)",
            LegacyGrmKind.AiSnippet => "레거시 AI 스니펫 (216B 헤더)",
            LegacyGrmKind.RawFloat  => "헤더 없는 float[] 스니펫 (정밀분석)",
            LegacyGrmKind.R8        => "신규 R8 (66B 헤더)",
            _ => "미상"
        };
    }

    /// <summary>
    /// 레거시 .grm 파서.
    ///
    /// 원본: DBImplant_N1toN2/Features/ShortPeriod/Parsers/GrmFileReader.cs
    /// 2026-07-21 CLASSIFICATIONDB\SNIPPET 실측으로 헤더 크기를 교정했다.
    /// 원본은 C++ 구조체 정렬 패딩을 반영하지 않아 NB 1B / AI 3B 만큼 오프셋이 어긋나 있었다.
    ///
    /// ── NB 스니펫 (nb_*.grm) : 헤더 128B ──
    ///   @0   char  cGram[3]        "L "/"V1"/"D " — 첫 글자가 그램 종류
    ///   @3   (패딩 1B)
    ///   @4   DWORD YYMMDD_Type     비트필드 day:5 / month:4 / year:7
    ///   @8   int32 StartTime       ms
    ///   @12  int32 EndTime         ms
    ///   @16  float StartFreq
    ///   @20  float EndFreq
    ///   @24  float Resolution      Hz/bin
    ///   @28  int32 FrameNum
    ///   @32  int32 BinWidth
    ///   @36  float IntTime
    ///   @40  float UpdateTime
    ///   @44  char  Comment[81]     CP949
    ///   @125 (패딩 3B)
    ///   본문  BYTE[FrameNum × BinWidth]
    ///   꼬리  BYTE[BinWidth] ALI   (LOFAR 만, 남는 바이트가 있을 때)
    ///
    /// ── AI 스니펫 (ai_*.grm) : 헤더 216B ──
    ///   @0   int64 StartTime
    ///   @8   int64 EndTime
    ///   @16  float StartFreq
    ///   @20  float EndFreq
    ///   @24  int32 BinWidth
    ///   @28  int32 FrameNum
    ///   @32  float Resolution
    ///   @36  char  Comment[81]
    ///   @117 char  AudioSnippetFileName[81]
    ///   @198 (패딩 2B)
    ///   @200 int32 EventID
    ///   @204 char  DetSonar[11]
    ///   @215 (패딩 1B)
    ///   본문  BYTE[FrameNum × BinWidth]
    ///
    /// ── 진폭 값 영역 (AAS·CDMU 교차확인, 2026-09-03) ──
    ///   그램 종류마다 생성 경로가 다르다. 하나의 역산식을 전부에 적용하면 안 된다.
    ///
    ///   ① NB — LOFAR / VERNIER1~4 / DEMON1~4  (태그 L/V/D)
    ///      생성: sti_Log = log10(정규화 진폭)      AAS ModelSPLofar.cpp:357 / SPVernier.cpp:518 / SPDemon.cpp:474
    ///                                             (NAIMS-I DSP 는 ez_v10log_f — AAS 가 ippsLog10 으로 포팅한 주석이 남아 있다)
    ///            scaled = sti_Log × AScale + BScale   AAS ModelPreciseAnalysis.cpp:8916 / CDMU CommManager.cpp:464
    ///            BYTE   = round(clamp(scaled, 0, 255))
    ///            파일 기록 경로가 <b>둘</b>이고, 남는 해상도가 다르다:
    ///              · CDMU FileReadWriteFunc.cpp:2847 (short 오버로드) — "0x00FC &amp; ((temp&gt;&gt;5)&lt;&lt;2)"
    ///                → 4의 배수 <b>64단계</b>(6비트). 이관 대상인 nb_*.grm 스니펫이 이 경로다.
    ///              · AAS UtilFileManage.cpp:5582 — 같은 줄이 주석 처리되고 "0x00FF &amp; temp" 로 8비트 기록.
    ///            역산식은 둘 다 같다. 다른 것은 되살아나는 해상도뿐이다.
    ///      실파일 확인: nb_*.grm 3건 — 128B 헤더 정상(태그 'D'/'V3', 200~202프레임 × 1601빈),
    ///                   본문 고유값 64개 · 최대 252 · 전부 4의 배수 → CDMU 6비트 경로와 일치.
    ///      ※ 같은 폴더의 LOFAR.grm / DEMON*.grm / VERNIER*.grm 은 <b>이 포맷이 아니다</b>
    ///         (128B 로 읽으면 FrameNum 이 1억대로 나온다). 스니펫이 아니라 정밀분석 풀그램이고,
    ///         이관 대상이 아니므로 이 파서가 거부하는 것이 맞다.
    ///      역산: 선형 = 10 ^ ((BYTE − BScale) / AScale)      → NAIMS-II 는 전시 때 log10 을 다시 건다
    ///            유효 계단은 4/AScale (A=150 이면 선형 1.063배, A=250 이면 1.038배)
    ///
    ///   ② NB ALI (LOFAR 본문 뒤 꼬리 블록)
    ///      생성: ali_Log = 20 × log10(...) − fft_offset_dB   AAS ModelSPLofar.cpp:368  → <b>dB</b>
    ///            스케일은 A/B 가 아니라 "× 1 + (5 − nMin)" 이다.
    ///      ⇒ 본문과 같은 식을 적용하면 안 된다. 여기서는 원본 BYTE 를 그대로 보존만 한다.
    ///
    ///   ③ AI / AR (ai_*.grm, 태그 A0)
    ///      생성: 20 × log10 → 최대값 기준 상대 dB → clamp[−12, +6] → v = (dB+12)/18 × 255
    ///            파일 기록 시 상위 3비트만: BYTE = ((v &gt;&gt; 5) &lt;&lt; 2) &amp; 0xFC  → {0,4,…,28} <b>8단계</b>
    ///            AAS SPUtil.cpp:98 · CDMU SPUtil.cpp:98 · 양쪽 WriteARGramFile 동일
    ///      실파일 확인: ai_*.grm 5건 본문 고유값이 정확히 {0,4,…,28}
    ///      역산: 상대 dB = (32·(BYTE÷4) + 15.5) / 255 × 18 − 12   → <b>로그·A/B 모두 무관</b>
    ///
    ///   AScale/BScale 은 상수가 아니라 자료마다 다른 운용 파라미터다(① 에만 해당).
    ///   같은 폴더의 "정밀신호처리파라미터.set" 마지막 줄 두 값이 정본이며
    ///   (UtilFileManage.cpp:5564 이 "%16.7f%16.7f" 로 기록),
    ///   실자료에 150/40 아닌 값이 존재한다(ETC_20100415105233…nb → 250/40).
    ///   없거나 0 이하일 때만 기본값(150/40)으로 떨어진다 — AAS·CDMU 가 쓰는 폴백 규칙과 동일
    ///   (AASBaseDefine.h:325 / CDMU StdAfx.h:66 둘 다 "#define AScale 150").
    /// </summary>
    public static class LegacyGrmReader
    {
        public const int NbHeaderSize = 128;
        public const int AiHeaderSize = 216;

        public const float AScale = 150.0f;
        public const float BScale = 40.0f;

        private static readonly Encoding Cp949 = GetCp949();
        private static Encoding GetCp949()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949);
            }
            catch { return Encoding.UTF8; }
        }

        public static LegacyGrm Read(string filePath)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(filePath); }
            catch (Exception ex)
            {
                return new LegacyGrm { FilePath = filePath, Error = "읽기 실패: " + ex.Message };
            }

            if (bytes.Length < 66)
                return new LegacyGrm { FilePath = filePath, FileSize = bytes.Length, Error = $"파일이 너무 작음 ({bytes.Length}B)" };

            // 이미 신규 R8 포맷이면 변환 대상이 아니다.
            var r8 = GramHeader66.TryRead(bytes);
            if (r8 != null && r8.IsKnownR8Tag && r8.FrameCount > 0 && r8.FrameCount < 100_000)
            {
                long body = bytes.LongLength - GramHeader66.TotalSize;
                long perFrame = body / r8.FrameCount;
                if (perFrame > GramHeader66.TimestampSize)
                {
                    return new LegacyGrm
                    {
                        Kind = LegacyGrmKind.R8,
                        FilePath = filePath,
                        FileSize = bytes.Length,
                        GramTag = r8.GramType,
                        FrameCount = r8.FrameCount,
                        BinCount = (int)((perFrame - GramHeader66.TimestampSize) / sizeof(float)),
                        StartFreq = (float)r8.StartFreq,
                        EndFreq = (float)r8.EndFreq,
                        Resolution = (float)r8.BinResolution,
                        IntegralTime = r8.IntegralTime,
                        HeaderSize = GramHeader66.TotalSize,
                    };
                }
            }

            // 파일명이 1차 힌트지만, 최종 판정은 "헤더 크기 + 본문 크기"가 파일 크기와 맞는지로 한다.
            var name = Path.GetFileName(filePath).ToLowerInvariant();
            LegacyGrm first, second;
            if (name.StartsWith("ai_"))
            {
                first  = TryReadAi(bytes, filePath);
                if (first.Ok) return first;
                second = TryReadNb(bytes, filePath);
            }
            else
            {
                first  = TryReadNb(bytes, filePath);
                if (first.Ok) return first;
                second = TryReadAi(bytes, filePath);
            }
            if (second.Ok) return second;

            // 헤더가 있는 두 포맷 모두 실패 → 헤더 없는 순수 float[] 인지 확인
            var rawFloat = TryReadRawFloat(bytes, filePath);
            return rawFloat.Ok ? rawFloat : first;
        }

        // ── 헤더 없는 float[] 스니펫 ─────────────────────────────────
        /// <summary>
        /// 정밀분석 스니펫은 태그·헤더 없이 float[frame × bin] 만 저장된다
        /// (NAIMS-II GramReader 의 PreciseNbSnippet 경로와 동일).
        /// 빈 수는 알려진 후보(1601 = PRECISION_BIN_SIZE, 801)로 나누어떨어지는지로 판정한다.
        /// </summary>
        private static readonly int[] KnownBinSizes = { 1601, 801 };

        private static LegacyGrm TryReadRawFloat(byte[] b, string path)
        {
            if (b.Length % sizeof(float) != 0)
                return Fail(path, b.Length, "RAW: float 배열 크기가 아님");

            int totalFloats = b.Length / sizeof(float);

            foreach (var bins in KnownBinSizes)
            {
                if (totalFloats % bins != 0) continue;
                int frames = totalFloats / bins;
                if (frames <= 0 || frames > 100_000) continue;

                var amp = new float[totalFloats];
                Buffer.BlockCopy(b, 0, amp, 0, b.Length);

                // 값이 dB 스케일(대체로 -10 ~ +10)인지 가볍게 확인해 오탐을 줄인다.
                int sane = 0, sample = Math.Min(totalFloats, 4096);
                for (int i = 0; i < sample; i++)
                    if (float.IsFinite(amp[i]) && Math.Abs(amp[i]) < 1000f) sane++;
                if (sane < sample * 0.9) continue;

                return new LegacyGrm
                {
                    Kind = LegacyGrmKind.RawFloat,
                    FilePath = path,
                    FileSize = b.Length,
                    FrameCount = frames,
                    BinCount = bins,
                    HeaderSize = 0,
                    // [NAIMS-ISSUE:RAWFLOAT-DOMAIN] 원본 그대로 통과시킨다.
                    //   이 경로의 값이 log 영역이면 NB 와 같은 규약(선형 저장)으로 맞추기 위해
                    //   10^ 가 필요하지만, 값의 영역을 확정할 근거를 아직 못 찾았다. 확정 전까지 무변환.
                    Amplitude = amp,
                    ScaleSource = "변환 없음(원본 float)",
                };
            }

            return Fail(path, b.Length, $"RAW: 알려진 빈 수({string.Join("/", KnownBinSizes)})로 나누어떨어지지 않음");
        }

        // ── NB 스니펫 ────────────────────────────────────────────────
        private static LegacyGrm TryReadNb(byte[] b, string path)
        {
            try
            {
                var sc = ReadScaleParams(path);
                if (b.Length <= NbHeaderSize)
                    return Fail(path, b.Length, "NB: 헤더보다 작음");

                string cGram = Cp949.GetString(b, 0, 3).TrimEnd('\0', ' ');
                if (cGram.Length == 0 || !"LVD".Contains(cGram[0]))
                    return Fail(path, b.Length, $"NB: 알 수 없는 그램 태그 '{cGram}'");

                // YYMMDD 비트필드 @4
                int raw = BitConverter.ToUInt16(b, 4);
                int day   =  raw        & 0x1F;
                int month = (raw >> 5)  & 0x0F;
                int year  = (raw >> 9)  & 0x7F;
                year += year < 80 ? 2000 : 1900;
                DateTime? det = null;
                try { if (month is >= 1 and <= 12 && day is >= 1 and <= 31) det = new DateTime(year, month, day); }
                catch { }

                int   startMs = BitConverter.ToInt32(b, 8);
                int   endMs   = BitConverter.ToInt32(b, 12);
                float sf      = BitConverter.ToSingle(b, 16);
                float ef      = BitConverter.ToSingle(b, 20);
                float res     = BitConverter.ToSingle(b, 24);
                int   frames  = BitConverter.ToInt32(b, 28);
                int   bins    = BitConverter.ToInt32(b, 32);
                float intTime = BitConverter.ToSingle(b, 36);
                float updTime = BitConverter.ToSingle(b, 40);
                string comment = ReadFixed(b, 44, 81);

                if (frames <= 0 || frames > 100_000) return Fail(path, b.Length, $"NB: FrameNum 비정상 ({frames})");
                if (bins   <= 0 || bins   > 100_000) return Fail(path, b.Length, $"NB: BinWidth 비정상 ({bins})");

                long bodySize = (long)frames * bins;
                long avail = b.LongLength - NbHeaderSize;
                if (avail < bodySize)
                    return Fail(path, b.Length, $"NB: 본문 부족 (필요 {bodySize:N0}, 가능 {avail:N0})");

                var body = new byte[bodySize];
                Buffer.BlockCopy(b, NbHeaderSize, body, 0, (int)bodySize);

                // LOFAR 는 본문 뒤에 ALI 블록이 붙는다.
                byte[]? ali = null;
                long tail = avail - bodySize;
                if (tail >= bins)
                {
                    ali = new byte[bins];
                    Buffer.BlockCopy(b, NbHeaderSize + (int)bodySize, ali, 0, bins);
                }

                return new LegacyGrm
                {
                    Kind = LegacyGrmKind.NbSnippet,
                    FilePath = path,
                    FileSize = b.Length,
                    GramTag = cGram,
                    DetDate = det,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    StartFreq = sf,
                    EndFreq = ef,
                    Resolution = res,
                    FrameCount = frames,
                    BinCount = bins,
                    IntegralTime = intTime,
                    UpdateTime = updTime,
                    Comment = comment,
                    HeaderSize = NbHeaderSize,
                    RawBody = body,
                    Ali = ali,
                    Amplitude = DescaleToLinear(body, sc.A, sc.B),
                    UsedAScale = sc.A,
                    UsedBScale = sc.B,
                    ScaleSource = sc.Source,
                };
            }
            catch (Exception ex)
            {
                return Fail(path, b.Length, "NB: " + ex.Message);
            }
        }

        // ── AI 스니펫 ────────────────────────────────────────────────
        private static LegacyGrm TryReadAi(byte[] b, string path)
        {
            try
            {
                if (b.Length <= AiHeaderSize)
                    return Fail(path, b.Length, "AI: 헤더보다 작음");

                long  startT = BitConverter.ToInt64(b, 0);
                long  endT   = BitConverter.ToInt64(b, 8);
                float sf     = BitConverter.ToSingle(b, 16);
                float ef     = BitConverter.ToSingle(b, 20);
                int   bins   = BitConverter.ToInt32(b, 24);
                int   frames = BitConverter.ToInt32(b, 28);
                float res    = BitConverter.ToSingle(b, 32);
                string comment  = ReadFixed(b, 36, 81);
                string audioName = ReadFixed(b, 117, 81);
                int   eventId = BitConverter.ToInt32(b, 200);   // @198 은 2B 패딩
                string sonar  = ReadFixed(b, 204, 11);

                if (frames <= 0 || frames > 100_000) return Fail(path, b.Length, $"AI: FrameNum 비정상 ({frames})");
                if (bins   <= 0 || bins   > 100_000) return Fail(path, b.Length, $"AI: BinWidth 비정상 ({bins})");

                long bodySize = (long)frames * bins;
                long avail = b.LongLength - AiHeaderSize;
                if (avail < bodySize)
                    return Fail(path, b.Length, $"AI: 본문 부족 (필요 {bodySize:N0}, 가능 {avail:N0})");

                var body = new byte[bodySize];
                Buffer.BlockCopy(b, AiHeaderSize, body, 0, (int)bodySize);

                return new LegacyGrm
                {
                    Kind = LegacyGrmKind.AiSnippet,
                    FilePath = path,
                    FileSize = b.Length,
                    StartTimeMs = startT,
                    EndTimeMs = endT,
                    StartFreq = sf,
                    EndFreq = ef,
                    Resolution = res,
                    FrameCount = frames,
                    BinCount = bins,
                    Comment = comment,
                    AudioSnippetFileName = audioName,
                    EventId = eventId,
                    DetSonar = sonar,
                    HeaderSize = AiHeaderSize,
                    RawBody = body,
                    Amplitude = DescaleAiGram(body),
                    ScaleSource = "AI: A/B 스케일 무관 ((BYTE÷4)→상대 dB)",
                };
            }
            catch (Exception ex)
            {
                return Fail(path, b.Length, "AI: " + ex.Message);
            }
        }

        // ── A/B 스케일 조회 ─────────────────────────────────────────
        /// <summary>역산에 쓸 A/B 스케일과 그 출처.</summary>
        public readonly record struct ScaleParams(float A, float B, string Source);

        /// <summary>AASBaseDefine.h 기본값. .set 이 없거나 값이 0 이하일 때만 쓴다.</summary>
        public static readonly ScaleParams DefaultScale = new(AScale, BScale, "기본값(150/40)");

        private const string NbParamFileName = "정밀신호처리파라미터.set";

        /// <summary>
        /// .grm 과 같은 폴더의 "정밀신호처리파라미터.set" 마지막 줄에서 A/B 스케일을 읽는다.
        ///
        /// 파일은 AAS 가 <c>fprintf(fp, "%16.7f%16.7f", AScale, BScale)</c> 로 마지막에 덧붙인 것이라
        /// 마지막 비어있지 않은 줄의 끝 두 토큰이 곧 A/B 다(UtilFileManage.cpp:5564).
        /// 읽지 못하거나 값이 0 이하면 레거시와 같은 규칙으로 기본값(150/40)에 떨어진다.
        /// </summary>
        public static ScaleParams ReadScaleParams(string grmPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(grmPath);
                if (string.IsNullOrEmpty(dir)) return DefaultScale;

                var setPath = Path.Combine(dir, NbParamFileName);
                if (!File.Exists(setPath)) return DefaultScale;

                string? last = null;
                foreach (var line in File.ReadLines(setPath, Cp949))
                    if (!string.IsNullOrWhiteSpace(line)) last = line;
                if (last == null) return DefaultScale;

                var tok = last.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) return DefaultScale;

                if (!float.TryParse(tok[^2], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var a) ||
                    !float.TryParse(tok[^1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var b))
                    return DefaultScale;

                // 레거시 폴백 규칙(ViewPropertyPAMNBSignalProcessing.cpp:2504) 과 동일하게 0 이하는 기본값.
                if (a <= 0) a = AScale;
                if (b <= 0) b = BScale;
                return new ScaleParams(a, b, setPath);
            }
            catch (IOException)          { return DefaultScale; }
            catch (UnauthorizedAccessException) { return DefaultScale; }
        }

        // ── 역산 ────────────────────────────────────────────────────
        /// <summary>
        /// NB 용 완전 역산: BYTE → <b>선형 진폭</b>. 레거시의 log10 과 ×A+B 를 모두 되돌린다.
        ///
        ///   선형 = 10 ^ ((BYTE − B) / A)
        ///
        /// 입력 정의역이 0~255 뿐이라 256칸 표를 만들어 쓴다(Pow 를 샘플마다 부르지 않는다).
        /// BYTE 0 / 255 는 레거시가 클램프한 포화값이다 — 되돌린 값은 "그 이하/이상"이라는 뜻이지
        /// 참값이 아니다. 클램프와 반올림으로 잃은 정보는 여기서 되살아나지 않는다.
        /// </summary>
        private static float[] DescaleToLinear(byte[] body, float a, float b)
        {
            var lut = new float[256];
            for (int v = 0; v < 256; v++)
                lut[v] = (float)Math.Pow(10.0, (v - b) / a);

            var f = new float[body.Length];
            for (int i = 0; i < body.Length; i++)
                f[i] = lut[body[i]];
            return f;
        }

        /// <summary>
        /// AI(AR 그램) 용 역산: BYTE → <b>상대 dB</b>. AScale/BScale 과 무관하고, 로그도 되돌리지 않는다.
        ///
        /// 레거시 생성식(AAS <c>SPUtil.cpp:98</c> · CDMU <c>SPUtil.cpp:98</c> — 두 체계 동일):
        ///   ① AR 스펙트럼 = 20 × log10(|FFT|) → <b>dB</b>
        ///   ② 최대값 기준 정규화: <c>SubC(max − 6)</c> → 최대가 +6 인 상대 dB
        ///   ③ 클램프 <c>[−AR_GRAM_BW(12), +6]</c>
        ///   ④ <c>v = (dB + 12) / 18 × 255</c>  → 0~255
        /// 그리고 파일에 쓸 때(AAS·CDMU <c>WriteARGramFile</c>) 상위 3비트만 남긴다:
        ///   <c>BYTE = ((v >> 5) &lt;&lt; 2) &amp; 0xFC</c>  →  {0,4,8,…,28} <b>8단계뿐</b>
        ///
        /// 실파일 확인(2026-09-03, D:/MSU/CLASSIFICATIONDB/SNIPPET/ai_*.grm 5건):
        /// 본문 고유값이 정확히 {0,4,…,28} 8개 — 위 식과 일치한다.
        ///
        /// [NAIMS-ISSUE:AI-GRAM-3BIT] 원본이 이미 8단계다. 이관으로 되살릴 수 없는 상한이므로
        /// 각 계단의 <b>중앙값</b>으로 복원한다(v ≈ 32·i + 15.5, i = BYTE ÷ 4).
        /// 레거시 전시는 자기 최대값으로 재정규화(<c>DivC(max)</c>)해서 이 손실이 보이지 않았지만,
        /// NAIMS-II <c>ArGramRenderer</c> 는 <c>(val+12)/18×255</c> 로 <b>절대</b> 매핑하므로
        /// 여기서 넣는 값이 곧 화면 밝기가 된다.
        /// </summary>
        private static float[] DescaleAiGram(byte[] body)
        {
            const float ArGramBw = 12.0f;          // AAS/CDMU: #define AR_GRAM_BW 12
            const float Span     = ArGramBw + 6f;  // 18

            var lut = new float[256];
            for (int v = 0; v < 256; v++)
            {
                int step = v >> 2;                 // 0~7 (그 밖의 값은 규격 외 — 같은 식으로 처리)
                if (step > 7) step = 7;
                float mid = step * 32f + 15.5f;    // 계단이 덮는 v 구간의 중앙
                lut[v] = mid / 255f * Span - ArGramBw;
            }

            var f = new float[body.Length];
            for (int i = 0; i < body.Length; i++)
                f[i] = lut[body[i]];
            return f;
        }

        private static string ReadFixed(byte[] b, int offset, int len)
        {
            if (offset >= b.Length) return "";
            int n = Math.Min(len, b.Length - offset);
            int end = Array.IndexOf(b, (byte)0, offset, n);
            int take = end < 0 ? n : end - offset;
            return take <= 0 ? "" : Cp949.GetString(b, offset, take).Trim();
        }

        private static LegacyGrm Fail(string path, long size, string msg) =>
            new() { FilePath = path, FileSize = size, Error = msg, Kind = LegacyGrmKind.Unknown };
    }
}
