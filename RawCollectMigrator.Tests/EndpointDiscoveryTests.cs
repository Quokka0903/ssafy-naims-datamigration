using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// 연결 탐색의 "대상 해석" 회귀 테스트.
    ///
    /// 배경(2026-08-18 로컬 실측): 이 PC 의 Oracle 12c(NAIMS12)가 탐색에 안 잡혔다. 원인이 셋이었다.
    ///   ① Oracle DSN 의 접속 대상이 ServerName 이 아니라 DBQ 에 EZConnect 로 들어 있었다.
    ///   ② tnsnames.ora 에 서비스 별칭이 없고 리스너 주소 정의(HOST=0.0.0.0)만 있었는데
    ///      그것을 접속 후보로 오인했다.
    ///   ③ 리스너 포트가 1521 이 아니라 1522 였다.
    /// 아래 테스트는 ①②의 해석 규칙을 고정한다.
    /// </summary>
    public class EndpointDiscoveryTests
    {
        [Theory]
        [InlineData("localhost:1522/NAIMS12", "localhost", 1522, "NAIMS12")]
        [InlineData("localhost:1522/LegacyNaims", "localhost", 1522, "LegacyNaims")]
        [InlineData("10.0.0.5:1521/ORCL", "10.0.0.5", 1521, "ORCL")]
        public void EZConnect_호스트포트서비스로_분해된다(string input, string host, int port, string svc)
        {
            Assert.True(EndpointDiscovery.TryParseEzConnect(input, out var h, out var p, out var s));
            Assert.Equal(host, h);
            Assert.Equal(port, p);
            Assert.Equal(svc, s);
        }

        [Fact]
        public void EZConnect_포트가_없으면_1521로_본다()
        {
            Assert.True(EndpointDiscovery.TryParseEzConnect("dbhost/NAIMS", out var h, out var p, out var s));
            Assert.Equal("dbhost", h);
            Assert.Equal(1521, p);
            Assert.Equal("NAIMS", s);
        }

        [Theory]
        [InlineData("")]
        [InlineData("NAIMS12")]          // 순수 TNS 별칭 — EZConnect 가 아니다
        [InlineData("localhost:1522")]   // 서비스명이 없다
        [InlineData("host/")]
        public void EZConnect가_아니면_false(string input)
        {
            Assert.False(EndpointDiscovery.TryParseEzConnect(input, out _, out _, out _));
        }

        [Fact]
        public void tnsnames의_리스너_주소정의는_접속후보가_아니다()
        {
            // 이 PC 의 실제 tnsnames.ora 내용 — CONNECT_DATA 가 없는 리스너 정의뿐이다.
            const string text = @"
LISTENER_NAIMS12 =
  (ADDRESS = (PROTOCOL = TCP)(HOST = 0.0.0.0)(PORT = 1522))
";
            var entries = TnsCatalog.Parse(text, "tnsnames.ora");
            Assert.Empty(entries);
        }

        [Fact]
        public void tnsnames의_정상_별칭은_호스트포트서비스로_해석된다()
        {
            const string text = @"
NAIMS12 =
  (DESCRIPTION =
    (ADDRESS = (PROTOCOL = TCP)(HOST = localhost)(PORT = 1522))
    (CONNECT_DATA = (SERVER = DEDICATED)(SERVICE_NAME = NAIMS12))
  )
";
            var entries = TnsCatalog.Parse(text, "tnsnames.ora");
            var e = Assert.Single(entries);
            Assert.Equal("NAIMS12", e.Alias);
            Assert.Equal("localhost", e.Host);
            Assert.Equal(1522, e.Port);
            Assert.Equal("NAIMS12", e.Service);
        }

        [Fact]
        public void tnsnames에서_SID표기도_서비스로_받는다()
        {
            const string text = @"
OLD =
  (DESCRIPTION =
    (ADDRESS = (PROTOCOL = TCP)(HOST = 192.168.0.9)(PORT = 1521))
    (CONNECT_DATA = (SID = NAIMS))
  )
";
            var e = Assert.Single(TnsCatalog.Parse(text));
            Assert.Equal("NAIMS", e.Service);
            Assert.Equal(1521, e.Port);
        }

        [Fact]
        public void 기본_스캔포트에_1522가_포함된다()
        {
            // 현장·로컬 리그 모두 1522 를 쓰므로 기본값에서 빠지면 못 찾는다.
            Assert.Contains(1522, EndpointDiscovery.DefaultScanPorts);
        }
    }
}
