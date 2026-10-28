using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RawCollectMigrator.Features.DbToDb;

namespace RawCollectMigrator.Infrastructure
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:접속기억] [찾기:접속기억] [NAIMS-ISSUE:CONN-RECALL]
    //  ▣ 이 파일 = "한 번 붙어 본 대상"의 기억 창고.
    //
    //  ▶ 왜 만들었나: 예전에는 AppSession 이 마지막 한 벌만 들고 있었다.
    //    타겟을 로컬 → 현장 → 로컬 로 옮겨 다니면 되돌아올 때마다 접속문자열을 다시 짜고
    //    비밀번호를 다시 쳐야 했다. 현장에서 이 재입력이 오타·오지정의 입구다.
    //    → 붙는 데 성공한 접속문자열을 대상별로 기억해 두고, 같은 대상을 다시 고르면
    //      비밀번호까지 채워진 완성된 문자열을 그대로 돌려준다.
    //
    //  ▶ 무엇을 키로 같은 대상이라 보나: 서버(또는 DSN·DBQ) + 계정 + 종류 + 접속방식.
    //    비밀번호는 키가 아니다 — 그게 이 창고의 존재 이유다(비밀번호만 비어 있는 문자열을
    //    들고 와도 같은 대상으로 알아보고 채워 준다).
    //
    //  ▶ 비밀번호: 디스크에는 SecretStore 를 통해 DPAPI 로만 들어간다.
    //    [비밀번호까지 저장] 을 끄면 저장분에서 비밀번호를 지운다(ClearSecrets).
    //
    //  ▶ 기본 항목(Seeds): 로컬 리그(이 저장소 스크립트가 만든 격리 DB)만 미리 넣어 둔다.
    //    이 값들은 파일에 쓰지 않는다 — 소스에 있는 그대로 매 실행 메모리에서 만든다.
    //    현장·실운영 계정(NAIMS2DB / NaimsMig)은 절대 넣지 않는다. [찾기:비밀번호저장]
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>기억해 둔 접속이 어느 자리에 쓰이는지.</summary>
    public enum ConnectionRole
    {
        /// <summary>적재 대상 NAIMS2(항상 MsSQL 다이렉트).</summary>
        Target,
        /// <summary>읽는 곳 — 구 NAIMS.</summary>
        Source,
    }

    /// <summary>기억해 둔 접속 한 벌.</summary>
    public sealed class ConnectionProfile
    {
        public ConnectionRole Role { get; init; }
        public DbProviderKind Kind { get; init; } = DbProviderKind.MsSql;
        public DbConnectionMode Mode { get; init; } = DbConnectionMode.Direct;

        /// <summary>완성된 접속문자열(비밀번호 포함 — 화면에 그대로 넣으면 붙는다).</summary>
        public string ConnStr { get; set; } = "";

        /// <summary>사람이 읽는 이름. 없으면 서버·계정만 보인다.</summary>
        public string Label { get; init; } = "";

        public DateTime LastUsed { get; set; }

        /// <summary>소스에 박아 둔 기본 항목인지(파일에 저장하지 않는다).</summary>
        public bool Seeded { get; init; }

        public string Key => ConnectionVault.KeyOf(Role, Kind, Mode, ConnStr);

        /// <summary>드롭다운에 보이는 한 줄.</summary>
        public string Display
        {
            get
            {
                var user = AppSession.UserOf(ConnStr);
                var who = user.Length > 0 ? user : "통합 인증";
                var head = Label.Length > 0 ? Label + " — " : "";
                var how = Role == ConnectionRole.Source
                    ? $" [{DbProvider.DisplayName(Kind)}·{DbProvider.ModeName(Mode)}]"
                    : "";
                var pw = SecretStore.NeedsPassword(ConnStr) ? "  🔑없음" : "";
                var mark = Seeded ? "  (기본)" : "";
                return $"{head}{AppSession.ServerOf(ConnStr)} · {who}{how}{mark}{pw}";
            }
        }

        public override string ToString() => Display;
    }

    /// <summary>붙어 본 접속을 기억해 두고 다시 완성해 주는 창고.</summary>
    public static class ConnectionVault
    {
        /// <summary>저장 파일(exe 옆 config\endpoints.json).</summary>
        public static string StorePath =>
            Path.Combine(AppContext.BaseDirectory, "config", "endpoints.json");

        private static List<ConnectionProfile>? _user;          // 사람이 실제로 붙어 본 것
        private static HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

        // ── 같은 대상인지 판정 ─────────────────────────────────────

        /// <summary>같은 대상으로 볼 키. 비밀번호는 넣지 않는다 — 그래야 비어 있어도 알아본다.</summary>
        public static string KeyOf(ConnectionRole role, DbProviderKind kind, DbConnectionMode mode, string conn) =>
            string.Join("|",
                role,
                role == ConnectionRole.Target ? "MsSql" : kind.ToString(),
                role == ConnectionRole.Target ? "Direct" : mode.ToString(),
                AppSession.ServerOf(conn).ToUpperInvariant(),
                AppSession.UserOf(conn).ToUpperInvariant());

        // ── 읽기 ───────────────────────────────────────────────────

        /// <summary>이 자리에 쓸 수 있는 최근 접속들(최근 것 먼저, 기본 항목은 뒤).</summary>
        public static IReadOnlyList<ConnectionProfile> Recent(ConnectionRole role)
        {
            EnsureLoaded();
            var mine = _user!.Where(p => p.Role == role).OrderByDescending(p => p.LastUsed).ToList();
            var seen = new HashSet<string>(mine.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            var seeds = Seeds.Where(p => p.Role == role
                                      && !seen.Contains(p.Key)
                                      && !_hidden.Contains(p.Key));
            return mine.Concat(seeds).ToList();
        }

        // [찾기:접속완성]
        /// <summary>
        /// 접속문자열을 완성한다 — 비밀번호가 비어 있고 같은 대상을 기억하고 있으면 채워 넣는다.
        /// 이미 채워져 있거나 통합 인증이면 손대지 않는다(사용자가 방금 친 값이 언제나 우선).
        /// </summary>
        public static string Complete(ConnectionRole role, DbProviderKind kind, DbConnectionMode mode, string conn)
        {
            if (string.IsNullOrWhiteSpace(conn) || !SecretStore.NeedsPassword(conn)) return conn;
            EnsureLoaded();

            var key = KeyOf(role, kind, mode, conn);
            var hit = _user!.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                   ?? Seeds.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return conn;

            var pw = SecretStore.PasswordOf(hit.ConnStr);
            return pw.Length == 0 ? conn : SecretStore.WithPassword(conn, pw);
        }

        // ── 쓰기 ───────────────────────────────────────────────────

        // [찾기:접속기억]
        /// <summary>
        /// 이 접속을 기억한다. 붙는 데 성공했을 때만 부른다 — 실패한 문자열을 기억해 두면
        /// 다음 실행에서 같은 실패를 되풀이하게 된다.
        /// </summary>
        public static void Remember(ConnectionRole role, DbProviderKind kind, DbConnectionMode mode, string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return;
            EnsureLoaded();

            var key = KeyOf(role, kind, mode, conn);
            _hidden.Remove(key);   // 다시 붙었으면 [잊기] 는 풀린다

            var hit = _user!.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                hit.ConnStr = conn;
                hit.LastUsed = DateTime.Now;
            }
            else
            {
                _user!.Add(new ConnectionProfile
                {
                    Role = role,
                    Kind = role == ConnectionRole.Target ? DbProviderKind.MsSql : kind,
                    Mode = role == ConnectionRole.Target ? DbConnectionMode.Direct : mode,
                    ConnStr = conn,
                    LastUsed = DateTime.Now,
                });
            }
            Save();
        }

        /// <summary>목록에서 지운다(기본 항목은 지울 수 없으므로 숨김으로 기록한다).</summary>
        public static void Forget(string key)
        {
            EnsureLoaded();
            _user!.RemoveAll(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (Seeds.Any(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)))
                _hidden.Add(key);
            Save();
        }

        /// <summary>[비밀번호까지 저장] 을 껐을 때 — 이미 저장된 비밀번호까지 지운다.</summary>
        public static void ClearSecrets()
        {
            EnsureLoaded();
            foreach (var p in _user!) p.ConnStr = SecretStore.StripPassword(p.ConnStr);
            Save();
        }

        // ── 파일 ───────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_user != null) return;
            _user = new List<ConnectionProfile>();
            try
            {
                if (!File.Exists(StorePath)) return;
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(StorePath));
                if (dto == null) return;

                foreach (var e in dto.Entries ?? new List<Entry>())
                {
                    // 복호가 안 되는 항목(다른 계정·다른 PC 에서 온 파일)은 조용히 버린다.
                    var conn = SecretStore.Unprotect(e.Conn);
                    if (string.IsNullOrWhiteSpace(conn)) continue;
                    _user.Add(new ConnectionProfile
                    {
                        Role = Enum.TryParse<ConnectionRole>(e.Role, out var r) ? r : ConnectionRole.Source,
                        Kind = Enum.TryParse<DbProviderKind>(e.Kind, out var k) ? k : DbProviderKind.Oracle,
                        Mode = Enum.TryParse<DbConnectionMode>(e.Mode, out var m) ? m : DbConnectionMode.Odbc,
                        ConnStr = conn,
                        Label = e.Label ?? "",
                        LastUsed = DateTime.TryParse(e.LastUsed, out var t) ? t : DateTime.MinValue,
                    });
                }
                _hidden = new HashSet<string>(dto.Hidden ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch { /* 기억을 못 읽는 것이 작업을 막을 이유는 아니다 */ }
        }

        private static void Save()
        {
            try
            {
                bool keepPw = AppSession.Current.SaveCredentials;
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var dto = new Dto
                {
                    Entries = _user!.Select(p => new Entry
                    {
                        Role = p.Role.ToString(),
                        Kind = p.Kind.ToString(),
                        Mode = p.Mode.ToString(),
                        // [찾기:비밀번호저장] 파일로 나가는 유일한 지점 — 언제나 DPAPI 를 거친다.
                        Conn = SecretStore.Protect(keepPw ? p.ConnStr : SecretStore.StripPassword(p.ConnStr)),
                        Label = p.Label,
                        LastUsed = p.LastUsed.ToString("yyyy-MM-dd HH:mm:ss"),
                    }).ToList(),
                    Hidden = _hidden.ToList(),
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Note = "접속문자열은 Windows DPAPI(현재 사용자)로 암호화되어 있습니다. " +
                           "다른 계정·다른 PC 에서는 풀리지 않습니다.",
                };
                File.WriteAllText(StorePath,
                    JsonSerializer.Serialize(dto, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }),
                    new UTF8Encoding(false));
            }
            catch { /* 저장 실패가 작업을 막지 않는다 */ }
        }

        private sealed class Dto
        {
            public List<Entry>? Entries { get; set; }
            public List<string>? Hidden { get; set; }
            public string? SavedAt { get; set; }
            public string? Note { get; set; }
        }

        private sealed class Entry
        {
            public string? Role { get; set; }
            public string? Kind { get; set; }
            public string? Mode { get; set; }
            public string? Conn { get; set; }
            public string? Label { get; set; }
            public string? LastUsed { get; set; }
        }

        // ── 기본 항목(로컬 리그 전용) ──────────────────────────────

        // [NAIMS-ISSUE:NO-EMBEDDED-SECRET] 여기에 들어갈 수 있는 것은 이 저장소 스크립트가 만든
        //   격리 DB 뿐이다. 값을 잃으면 리그를 못 쓰므로 문서·스크립트와 같은 값을 둔다
        //   (migration_rig/FIELD_RUNBOOK.md 와 동일). 현장·실운영 계정은 여기 넣지 않는다.
        private static readonly Lazy<List<ConnectionProfile>> _seeds = new(BuildSeeds);
        private static List<ConnectionProfile> Seeds => _seeds.Value;

        private static List<ConnectionProfile> BuildSeeds() => new()
        {
            // ── 적재 대상 ──
            new ConnectionProfile
            {
                Role = ConnectionRole.Target, Seeded = true, Label = "로컬 NAIMS2",
                ConnStr = "Server=localhost;Database=NAIMS2;Trusted_Connection=yes;" +
                          "Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;",
            },

            // ── 읽는 곳: 로컬 Oracle 리그 ──
            new ConnectionProfile
            {
                Role = ConnectionRole.Source, Seeded = true, Label = "로컬 레거시(12c)",
                Kind = DbProviderKind.Oracle, Mode = DbConnectionMode.Direct,
                ConnStr = "User Id=LEGACYNAIMS;Password=naims2!;Data Source=localhost:1522/LegacyNaims;",
            },
            new ConnectionProfile
            {
                Role = ConnectionRole.Source, Seeded = true, Label = "로컬 레거시(12c) · DSN",
                Kind = DbProviderKind.Oracle, Mode = DbConnectionMode.Odbc,
                ConnStr = "DSN=naims;Uid=LEGACYNAIMS;Pwd=naims2!;",
            },
            new ConnectionProfile
            {
                Role = ConnectionRole.Source, Seeded = true, Label = "로컬 NAIMS12(SYSTEM)",
                Kind = DbProviderKind.Oracle, Mode = DbConnectionMode.Direct,
                ConnStr = "User Id=SYSTEM;Password=Naims12#2026;Data Source=localhost:1522/NAIMS12;",
            },
            new ConnectionProfile
            {
                Role = ConnectionRole.Source, Seeded = true, Label = "리허설 스키마(21c XE)",
                Kind = DbProviderKind.Oracle, Mode = DbConnectionMode.Direct,
                ConnStr = "User Id=REH;Password=Reh2026x;Data Source=localhost:1521/XEPDB1;",
            },

            // ── 읽는 곳: 로컬 MsSQL 사본 ──
            new ConnectionProfile
            {
                Role = ConnectionRole.Source, Seeded = true, Label = "로컬 NAIMS 사본",
                Kind = DbProviderKind.MsSql, Mode = DbConnectionMode.Direct,
                ConnStr = "Server=localhost,1433;Database=NAIMS;Trusted_Connection=yes;" +
                          "Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;",
            },
        };
    }
}
