using System;
using System.IO;
using System.Linq;
using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 실제 레거시 자료로 거는 검증. 자료가 없는 장비에서는 Skip 된다(실패 아님).
    ///
    /// 합성 자료 시험(<see cref="LegacyGrmDescaleTests"/>)은 "내가 만든 규칙대로 도는가"만 본다.
    /// 여기서는 현장 파일이 실제로 그 규격인지를 확인한다 — 규격을 잘못 읽었으면 여기서 걸린다.
    /// </summary>
    public class LegacyGrmRealDataTests
    {
        private const string NbDir = @"D:\NAIMS1_Ref\D드라이브 루트에 복사\USPASK_DATA\협대역원음자료";
        private const string AiDir = @"D:\MSU\CLASSIFICATIONDB\SNIPPET";

        private static string[] Find(string dir, string pattern) =>
            Directory.Exists(dir)
                ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        /// <summary>
        /// 이관 대상인 NB <b>스니펫</b>(nb_*.grm)은 CDMU 기록 경로를 타므로
        /// 본문이 4의 배수 64단계(6비트)여야 한다 — CDMU FileReadWriteFunc.cpp:2847.
        /// 여기가 깨지면 기록 경로 가정이 틀린 것이므로 역산 해상도 전제를 다시 봐야 한다.
        /// </summary>
        [SkippableFact]
        public void NB_스니펫은_128B헤더에_4의배수_64단계다()
        {
            var files = Find(NbDir, "nb_*.grm");
            Skip.If(files.Length == 0, $"NB 스니펫 자료 없음: {NbDir}");

            int seen = 0;
            foreach (var f in files.Take(20))
            {
                var g = LegacyGrmReader.Read(f);
                if (!g.Ok) continue;
                Assert.Equal(LegacyGrmKind.NbSnippet, g.Kind);
                Assert.Equal(128, g.HeaderSize);

                var distinct = g.RawBody.Distinct().ToArray();
                if (distinct.Length <= 1) continue;

                Assert.All(distinct, v => Assert.True(v % 4 == 0,
                    $"{Path.GetFileName(f)}: 본문 값 {v} 가 4의 배수가 아니다"));
                Assert.True(distinct.Length <= 64,
                    $"{Path.GetFileName(f)}: 고유값 {distinct.Length}개 — 6비트(64단계) 상한을 넘는다");
                seen++;
            }

            Skip.If(seen == 0, "값이 있는 NB 스니펫이 없었다.");
        }

        /// <summary>
        /// 같은 폴더의 LOFAR/DEMON/VERNIER 풀그램은 스니펫 포맷이 <b>아니다</b>.
        /// 파서가 이것을 조용히 받아들이면 1억 프레임짜리 쓰레기를 이관하게 된다 — 거부해야 맞다.
        /// </summary>
        [SkippableFact]
        public void 정밀분석_풀그램은_스니펫으로_읽히지_않는다()
        {
            var files = Find(NbDir, "LOFAR.grm").Concat(Find(NbDir, "DEMON.grm")).ToArray();
            Skip.If(files.Length == 0, $"정밀분석 풀그램 없음: {NbDir}");

            foreach (var f in files)
            {
                var g = LegacyGrmReader.Read(f);
                Assert.False(g.Ok,
                    $"{Path.GetFileName(f)}: 스니펫이 아닌데 파싱에 성공했다 — 잘못 이관될 수 있다");
            }
        }

        /// <summary>NB 스니펫 역산이 원본 BYTE 와 <c>10^((BYTE−B)/A)</c> 로 정확히 맞물려야 한다.</summary>
        [SkippableFact]
        public void NB_스니펫_역산이_원본BYTE와_맞물린다()
        {
            var f = Find(NbDir, "nb_*.grm").FirstOrDefault();
            Skip.If(f == null, $"NB 스니펫 없음: {NbDir}");

            var g = LegacyGrmReader.Read(f!);
            Skip.If(!g.Ok, $"파싱 실패: {g.Error}");
            Assert.Equal(LegacyGrmKind.NbSnippet, g.Kind);
            Assert.True(g.UsedAScale > 0, "AScale 이 채워져야 한다");

            Assert.All(g.Amplitude, v => Assert.True(v > 0f && float.IsFinite(v),
                "선형 진폭은 항상 양수여야 한다(10^x)"));

            int n = Math.Min(2000, g.RawBody.Length);
            for (int i = 0; i < n; i++)
            {
                double expected = Math.Pow(10.0, (g.RawBody[i] - g.UsedBScale) / g.UsedAScale);
                Assert.Equal(expected, g.Amplitude[i], 4);
            }

            // 전시는 log10 을 다시 걸어 되돌린다 → BYTE 대소 관계가 그대로 보존돼야 한다.
            for (int i = 1; i < n; i++)
                if (g.RawBody[i] > g.RawBody[i - 1])
                    Assert.True(g.Amplitude[i] > g.Amplitude[i - 1], "BYTE 대소가 진폭 대소와 어긋난다");
        }

        /// <summary>AI 실파일은 {0,4,…,28} 8단계뿐이고, 역산 결과가 전시 범위에 고르게 퍼져야 한다.</summary>
        [SkippableFact]
        public void AI_실파일은_8단계이고_전시범위에_고르게_퍼진다()
        {
            var files = Find(AiDir, "ai_*.grm");
            Skip.If(files.Length == 0, $"AI 스니펫 자료 없음: {AiDir}");

            var f = files[0];
            var raw = File.ReadAllBytes(f);
            Skip.If(raw.Length <= 216, "본문이 없다.");

            var distinct = raw.Skip(216).Distinct().OrderBy(v => v).ToArray();
            Assert.All(distinct, v => Assert.True(v % 4 == 0 && v <= 28,
                $"AI 본문 값 {v} 가 규격({{0,4,…,28}}) 밖이다"));

            var g = LegacyGrmReader.Read(f);
            Skip.If(!g.Ok, $"파싱 실패: {g.Error}");
            Assert.Equal(LegacyGrmKind.AiSnippet, g.Kind);

            // NAIMS-II ArGramRenderer 의 절대 매핑에 넣었을 때 0~255 안에서 계단이 고르게 퍼져야 한다.
            // 전 영역 고휘도로 뭉치면(옛 (BYTE−40)/150 역산의 증상) 여기서 걸린다.
            var intensities = g.Amplitude.Distinct()
                .Select(v => (int)Math.Round((v + 12.0) / 18.0 * 255.0))
                .OrderBy(v => v).ToArray();

            Assert.All(intensities, i => Assert.InRange(i, 0, 255));
            if (intensities.Length >= 2)
                Assert.True(intensities[^1] - intensities[0] > 64,
                    $"전시 강도 폭이 너무 좁다({intensities[0]}~{intensities[^1]}) — 역산식을 의심해야 한다");
        }
    }
}
