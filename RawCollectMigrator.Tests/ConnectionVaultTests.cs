using RawCollectMigrator.Features.DbToDb;
using RawCollectMigrator.Infrastructure;
using Xunit;

namespace RawCollectMigrator.Tests
{
    // [찾기:접속기억] [NAIMS-ISSUE:CONN-RECALL]
    /// <summary>
    /// 접속 기억·완성 규칙 시험.
    ///
    /// 지키려는 것은 두 가지다.
    ///   ① <b>같은 대상</b> 판정에 비밀번호가 끼어들지 않는다 — 비어 있어도 같은 대상으로 알아봐야
    ///      채워 넣을 수 있다. 이게 무너지면 기억은 있으나 영영 쓰이지 않는다.
    ///   ② 사용자가 방금 친 값이 언제나 이긴다 — 기억이 사용자 입력을 덮으면
    ///      "왜 다른 서버에 붙었는지" 를 아무도 설명할 수 없게 된다.
    /// </summary>
    public class ConnectionVaultTests
    {
        // ── 비밀번호 끼워 넣기 ──────────────────────────────────

        [Fact]
        public void WithPassword_키표기를_보존한다()
        {
            Assert.Equal("DSN=naims;Uid=REH;Pwd=Reh2026x;",
                SecretStore.WithPassword("DSN=naims;Uid=REH;Pwd=;", "Reh2026x"));

            Assert.Equal("User Id=REH;Password=Reh2026x;Data Source=localhost:1521/XEPDB1;",
                SecretStore.WithPassword("User Id=REH;Password=;Data Source=localhost:1521/XEPDB1;", "Reh2026x"));
        }

        [Fact]
        public void WithPassword_키가_없으면_방언에_맞춰_붙인다()
        {
            Assert.Equal("DSN=naims;Uid=REH;Pwd=x1;",
                SecretStore.WithPassword("DSN=naims;Uid=REH;", "x1"));

            Assert.Equal("Server=s;Database=d;User Id=u;Password=x1;",
                SecretStore.WithPassword("Server=s;Database=d;User Id=u;", "x1"));
        }

        [Fact]
        public void WithPassword_특수문자를_그대로_넣는다()
        {
            // $ 는 Regex 치환에서 그룹 참조로 먹힐 수 있고, # 는 실제 리그 비밀번호에 들어 있다.
            Assert.Equal("User Id=SYSTEM;Password=Naims12#2026;",
                SecretStore.WithPassword("User Id=SYSTEM;Password=;", "Naims12#2026"));
            Assert.Equal("User Id=u;Password=a$1b;",
                SecretStore.WithPassword("User Id=u;Password=;", "a$1b"));
        }

        [Fact]
        public void NeedsPassword_통합인증은_요구하지_않는다()
        {
            Assert.False(SecretStore.NeedsPassword(
                "Server=localhost;Database=NAIMS2;Trusted_Connection=yes;"));
            Assert.False(SecretStore.NeedsPassword(
                "Server=localhost;Database=NAIMS;Integrated Security=true;"));
            Assert.True(SecretStore.NeedsPassword("DSN=naims;Uid=LEGACYNAIMS;Pwd=;"));
            Assert.False(SecretStore.NeedsPassword("DSN=naims;Uid=LEGACYNAIMS;Pwd=naims2!;"));
        }

        [Fact]
        public void StripPassword_는_키를_남긴다()
        {
            Assert.Equal("DSN=naims;Uid=REH;Pwd=;",
                SecretStore.StripPassword("DSN=naims;Uid=REH;Pwd=Reh2026x;"));
        }

        // ── 같은 대상 판정 ──────────────────────────────────────

        [Fact]
        public void KeyOf_는_비밀번호에_좌우되지_않는다()
        {
            var withPw = ConnectionVault.KeyOf(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, "User Id=REH;Password=Reh2026x;Data Source=localhost:1521/XEPDB1;");
            var noPw = ConnectionVault.KeyOf(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, "User Id=REH;Password=;Data Source=localhost:1521/XEPDB1;");

            Assert.Equal(withPw, noPw);
        }

        [Fact]
        public void KeyOf_는_계정이_다르면_다른_대상이다()
        {
            var reh = ConnectionVault.KeyOf(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, "User Id=REH;Password=;Data Source=localhost:1521/XEPDB1;");
            var other = ConnectionVault.KeyOf(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, "User Id=SYSTEM;Password=;Data Source=localhost:1521/XEPDB1;");

            Assert.NotEqual(reh, other);
        }

        // ── 완성 ────────────────────────────────────────────────

        [Fact]
        public void Complete_기본항목의_비밀번호를_채운다()
        {
            // 로컬 리그는 기본 항목으로 미리 들어 있다 — 비밀번호만 비워 들고 와도 완성돼야 한다.
            var filled = ConnectionVault.Complete(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, "User Id=LEGACYNAIMS;Password=;Data Source=localhost:1522/LegacyNaims;");

            Assert.Equal("naims2!", SecretStore.PasswordOf(filled));
        }

        [Fact]
        public void Complete_ODBC_DSN_도_채운다()
        {
            var filled = ConnectionVault.Complete(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Odbc, "DSN=naims;Uid=LEGACYNAIMS;Pwd=;");

            Assert.Equal("naims2!", SecretStore.PasswordOf(filled));
        }

        [Fact]
        public void Complete_사용자가_친_비밀번호를_덮지_않는다()
        {
            const string mine = "User Id=LEGACYNAIMS;Password=내가친값;Data Source=localhost:1522/LegacyNaims;";
            Assert.Equal(mine, ConnectionVault.Complete(ConnectionRole.Source, DbProviderKind.Oracle,
                DbConnectionMode.Direct, mine));
        }

        [Fact]
        public void Complete_모르는_대상은_비워_둔다()
        {
            // 현장·실운영 계정은 기본 항목에 없다 — 있으면 안 된다. [NAIMS-ISSUE:NO-EMBEDDED-SECRET]
            const string field =
                "Server=tcp:192.168.200.205,1433;Database=NAIMS2;User Id=NAIMS2DB;Password=;Encrypt=true;";
            Assert.Equal(field, ConnectionVault.Complete(ConnectionRole.Target, DbProviderKind.MsSql,
                DbConnectionMode.Direct, field));
        }

        [Fact]
        public void 기본항목에_현장계정은_없다()
        {
            foreach (var p in ConnectionVault.Recent(ConnectionRole.Target)
                        .Concat(ConnectionVault.Recent(ConnectionRole.Source)))
            {
                if (!p.Seeded) continue;   // 사용자가 실제로 붙어 본 것은 이 시험의 대상이 아니다
                Assert.DoesNotContain("192.168.", p.ConnStr);
                Assert.DoesNotContain("NAIMS2DB", p.ConnStr);
                Assert.DoesNotContain("NaimsMig", p.ConnStr);
            }
        }
    }
}
