using System;
using System.IO;
using System.Text;
using RawCollectMigrator.Infrastructure;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 보조정보.alg 파서(AlgParser) 단위 테스트.
    ///
    /// LOGSHT 임무정보 헤더(고정폭 EUC-KR/CP949, 최소 266바이트)를 인메모리로 조립해
    /// 임시 파일로 떨어뜨린 뒤 파싱 계약을 검증한다:
    ///   offset 0..29   진수체계(30) / 30..49 진수계급(20) / 50..69 진수함정(20)
    ///   offset 70..77  임무시작일(8, YYYYMMDD) / 78..85 임무종료일(8)
    ///   offset 86..105 임무해역(20) / 106..185 임무요약(80) / 186..265 비고(80)
    /// </summary>
    public sealed class AlgParserTests
    {
        private static readonly Encoding Cp949;

        static AlgParserTests()
        {
            // CP949(EUC-KR) 코드페이지 제공자 등록 — 테스트에서 기대 바이트를 조립할 때 필요.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Cp949 = Encoding.GetEncoding(949);
        }

        private const int HeaderLength = 266;   // 마지막 필드(비고 186..265) 포함 최소 길이

        /// <summary>고정폭 헤더를 규격 오프셋대로 조립한다. pad 는 필드 밖·잔여 공간 채움 바이트.</summary>
        private static byte[] BuildHeader(
            string platform = "", string vesselClass = "", string vesselName = "",
            string dateFrom = "", string dateTo = "", string seaArea = "",
            string summary = "", string comment = "",
            int totalLength = HeaderLength, byte pad = 0)
        {
            var bytes = new byte[totalLength];
            if (pad != 0) Array.Fill(bytes, pad);

            void Put(string s, int offset, int len)
            {
                var b = Cp949.GetBytes(s);
                Assert.True(b.Length <= len, $"테스트 데이터가 필드 폭 초과: '{s}' {b.Length}B > {len}B");
                Array.Copy(b, 0, bytes, offset, b.Length);
            }

            Put(platform, 0, 30);
            Put(vesselClass, 30, 20);
            Put(vesselName, 50, 20);
            Put(dateFrom, 70, 8);
            Put(dateTo, 78, 8);
            Put(seaArea, 86, 20);
            Put(summary, 106, 80);
            Put(comment, 186, 80);
            return bytes;
        }

        /// <summary>바이트를 임시 .alg 파일로 쓰고 파싱한 뒤 파일을 지운다.</summary>
        private static AlgInfo? ParseBytes(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "algtest_" + Guid.NewGuid().ToString("N") + ".alg");
            try
            {
                File.WriteAllBytes(path, bytes);
                return AlgParser.TryParse(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* 임시 파일 정리 실패는 무시 */ }
            }
        }

        // ── 정상 파싱 ────────────────────────────────────────────────────────

        [Fact]
        public void TryParse_ValidHeader_ParsesAllFields_IncludingCp949Korean()
        {
            var bytes = BuildHeader(
                platform:    "수상함전투체계",
                vesselClass: "ISUS90",
                vesselName:  "장보고함",
                dateFrom:    "20260101",
                dateTo:      "20260107",
                seaArea:     "동해 임무해역",
                summary:     "협대역 원음 수집 임무",
                comment:     "비고: 리그 검증용");

            var info = ParseBytes(bytes);

            Assert.NotNull(info);
            Assert.Equal("수상함전투체계", info!.DetPlatform);
            Assert.Equal("ISUS90", info.DetVesselClass);
            Assert.Equal("장보고함", info.DetVesselName);
            Assert.Equal("20260101", info.MissionDateFrom);
            Assert.Equal("20260107", info.MissionDateTo);
            Assert.Equal("동해 임무해역", info.MissionSeaArea);
            Assert.Equal("협대역 원음 수집 임무", info.MissionSummary);
            Assert.Equal("비고: 리그 검증용", info.MissionComment);

            Assert.Equal(new DateTime(2026, 1, 1), info.RecordingDateTime);
            Assert.Equal("ISUS90", info.RecordingSystemMapped);
        }

        [Fact]
        public void TryParse_SpacePaddedFields_AreTrimmed()
        {
            // 레거시 기록기는 필드 잔여를 공백(0x20)으로 채우기도 한다 — 널 패딩과 동일하게 트림돼야 한다.
            var bytes = BuildHeader(
                platform: "ISUS", vesselClass: "ADD", vesselName: "손원일함",
                dateFrom: "20250315", dateTo: "20250316",
                pad: 0x20);

            var info = ParseBytes(bytes);

            Assert.NotNull(info);
            Assert.Equal("ISUS", info!.DetPlatform);
            Assert.Equal("ADD", info.DetVesselClass);
            Assert.Equal("손원일함", info.DetVesselName);
            Assert.Equal("", info.MissionSeaArea);      // 공백뿐인 필드는 빈 문자열
            Assert.Equal("", info.MissionSummary);
        }

        [Fact]
        public void TryParse_TrailingBytesAfterHeader_AreIgnored()
        {
            // 실제 .alg 는 266바이트 헤더 뒤에 본문 데이터가 이어진다 — 헤더만 읽혀야 한다.
            var bytes = BuildHeader(vesselClass: "JANGBOGO3", dateFrom: "20260401", totalLength: 4096);

            var info = ParseBytes(bytes);

            Assert.NotNull(info);
            Assert.Equal("JANGBOGO3", info!.DetVesselClass);
            Assert.Equal("JANGBOGO3", info.RecordingSystemMapped);
            Assert.Equal(new DateTime(2026, 4, 1), info.RecordingDateTime);
        }

        // ── 실패 계약: 예외가 아니라 null ────────────────────────────────────

        [Theory]
        [InlineData(0)]     // 빈 파일
        [InlineData(1)]
        [InlineData(70)]    // 날짜 필드 시작 직전에서 잘림
        [InlineData(265)]   // 최소 길이(266) 바로 미달
        public void TryParse_BelowMinimumLength_ReturnsNull(int length)
        {
            var info = ParseBytes(new byte[length]);
            Assert.Null(info);
        }

        [Fact]
        public void TryParse_MissingFile_ReturnsNull()
        {
            string path = Path.Combine(Path.GetTempPath(), "algtest_missing_" + Guid.NewGuid().ToString("N") + ".alg");
            Assert.Null(AlgParser.TryParse(path));
        }

        [Fact]
        public void TryParse_CorruptBinary_DoesNotThrow_AndDateIsNull()
        {
            // CP949 로 해석 불가능한 바이트(0xFF 연속)로 채워도 예외 없이 정의된 결과가 나와야 한다.
            var bytes = new byte[HeaderLength];
            Array.Fill(bytes, (byte)0xFF);

            var info = ParseBytes(bytes);   // 예외가 새면 여기서 테스트 실패

            Assert.NotNull(info);           // 길이 요건은 충족 → 파싱 자체는 성공(내용은 대체문자)
            Assert.Null(info!.RecordingDateTime);   // 쓰레기 날짜는 날짜로 해석되지 않는다
        }

        // ── 파생값 계약: RecordingDateTime ──────────────────────────────────

        [Theory]
        [InlineData("2026010")]    // 8자리 미만
        [InlineData("ABCD0101")]   // 숫자 아님
        [InlineData("20261301")]   // 13월 — DateTime 생성 실패
        [InlineData("20260230")]   // 2월 30일 — DateTime 생성 실패
        [InlineData("")]           // 빈 값
        public void RecordingDateTime_InvalidDateField_ReturnsNull(string dateFrom)
        {
            var info = new AlgInfo { MissionDateFrom = dateFrom };
            Assert.Null(info.RecordingDateTime);
        }

        // ── 파생값 계약: RecordingSystemMapped (레거시 UpdateRawDMFfromLogsht 매핑) ──

        [Theory]
        [InlineData("ISUS83-TCIS", "ISUS83-TCIS")]
        [InlineData("ISUS83TCIS", "ISUS83-TCIS")]
        [InlineData("ISUSTCIS", "ISUS83-TCIS")]
        [InlineData("ISUS83", "ISUS83-TCIS")]
        [InlineData("isus83", "ISUS83-TCIS")]      // 대소문자 무시
        [InlineData("ISUS90", "ISUS90")]
        [InlineData("JANGBOGO3", "JANGBOGO3")]
        [InlineData("ADD", "ADD")]
        [InlineData("US", "US-NAVY")]
        [InlineData("USNAVY", "US-NAVY")]
        [InlineData("", "ETC")]
        [InlineData("   ", "ETC")]                  // 공백뿐 → 트림 후 빈 값
        [InlineData("장보고", "ETC")]                // 미등록 값
        public void RecordingSystemMapped_MapsLegacyClassCodes(string vesselClass, string expected)
        {
            var info = new AlgInfo { DetVesselClass = vesselClass };
            Assert.Equal(expected, info.RecordingSystemMapped);
        }
    }
}
