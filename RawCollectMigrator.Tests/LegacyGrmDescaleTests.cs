using System;
using System.IO;
using System.Text;
using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 레거시 .grm 진폭 역산 회귀 시험.
    ///
    /// 왜 시험으로 못 박는가 — 스케일이 틀려도 <b>그램은 정상적으로 그려진다.</b>
    /// 밝기/대비만 달라지므로 눈으로는 잡히지 않고, 이관은 성공으로 끝난다.
    /// 게다가 A 는 지수 자리에 들어가므로(10^((BYTE−B)/A)) 값이 어긋나면
    /// 오차가 선형이 아니라 배수로 벌어진다.
    ///
    /// 정본:
    ///   저장 3단  — AAS ModelPreciseAnalysis.cpp:8908~ (log10 → ×A+B → clamp/round)
    ///   A/B 출처  — 같은 폴더 "정밀신호처리파라미터.set" 마지막 줄 (UtilFileManage.cpp:5564)
    ///   전시 규약  — NAIMS-II S0GramRenderer (intensity = 255 × log10(1 + amplitude))
    /// </summary>
    public class LegacyGrmDescaleTests : IDisposable
    {
        private readonly string _dir;

        public LegacyGrmDescaleTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "grmdescale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        // ── .set 조회 ────────────────────────────────────────────────

        [Fact]
        public void set_이_없으면_기본값_150_40()
        {
            var grm = Path.Combine(_dir, "LOFAR.grm");
            var sc = LegacyGrmReader.ReadScaleParams(grm);

            Assert.Equal(150f, sc.A);
            Assert.Equal(40f, sc.B);
        }

        /// <summary>
        /// 실자료 ETC_20100415105233_00_000_001.nb 의 마지막 줄이 이 형식·이 값이다.
        /// "기본값이라 신경 안 써도 된다"가 성립하지 않는다는 증거.
        /// </summary>
        [Fact]
        public void set_마지막줄에서_A_B_를_읽는다()
        {
            WriteSet("     250.0000000      40.0000000");

            var sc = LegacyGrmReader.ReadScaleParams(Path.Combine(_dir, "LOFAR.grm"));

            Assert.Equal(250f, sc.A);
            Assert.Equal(40f, sc.B);
            Assert.Contains("정밀신호처리파라미터.set", sc.Source, StringComparison.Ordinal);
        }

        /// <summary>레거시 자신의 폴백 규칙(ViewPropertyPAMNBSignalProcessing.cpp:2504)과 같아야 한다.</summary>
        [Fact]
        public void set_값이_0이하면_기본값으로_떨어진다()
        {
            WriteSet("       0.0000000       0.0000000");

            var sc = LegacyGrmReader.ReadScaleParams(Path.Combine(_dir, "LOFAR.grm"));

            Assert.Equal(150f, sc.A);
            Assert.Equal(40f, sc.B);
        }

        // ── 왕복 ────────────────────────────────────────────────────

        /// <summary>
        /// 선형 진폭 → 레거시 3단 정방향 → 역산이 원래 선형값으로 돌아와야 한다.
        /// 허용오차는 BYTE 양자화 한 칸(10^(1/A) 배) 이내.
        /// </summary>
        [Theory]
        [InlineData(150f, 40f)]
        [InlineData(250f, 40f)]   // 실자료에 존재하는 값
        public void NB_왕복_선형진폭이_보존된다(float a, float b)
        {
            WriteSet($"     {a:F7}      {b:F7}");

            // A/B 로 표현 가능한 선형 구간 안의 값들만 고른다(밖은 레거시가 이미 클램프해 버렸다).
            float lo = (float)Math.Pow(10.0, (0 - b) / a);
            float hi = (float)Math.Pow(10.0, (255 - b) / a);
            float[] expected = { lo * 1.2f, 1.0f, MathF.Sqrt(lo * hi), hi * 0.8f };

            var path = WriteNbGrm(expected, a, b);
            var g = LegacyGrmReader.Read(path);

            Assert.True(g.Ok, g.Error);
            Assert.Equal(LegacyGrmKind.NbSnippet, g.Kind);
            Assert.Equal(a, g.UsedAScale);
            Assert.Equal(b, g.UsedBScale);

            double tolerance = Math.Pow(10.0, 1.0 / a);   // 한 칸 = 배수
            for (int i = 0; i < expected.Length; i++)
            {
                double ratio = g.Amplitude[i] / expected[i];
                Assert.InRange(ratio, 1.0 / tolerance, tolerance);
            }
        }

        /// <summary>
        /// 같은 BYTE 라도 A 가 다르면 선형값이 <b>배수로</b> 달라진다.
        /// A 를 150 으로 못 박아두면 250 짜리 자료가 조용히 틀리는 지점.
        /// </summary>
        [Fact]
        public void A_가_다르면_역산_결과가_배수로_갈린다()
        {
            var body = new byte[] { 200, 200, 200, 200 };

            WriteSet("     150.0000000      40.0000000");
            var at150 = LegacyGrmReader.Read(WriteNbGrmRaw(body)).Amplitude[0];

            File.Delete(Path.Combine(_dir, "LOFAR.grm"));
            WriteSet("     250.0000000      40.0000000");
            var at250 = LegacyGrmReader.Read(WriteNbGrmRaw(body)).Amplitude[0];

            // 10^(160/150)=11.66  vs  10^(160/250)=4.37 — 2.7 배 차이
            Assert.Equal(Math.Pow(10, 160.0 / 150.0), at150, 2);
            Assert.Equal(Math.Pow(10, 160.0 / 250.0), at250, 2);
            Assert.True(at150 / at250 > 2.5f);
        }

        /// <summary>
        /// AI(AR 그램)는 NB 와 <b>다른 체계</b>다 — A/B 스케일도, 로그 되돌리기도 무관하다.
        ///
        /// 레거시 생성식(AAS SPUtil.cpp:98 · CDMU SPUtil.cpp:98, 두 체계 동일):
        ///   v = (상대dB + 12) / 18 × 255,  파일 기록 시 BYTE = ((v &gt;&gt; 5) &lt;&lt; 2) &amp; 0xFC
        /// 따라서 파일에는 {0,4,…,28} 8단계만 남는다(실파일 5건 확인).
        /// </summary>
        [Theory]
        [InlineData(0,  -10.906f)]   // 계단 0 → v≈15.5
        [InlineData(12,  -4.129f)]   // 계단 3 → v≈111.5
        [InlineData(28,   4.906f)]   // 계단 7 → v≈239.5
        public void AI_는_상대dB_로_역산된다(byte raw, float expectedDb)
        {
            var g = LegacyGrmReader.Read(WriteAiGrm(raw));

            Assert.Equal(LegacyGrmKind.AiSnippet, g.Kind);
            Assert.Equal(expectedDb, g.Amplitude[0], 3);

            // NAIMS-II ArGramRenderer 의 절대 매핑 (val+12)/18×255 에 넣었을 때
            // 8계단이 0~255 에 고르게 퍼져야 한다(전 영역 고휘도로 뭉치면 안 된다).
            int intensity = (int)Math.Round((g.Amplitude[0] + 12.0) / 18.0 * 255.0);
            int expectedCenter = (raw >> 2) * 32 + 16;   // 8계단이 0~255 를 32씩 균등 분할한 중앙
            Assert.InRange(intensity, expectedCenter - 1, expectedCenter + 1);
        }

        /// <summary>A/B 를 바꿔도 AI 결과는 흔들리지 않아야 한다(AI 는 .set 과 무관).</summary>
        [Fact]
        public void AI_는_set_의_A_B_에_영향받지_않는다()
        {
            WriteSet("     250.0000000      40.0000000");
            var withSet = LegacyGrmReader.Read(WriteAiGrm(20)).Amplitude[0];

            File.Delete(Path.Combine(_dir, "정밀신호처리파라미터.set"));
            var noSet = LegacyGrmReader.Read(WriteAiGrm(20)).Amplitude[0];

            Assert.Equal(withSet, noSet, 5);
        }

        // ── 도우미 ──────────────────────────────────────────────────

        private void WriteSet(string lastLine)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllText(
                Path.Combine(_dir, "정밀신호처리파라미터.set"),
                "      4096\n      1600\nKAISER-BESSEL\n" + lastLine + "\n",
                Encoding.GetEncoding(949));
        }

        /// <summary>본문이 모두 같은 BYTE 인 AI(216B 헤더) .grm 을 만든다.</summary>
        private string WriteAiGrm(byte raw)
        {
            const int frames = 2, bins = 3;
            var buf = new byte[216 + frames * bins];
            BitConverter.GetBytes(0f).CopyTo(buf, 16);      // StartFreq
            BitConverter.GetBytes(100f).CopyTo(buf, 20);    // EndFreq
            BitConverter.GetBytes(bins).CopyTo(buf, 24);    // BinWidth
            BitConverter.GetBytes(frames).CopyTo(buf, 28);  // FrameNum
            for (int i = 0; i < frames * bins; i++) buf[216 + i] = raw;

            var path = Path.Combine(_dir, "ai_1.grm");
            File.WriteAllBytes(path, buf);
            return path;
        }

        /// <summary>선형 진폭을 레거시 3단(log10 → ×A+B → clamp/round)으로 눌러 담은 NB .grm 을 만든다.</summary>
        private string WriteNbGrm(float[] linear, float a, float b)
        {
            var body = new byte[linear.Length];
            for (int i = 0; i < linear.Length; i++)
            {
                double scaled = Math.Log10(linear[i]) * a + b;
                body[i] = (byte)Math.Clamp((int)Math.Round(scaled), 0, 255);
            }
            return WriteNbGrmRaw(body);
        }

        private string WriteNbGrmRaw(byte[] body)
        {
            const int header = 128;
            var buf = new byte[header + body.Length];

            buf[0] = (byte)'L';                                  // 그램 태그
            BitConverter.GetBytes((ushort)0).CopyTo(buf, 4);     // 날짜 비트필드(미사용)
            BitConverter.GetBytes(0).CopyTo(buf, 8);             // StartTimeMs
            BitConverter.GetBytes(1000).CopyTo(buf, 12);         // EndTimeMs
            BitConverter.GetBytes(0f).CopyTo(buf, 16);           // StartFreq
            BitConverter.GetBytes(100f).CopyTo(buf, 20);         // EndFreq
            BitConverter.GetBytes(1f).CopyTo(buf, 24);           // Resolution
            BitConverter.GetBytes(1).CopyTo(buf, 28);            // FrameNum
            BitConverter.GetBytes(body.Length).CopyTo(buf, 32);  // BinWidth
            body.CopyTo(buf, header);

            var path = Path.Combine(_dir, "LOFAR.grm");
            File.WriteAllBytes(path, buf);
            return path;
        }
    }
}
