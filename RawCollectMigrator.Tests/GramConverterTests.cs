using System;
using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// R8 66B 그램 태그 회귀 시험.
    ///
    /// 왜 시험으로 못 박는가 — 태그가 틀려도 <b>아무도 오류를 내지 않는다.</b>
    /// 전시 측 ArGramCodec.TryDeserialize 는 선두 2B 가 "A0" 가 아니면 곧바로
    /// legacy AIGramSnippetHeader(213B) 경로로 넘어가 blob 을 "거부하지 않고 잘못 읽는다".
    /// 즉 이관은 성공으로 끝나고, 몇 달 뒤 화면에서 엉뚱한 그램을 보게 된다.
    /// 정본: 260128_svn/AIATUSW/Model/STFT/ArGramCodec.cs
    /// </summary>
    public class GramConverterTests
    {
        private static LegacyGrm Make(LegacyGrmKind kind, string tag = "") => new()
        {
            Kind = kind,
            GramTag = tag,
            FrameCount = 2,
            BinCount = 3,
            StartFreq = 0f,
            EndFreq = 100f,
            Resolution = 50f,
            Amplitude = new float[6],
        };

        [Fact]
        public void AiSnippet_태그는_A0_다()
        {
            // ai_*.grm 은 AR 그램. S0(STFT 스펙트로그램)과 서로 다른 자료다.
            Assert.Equal("A0", GramConverter.ToR8Tag(Make(LegacyGrmKind.AiSnippet)));
        }

        [Theory]
        [InlineData("L", "L0")]
        [InlineData("D", "D0")]
        [InlineData("V2", "V2")]
        [InlineData("V", "V1")]   // 대역 번호가 없으면 V1
        public void 수동계열_태그_매핑(string legacyTag, string expected)
        {
            Assert.Equal(expected, GramConverter.ToR8Tag(Make(LegacyGrmKind.NbSnippet, legacyTag)));
        }

        [Fact]
        public void RawFloat_는_L0()
        {
            Assert.Equal("L0", GramConverter.ToR8Tag(Make(LegacyGrmKind.RawFloat)));
        }

        [Fact]
        public void ForceTag_는_원본종류보다_우선한다()
        {
            // 타겟 컬럼(STD_AI_SNIPPET.GRAM_FILE)이 요구하는 규약이 원본 파일 종류를 이긴다.
            var r = GramConverter.ToR8(Make(LegacyGrmKind.NbSnippet, "L"), null, forceTag: "A0");
            Assert.True(r.Ok);
            Assert.Equal("A0", r.Tag);
            Assert.Equal((byte)'A', r.Data[0]);
            Assert.Equal((byte)'0', r.Data[1]);
        }

        [Fact]
        public void 출력은_66B_헤더_뒤에_프레임당_TS16과_float가_온다()
        {
            var g = Make(LegacyGrmKind.AiSnippet);
            var r = GramConverter.ToR8(g);
            Assert.True(r.Ok);
            Assert.Equal(66 + g.FrameCount * (16 + g.BinCount * sizeof(float)), r.Data.Length);
        }

        // ══════════ [NAIMS-ISSUE:NB-GRAM-R8] 협대역 스니펫 그램 바이트 규격 ══════════
        /// <summary>
        /// 협대역 스니펫 그램은 <b>R8 66B</b> 다 — 순수 float 이 아니다.
        ///
        /// 이 규격을 두 번 틀렸고 두 번 다 조용히 지나갔다. INSERT 는 성공하고 길이도 그럴듯해서,
        /// 화면을 열어 보기 전에는 아무 신호가 없다. 정본(260128_svn) 세 곳이 일치한다:
        ///   · Model/StdClassfDB/NBSnippet.cs        — 66B 먼저 시도, 실패 시 "RAW-FALLBACK"(구포맷)
        ///   · Common/Migration/R8SnippetMigrator.cs — 구 raw-float 을 66B 로 일괄 재직렬화하는 도구
        ///   · Model/NbGrmInfo.cs                    — 바이트 규격 원본
        ///
        /// 본문 배치는 <b>진폭전용</b>: 프레임마다 [TS16 + float[bin]]. 태그가 L0/V 여도 그렇다 —
        /// 파서가 태그가 아니라 본문 길이로 레이아웃을 정하고, DemonAmpOnly 를 먼저 시도하기 때문이다.
        /// </summary>
        [Theory]
        [InlineData("L", "L0")]
        [InlineData("D", "D0")]
        [InlineData("V2", "V2")]
        public void NbSnippet_R8_66B_진폭전용_배치(string legacyTag, string expectTag)
        {
            const int frames = 4, bins = 7;
            var g = new LegacyGrm
            {
                Kind = LegacyGrmKind.NbSnippet,
                GramTag = legacyTag,
                FrameCount = frames,
                BinCount = bins,
                StartFreq = 10f,
                EndFreq = 70f,
                Resolution = 10f,
                StartTimeMs = 1_000,
                EndTimeMs = 5_000,
                DetDate = new DateTime(2026, 8, 19),
                Amplitude = new float[frames * bins],
            };

            var r = GramConverter.ToR8(g);

            Assert.True(r.Ok, r.Message);
            Assert.Equal(expectTag, r.Tag);

            // 선두 2B 는 ASCII 태그
            Assert.Equal((byte)expectTag[0], r.Data[0]);
            Assert.Equal((byte)expectTag[1], r.Data[1]);

            // 본문 = 프레임 × (TS16 + float[bin]) — AutoLine 이 끼면 이 등식이 깨진다
            long body = r.Data.Length - GramHeader66.TotalSize;
            Assert.Equal((long)frames * (GramHeader66.TimestampSize + bins * sizeof(float)), body);

            // 헤더를 되읽어 프레임 수가 일치해야 한다(정본 마이그레이터도 왕복 검증을 건다)
            var h = GramHeader66.TryRead(r.Data);
            Assert.NotNull(h);
            Assert.Equal(frames, h!.FrameCount);
            Assert.Equal(10.0, h.StartFreq, 3);
            Assert.Equal(70.0, h.EndFreq, 3);
        }

        /// <summary>순수 float(헤더 없음)은 <b>구포맷</b>이다 — 길이가 66B 규격과 절대 같을 수 없다.</summary>
        [Fact]
        public void NbSnippet_순수float_은_66B_와_길이가_다르다()
        {
            const int frames = 4, bins = 7;
            long rawFloatLen = (long)frames * bins * sizeof(float);
            long r8Len = GramHeader66.TotalSize + (long)frames * (GramHeader66.TimestampSize + bins * sizeof(float));
            Assert.NotEqual(rawFloatLen, r8Len);
        }
    }
}
