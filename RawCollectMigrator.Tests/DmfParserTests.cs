using System;
using System.IO;
using System.Text;
using RawCollectMigrator.Infrastructure;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 자료관리파일.DMF 파서 단위 테스트.
    ///
    /// DMF 는 CDMU(USPASK/APU)가 만드는 CP949 INI 메타데이터다. 없으면 넘어가고 있으면 채우는
    /// 선택 입력이므로, "부재·손상은 조용히 null" 과 "있으면 한 키도 흘리지 않는다" 두 가지가 계약이다.
    /// </summary>
    public sealed class DmfParserTests
    {
        static DmfParserTests() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        /// <summary>CP949 로 임시 DMF 를 만든다.</summary>
        /// <param name="body">INI 본문.</param>
        /// <param name="dir">넣을 폴더(없으면 임시 폴더 새로 생성).</param>
        /// <param name="name">파일명(기본 자료관리파일.DMF).</param>
        /// <returns>생성된 파일 경로.</returns>
        private static string WriteDmf(string body, string? dir = null, string name = "자료관리파일.DMF")
        {
            dir ??= Path.Combine(Path.GetTempPath(), "dmf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, body, Encoding.GetEncoding(949));
            return path;
        }

        /// <summary>현장 표본(ISUS90_20191126151223_00_000_001.sd)을 그대로 옮긴 본문.</summary>
        private const string RealSample = """
[LIST]
연동ID=ISUS90_20191126151223
수집일시=20191017001900
유효트랙수=2
발췌원음수=6
처리상태=신호처리완료
수집체계=ISUS90
수집함=손원일
연동경로=Online
연동매체=Network
설명=-
CDMU수신완료=수신완료
연동수행장치=APU3
원음자료명=ISUS90_20191126151223_00_000_001.sd
종류=단주기
분석상태=-
크기=0
탐지소나=214-CAS
샘플율=32768
영상자료수=0
[SUBDB]
수집함정급=214
수신일시=20191126151223
위도=36:24:54 N
경도=130:39:48 E
거리=-
임무=전투탄 실사
Quick분석장치=APU1
Quick분석자=배명철
Quick일시=20191223152004
""";

        // ── 파싱 ─────────────────────────────────────────────────────────

        /// <summary>현장 표본의 주요 키가 타입 접근자로 그대로 나와야 한다.</summary>
        [Fact]
        public void 현장표본_DMF를_읽는다()
        {
            var path = WriteDmf(RealSample);
            try
            {
                var d = DmfParser.TryParse(path);
                Assert.NotNull(d);
                Assert.Equal("ISUS90", d!.RecordingSystem);
                Assert.Equal("손원일", d.RecordingVessel);
                Assert.Equal("단주기", d.DatasetType);
                Assert.Equal("214-CAS", d.DetSonar);
                Assert.Equal("신호처리완료", d.ProcessingState);
                Assert.Equal(32768, d.SampleRate);
                Assert.Equal(6, d.ExtractCount);
                Assert.Equal("214", d.VesselClass);
                Assert.Equal("전투탄 실사", d.Mission);
                Assert.Equal("배명철", d.QaOperator);
                Assert.Equal(new DateTime(2019, 10, 17, 0, 19, 0), d.CollectDateTime);
                Assert.Equal(new DateTime(2019, 11, 26, 15, 12, 23), d.ReceiptDateTime);
                Assert.True(d.HasListSection);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        /// <summary>
        /// 수집일시(=녹음)와 수신일시(=폴더명 스탬프)는 다른 값이다.
        /// 이 둘을 뒤섞으면 RECORDING_DATETIME 이 한 달 넘게 어긋난다.
        /// </summary>
        [Fact]
        public void 수집일시와_수신일시는_다른_값이다()
        {
            var path = WriteDmf(RealSample);
            try
            {
                var d = DmfParser.TryParse(path)!;
                Assert.NotEqual(d.CollectDateTime, d.ReceiptDateTime);
                Assert.True(d.CollectDateTime < d.ReceiptDateTime);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        /// <summary>미설정 표기 "-" 는 빈 문자열로 읽혀야 한다(문자열 "-" 가 DB 로 새어 들어가면 안 된다).</summary>
        [Fact]
        public void 미설정값_하이픈은_빈값이다()
        {
            var path = WriteDmf(RealSample);
            try
            {
                var d = DmfParser.TryParse(path)!;
                Assert.Equal("", d.Get("LIST", "설명"));
                Assert.Equal("", d.Get("LIST", "분석상태"));
                Assert.Equal("", d.Get("SUBDB", "거리"));
                Assert.Equal("", d.Get("LIST", "없는키"));
                Assert.Equal("", d.Get("NOSUCH", "아무키"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        /// <summary>Signal_NN_NNN.DMF([SIGNAL] 전용)도 같은 파서로 읽힌다.</summary>
        [Fact]
        public void Signal_DMF를_읽는다()
        {
            var path = WriteDmf("""
[SIGNAL]
원음신호명=Signal_00_000
발췌수=1
발췌종류=협대역/단주기
샘플율=32768
이벤트개수=6
처리상태=발췌완료
원음신호길이=00:19:18
탐지소나=214-CAS
연동파일명=831_90_20191017084000_sign_01.wav
""", name: "Signal_00_000.DMF");
            try
            {
                var d = DmfParser.TryParse(path)!;
                Assert.False(d.HasListSection);
                Assert.Equal("Signal_00_000", d.SignalName);
                Assert.Equal("협대역/단주기", d.SignalType);
                Assert.Equal(6, d.SignalEventCount);
                Assert.Equal("00:19:18", d.SignalLength);
                Assert.Equal("831_90_20191017084000_sign_01.wav", d.LinkedFileName);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        /// <summary>레거시 실데이터의 오염된 시각값("82557828" 등)은 예외 없이 null 이어야 한다.</summary>
        [Fact]
        public void 오염된_시각값은_null이다()
        {
            var path = WriteDmf("[LIST]\n수집일시=82557828\n수집체계=ISUS90\n");
            try
            {
                var d = DmfParser.TryParse(path)!;
                Assert.Null(d.CollectDateTime);
                Assert.Equal("ISUS90", d.RecordingSystem);   // 나머지 키는 정상적으로 읽힌다
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        // ── 없거나 못 읽는 경우 ───────────────────────────────────────────

        /// <summary>DMF 는 선택 입력 — 없으면 예외가 아니라 null 이다.</summary>
        [Fact]
        public void 없는_파일은_null이다()
        {
            Assert.Null(DmfParser.TryParse(Path.Combine(Path.GetTempPath(), "no_such.DMF")));
            Assert.Null(DmfParser.TryParse(null));
            Assert.Null(DmfParser.TryParse(""));
        }

        /// <summary>INI 가 아닌 내용은 섹션 0개 → null.</summary>
        [Fact]
        public void INI가_아니면_null이다()
        {
            var path = WriteDmf("이건 그냥 텍스트\n키도 없고 섹션도 없다\n");
            try { Assert.Null(DmfParser.TryParse(path)); }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        // ── 탐색 위치 ────────────────────────────────────────────────────

        /// <summary>항목 폴더의 DMF 를 먼저 쓰고, 없으면 상위 연동ID 폴더의 것을 쓴다.</summary>
        [Fact]
        public void 항목폴더_우선_없으면_상위폴더를_본다()
        {
            var root = Path.Combine(Path.GetTempPath(), "dmf_" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(root, "ISUS90_20191126151223");
            var item = Path.Combine(link, "ISUS90_20191126151223_00_000_001.sd");
            Directory.CreateDirectory(item);
            try
            {
                WriteDmf("[LIST]\n수집체계=FTAS-TCIS\n", link);        // ① 연동ID 계층
                Assert.Equal("FTAS-TCIS", DmfParser.TryParse(DmfParser.FindForItem(item))!.RecordingSystem);

                WriteDmf("[LIST]\n수집체계=ISUS90\n", item);            // ② 항목 계층이 이긴다
                Assert.Equal("ISUS90", DmfParser.TryParse(DmfParser.FindForItem(item))!.RecordingSystem);
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>형제 폴더의 DMF 를 잘못 집어오면 안 된다(하위 전체 탐색 금지).</summary>
        [Fact]
        public void 형제폴더의_DMF는_집어오지_않는다()
        {
            var root = Path.Combine(Path.GetTempPath(), "dmf_" + Guid.NewGuid().ToString("N"));
            var mine = Path.Combine(root, "A.sd");
            var other = Path.Combine(root, "B.sd");
            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(other);
            try
            {
                WriteDmf("[LIST]\n수집체계=ISUS90\n", other);
                Assert.Null(DmfParser.FindForItem(mine));
            }
            finally { Directory.Delete(root, true); }
        }

        // ── 누락 감지 ────────────────────────────────────────────────────

        /// <summary>
        /// 값이 채워졌는데 DB 로 가지 않는 키는 PendingKeys 로 드러나야 한다 — 판정 기준은
        /// "파서가 읽었는가"가 아니라 "이관 결과에 남는가"다. 그래서 타입 접근자가 있는
        /// 위도·임무·Quick분석자 도, 아직 대상 컬럼이 없으므로 여기 나와야 한다.
        /// 반대로 실제로 반영되는 키와 미설정("-"/"0")은 나오지 않아야 한다.
        /// </summary>
        [Fact]
        public void DB로_가지_않는_채워진_키가_드러난다()
        {
            var path = WriteDmf(RealSample);
            try
            {
                var pending = DmfParser.TryParse(path)!.PendingKeys().ToList();

                // 대상 컬럼이 아직 없는 값 — 코드표/규약이 정해질 때까지 DB 에 안 들어간다
                Assert.Contains("LIST/탐지소나=214-CAS", pending);
                Assert.Contains("LIST/처리상태=신호처리완료", pending);
                Assert.Contains("SUBDB/위도=36:24:54 N", pending);
                Assert.Contains("SUBDB/임무=전투탄 실사", pending);
                Assert.Contains("SUBDB/Quick분석자=배명철", pending);
                Assert.Contains("LIST/연동수행장치=APU3", pending);
                Assert.Contains("LIST/유효트랙수=2", pending);

                // 실제로 이관 결과에 남는 값
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/수집체계"));
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/수집일시"));
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/수집함="));
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/원음자료명"));
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/샘플율"));

                // 미설정은 누락이 아니다
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/설명"));        // "-"
                Assert.DoesNotContain(pending, x => x.StartsWith("LIST/영상자료수"));   // "0"
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        // ── RECORDING_SYSTEM 어휘 통일 ───────────────────────────────────

        /// <summary>.alg 표기와 DMF 표기가 같은 정본으로 모여야 한다(한 컬럼에 두 어휘가 섞이면 안 된다).</summary>
        [Theory]
        [InlineData("ISUS83", "ISUS83-TCIS")]        // .alg
        [InlineData("ISUSTCIS", "ISUS83-TCIS")]      // .alg
        [InlineData("ISUS83-TCIS", "ISUS83-TCIS")]   // DMF
        [InlineData("ISUS90", "ISUS90")]
        [InlineData("FTAS-TCIS", "FTAS-TCIS")]       // DMF 전용 — 예전 매퍼는 ETC 로 떨궜다
        [InlineData("WSD-210K", "WSD-210K")]
        [InlineData("SQR-230K", "SQR-230K")]
        [InlineData("TSM2932S", "TSM2932S")]
        [InlineData("FAMS", "FAMS")]
        [InlineData("USNAVY", "US-NAVY")]            // .alg
        [InlineData("US Navy", "US-NAVY")]           // DMF
        [InlineData("ADD", "ADD")]                   // .alg
        [InlineData("ADD_TEC", "ADD")]               // DMF
        [InlineData("214", "ETC")]                   // 함정급 — 체계가 아니다
        [InlineData("aaa", "ETC")]                   // 시험 오염값
        [InlineData("", "ETC")]
        [InlineData(null, "ETC")]
        public void 수집체계_어휘가_한_벌로_모인다(string? raw, string expected)
            => Assert.Equal(expected, RecordingSystem.Normalize(raw));

        /// <summary>ETC 로 떨어지는 값은 "식별 실패"로 취급돼 다른 출처로 넘어갈 수 있어야 한다.</summary>
        [Fact]
        public void 식별여부_판정()
        {
            Assert.True(RecordingSystem.IsIdentified("ISUS90"));
            Assert.True(RecordingSystem.IsIdentified("FTAS-TCIS"));
            Assert.False(RecordingSystem.IsIdentified("214"));
            Assert.False(RecordingSystem.IsIdentified("test"));
            Assert.False(RecordingSystem.IsIdentified(null));
        }
    }
}
