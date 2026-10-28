using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DBImplant_N1toN2.Features.ShortPeriod.Parsers
{
    // ══════════════════════════════════════════════════════════════
    //  레거시 .grm 파일 파서 — 헤더(DB필드) + 바디(전시데이터) 분리
    // ══════════════════════════════════════════════════════════════

    /// <summary>레거시 NB 그램 스니펫 헤더 (DB INSERT용 메타데이터)</summary>
    public sealed class LegacyNbGrmHeader
    {
        public string GramType { get; set; } = "";       // "L"=LOFAR, "V"=Vernier, "D"=Demon
        public int DetYear { get; set; }
        public int DetMonth { get; set; }
        public int DetDay { get; set; }
        public long StartTimeMs { get; set; }
        public long EndTimeMs { get; set; }
        public float StartFreq { get; set; }
        public float EndFreq { get; set; }
        public float Resolution { get; set; }
        public int FrameNum { get; set; }
        public int BinWidth { get; set; }
        public float IntTime { get; set; }
        public float UpdateTime { get; set; }
        public string Comment { get; set; } = "";
    }

    /// <summary>레거시 AI 그램 스니펫 헤더 (DB INSERT용 메타데이터)</summary>
    public sealed class LegacyAiGrmHeader
    {
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public float StartFreq { get; set; }
        public float EndFreq { get; set; }
        public int BinWidth { get; set; }
        public int FrameNum { get; set; }
        public float Resolution { get; set; }
        public string Comment { get; set; } = "";
        public string AudioSnippetFileName { get; set; } = "";
        public int EventID { get; set; }
        public string DetSonar { get; set; } = "";
    }

    /// <summary>변환된 그램 데이터 (전시/저장용)</summary>
    public sealed class ConvertedGramData
    {
        public int FrameCount { get; set; }
        public int BinCount { get; set; }          // 원본 bin 수 그대로 (801/1601/가변)
        public float StartFreq { get; set; }
        public float EndFreq { get; set; }

        /// <summary>신규 체계 float[frameCount × binCount] — 역스케일링된 dB 진폭값</summary>
        public float[] FloatBuffer { get; set; } = Array.Empty<float>();

        /// <summary>전시 검증용 short[frameCount × binCount] — 원본 BYTE 값 보존</summary>
        public short[] ShortBuffer { get; set; } = Array.Empty<short>();

        /// <summary>ALI 데이터 (LOFAR만, 나머지 null)</summary>
        public byte[]? AliData { get; set; }
    }

    /// <summary>헤더리스 raw short[] 그램 결과 (Lofar/Demon/Event)</summary>
    public sealed class RawHeaderlessGrmData
    {
        public int FrameCount { get; set; }
        public int BinCount { get; set; }           // 801 (QUICK_BIN_SIZE)
        public float[] FloatBuffer { get; set; } = Array.Empty<float>();   // 역스케일링된 dB
        public short[] ShortBuffer { get; set; } = Array.Empty<short>();   // 원본 short 보존
        public string GramSubType { get; set; } = "";  // "Lofar" / "Demon" / "Event"
    }

    /// <summary>신규 체계 .grm 데이터 (L0/D0/V1~V4 — float[] 원본)</summary>
    public sealed class NewFormatGrmData
    {
        public string Header { get; set; } = "";        // "L0", "D0", "V1"~"V4", "S0", "A0"
        public int FrameCount { get; set; }
        public int BinCount { get; set; }
        public double BinResolution { get; set; }
        public double StartFreq { get; set; }
        public double EndFreq { get; set; }
        public float[] FloatBuffer { get; set; } = Array.Empty<float>();  // 원본 float (스케일링 전)
        public bool HasAli { get; set; }
    }

    /// <summary>파싱 결과 전체</summary>
    public sealed class LegacyGrmParseResult
    {
        public enum GrmFileType { NbSnippet, AiSnippet, RawHeaderless, NewFormat, Unknown }

        public GrmFileType FileType { get; set; }
        public string FilePath { get; set; } = "";

        // 헤더 (DB INSERT용) — FileType에 따라 하나만 채워짐
        public LegacyNbGrmHeader? NbHeader { get; set; }
        public LegacyAiGrmHeader? AiHeader { get; set; }

        // 변환된 데이터 — 스니펫 포맷 (NbSnippet/AiSnippet)
        public ConvertedGramData? Data { get; set; }

        // 변환된 데이터 — 헤더리스 포맷 (RawHeaderless: Lofar/Demon/Event)
        public RawHeaderlessGrmData? RawData { get; set; }

        // 변환된 데이터 — 신규 체계 포맷 (L0/D0/V1~V4/S0/A0)
        public NewFormatGrmData? NewData { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>소스 폴더 힌트 — .grm이 어느 분석 단계의 결과인지</summary>
    public enum GrmSourceHint
    {
        Auto,           // 자동 판별 (기존 로직)
        RawAnalysis,    // 수집원음자료/협대역/ → 원음분석 (NB_SINGLE, 801bin, TIMESTAMP 없음)
        PrecAnalysis,   // 협대역원음자료/*.nb/ 또는 단주기/*.sd/ → 정밀분석 (NB_PREC, 1601bin)
        Snippet,        // Snippets/ 하위 → 스니펫 (bin 가변)
    }

    /// <summary>
    /// 레거시 .grm 파일 읽기 + 변환.
    /// NB 스니펫과 AI 스니펫 두 포맷을 자동 판별한다.
    /// </summary>
    public static class GrmFileReader
    {
        private static readonly Encoding Cp949 = Encoding.GetEncoding(949);

        /// <summary>
        /// .grm 파일을 읽고, 헤더(DB필드)와 변환된 바디(전시데이터)를 반환한다.
        /// sourceHint가 Auto이면 기존 레거시 포맷만 시도.
        /// RawAnalysis/PrecAnalysis/Snippet이면 신규 포맷도 시도.
        /// </summary>
        public static LegacyGrmParseResult Read(string filePath, GrmSourceHint hint = GrmSourceHint.Auto)
        {
            var result = new LegacyGrmParseResult { FilePath = filePath };
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 10)
                {
                    result.Error = $"파일이 너무 작음: {bytes.Length}B";
                    result.FileType = LegacyGrmParseResult.GrmFileType.Unknown;
                    return result;
                }

                // 파일명으로 헤더리스 raw short[] 포맷 판별 (레거시 수집원음 _Lofar/_Demon/_Event)
                var fileName = Path.GetFileName(filePath);
                if (IsRawHeaderlessGrm(fileName))
                {
                    ParseRawHeaderless(bytes, fileName, result);
                    return result;
                }

                // 힌트가 Auto가 아니면 → 신규 체계 .grm 포맷 시도
                if (hint != GrmSourceHint.Auto)
                {
                    if (TryParseAsNewFormat(bytes, hint, result))
                        return result;
                }

                // 첫 2바이트가 신규 포맷 헤더인지 확인 (Auto에서도 fallback)
                if (bytes.Length >= 2)
                {
                    string header2 = Encoding.ASCII.GetString(bytes, 0, 2);
                    if (IsNewFormatHeader(header2))
                    {
                        // 경로에서 힌트 자동 추론
                        var autoHint = InferHintFromPath(filePath);
                        if (TryParseAsNewFormat(bytes, autoHint, result))
                            return result;
                    }
                }

                // 레거시 NB 스니펫 / AI 스니펫 시도
                if (TryParseAsNbSnippet(bytes, result))
                    return result;

                if (TryParseAsAiSnippet(bytes, result))
                    return result;

                result.Error = "지원하지 않는 .grm 포맷";
                result.FileType = LegacyGrmParseResult.GrmFileType.Unknown;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        private static bool IsNewFormatHeader(string h)
        {
            if (h.Length < 2) return false;
            char c0 = h[0], c1 = h[1];
            // L + (공백/0~9) = LOFAR
            // D + (공백/0~9) = DEMON
            // V + (1~4) = VERNIER
            // S + (공백/0) = Spectrogram
            // A + (공백/0) = AR gram
            // B + 0 = Beam, E + 0 = Echo
            return (c0 == 'L' || c0 == 'D' || c0 == 'V' || c0 == 'S' || c0 == 'A' || c0 == 'B' || c0 == 'E')
                && (c1 == ' ' || c1 == '0' || c1 == '1' || c1 == '2' || c1 == '3' || c1 == '4'
                    || c1 == '5' || c1 == '6' || c1 == '7' || c1 == '8' || c1 == '9');
        }

        private static GrmSourceHint InferHintFromPath(string path)
        {
            var lower = path.Replace('\\', '/').ToLowerInvariant();
            if (lower.Contains("snippet")) return GrmSourceHint.Snippet;
            if (lower.Contains("수집원음") || lower.Contains("협대역/")) return GrmSourceHint.RawAnalysis;
            if (lower.Contains("협대역원음") || lower.Contains("단주기원음") || lower.Contains(".nb/") || lower.Contains(".sd/"))
                return GrmSourceHint.PrecAnalysis;
            return GrmSourceHint.PrecAnalysis; // 기본은 정밀분석
        }

        // ── NB 스니펫 파싱 ───────────────────────────────────────────

        private static bool TryParseAsNbSnippet(byte[] bytes, LegacyGrmParseResult result)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms, Cp949);

                // cGram[3] — 전시형태
                var cGramBytes = br.ReadBytes(3);
                string cGram = Cp949.GetString(cGramBytes).TrimEnd('\0');

                if (cGram.Length == 0 || !"LVD".Contains(cGram[0]))
                    return false;

                // YYMMDD_Type (4 bytes — 비트필드)
                var yymmddBytes = br.ReadBytes(4);
                // day:5, month:4, year:7, spare1:8, spare2:8
                int raw = yymmddBytes[0] | (yymmddBytes[1] << 8);
                int day = raw & 0x1F;
                int month = (raw >> 5) & 0x0F;
                int year = (raw >> 9) & 0x7F;
                // year 오프셋: 보통 2000+
                if (year < 80) year += 2000; else year += 1900;

                var hdr = new LegacyNbGrmHeader
                {
                    GramType = cGram.Substring(0, 1),
                    DetYear = year,
                    DetMonth = month,
                    DetDay = day,
                    StartTimeMs = br.ReadInt32(),
                    EndTimeMs = br.ReadInt32(),
                    StartFreq = br.ReadSingle(),
                    EndFreq = br.ReadSingle(),
                    Resolution = br.ReadSingle(),
                    FrameNum = br.ReadInt32(),
                    BinWidth = br.ReadInt32(),
                    IntTime = br.ReadSingle(),
                    UpdateTime = br.ReadSingle(),
                    Comment = ReadFixedString(br, 81),
                };

                // 유효성 검증
                if (hdr.FrameNum < 0 || hdr.FrameNum > 10000) return false;
                if (hdr.BinWidth < 1 || hdr.BinWidth > 10000) return false;
                if (hdr.StartFreq < 0 || hdr.EndFreq < 0) return false;

                int bodySize = hdr.FrameNum * hdr.BinWidth;
                if (ms.Position + bodySize > bytes.Length) return false;

                // 바디: BYTE[frame×bin]
                byte[] bodyBytes = br.ReadBytes(bodySize);

                // ALI (LOFAR만)
                byte[]? aliData = null;
                if (hdr.GramType == "L" && ms.Position + hdr.BinWidth <= bytes.Length)
                    aliData = br.ReadBytes(hdr.BinWidth);

                // 변환
                var data = ConvertBody(bodyBytes, hdr.FrameNum, hdr.BinWidth,
                    hdr.StartFreq, hdr.EndFreq, aliData);

                result.FileType = LegacyGrmParseResult.GrmFileType.NbSnippet;
                result.NbHeader = hdr;
                result.Data = data;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── AI 스니펫 파싱 ───────────────────────────────────────────

        private static bool TryParseAsAiSnippet(byte[] bytes, LegacyGrmParseResult result)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms, Cp949);

                var hdr = new LegacyAiGrmHeader
                {
                    StartTime = br.ReadInt64(),
                    EndTime = br.ReadInt64(),
                    StartFreq = br.ReadSingle(),
                    EndFreq = br.ReadSingle(),
                    BinWidth = br.ReadInt32(),
                    FrameNum = br.ReadInt32(),
                    Resolution = br.ReadSingle(),
                    Comment = ReadFixedString(br, 81),
                    AudioSnippetFileName = ReadFixedString(br, 81),
                    EventID = br.ReadInt32(),
                    DetSonar = ReadFixedString(br, 11),
                };

                // 유효성 검증
                if (hdr.FrameNum < 0 || hdr.FrameNum > 10000) return false;
                if (hdr.BinWidth < 1 || hdr.BinWidth > 10000) return false;
                if (hdr.StartFreq < 0 || hdr.EndFreq < 0) return false;

                int bodySize = hdr.FrameNum * hdr.BinWidth;
                if (ms.Position + bodySize > bytes.Length) return false;

                byte[] bodyBytes = br.ReadBytes(bodySize);

                var data = ConvertBody(bodyBytes, hdr.FrameNum, hdr.BinWidth,
                    hdr.StartFreq, hdr.EndFreq, null);

                result.FileType = LegacyGrmParseResult.GrmFileType.AiSnippet;
                result.AiHeader = hdr;
                result.Data = data;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── 레거시 스케일링 상수 ─────────────────────────────────────
        // ModelPreciseAnalysis.cpp / AASBaseDefine.h에서 확인:
        //   #define AScale 150
        //   #define BScale 40
        // Forward: scaled = log10_dB_amplitude × AScale + BScale → clamp(0,255)
        // Reverse: log10_dB = (scaled - BScale) / AScale
        private const float DefaultAScale = 150.0f;
        private const float DefaultBScale = 40.0f;

        // ── 바디 변환 (공통) ─────────────────────────────────────────

        /// <summary>
        /// 레거시 BYTE[frame×bin] → 신규 float[] amplitude 변환.
        ///
        /// 레거시 저장 파이프라인 (Forward):
        ///   1. FFT → magnitude(float)
        ///   2. log10(magnitude) → log10값
        ///   3. × 20 - fft_offset_dB → dB값
        ///   4. × AScale(150) + BScale(40) → scaled값
        ///   5. clamp(0, 255) → BYTE 저장
        ///
        /// 역변환 (Reverse):
        ///   1. BYTE → float
        ///   2. (value - BScale) / AScale → log10_dB (스케일링 복원)
        ///   이것이 신규 체계 float[] 포맷에 저장될 값
        /// </summary>
        private static ConvertedGramData ConvertBody(byte[] bodyBytes, int frameNum, int binWidth,
            float startFreq, float endFreq, byte[]? aliData,
            float aScale = DefaultAScale, float bScale = DefaultBScale)
        {
            var result = new ConvertedGramData
            {
                FrameCount = frameNum,
                BinCount = binWidth,
                StartFreq = startFreq,
                EndFreq = endFreq,
                AliData = aliData,
            };

            int totalSamples = frameNum * binWidth;

            // 1) BYTE → float: 역스케일링 적용 (AScale/BScale 복원)
            //    레거시: scaled = dB × AScale + BScale → clamp(0,255)
            //    역산:   dB = (scaled - BScale) / AScale
            //    이 dB 값이 신규 체계의 float[] amplitude에 해당
            float[] floatBuf = new float[totalSamples];
            for (int i = 0; i < bodyBytes.Length && i < totalSamples; i++)
            {
                float scaled = bodyBytes[i]; // 0~255
                float dBValue = (scaled - bScale) / aScale;
                floatBuf[i] = dBValue;
            }
            result.FloatBuffer = floatBuf;

            // 2) 전시 검증용 short[]: 원본 BYTE 값 그대로 보존
            short[] shortBuf = new short[totalSamples];
            for (int i = 0; i < bodyBytes.Length && i < totalSamples; i++)
                shortBuf[i] = bodyBytes[i];
            result.ShortBuffer = shortBuf;

            // binWidth는 원본 그대로 보존 (801/1601/가변 — 리샘플링 불필요)
            // startFreq/endFreq와 함께 저장하면 전시 측에서 처리

            return result;
        }

        // ── 신규 체계 .grm 파싱 (L0/D0/V1~V4/S0/A0) ──────────────

        /// <summary>
        /// 신규 체계 .grm 파싱.
        /// 포맷: [2B 헤더] + [NbGram 48B: TIMESTAMP(16)+FrameCount(8)+BinRes(8)+StartFreq(8)+EndFreq(8)]
        ///       + [FrameBlock × FrameCount]
        ///
        /// FrameBlock 구조는 헤더 타입 + 분석 단계에 따라 다름:
        ///   NB_SINGLE L0/D0 (원음분석): float[binCount] (TIMESTAMP 없음)
        ///   NB_PREC L0 (정밀 LOFAR): TIMESTAMP(16) + short[binCount] ALI + float[binCount] Amp
        ///   NB_PREC V/D (정밀 Vernier/Demon): TIMESTAMP(16) + float[binCount] Amp
        ///   Snippet: 위와 동일하되 binCount가 가변
        /// </summary>
        /// <summary>
        /// 신규 체계 .grm 파싱.
        /// 실제 파일 포맷 (실측 확인):
        ///   [2B 헤더: "L ", "D ", "D1"~"D3", "V1"~"V4" 등]
        ///   [int32 FrameCount (4B)]
        ///   [double BinResolution (8B)]
        ///   [double StartFreq (8B)]
        ///   [double EndFreq (8B)]
        ///   = 30B 헤더
        ///   [데이터: 프레임 × 프레임 바이트]
        ///
        /// 파일명이 헤더 역할 — 바이너리 헤더의 2B는 참고용.
        /// binCount = round((EndFreq - StartFreq) / BinResolution) + 1 로 역산.
        /// 프레임 구조는 남은 바이트에서 자동 판별.
        /// </summary>
        private static bool TryParseAsNewFormat(byte[] bytes, GrmSourceHint hint, LegacyGrmParseResult result)
        {
            try
            {
                if (bytes.Length < 30) return false;

                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms);

                // 2B 헤더 (참고용)
                string header = Encoding.ASCII.GetString(br.ReadBytes(2));

                // int32 FrameCount (4B)
                int frameCount = br.ReadInt32();
                if (frameCount <= 0 || frameCount > 100000) return false;

                // double BinResolution (8B)
                double binRes = br.ReadDouble();
                if (binRes <= 0 || double.IsNaN(binRes) || double.IsInfinity(binRes)) return false;

                // double StartFreq, EndFreq (각 8B)
                double startFreq = br.ReadDouble();
                double endFreq = br.ReadDouble();
                if (startFreq < 0 || endFreq < 0 || endFreq < startFreq) return false;

                // 30B 헤더 완료, 나머지 = 데이터
                long remaining = bytes.Length - 30;

                // bin 수 역산 (힌트2)
                int binCount = (int)Math.Round((endFreq - startFreq) / binRes) + 1;
                if (binCount <= 0 || binCount > 10000) return false;

                // ── 프레임 구조 자동 판별 (레거시 WriteData 포맷 기반) ──
                // 레거시 .grm 저장 포맷 (ModelPreciseAnalysis::WriteData):
                //   LOFAR (nIndex=0):   SYSTEMTIME(16) + BYTE[bin] ALI + short[bin] Gram
                //   VERNIER (nIndex=1~4): SYSTEMTIME(16) + short[bin] Gram + (파일 끝에 BYTE[bin] ALI 1블록)
                //   DEMON (nIndex=5+):   SYSTEMTIME(16) + short[bin] Gram
                // 파일명으로 타입 구분 (사용자 지시: "파일명이 헤더 역할")

                string headerTrimmed = header.Trim();
                bool isLofar = headerTrimmed.StartsWith("L");
                bool isVernier = headerTrimmed.StartsWith("V");

                // 프레임당 바이트 계산
                int frameBytesCalc = 16; // SYSTEMTIME 항상 있음
                if (isLofar) frameBytesCalc += binCount; // ALI BYTE[bin] (프레임별)
                frameBytesCalc += binCount * sizeof(short); // Gram short[bin]

                int tailBytes = isVernier ? binCount : 0; // VERNIER: 끝에 ALI 1블록

                long expectedTotal = (long)frameCount * frameBytesCalc + tailBytes;
                if (remaining < expectedTotal)
                {
                    // 크기 불일치 → 신규 체계 float[] 포맷일 수 있음 (NB_SINGLE 등)
                    // 신규 float[] 시도: TIMESTAMP 없이 float[bin] only
                    int floatFrameSize = binCount * sizeof(float);
                    if (remaining >= (long)frameCount * floatFrameSize)
                    {
                        ms.Position = 30;
                        int totalSamples2 = frameCount * binCount;
                        float[] floatBuf2 = new float[totalSamples2];
                        for (int f2 = 0; f2 < frameCount; f2++)
                        {
                            byte[] ampBytes2 = br.ReadBytes(floatFrameSize);
                            if (ampBytes2.Length < floatFrameSize) break;
                            Buffer.BlockCopy(ampBytes2, 0, floatBuf2, f2 * binCount * sizeof(float), ampBytes2.Length);
                        }
                        result.FileType = LegacyGrmParseResult.GrmFileType.NewFormat;
                        result.NewData = new NewFormatGrmData
                        {
                            Header = headerTrimmed, FrameCount = frameCount, BinCount = binCount,
                            BinResolution = binRes, StartFreq = startFreq, EndFreq = endFreq,
                            FloatBuffer = floatBuf2, HasAli = false,
                        };
                        return true;
                    }
                    return false;
                }

                // ── 레거시 short[] 데이터 읽기 + 역스케일링 ──
                ms.Position = 30;
                int totalSamples = frameCount * binCount;
                float[] floatBuf = new float[totalSamples];
                short[] shortBuf = new short[totalSamples];

                for (int f = 0; f < frameCount; f++)
                {
                    br.ReadBytes(16); // SYSTEMTIME 소비
                    if (isLofar) br.ReadBytes(binCount); // ALI BYTE[bin] 소비

                    // short[bin] Gram 읽기
                    byte[] gramBytes = br.ReadBytes(binCount * sizeof(short));
                    if (gramBytes.Length < binCount * sizeof(short)) break;

                    Buffer.BlockCopy(gramBytes, 0, shortBuf, f * binCount * sizeof(short), gramBytes.Length);

                    // 역스케일링: (short & 0xFF - BScale) / AScale → float dB
                    for (int b = 0; b < binCount; b++)
                    {
                        int idx = f * binCount + b;
                        byte val = (byte)(shortBuf[idx] & 0x00FF);
                        floatBuf[idx] = (val - DefaultBScale) / DefaultAScale;
                    }
                }

                // VERNIER 꼬리 ALI 소비 (파싱 결과에는 포함 안 함)
                if (isVernier && ms.Position + binCount <= bytes.Length)
                    br.ReadBytes(binCount);

                // 레거시 short[] 데이터 → ConvertedGramData에도 저장 (비교 뷰용)
                result.FileType = LegacyGrmParseResult.GrmFileType.NewFormat;
                result.NewData = new NewFormatGrmData
                {
                    Header = headerTrimmed,
                    FrameCount = frameCount,
                    BinCount = binCount,
                    BinResolution = binRes,
                    StartFreq = startFreq,
                    EndFreq = endFreq,
                    FloatBuffer = floatBuf,
                    HasAli = isLofar,
                };
                // ConvertedGramData에도 저장 (레거시 short[] + 역스케일링 float[])
                result.Data = new ConvertedGramData
                {
                    FrameCount = frameCount,
                    BinCount = binCount,
                    StartFreq = (float)startFreq,
                    EndFreq = (float)endFreq,
                    FloatBuffer = floatBuf,
                    ShortBuffer = shortBuf,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── 헤더리스 raw short[] 파싱 (Lofar/Demon/Event) ──────────

        private const int QuickBinSize = 801;

        private static bool IsRawHeaderlessGrm(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            return lower.Contains("_lofar.grm") ||
                   lower.Contains("_demon.grm") ||
                   lower.Contains("_event.grm");
        }

        private static string DetectGramSubType(string fileName)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.Contains("_lofar")) return "Lofar";
            if (lower.Contains("_demon")) return "Demon";
            if (lower.Contains("_event")) return "Event";
            return "Unknown";
        }

        private static void ParseRawHeaderless(byte[] bytes, string fileName, LegacyGrmParseResult result)
        {
            var subType = DetectGramSubType(fileName);
            result.FileType = LegacyGrmParseResult.GrmFileType.RawHeaderless;

            if (subType == "Event")
            {
                // _Event.grm: byte[maxFrameSize] — 1바이트/프레임, bin 개념 없음
                var rawData = new RawHeaderlessGrmData
                {
                    GramSubType = subType,
                    FrameCount = bytes.Length,
                    BinCount = 1,
                };
                // 역스케일링: byte → float dB
                rawData.FloatBuffer = new float[bytes.Length];
                rawData.ShortBuffer = new short[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    rawData.ShortBuffer[i] = bytes[i];
                    rawData.FloatBuffer[i] = (bytes[i] - DefaultBScale) / DefaultAScale;
                }
                result.RawData = rawData;
                return;
            }

            // _Lofar.grm / _Demon.grm: short[frameCount × 801] — 헤더 없음
            if (bytes.Length % (QuickBinSize * sizeof(short)) != 0)
            {
                result.Error = $"파일 크기가 short[N×801]과 맞지 않음: {bytes.Length}B";
                return;
            }

            int frameCount = bytes.Length / (QuickBinSize * sizeof(short));
            int totalSamples = frameCount * QuickBinSize;

            // byte[] → short[]
            short[] shortBuf = new short[totalSamples];
            Buffer.BlockCopy(bytes, 0, shortBuf, 0, bytes.Length);

            // short → float dB (역스케일링: short & 0xFF → byte → (byte - BScale) / AScale)
            float[] floatBuf = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++)
            {
                byte val = (byte)(shortBuf[i] & 0x00FF);
                floatBuf[i] = (val - DefaultBScale) / DefaultAScale;
            }

            result.RawData = new RawHeaderlessGrmData
            {
                GramSubType = subType,
                FrameCount = frameCount,
                BinCount = QuickBinSize,
                FloatBuffer = floatBuf,
                ShortBuffer = shortBuf,
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  신규 DB 포맷 바이너리 생성 (VARBINARY(MAX) FILESTREAM용)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 파싱된 레거시 .grm 결과를 신규 체계 DB 저장 포맷으로 변환한다.
        ///
        /// NB_SINGLE_GRM (수집원음 Lofar/Demon) DB 포맷:
        ///   [48B 헤더] TIMESTAMP(16) + int64 frameCount(8) + double binRes(8)
        ///             + double startFreq(8) + double endFreq(8)
        ///   [데이터]   float[frameCount × binCount]  (프레임별 TIMESTAMP 없음)
        ///
        /// NB_PREC_GRM L0 (정밀 LOFAR) DB 포맷:
        ///   [48B 헤더] 동일
        ///   [데이터]   프레임별: TIMESTAMP(16) + short[1601] ALI + float[1601] Amp
        ///
        /// NB_PREC_GRM V/D (정밀 Vernier/Demon) DB 포맷:
        ///   [48B 헤더] 동일
        ///   [데이터]   프레임별: TIMESTAMP(16) + float[1601] Amp
        /// </summary>
        public static byte[] ToNewDbFormat(LegacyGrmParseResult parsed, DateTime? timestamp = null)
        {
            var ts = timestamp ?? DateTime.Now;

            if (parsed.FileType == LegacyGrmParseResult.GrmFileType.RawHeaderless && parsed.RawData != null)
                return BuildNbSingleGram(parsed.RawData, ts);

            if (parsed.Data != null)
                return BuildNbSingleGram(parsed.Data, ts);

            return Array.Empty<byte>();
        }

        /// <summary>NB_SINGLE_GRM 포맷: 48B 헤더 + float[frame×bin] (TIMESTAMP 없는 FrameBlock)</summary>
        private static byte[] BuildNbSingleGram(RawHeaderlessGrmData raw, DateTime ts)
        {
            int headerSize = 48;
            int dataBytes = raw.FloatBuffer.Length * sizeof(float);
            byte[] output = new byte[headerSize + dataBytes];

            int pos = 0;
            // TIMESTAMP (16B)
            WriteTimestamp(output, ref pos, ts);
            // int64 frameCount (8B)
            WriteInt64(output, ref pos, raw.FrameCount);
            // double binResolution (8B) — 레거시에서는 freq 범위 / bin 수로 추정
            double binRes = raw.BinCount > 1 ? 1.0 : 0.0; // 실제값은 WAV 헤더에서 계산 필요
            WriteDouble(output, ref pos, binRes);
            // double startFreq (8B)
            WriteDouble(output, ref pos, 0.0); // 레거시 헤더리스에는 freq 정보 없음 — WAV에서 채워야 함
            // double endFreq (8B)
            WriteDouble(output, ref pos, 0.0);

            // float[] 데이터
            Buffer.BlockCopy(raw.FloatBuffer, 0, output, headerSize, dataBytes);

            return output;
        }

        /// <summary>NB_SINGLE_GRM 포맷: 스니펫용 (헤더에서 freq 정보 사용 가능)</summary>
        private static byte[] BuildNbSingleGram(ConvertedGramData data, DateTime ts)
        {
            int headerSize = 48;
            int dataBytes = data.FloatBuffer.Length * sizeof(float);
            byte[] output = new byte[headerSize + dataBytes];

            int pos = 0;
            WriteTimestamp(output, ref pos, ts);
            WriteInt64(output, ref pos, data.FrameCount);
            double binRes = data.BinCount > 1
                ? (data.EndFreq - data.StartFreq) / (data.BinCount - 1)
                : 0.0;
            WriteDouble(output, ref pos, binRes);
            WriteDouble(output, ref pos, data.StartFreq);
            WriteDouble(output, ref pos, data.EndFreq);

            Buffer.BlockCopy(data.FloatBuffer, 0, output, headerSize, dataBytes);

            return output;
        }

        /// <summary>레거시 전시용 바이트 배열 생성 (Green 채널 BGRA32)</summary>
        public static byte[] ToLegacyBgra32(short[] shortBuf, int width, int height)
        {
            int pixelCount = width * height;
            byte[] pixels = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount && i < shortBuf.Length; i++)
            {
                byte val = (byte)(shortBuf[i] & 0x00FF);
                pixels[i * 4 + 0] = 0;     // Blue
                pixels[i * 4 + 1] = val;   // Green
                pixels[i * 4 + 2] = 0;     // Red
                pixels[i * 4 + 3] = 255;   // Alpha
            }
            return pixels;
        }

        /// <summary>신규 전시용 바이트 배열 생성 (float→×150+40→clamp→Green BGRA32)</summary>
        public static byte[] ToNewBgra32(float[] floatBuf, int width, int height,
            float aScale = DefaultAScale, float bScale = DefaultBScale)
        {
            int pixelCount = width * height;
            byte[] pixels = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount && i < floatBuf.Length; i++)
            {
                float scaled = floatBuf[i] * aScale + bScale;
                byte val = (byte)Math.Clamp((int)Math.Round(scaled), 0, 255);
                pixels[i * 4 + 0] = 0;
                pixels[i * 4 + 1] = val;
                pixels[i * 4 + 2] = 0;
                pixels[i * 4 + 3] = 255;
            }
            return pixels;
        }

        // ── 바이너리 쓰기 헬퍼 ───────────────────────────────────────

        private static void WriteTimestamp(byte[] buf, ref int pos, DateTime dt)
        {
            WriteInt16(buf, ref pos, (short)dt.Year);
            WriteInt16(buf, ref pos, (short)dt.Month);
            WriteInt16(buf, ref pos, (short)dt.DayOfWeek);
            WriteInt16(buf, ref pos, (short)dt.Day);
            WriteInt16(buf, ref pos, (short)dt.Hour);
            WriteInt16(buf, ref pos, (short)dt.Minute);
            WriteInt16(buf, ref pos, (short)dt.Second);
            WriteInt16(buf, ref pos, (short)dt.Millisecond);
        }

        private static void WriteInt16(byte[] buf, ref int pos, short val)
        {
            buf[pos] = (byte)(val & 0xFF);
            buf[pos + 1] = (byte)((val >> 8) & 0xFF);
            pos += 2;
        }

        private static void WriteInt64(byte[] buf, ref int pos, long val)
        {
            for (int i = 0; i < 8; i++)
                buf[pos + i] = (byte)((val >> (i * 8)) & 0xFF);
            pos += 8;
        }

        private static void WriteDouble(byte[] buf, ref int pos, double val)
        {
            byte[] tmp = BitConverter.GetBytes(val);
            Buffer.BlockCopy(tmp, 0, buf, pos, 8);
            pos += 8;
        }

        // ── 유틸 ─────────────────────────────────────────────────────

        private static string ReadFixedString(BinaryReader br, int maxLen)
        {
            var bytes = br.ReadBytes(maxLen);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Cp949.GetString(bytes, 0, end).Trim();
        }
    }
}
