using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:미리보기]
    /// <summary>
    /// 바이트·파일을 사람이 볼 수 있는 그림으로 바꾼다.
    ///
    /// 이관 검증에서 숫자만으로는 확인되지 않는 것이 있다 — blob 이 "몇 바이트 들어갔다" 는
    /// 형식이 맞다는 뜻일 뿐, <b>그 안이 온전한 그램인지</b>는 그려 봐야 안다.
    /// 실제로 이 프로젝트에서 AI 그램 태그(A0/S0)와 NB 순수 float 규약을 잘못 잡았던 적이 있는데,
    /// 둘 다 INSERT 는 멀쩡히 성공했고 길이도 그럴듯했다.
    ///
    /// 그래서 소스와 타겟을 <b>각자의 규약대로</b> 따로 읽어 나란히 그린다.
    /// 소스는 레거시 파싱(.grm 128B/216B 헤더, 원음 WAV), 타겟은 R8 파싱(66B 헤더 또는 순수 float).
    /// 같은 그림이 나오면 규약이 맞은 것이고, 다르면 어디가 틀렸는지 눈으로 보인다.
    /// </summary>
    public static class PreviewRenderer
    {
        /// <summary>전시 결과 한 장.</summary>
        /// <param name="Image">그려진 그림(실패면 null).</param>
        /// <param name="Caption">무엇을 어떻게 읽었는지 — 판단 근거가 되므로 반드시 남긴다.</param>
        public sealed record Panel(ImageSource? Image, string Caption, string? Error = null);

        // ── 이미지 (국기·영상) ──────────────────────────────────────────

        /// <summary>PNG/JPEG 등 이미지 바이트를 그대로 디코드한다.</summary>
        public static Panel FromImageBytes(byte[]? bytes, string what)
        {
            if (bytes == null || bytes.Length == 0)
                return new Panel(null, $"{what}: 비어 있음 (0 바이트)");
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return new Panel(bmp, $"{what}: {Describe(bytes)} · {bmp.PixelWidth}×{bmp.PixelHeight}");
            }
            catch (Exception ex)
            {
                return new Panel(null, $"{what}: {Describe(bytes)}", "이미지로 디코드할 수 없습니다 — " + ex.Message);
            }
        }

        public static Panel FromImageFile(string? path, string what)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Panel(null, $"{what}: 파일 없음", path is { Length: > 0 } ? path : null);
            try { return FromImageBytes(File.ReadAllBytes(path), what); }
            catch (Exception ex) { return new Panel(null, $"{what}: 읽기 실패", ex.Message); }
        }

        // ── 그램 ────────────────────────────────────────────────────────

        /// <summary>
        /// 타겟 blob 을 그램으로 읽는다. 규약을 <b>추측하지 않고 순서대로 시도</b>하고,
        /// 무엇으로 읽혔는지 캡션에 남긴다 — 이게 곧 "규약이 맞았나" 의 답이다.
        ///   ① 선두 2B 가 ASCII 태그(A0/S0/L0/D0/V1~4) → R8 66B
        ///   ② 길이가 4의 배수 → 순수 float[] (NB 스니펫 규약)
        /// </summary>
        /// <param name="hint">
        /// blob 에서 못 읽는 축 값을 <b>DB 행에서</b> 받아 온다.
        /// NB 협대역 그램은 순수 float(헤더 없음) 이라 blob 만으로는 주파수도 시각도 알 수 없다 —
        /// 그렇다고 축을 번호로 두면 정작 대조해야 할 값이 화면에 없다.
        /// 그 값들은 STD_*_SNIPPET 행의 FREQ_MIN/FREQ_MAX·SNIPPET_START/END_DATETIME 에 있다.
        /// </param>
        public static Panel FromTargetGramBlob(byte[]? blob, int binHint, string what, GramAxes? hint = null)
        {
            if (blob == null || blob.Length == 0)
                return new Panel(null, $"{what}: 비어 있음 (0 바이트)");

            if (blob.Length >= GramHeader66.TotalSize && IsAsciiTag(blob[0], blob[1]))
            {
                string tag = $"{(char)blob[0]}{(char)blob[1]}";
                var h = GramHeader66.TryRead(blob);
                if (h != null)
                {
                    int bins = BinsFromR8(blob, h.FrameCount);
                    if (bins > 0 && h.FrameCount > 0)
                    {
                        var amp = ReadR8Frames(blob, h.FrameCount, bins);
                        // 헤더가 정본이다. 헤더에 없는 값만 힌트로 메운다.
                        var axes = new GramAxes(
                            h.StartFreq, h.EndFreq,
                            h.HasValidTimeRange ? h.StartTime : hint?.TimeStart,
                            h.HasValidTimeRange ? h.EndTime : hint?.TimeEnd);
                        var dom = DomainOfR8Tag(tag);
                        return Draw(amp, bins, h.FrameCount,
                            $"{what}: R8 66B '{tag}' · {h.FrameCount}프레임 × {bins}빈 · " +
                            RangeText(axes) + DomainText(dom), axes, dom);
                    }
                    return new Panel(null, $"{what}: R8 66B '{tag}' · 헤더는 읽혔으나 프레임 수·빈 수가 비정상",
                                     $"FrameCount={h.FrameCount}, 본문 {blob.Length - GramHeader66.TotalSize}B");
                }
            }

            if (blob.Length % sizeof(float) == 0)
            {
                int n = blob.Length / sizeof(float);
                var amp = new float[n];
                Buffer.BlockCopy(blob, 0, amp, 0, blob.Length);
                int bins = binHint > 0 ? binHint : 1601;
                int frames = Math.Max(1, n / Math.Max(1, bins));
                // 헤더가 없으므로 축 값은 전적으로 DB 행(hint)에서 온다. 힌트도 없으면 번호로 찍는다.
                // 태그가 없으니 값 영역을 단정할 근거가 없다 — 변환 없이 그리고 그 사실을 캡션에 남긴다.
                return Draw(amp, bins, frames,
                    $"{what}: 순수 float[] (헤더 없음) · {n:N0}값 → {frames}프레임 × {bins}빈" +
                    (hint != null ? " · " + RangeText(hint) : " · 축 값은 DB 행에서 가져옵니다") +
                    DomainText(GramAmplitudeDomain.Unknown),
                    hint, GramAmplitudeDomain.Unknown);
            }

            return new Panel(null, $"{what}: {Describe(blob)}",
                "R8 태그도 아니고 길이가 4의 배수도 아닙니다 — 알 수 없는 형식입니다.");
        }

        /// <summary>소스 레거시 .grm 을 그 파서(128B/216B 헤더)로 읽어 그린다.</summary>
        public static Panel FromLegacyGramFile(string? path, string what)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Panel(null, $"{what}: 파일 없음", path);
            try
            {
                var g = LegacyGrmReader.Read(path);
                if (!g.Ok) return new Panel(null, $"{what}: 파싱 실패", g.Error);
                if (g.Amplitude.Length == 0 || g.BinCount <= 0)
                    return new Panel(null, $"{what}: {g.KindText} · 진폭 없음");
                // 레거시 헤더의 시각은 <자정 이후 밀리초> 다. 날짜는 헤더의 DetDate 에 따로 있다.
                // 둘을 합쳐야 절대 시각이 된다 — 날짜가 없으면 시:분:초만 뜻이 있으므로 오늘 날짜에 얹는다.
                DateTime? gs = null, ge = null;
                if (g.EndTimeMs > g.StartTimeMs)
                {
                    var day = (g.DetDate ?? DateTime.Today).Date;
                    gs = day.AddMilliseconds(g.StartTimeMs);
                    ge = day.AddMilliseconds(g.EndTimeMs);
                }
                var gax = new GramAxes(g.StartFreq, g.EndFreq, gs, ge);
                // LegacyGrmReader 가 종류별로 다르게 역산해 둔다 — NB=선형, AI=상대 dB.
                var dom = g.Kind switch
                {
                    LegacyGrmKind.NbSnippet => GramAmplitudeDomain.Linear,
                    LegacyGrmKind.AiSnippet => GramAmplitudeDomain.Db,
                    LegacyGrmKind.R8        => DomainOfR8Tag(g.GramTag),
                    _ => GramAmplitudeDomain.Unknown,   // RawFloat: 값 영역 미확정
                };
                return Draw(g.Amplitude, g.BinCount, g.FrameCount,
                    $"{what}: {g.KindText} · {g.FrameCount}프레임 × {g.BinCount}빈 · " +
                    RangeText(gax) + $" · 태그 '{g.GramTag}'" + DomainText(dom), gax, dom);
            }
            catch (Exception ex) { return new Panel(null, $"{what}: 읽기 실패", ex.Message); }
        }

        // ── 시계열(원음 파형) ───────────────────────────────────────────

        public static Panel FromWaveFile(string? path, string what, DateTime? startTime = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Panel(null, $"{what}: 파일 없음", path);
            try { return FromWaveBytes(File.ReadAllBytes(path), what, startTime); }
            catch (Exception ex) { return new Panel(null, $"{what}: 읽기 실패", ex.Message); }
        }

        /// <summary>
        /// WAV 바이트에서 PCM 을 뽑아 파형을 그린다.
        /// 레거시 원음은 BWF(bext + fmt + NDID 512B + data) 라 청크를 순회해 data 를 찾는다 —
        /// 헤더 길이를 44 로 가정하면 NDID 512B 를 신호로 읽어 파형이 앞부분부터 망가진다.
        /// </summary>
        public static Panel FromWaveBytes(byte[]? bytes, string what, DateTime? startTime = null)
        {
            if (bytes == null || bytes.Length == 0)
                return new Panel(null, $"{what}: 비어 있음 (0 바이트)");
            try
            {
                if (!TryFindWaveData(bytes, out int dataOff, out int dataLen, out int bits, out int ch, out int rate))
                    return new Panel(null, $"{what}: {Describe(bytes)}", "WAV 청크 구조를 찾을 수 없습니다.");

                int step = Math.Max(1, bits / 8 * Math.Max(1, ch));
                int samples = dataLen / step;
                if (samples <= 0) return new Panel(null, $"{what}: 신호 데이터 없음");

                // 폭은 그램과 맞춘다 — 나란히 놓고 같은 시각을 짚어야 하므로 축 길이가 같아야 한다.
                // (900 은 패널보다 넓어 끝이 잘려 "wav 보다 짧게" 보였다. 실제로는 스크롤 밖이었다.)
                const int W = 640, H = 200;
                var px = NewCanvas(W, H, out int stride);
                // 가운데 0선
                for (int x = 0; x < W; x++) SetPx(px, stride, x, H / 2, 60, 60, 75);

                int per = Math.Max(1, samples / W);
                for (int x = 0; x < W; x++)
                {
                    float lo = 1f, hi = -1f;
                    int s0 = x * per, s1 = Math.Min(samples, s0 + per);
                    for (int s = s0; s < s1; s++)
                    {
                        float v = ReadSample(bytes, dataOff + s * step, bits);
                        if (v < lo) lo = v; if (v > hi) hi = v;
                    }
                    if (lo > hi) continue;
                    int y0 = (int)((1 - hi) * 0.5f * (H - 1));
                    int y1 = (int)((1 - lo) * 0.5f * (H - 1));
                    for (int y = Math.Max(0, y0); y <= Math.Min(H - 1, y1); y++)
                        SetPx(px, stride, x, y, 90, 200, 255);
                }
                float sec = rate > 0 ? (float)samples / rate : 0;
                // 파형도 그램 바로 옆에 놓이므로 같은 눈금 규약을 쓴다 — 가로=시간(초).
                var wave = WithWaveAxes(ToBitmap(px, W, H, stride), W, H, sec, startTime);
                return new Panel(wave,
                    $"{what}: WAV {rate:N0}Hz · {bits}bit · {ch}ch · {samples:N0}샘플 ({sec:0.00}초)");
            }
            catch (Exception ex) { return new Panel(null, $"{what}: 파형 생성 실패", ex.Message); }
        }

        // ── 공통 ────────────────────────────────────────────────────────

        /// <summary>
        /// 저장된 진폭 값이 어느 영역인지. 전시 전에 로그를 걸어야 하는지가 여기서 갈린다.
        /// 정본 근거는 LegacyGrmReader 클래스 주석(AAS·CDMU 교차확인) 참조.
        /// </summary>
        public enum GramAmplitudeDomain
        {
            /// <summary>판단 근거 없음 — 손대지 않고 그대로 그린다.</summary>
            Unknown,
            /// <summary>선형 진폭(NB L0/D0/V*, S0). 전시 전 log10 을 건다.</summary>
            Linear,
            /// <summary>이미 로그/ dB 영역(AI A0). 그대로 그린다.</summary>
            Db
        }

        /// <summary>R8 2바이트 태그로 값 영역을 정한다.</summary>
        private static GramAmplitudeDomain DomainOfR8Tag(string tag) => tag switch
        {
            "A0" => GramAmplitudeDomain.Db,       // AR 그램 — 상대 dB
            "L0" or "D0" or "S0" => GramAmplitudeDomain.Linear,
            _ => tag.Length == 2 && tag[0] == 'V' ? GramAmplitudeDomain.Linear
                                                  : GramAmplitudeDomain.Unknown
        };

        /// <summary>선형 저장분에 log10 을 걸어 전시 영역으로 옮긴다. 0 이하·비정상 값은 건드리지 않는다.</summary>
        private static float[] ToDisplayDomain(float[] amp, GramAmplitudeDomain domain)
        {
            if (domain != GramAmplitudeDomain.Linear) return amp;

            var outv = new float[amp.Length];
            for (int i = 0; i < amp.Length; i++)
            {
                float v = amp[i];
                outv[i] = v > 0f && float.IsFinite(v) ? (float)Math.Log10(v) : float.NaN;
            }
            return outv;
        }

        /// <summary>캡션 꼬리에 붙일 값 영역 표기 — 화면이 무엇을 그린 것인지 남긴다.</summary>
        private static string DomainText(GramAmplitudeDomain d) => d switch
        {
            GramAmplitudeDomain.Linear => " · 선형 저장분에 log10 적용",
            GramAmplitudeDomain.Db     => " · dB 저장분 그대로",
            _ => " · 값 영역 미상 — 변환 없이 표시"
        };

        /// <summary>진폭 배열(frame-major)을 그램 이미지로 그린다. 값 범위는 실제 분포로 정규화한다.</summary>
        // [찾기:그램전시] [NAIMS-ISSUE:GRAM-AXES]
        //   그램은 <b>가로=주파수 · 세로=시간</b> 으로 그린다. 축을 바꿔 그리면 토널선(세로줄)이
        //   가로줄로 보여서, 맞게 이관된 그램도 "깨진 것" 처럼 보인다.
        //   정본 두 곳이 같은 규약이다:
        //     · AAS(C++) Library_Gram/GramToBmp.cpp — 플롯 폭 = iBinWidth, 높이 = iFrameCount,
        //       X축 눈금 iXPos = (fFreq - fStartFreq) / fBinResol  → 좌측이 시작 주파수.
        //     · 현 솔루션 DigitalTwin/Controls/SignalAnalysis/GramControl.Rendering.cs
        //       TransposeAxes 분기 — "X=주파수(너비), Y=시간(높이) / 시간0=하단(↑증가)".
        //   시간 방향도 정본을 따른다 — 프레임 0 이 <b>아래</b>, 위로 갈수록 나중이다.
        //
        //   축만큼 중요한 두 가지를 함께 옮겨 왔다. 이게 없으면 축을 고쳐도 화면은 여전히 잡음이다:
        //     ① 축소는 <b>MAX 집약</b>(피크 보존). 점추출하면 1592빈을 900px 로 줄이는 순간
        //        토널선이 통째로 사라진다 — 이 창은 "선이 있나" 를 보는 창이므로 치명적이다.
        //     ② 대비는 <b>시간줄별 mean~max 자동대비</b>. 전체 min~max 로 정규화하면
        //        한 프레임의 큰 값 하나가 나머지를 전부 바닥으로 눌러 균일한 얼룩이 된다.
        private static Panel Draw(float[] amp, int bins, int frames, string caption,
                                  GramAxes? axes = null,
                                  GramAmplitudeDomain domain = GramAmplitudeDomain.Unknown)
        {
            if (bins <= 0 || frames <= 0) return new Panel(null, caption, "프레임/빈 수가 0 입니다.");
            if ((long)bins * frames > amp.Length) frames = amp.Length / bins;
            if (frames <= 0) return new Panel(null, caption, "진폭 배열이 빈 수보다 짧습니다.");

            // 저장 영역 → 전시 영역. 아래 자동대비는 (m−mean)/(max−mean) 이라 <b>아핀 변환에는 불변</b>이지만
            // 로그↔선형은 아핀이 아니다 — 선형값을 그대로 넣으면 큰 값이 한 줄을 지배해 저역이 뭉갠다.
            // NAIMS-II 도 선형 저장분을 전시할 때 log10 을 먼저 건다(그래서 여기서도 먼저 건다).
            // ×A+B 는 자동대비에서 상쇄되므로 붙이지 않는다.
            amp = ToDisplayDomain(amp, domain);

            // 출력 크기 — 없는 해상도를 지어내지 않는다(확대는 WPF 가 NearestNeighbor 로).
            int W = Math.Min(900, bins);     // 가로 = 주파수
            int H = Math.Min(420, frames);   // 세로 = 시간
            if (W <= 0 || H <= 0) return new Panel(null, caption, "표시 크기를 정할 수 없습니다.");

            // 시간줄(프레임)별 평균·최대 — 자동 대비 기준.
            var lineMean = new float[frames];
            var lineMax = new float[frames];
            float lo = float.MaxValue, hi = float.MinValue;
            for (int f = 0; f < frames; f++)
            {
                int off = f * bins;
                double sum = 0; float mx = float.NegativeInfinity; int n = 0;
                for (int b = 0; b < bins; b++)
                {
                    float v = amp[off + b];
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    sum += v; n++;
                    if (v > mx) mx = v;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                lineMean[f] = n > 0 ? (float)(sum / n) : 0f;
                lineMax[f] = float.IsNegativeInfinity(mx) ? 0f : mx;
            }
            if (lo > hi) { lo = 0; hi = 1; }

            var px = NewCanvas(W, H, out int stride);
            for (int oy = 0; oy < H; oy++)
            {
                int f0 = (int)((long)oy * frames / H);
                int f1 = (int)((long)(oy + 1) * frames / H);
                if (f1 <= f0) f1 = f0 + 1;

                float aMean = 0f, aMax = float.NegativeInfinity;
                for (int f = f0; f < f1 && f < frames; f++)
                {
                    aMean += lineMean[f];
                    if (lineMax[f] > aMax) aMax = lineMax[f];
                }
                aMean /= Math.Max(1, Math.Min(f1, frames) - f0);

                int y = H - 1 - oy;   // 프레임 0 이 아래
                for (int ox = 0; ox < W; ox++)
                {
                    int b0 = (int)((long)ox * bins / W);
                    int b1 = (int)((long)(ox + 1) * bins / W);
                    if (b1 <= b0) b1 = b0 + 1;

                    float m = float.NegativeInfinity;          // 피크 보존
                    for (int f = f0; f < f1 && f < frames; f++)
                    {
                        int rb = f * bins;
                        for (int b = b0; b < b1 && b < bins; b++)
                        {
                            float v = amp[rb + b];
                            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                            if (v > m) m = v;
                        }
                    }
                    float denom = aMax - aMean;
                    float t = float.IsNegativeInfinity(m) || denom <= 1e-12f
                        ? 0f : Math.Clamp((m - aMean) / denom, 0f, 1f);

                    var (r, gg, bl) = Heat(t);
                    SetPx(px, stride, ox, y, r, gg, bl);
                }
            }

            var heat = ToBitmap(px, W, H, stride);
            var framed = WithAxes(heat, W, H, bins, frames, axes);
            return new Panel(framed,
                $"{caption} · 가로=주파수 {bins:N0}빈 · 세로=시간 {frames:N0}프레임(아래가 처음) · 값 {lo:0.##}~{hi:0.##}");
        }

        // ══════════ 눈금(틱) ══════════
        // [찾기:그램전시]
        // [찾기:그램전시]
        /// <summary>
        /// 그램의 축 실값. 모르는 축은 null 로 두고 <b>번호</b>(빈·프레임)로 표기한다 — 지어내지 않는다.
        ///
        /// 시간은 <b>절대 시각</b>이다. 전시 규약이 그렇다 — 그램에는 시작시각과 끝시각이 찍히고,
        /// 주파수축에는 시작주파수와 끝주파수가 찍힌다. "0초부터 138초" 같은 상대값은 그램 한 장
        /// 안에서만 뜻이 통해서, 다른 자료와 맞대 보거나 원본 화면과 대조할 수가 없다.
        /// </summary>
        /// <param name="FreqStart">좌측 끝 주파수(Hz). null 이면 빈 번호로 표기.</param>
        /// <param name="FreqEnd">우측 끝 주파수(Hz).</param>
        /// <param name="TimeStart">아래쪽 끝(첫 프레임) 시각. null 이면 프레임 번호로 표기.</param>
        /// <param name="TimeEnd">위쪽 끝(마지막 프레임) 시각.</param>
        public sealed record GramAxes(
            double? FreqStart, double? FreqEnd, DateTime? TimeStart = null, DateTime? TimeEnd = null)
        {
            public bool HasFreq => FreqStart is { } a && FreqEnd is { } b && b > a;
            public bool HasTime => TimeStart is { } a && TimeEnd is { } b && b > a;
            public double SpanSec => HasTime ? (TimeEnd!.Value - TimeStart!.Value).TotalSeconds : 0;

            /// <summary>축 눈금 글자 형식 — 범위가 짧을수록 잘게 적는다.</summary>
            public string TimeFormat => SpanSec >= 3600 ? "HH:mm"
                                      : SpanSec >= 10 ? "HH:mm:ss"
                                      : "HH:mm:ss.f";
        }

        // 절대 시각("12:34:56")이 들어가므로 왼쪽 여백을 넉넉히 둔다.
        private const int AxLeft = 68, AxBottom = 30, AxTop = 8, AxRight = 12;

        /// <summary>
        /// 히트맵 둘레에 눈금과 값을 그린다.
        ///
        /// 눈금 규칙은 정본(AAS Library_Gram/GramToBmp.cpp)을 그대로 따른다:
        ///   · 가로(주파수) — <b>큰 눈금 50px 마다 값 표기, 작은 눈금 5px</b>
        ///     (원본: step = 5 * 10 → 큰 눈금 / step = 5 → 작은 눈금, 값은 그 위치의 실제 주파수)
        ///     원본은 1px = 1빈 이라 50px = 50빈 이었다. 여기서는 축소해 그릴 수 있으므로
        ///     같은 <b>화면 간격</b>을 유지하고 값은 그 픽셀의 데이터 좌표에서 구한다 —
        ///     눈금이 촘촘해 보이거나 성겨 보이지 않게 하려는 것이 원본 규칙의 뜻이기 때문이다.
        ///   · 세로(시간) — 아래가 처음, 위로 갈수록 나중(현 솔루션 GramControl 규약).
        ///     원본 AAS 는 장시간 녹음이라 HH:MM 을 찍지만, 스니펫은 몇 초짜리라 전부 같은 값이 된다.
        ///     그래서 <b>시작으로부터의 초</b>로 찍는다. 길이를 모르면 프레임 번호로 찍는다.
        /// </summary>
        private static ImageSource WithAxes(ImageSource heat, int W, int H, int bins, int frames, GramAxes? ax)
        {
            try
            {
                // [NAIMS-ISSUE:GRAM-TINY] 셀 수가 적은 그램은 <정수배로 확대>해 그린다.
                //   AI 스펙트로그램은 5프레임 × 801빈 짜리라 1:1 로 그리면 세로 5px —
                //   눈금을 붙여 놔도 그림이 실선 한 줄로 보여 아무것도 확인할 수 없었다.
                //   보간은 하지 않는다(NearestNeighbor). 셀 하나가 사각형 하나로 커질 뿐이라
                //   없는 해상도가 생기지 않는다 — 현 솔루션 GramControl 의 업스케일 규약과 같다.
                int zx = Fit(W, 360, 1200), zy = Fit(H, 150, 420);
                int PW = W * zx, PH = H * zy;

                int totalW = AxLeft + PW + AxRight, totalH = AxTop + PH + AxBottom;
                var typeface = new Typeface("Segoe UI");
                var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)), 1);
                var axis = new Pen(new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xdc)), 1);
                tick.Freeze(); axis.Freeze();
                var label = new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xdc)); label.Freeze();
                var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1a)); bg.Freeze();

                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, totalH));
                    dc.DrawImage(heat, new Rect(AxLeft, AxTop, PW, PH));

                    double x0 = AxLeft, y1 = AxTop + PH;         // 플롯 좌하단
                    dc.DrawLine(axis, new Point(x0, y1 + 2), new Point(x0 + PW, y1 + 2));  // X 축선
                    dc.DrawLine(axis, new Point(x0 - 2, AxTop), new Point(x0 - 2, y1));    // Y 축선

                    // ── 가로: 주파수 ──
                    for (int x = 0; x <= PW; x += 5)                                 // 작은 눈금 5px
                        dc.DrawLine(tick, new Point(x0 + x, y1 + 2), new Point(x0 + x, y1 + 5));

                    bool haveFreq = ax?.HasFreq == true;
                    for (int x = 0; x <= PW; x += 50)                                // 큰 눈금 50px + 값
                    {
                        dc.DrawLine(tick, new Point(x0 + x, y1 + 2), new Point(x0 + x, y1 + 8));
                        double t = PW > 0 ? (double)x / PW : 0;
                        string text = haveFreq
                            ? FormatHz(ax!.FreqStart!.Value + (ax.FreqEnd!.Value - ax.FreqStart.Value) * t)
                            : ((int)(t * bins)).ToString();
                        var ft = Text(text, typeface, label);
                        double tx = Math.Min(totalW - ft.Width - 1, Math.Max(0, x0 + x - ft.Width / 2));
                        dc.DrawText(ft, new Point(tx, y1 + 9));
                    }
                    dc.DrawText(Text(haveFreq ? "Hz" : "빈", typeface, label, 9),
                                new Point(totalW - 22, y1 + 9));

                    // ── 세로: 시간(아래가 처음) — 시작시각 ~ 끝시각 ──
                    bool haveTime = ax?.HasTime == true;
                    string tfmt = ax?.TimeFormat ?? "HH:mm:ss";
                    int step = NiceStepPx(PH, 40);
                    for (int y = 0; y <= PH; y += step)
                    {
                        double py = y1 - y;                                          // 아래에서 위로
                        dc.DrawLine(tick, new Point(x0 - 6, py), new Point(x0 - 2, py));
                        double t = PH > 0 ? (double)y / PH : 0;
                        string text = haveTime
                            ? ax!.TimeStart!.Value.AddSeconds(ax.SpanSec * t).ToString(tfmt)
                            : ((int)(t * frames)).ToString();
                        var ft = Text(text, typeface, label, haveTime ? 9.5 : 10);
                        dc.DrawText(ft, new Point(Math.Max(0, x0 - 8 - ft.Width), py - ft.Height / 2));
                    }
                    var unit = Text(haveTime ? "시각" : "프레임", typeface, label, 9);
                    dc.DrawText(unit, new Point(2, AxTop - 1));
                }

                var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch
            {
                return heat;   // 눈금은 곁들이는 것이다 — 실패했다고 그램을 못 보게 만들지 않는다
            }
        }

        /// <summary>길이 n 을 최소 min 이상으로 키우는 정수 배율(최대 max 를 넘지 않는다). 축소는 하지 않는다.</summary>
        private static int Fit(int n, int min, int max)
        {
            if (n <= 0) return 1;
            int z = Math.Max(1, (int)Math.Ceiling((double)min / n));
            while (z > 1 && n * z > max) z--;
            return Math.Max(1, z);
        }

        private static FormattedText Text(string s, Typeface tf, Brush b, double size = 10) =>
            new(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, tf, size, b, 96);

        /// <summary>축 길이 H 안에서 목표 간격(px)에 가장 가까운 정수 간격.</summary>
        private static int NiceStepPx(int H, int target)
        {
            if (H <= target) return Math.Max(1, H);
            int n = Math.Max(1, (int)Math.Round((double)H / target));
            return Math.Max(1, H / n);
        }

        /// <summary>축 범위를 캡션에 적는다 — 눈금을 못 읽는 상황에서도 값이 남아야 한다.</summary>
        private static string RangeText(GramAxes ax)
        {
            var parts = new List<string>();
            parts.Add(ax.HasFreq
                ? $"주파수 {FormatHz(ax.FreqStart!.Value)}~{FormatHz(ax.FreqEnd!.Value)}Hz"
                : "주파수 미상(빈 번호로 표기)");
            parts.Add(ax.HasTime
                ? $"시각 {ax.TimeStart:HH:mm:ss.fff}~{ax.TimeEnd:HH:mm:ss.fff}"
                : "시각 미상(프레임 번호로 표기)");
            return string.Join(" · ", parts);
        }

        private static string FormatHz(double hz) =>
            Math.Abs(hz) >= 1000 ? (hz / 1000).ToString("0.##") + "k"
            : Math.Abs(hz) >= 10 ? hz.ToString("0")
            : hz.ToString("0.##");

        /// <summary>파형에 가로=시간(초) · 세로=진폭(-1~1) 눈금을 두른다.</summary>
        /// <param name="startTime">
        /// 절대 시작 시각. WAV(BWF)에도 bext 시각이 있지만 레거시 파일마다 채워진 정도가 달라
        /// <b>DB 행의 시각</b>을 정본으로 쓴다 — 그램 축과 같은 값을 써야 둘을 겹쳐 볼 수 있다.
        /// 없으면 종전대로 0 부터의 초로 찍는다.
        /// </param>
        private static ImageSource WithWaveAxes(ImageSource wave, int W, int H, double durationSec,
                                                DateTime? startTime = null)
        {
            try
            {
                int totalW = AxLeft + W + AxRight, totalH = AxTop + H + AxBottom;
                var typeface = new Typeface("Segoe UI");
                var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xb0)), 1);
                var axis = new Pen(new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xdc)), 1);
                tick.Freeze(); axis.Freeze();
                var label = new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xdc)); label.Freeze();
                var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1a)); bg.Freeze();

                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, totalH));
                    dc.DrawImage(wave, new Rect(AxLeft, AxTop, W, H));

                    double x0 = AxLeft, y1 = AxTop + H;
                    dc.DrawLine(axis, new Point(x0, y1 + 2), new Point(x0 + W, y1 + 2));
                    dc.DrawLine(axis, new Point(x0 - 2, AxTop), new Point(x0 - 2, y1));

                    for (int x = 0; x <= W; x += 5)
                        dc.DrawLine(tick, new Point(x0 + x, y1 + 2), new Point(x0 + x, y1 + 5));
                    bool haveSec = durationSec > 0;
                    bool haveAbs = haveSec && startTime is { } st0 && st0 != default;
                    string wfmt = durationSec >= 3600 ? "HH:mm" : durationSec >= 10 ? "HH:mm:ss" : "HH:mm:ss.f";
                    for (int x = 0; x <= W; x += 50)
                    {
                        dc.DrawLine(tick, new Point(x0 + x, y1 + 2), new Point(x0 + x, y1 + 8));
                        double t = W > 0 ? (double)x / W : 0;
                        string txt = haveAbs ? startTime!.Value.AddSeconds(durationSec * t).ToString(wfmt)
                                   : haveSec ? FormatSec(durationSec * t)
                                   : ((int)(t * W)).ToString();
                        var ft = Text(txt, typeface, label, haveAbs ? 9.0 : 10.0);
                        double tx = Math.Min(totalW - ft.Width - 1, Math.Max(0, x0 + x - ft.Width / 2));
                        dc.DrawText(ft, new Point(tx, y1 + 9));
                    }
                    dc.DrawText(Text(haveAbs ? "시각" : haveSec ? "초" : "px", typeface, label, 9), new Point(totalW - 26, y1 + 9));

                    // 진폭은 -1 / 0 / +1 세 칸이면 충분하다 — 더 찍어도 읽을 것이 없다.
                    foreach (var (v, y) in new[] { (1.0, AxTop), (0.0, AxTop + H / 2.0), (-1.0, (double)y1) })
                    {
                        dc.DrawLine(tick, new Point(x0 - 6, y), new Point(x0 - 2, y));
                        var ft = Text(v.ToString("+0;-0;0"), typeface, label);
                        dc.DrawText(ft, new Point(Math.Max(0, x0 - 8 - ft.Width), y - ft.Height / 2));
                    }
                }

                var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return wave; }
        }

        private static string FormatSec(double sec) =>
            sec >= 100 ? sec.ToString("0")
            : sec >= 10 ? sec.ToString("0.#")
            : sec.ToString("0.##");

        private static readonly float[][] ParulaLut = new float[][]
        {
        new[]{0.2081f,0.1663f,0.5292f}, new[]{0.2116f,0.1898f,0.5777f},
        new[]{0.2123f,0.2138f,0.6270f}, new[]{0.2081f,0.2386f,0.6771f},
        new[]{0.1959f,0.2645f,0.7279f}, new[]{0.1707f,0.2919f,0.7792f},
        new[]{0.1253f,0.3242f,0.8303f}, new[]{0.0591f,0.3598f,0.8683f},
        new[]{0.0117f,0.3875f,0.8820f}, new[]{0.0060f,0.4086f,0.8828f},
        new[]{0.0165f,0.4266f,0.8786f}, new[]{0.0329f,0.4430f,0.8720f},
        new[]{0.0498f,0.4586f,0.8641f}, new[]{0.0629f,0.4737f,0.8554f},
        new[]{0.0723f,0.4887f,0.8467f}, new[]{0.0779f,0.5040f,0.8384f},
        new[]{0.0793f,0.5200f,0.8312f}, new[]{0.0749f,0.5375f,0.8263f},
        new[]{0.0641f,0.5570f,0.8240f}, new[]{0.0488f,0.5772f,0.8228f},
        new[]{0.0343f,0.5966f,0.8199f}, new[]{0.0265f,0.6137f,0.8135f},
        new[]{0.0239f,0.6287f,0.8038f}, new[]{0.0231f,0.6418f,0.7913f},
        new[]{0.0228f,0.6535f,0.7768f}, new[]{0.0267f,0.6642f,0.7607f},
        new[]{0.0384f,0.6743f,0.7436f}, new[]{0.0590f,0.6838f,0.7254f},
        new[]{0.0843f,0.6928f,0.7062f}, new[]{0.1133f,0.7015f,0.6859f},
        new[]{0.1453f,0.7098f,0.6646f}, new[]{0.1801f,0.7177f,0.6424f},
        new[]{0.2178f,0.7250f,0.6193f}, new[]{0.2586f,0.7317f,0.5954f},
        new[]{0.3022f,0.7376f,0.5712f}, new[]{0.3482f,0.7424f,0.5473f},
        new[]{0.3953f,0.7459f,0.5244f}, new[]{0.4420f,0.7481f,0.5033f},
        new[]{0.4871f,0.7491f,0.4840f}, new[]{0.5300f,0.7491f,0.4661f},
        new[]{0.5709f,0.7485f,0.4494f}, new[]{0.6099f,0.7473f,0.4337f},
        new[]{0.6473f,0.7456f,0.4188f}, new[]{0.6834f,0.7435f,0.4044f},
        new[]{0.7184f,0.7411f,0.3905f}, new[]{0.7525f,0.7384f,0.3768f},
        new[]{0.7858f,0.7356f,0.3633f}, new[]{0.8185f,0.7327f,0.3498f},
        new[]{0.8507f,0.7299f,0.3360f}, new[]{0.8824f,0.7274f,0.3217f},
        new[]{0.9139f,0.7258f,0.3063f}, new[]{0.9450f,0.7261f,0.2886f},
        new[]{0.9739f,0.7314f,0.2666f}, new[]{0.9938f,0.7455f,0.2403f},
        new[]{0.9990f,0.7653f,0.2164f}, new[]{0.9955f,0.7861f,0.1967f},
        new[]{0.9880f,0.8066f,0.1794f}, new[]{0.9789f,0.8271f,0.1633f},
        new[]{0.9697f,0.8481f,0.1475f}, new[]{0.9626f,0.8705f,0.1309f},
        new[]{0.9589f,0.8949f,0.1132f}, new[]{0.9598f,0.9218f,0.0948f},
        new[]{0.9661f,0.9514f,0.0755f}, new[]{0.9763f,0.9831f,0.0538f},
        };

        // 현 솔루션 기본 컬러맵(Parula) — 화면에서 본 색과 이 창의 색이 달라 보이면
        // 사람은 "다른 자료"라고 의심하게 된다. 색까지 정본을 따른다.
        private static (byte r, byte g, byte b) Heat(float t)
        {
            float idx = Math.Clamp(t, 0f, 1f) * 63f;
            int i0 = (int)idx;
            if (i0 >= 63)
            {
                var c63 = ParulaLut[63];
                return (B(c63[0]), B(c63[1]), B(c63[2]));
            }
            if (i0 < 0) i0 = 0;
            float frac = idx - i0;
            float r = ParulaLut[i0][0] + (ParulaLut[i0 + 1][0] - ParulaLut[i0][0]) * frac;
            float g = ParulaLut[i0][1] + (ParulaLut[i0 + 1][1] - ParulaLut[i0][1]) * frac;
            float b = ParulaLut[i0][2] + (ParulaLut[i0 + 1][2] - ParulaLut[i0][2]) * frac;
            return (B(r), B(g), B(b));
        }

        private static byte B(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

        private static byte[] NewCanvas(int w, int h, out int stride)
        {
            stride = w * 4;
            var px = new byte[stride * h];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 20; px[i + 1] = 20; px[i + 2] = 26; px[i + 3] = 255; }
            return px;
        }

        private static void SetPx(byte[] px, int stride, int x, int y, byte r, byte g, byte b)
        {
            int o = y * stride + x * 4;
            if (o < 0 || o + 3 >= px.Length) return;
            px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
        }

        private static BitmapSource ToBitmap(byte[] px, int w, int h, int stride)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        private static bool IsAsciiTag(byte a, byte b) =>
            a is >= (byte)'A' and <= (byte)'Z' && (b is >= (byte)'0' and <= (byte)'9' || b is >= (byte)'A' and <= (byte)'Z');

        /// <summary>R8 본문에서 빈 수를 역산한다: 본문 = (TS16 + float[bin]) × frame.</summary>
        private static int BinsFromR8(byte[] blob, int frames)
        {
            if (frames <= 0) return 0;
            long body = blob.Length - GramHeader66.TotalSize;
            long perFrame = body / frames;
            long bins = (perFrame - GramHeader66.TimestampSize) / sizeof(float);
            return bins > 0 && bins < 100000 ? (int)bins : 0;
        }

        private static float[] ReadR8Frames(byte[] blob, int frames, int bins)
        {
            var amp = new float[(long)frames * bins];
            int pos = GramHeader66.TotalSize;
            int frameBytes = GramHeader66.TimestampSize + bins * sizeof(float);
            for (int f = 0; f < frames; f++)
            {
                int src = pos + f * frameBytes + GramHeader66.TimestampSize;
                if (src + bins * sizeof(float) > blob.Length) break;
                Buffer.BlockCopy(blob, src, amp, f * bins * sizeof(float), bins * sizeof(float));
            }
            return amp;
        }

        /// <summary>RIFF 청크를 순회해 fmt/data 를 찾는다(bext·NDID 가 앞에 오는 BWF 대응).</summary>
        private static bool TryFindWaveData(byte[] b, out int dataOff, out int dataLen,
                                            out int bits, out int ch, out int rate)
        {
            dataOff = dataLen = 0; bits = 16; ch = 1; rate = 0;
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
            int pos = 12;
            bool haveFmt = false, haveData = false;
            while (pos + 8 <= b.Length)
            {
                string id = $"{(char)b[pos]}{(char)b[pos + 1]}{(char)b[pos + 2]}{(char)b[pos + 3]}";
                int sz = BitConverter.ToInt32(b, pos + 4);
                if (sz < 0 || pos + 8 + sz > b.Length) sz = b.Length - pos - 8;
                int body = pos + 8;
                if (id == "fmt " && sz >= 16)
                {
                    ch   = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                    haveFmt = true;
                }
                else if (id == "data") { dataOff = body; dataLen = sz; haveData = true; }
                pos = body + sz + (sz & 1);
                if (haveFmt && haveData) break;
            }
            if (ch <= 0) ch = 1;
            if (bits <= 0) bits = 16;
            return haveData;
        }

        private static float ReadSample(byte[] b, int off, int bits)
        {
            if (off < 0 || off + bits / 8 > b.Length) return 0;
            return bits switch
            {
                8  => (b[off] - 128) / 128f,
                16 => BitConverter.ToInt16(b, off) / 32768f,
                24 => ((b[off] | (b[off + 1] << 8) | ((sbyte)b[off + 2] << 16))) / 8388608f,
                32 => BitConverter.ToSingle(b, off),
                _  => 0,
            };
        }

        /// <summary>바이트 앞머리로 형식을 알아본다(캡션에 쓸 한 줄).</summary>
        public static string Describe(byte[] b)
        {
            string sig =
                b.Length >= 2 && b[0] == 0x89 && b[1] == 0x50 ? "PNG"
                : b.Length >= 2 && b[0] == 0xFF && b[1] == 0xD8 ? "JPEG"
                : b.Length >= 4 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' ? "RIFF/WAV"
                : b.Length >= 2 && IsAsciiTag(b[0], b[1]) ? $"R8 '{(char)b[0]}{(char)b[1]}'"
                : b.Length % 4 == 0 ? "float[] 가능"
                : "미상";
            return $"{sig} {b.Length:N0}B";
        }
    }
}
