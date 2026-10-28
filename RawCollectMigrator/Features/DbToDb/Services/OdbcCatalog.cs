using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:ODBC탐색] [GUIDE:ODBC-CATALOG] [찾기:DSN] [찾기:tnsnames]
    //  ▣ 이 파일 = "이 PC 가 무엇에 붙을 수 있는가" 의 로컬 근거를 모으는 곳.
    //    · Drivers()/DataSources() : 레지스트리에서 ODBC 드라이버·DSN 열거 (32/64 양쪽)
    //    · DSN 은 이름만이 아니라 Server/Database/TNS별칭까지 전개한다 — 그래야 접속문자열을 만든다.
    //    · TnsCatalog : tnsnames.ora 를 파싱해 "별칭 → HOST/PORT/SERVICE" 를 얻는다.
    //      Oracle DSN 의 ServerName 은 TNS 별칭일 뿐이라, 이걸 풀어야 실제 호스트를 안다.
    //  ▶ 현장 최빈 사고: 앱은 64비트인데 Oracle ODBC 드라이버가 32비트만 설치 → DSN 이 "안 보임".
    //    그래서 양쪽을 다 읽고 UsableHere 로 사용 가능 여부를 구분해 보여준다.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>등록된 DSN 한 건(레지스트리 전개 결과 포함).</summary>
    /// <param name="Name">DSN 별칭.</param>
    /// <param name="Driver">드라이버 표시 이름.</param>
    /// <param name="Scope">"시스템" 또는 "사용자".</param>
    /// <param name="Bitness">이 DSN 이 등록된 레지스트리 뷰("64비트"/"32비트").</param>
    /// <param name="Server">SQL Server 계열 DSN 의 Server 값(없으면 "").</param>
    /// <param name="Database">SQL Server 계열 DSN 의 Database 값(없으면 "").</param>
    /// <param name="TnsAlias">Oracle 계열 DSN 의 ServerName(=TNS 별칭, 없으면 "").</param>
    /// <param name="UserId">DSN 에 저장된 계정(없으면 ""). 비밀번호는 읽지 않는다.</param>
    /// <param name="Dbq">
    /// Oracle DSN 의 DBQ 값. TNS 별칭일 수도, EZConnect(host:port/service)일 수도 있다.
    /// [찾기:DBQ] Oracle ODBC 관리자가 만든 DSN 은 ServerName 이 아니라 여기에 대상을 넣는다 —
    /// 이걸 안 읽으면 "DSN 은 보이는데 어디에 붙는지 모르는" 상태가 된다(2026-08-18 실측 반영).
    /// </param>
    public sealed record OdbcDsn(
        string Name, string Driver, string Scope, string Bitness,
        string Server = "", string Database = "", string TnsAlias = "", string UserId = "",
        string Dbq = "")
    {
        /// <summary>이 프로세스 비트수에서 실제로 사용할 수 있는 DSN 인지.</summary>
        public bool UsableHere => Bitness == OdbcCatalog.Bitness;

        /// <summary>드라이버 이름으로 추정한 DB 종류(판정 불가면 null).</summary>
        public DbProviderKind? GuessKind => OdbcCatalog.GuessKindFromDriver(Driver);

        /// <summary>목록 표시용 한 줄 요약.</summary>
        public string Display
        {
            get
            {
                var where = Server.Length > 0
                    ? Server + (Database.Length > 0 ? "/" + Database : "")
                    : (Dbq.Length > 0 ? Dbq
                       : (TnsAlias.Length > 0 ? "TNS:" + TnsAlias : ""));
                return $"{Name}   [{Scope}·{Bitness}{(UsableHere ? "" : " ✗사용불가")}]   {Driver}"
                     + (where.Length > 0 ? "   → " + where : "");
            }
        }
    }

    /// <summary>설치된 ODBC 드라이버 한 건.</summary>
    public sealed record OdbcDriver(string Name, string Bitness)
    {
        public bool UsableHere => Bitness == OdbcCatalog.Bitness;
        public DbProviderKind? GuessKind => OdbcCatalog.GuessKindFromDriver(Name);
        public string Display => $"{Name}   [{Bitness}{(UsableHere ? "" : " ✗사용불가")}]";
    }

    /// <summary>
    /// 이 PC에 설치된 ODBC 드라이버·등록된 DSN 을 Windows 레지스트리에서 읽는다.
    /// 접속문자열의 Driver={...} 이름과 DSN 별칭을 사용자가 직접 알 필요 없이 클릭으로 채우게 한다.
    ///
    /// ⚠ 비트수(32/64) 주의: 실제 접속은 프로세스 비트수와 같은 드라이버로만 된다.
    ///    그러나 "왜 안 보이나"를 진단하려면 반대편도 보여야 하므로 양쪽을 모두 열거하고
    ///    <see cref="OdbcDsn.UsableHere"/> 로 구분한다.
    /// </summary>
    public static class OdbcCatalog
    {
        /// <summary>현재 프로세스 비트수 라벨(UI 안내용).</summary>
        public static string Bitness => Environment.Is64BitProcess ? "64비트" : "32비트";

        private static readonly (RegistryView View, string Label)[] Views =
        {
            (RegistryView.Registry64, "64비트"),
            (RegistryView.Registry32, "32비트"),
        };

        /// <summary>드라이버 이름으로 DB 종류를 추정한다. 모르면 null.</summary>
        public static DbProviderKind? GuessKindFromDriver(string? driver)
        {
            var d = (driver ?? "").ToLowerInvariant();
            if (d.Length == 0) return null;
            // 드라이버 이름 대신 DLL 경로가 들어오는 DSN 도 있다(예: ...\BIN\SQORA32.DLL).
            if (d.Contains("oracle") || d.Contains("sqora")) return DbProviderKind.Oracle;
            if (d.Contains("sql server") || d.Contains("sqlncli") || d.Contains("msodbcsql"))
                return DbProviderKind.MsSql;
            return null;
        }

        /// <summary>설치된 ODBC 드라이버 목록(32/64 양쪽, 중복 제거).</summary>
        public static IReadOnlyList<OdbcDriver> DriversDetailed()
        {
            var list = new List<OdbcDriver>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (view, label) in Views)
            {
                // 32/64 가 같은 물리 키를 보는 환경에서는 중복이 나므로 이름+비트수로 걸러낸다.
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
                    if (k == null) continue;
                    foreach (var name in k.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!seen.Add(name + "|" + label)) continue;
                        list.Add(new OdbcDriver(name, label));
                    }
                }
                catch { /* 접근 불가 뷰는 건너뛴다 */ }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>설치된 ODBC 드라이버 이름 목록(이 프로세스에서 쓸 수 있는 것만).</summary>
        public static IReadOnlyList<string> Drivers()
        {
            var list = new List<string>();
            foreach (var d in DriversDetailed())
                if (d.UsableHere) list.Add(d.Name);
            return list;
        }

        /// <summary>등록된 DSN 목록(시스템+사용자 × 64/32비트). 각 DSN 의 Server/Database/TNS별칭까지 전개한다.</summary>
        public static IReadOnlyList<OdbcDsn> DataSources()
        {
            var list = new List<OdbcDsn>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (view, label) in Views)
            {
                Collect(RegistryHive.LocalMachine, "시스템", view, label, list, seen);
                Collect(RegistryHive.CurrentUser, "사용자", view, label, list, seen);
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void Collect(
            RegistryHive hive, string scope, RegistryView view, string bitness,
            List<OdbcDsn> into, HashSet<string> seen)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!seen.Add($"{name}|{scope}|{bitness}")) continue;

                    var driver = Convert.ToString(k.GetValue(name)) ?? "";
                    string server = "", database = "", tns = "", uid = "", dbq = "";
                    try
                    {
                        using var d = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\" + name);
                        if (d != null)
                        {
                            // SQL Server 계열: Server / Database.  Oracle 계열: ServerName(=TNS 별칭).
                            server   = Str(d, "Server");
                            database = Str(d, "Database");
                            tns      = Str(d, "ServerName");
                            dbq      = Str(d, "DBQ");          // [찾기:DBQ] Oracle DSN 의 실제 대상
                            uid      = Str(d, "UserID");
                            if (uid.Length == 0) uid = Str(d, "LastUser");
                            // 비밀번호는 의도적으로 읽지 않는다.
                        }
                    }
                    catch { /* 세부 키 접근 실패는 이름만으로 진행 */ }

                    into.Add(new OdbcDsn(name, driver, scope, bitness, server, database, tns, uid, dbq));
                }
            }
            catch { /* 무시 */ }
        }

        private static string Str(RegistryKey k, string name)
        {
            try { return (Convert.ToString(k.GetValue(name)) ?? "").Trim(); }
            catch { return ""; }
        }

        // [찾기:IM002] [찾기:DSN진단]
        /// <summary>
        /// ODBC 접속 실패(특히 IM002 "데이터 원본 이름이 없음") 원인을 이 프로세스 관점에서 진단한다.
        ///
        /// 왜 필요한가: IM002 는 "DSN 이 없다"가 아니라 <b>"이 프로세스가 그 DSN 을 못 본다"</b>는 뜻이다.
        /// 원인이 여러 갈래인데 메시지만으로는 구분이 안 된다.
        ///   · 비트수 불일치      — 64비트 앱인데 DSN/드라이버가 32비트에만 등록됨
        ///   · 권한(사용자) 차이  — 관리자로 실행하면 HKCU 가 관리자 것이라 '사용자 DSN'이 안 보인다
        ///   · 이름 오타·미등록
        /// 그래서 지금 이 프로세스가 실제로 무엇을 보고 있는지를 그대로 찍어 준다.
        /// </summary>
        /// <param name="connStr">실패한 접속문자열(DSN 이름 추출용).</param>
        /// <returns>여러 줄 진단 문자열.</returns>
        public static string DiagnoseDsn(string connStr)
        {
            var wanted = System.Text.RegularExpressions.Regex
                .Match(connStr ?? "", @"(?i)\bDSN\s*=\s*([^;]+)").Groups[1].Value.Trim();

            var sb = new System.Text.StringBuilder();
            sb.Append("[진단] 프로세스 ").Append(Bitness);
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                bool admin = pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                sb.Append(" · 사용자 ").Append(id.Name);
                if (admin) sb.Append(" · ⚠ 관리자 권한으로 실행 중 — 이 경우 '사용자 DSN'은 보이지 않습니다");
            }
            catch { }

            var all = DataSources();
            DbProviderKind? wantKind = null;

            if (wanted.Length > 0)
            {
                var hit = all.FirstOrDefault(d => d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                wantKind = hit?.GuessKind;
                sb.Append("\n  · 찾는 DSN: '").Append(wanted).Append("' → ");
                if (hit == null) sb.Append("이 프로세스에서 보이지 않음");
                else if (!hit.UsableHere)
                    sb.Append($"{hit.Bitness} 로 등록됨 — 이 앱({Bitness})에서는 쓸 수 없음");
                else sb.Append($"보임 ({hit.Scope}·{hit.Bitness}, {hit.Driver})");
            }

            // [찾기:DSN대안] 그냥 전부 나열하면 사람이 고를 수 없다 — 쓸 수 있는 것만, 시스템 DSN 을 앞세워 권한다.
            //   시스템 DSN(HKLM)은 프로세스 권한과 무관하게 보이지만, 사용자 DSN(HKCU)은
            //   관리자 실행 등으로 계정이 바뀌면 사라진 것처럼 보인다 — 그래서 시스템 DSN 이 더 안전하다.
            var usable = all.Where(d => d.UsableHere
                                     && !d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                     && d.GuessKind != null
                                     && (wantKind == null || d.GuessKind == wantKind))
                            .OrderByDescending(d => d.Scope == "시스템")
                            .ToList();
            if (usable.Count > 0)
            {
                var best = usable[0];
                sb.Append("\n  · 쓸 수 있는 대안: ")
                  .Append(string.Join(", ", usable.Take(5).Select(d => $"{d.Name}[{d.Scope}]")));
                sb.Append("\n  · 바로 고치려면 접속문자열의 DSN 을 '").Append(best.Name).Append("' 로 바꾸세요")
                  .Append(best.Scope == "시스템" ? " (시스템 DSN 이라 권한에 관계없이 보입니다)." : ".");
            }
            else
            {
                sb.Append("\n  · 보이는 DSN ").Append(all.Count).Append("개: ")
                  .Append(all.Count == 0 ? "(없음)" : string.Join(", ",
                      all.Take(8).Select(d => $"{d.Name}[{d.Scope}·{d.Bitness}]")));
            }

            // [NAIMS-ISSUE:ORACLE-DIRECT-12C] ODBC 는 DSN 등록·드라이버 비트수·프로세스 권한이 모두
            //   맞아야 동작한다. 셋 중 하나만 어긋나도 IM002 로 막히고 원인 구분이 어렵다.
            //   Oracle 은 관리형 드라이버로 그 의존을 통째로 없앨 수 있으므로 그쪽을 먼저 권한다.
            if (wantKind == DbProviderKind.Oracle || wantKind == null)
                sb.Append("\n  · ★ 권장: [다이렉트로 전환] — Oracle 관리형 드라이버는 DSN·클라이언트 설치가 " +
                          "필요 없어 이 문제 자체가 생기지 않습니다(12c 접속 확인됨).");
            sb.Append("\n  · 또는 [DSN 없이 접속] 으로 드라이버를 직접 지정하거나, " +
                      "ODBC 데이터 원본 관리자(" + Bitness + ")에서 DSN 을 등록하세요.");
            return sb.ToString();
        }

        // [찾기:다이렉트전환]
        /// <summary>
        /// ODBC 접속문자열(DSN= 또는 DBQ=)을 Oracle 다이렉트(Easy Connect) 형태로 바꾼다.
        /// DSN 의 DBQ(host:port/service)를 그대로 Data Source 로 옮기고 계정·비밀번호를 보존한다.
        /// 대상을 알아낼 수 없으면 빈 문자열을 돌려준다(호출부가 안내).
        /// </summary>
        /// <param name="connStr">ODBC 접속문자열.</param>
        /// <returns>"User Id=..;Password=..;Data Source=host:port/service;" 또는 빈 문자열.</returns>
        public static string ToOracleDirect(string connStr)
        {
            string Val(string key) => System.Text.RegularExpressions.Regex
                .Match(connStr ?? "", $@"(?i)\b{key}\s*=\s*([^;]*)").Groups[1].Value.Trim();

            var target = Val("DBQ");
            if (target.Length == 0)
            {
                var dsnName = Val("DSN");
                if (dsnName.Length > 0)
                {
                    var dsn = DataSources().FirstOrDefault(d => d.Name.Equals(dsnName, StringComparison.OrdinalIgnoreCase));
                    target = dsn?.Dbq ?? dsn?.TnsAlias ?? "";
                }
            }
            if (target.Length == 0) return "";

            var uid = Val("Uid"); if (uid.Length == 0) uid = Val("User Id");
            var pwd = Val("Pwd"); if (pwd.Length == 0) pwd = Val("Password");
            return $"User Id={uid};Password={pwd};Data Source={target};";
        }

        // [찾기:DSN없이접속]
        /// <summary>
        /// "DSN=이름;Uid=..;Pwd=.." 를 DSN 없이 붙는 형태로 바꾼다.
        /// 레지스트리에서 그 DSN 의 드라이버·대상(DBQ/Server)을 읽어 Driver={...} 형태로 펼친다.
        /// DSN 자체를 못 찾으면 원본을 그대로 돌려준다(호출부가 판단하도록).
        /// </summary>
        /// <param name="connStr">DSN 형식 접속문자열.</param>
        /// <returns>DSN-less 접속문자열. 변환 불가면 원본.</returns>
        public static string ToDsnLess(string connStr)
        {
            var name = System.Text.RegularExpressions.Regex
                .Match(connStr ?? "", @"(?i)\bDSN\s*=\s*([^;]+)").Groups[1].Value.Trim();
            if (name.Length == 0) return connStr ?? "";

            var dsn = DataSources().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (dsn == null) return connStr ?? "";

            // 드라이버 값이 DLL 경로면 드라이버 '이름'으로 바꿔 준다(Driver={이름} 형식이라야 한다).
            var driverName = dsn.Driver;
            if (driverName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var byName = DriversDetailed().FirstOrDefault(d => d.UsableHere && d.GuessKind == dsn.GuessKind);
                if (byName != null) driverName = byName.Name;
            }

            var target = dsn.Dbq.Length > 0 ? $"DBQ={dsn.Dbq};"
                       : dsn.Server.Length > 0 ? $"Server={dsn.Server};" + (dsn.Database.Length > 0 ? $"Database={dsn.Database};" : "")
                       : dsn.TnsAlias.Length > 0 ? $"DBQ={dsn.TnsAlias};" : "";

            // 계정·비밀번호 등 나머지 토큰은 보존한다.
            var kept = (connStr ?? "").Split(';')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0 && !t.StartsWith("DSN=", StringComparison.OrdinalIgnoreCase));

            return $"Driver={{{driverName}}};{target}" + string.Join(";", kept) + ";";
        }

        /// <summary>
        /// 기존 접속문자열에서 Driver={...} / DSN=... 토큰만 골라내고 새 토큰을 앞에 붙인다.
        /// 서버·계정 등 나머지 값은 보존한다. (예: Driver 만 바꿔 끼우기)
        /// </summary>
        public static string InjectTarget(string connStr, string newToken)
        {
            var kept = new List<string>();
            foreach (var part in (connStr ?? "").Split(';'))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                var key = t.Split('=', 2)[0].Trim();
                if (key.Equals("Driver", StringComparison.OrdinalIgnoreCase)) continue;   // 기존 Driver 제거
                if (key.Equals("DSN", StringComparison.OrdinalIgnoreCase)) continue;       // 기존 DSN 제거
                kept.Add(t);
            }
            var rest = string.Join(";", kept);
            return newToken.TrimEnd(';') + ";" + (rest.Length > 0 ? rest + ";" : "");
        }
    }

    /// <summary>이 PC에 설치된 Oracle 인스턴스 한 건(서비스 등록 기준).</summary>
    /// <param name="Sid">인스턴스 SID(서비스명 OracleService&lt;SID&gt; 에서 추출).</param>
    /// <param name="StartType">서비스 시작 유형(자동/수동/사용 안 함).</param>
    /// <param name="InstanceRunning">oracle.exe 가 돌고 있는지.</param>
    /// <param name="ListenerRunning">tnslsnr.exe 가 돌고 있는지.</param>
    /// <param name="ServiceName">Windows 서비스 이름(시작 안내에 쓴다).</param>
    public sealed record LocalOracleInstance(
        string Sid, string StartType, bool InstanceRunning, bool ListenerRunning, string ServiceName)
    {
        /// <summary>지금 접속을 시도할 수 있는 상태인지(인스턴스·리스너 모두 구동 중).</summary>
        public bool Up => InstanceRunning && ListenerRunning;

        /// <summary>사람이 읽는 상태 설명.</summary>
        public string StateText => Up
            ? "구동 중"
            : (!InstanceRunning && !ListenerRunning ? "인스턴스·리스너 정지"
               : (!InstanceRunning ? "인스턴스 정지" : "리스너 정지"));
    }

    /// <summary>tnsnames.ora 항목 한 건.</summary>
    /// <param name="Alias">TNS 별칭(DSN 의 ServerName 과 대조된다).</param>
    /// <param name="Host">ADDRESS 의 HOST.</param>
    /// <param name="Port">ADDRESS 의 PORT(없으면 1521).</param>
    /// <param name="Service">SERVICE_NAME 또는 SID.</param>
    /// <param name="SourceFile">이 항목이 나온 파일 경로.</param>
    public sealed record TnsEntry(string Alias, string Host, int Port, string Service, string SourceFile)
    {
        public string Display => $"{Alias}   → {Host}:{Port}/{Service}";
    }

    /// <summary>
    /// tnsnames.ora 를 찾아 파싱한다.
    ///
    /// Oracle DSN 은 실제 호스트를 갖고 있지 않고 TNS 별칭만 저장한다. 그래서 별칭을 풀지 못하면
    /// "이 PC 가 어느 Oracle 에 붙는가"를 알 수 없다. 탐색 순서는 Oracle 클라이언트 관례를 따른다:
    ///   ① %TNS_ADMIN%
    ///   ② 레지스트리 HKLM\SOFTWARE\ORACLE\KEY_* 의 ORACLE_HOME\network\admin
    ///   ③ %ORACLE_HOME%\network\admin
    /// </summary>
    public static class TnsCatalog
    {
        /// <summary>탐색 대상 tnsnames.ora 경로 목록(존재하는 것만, 중복 제거).</summary>
        public static IReadOnlyList<string> LocateFiles()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? dir)
            {
                if (string.IsNullOrWhiteSpace(dir)) return;
                try
                {
                    var p = Path.Combine(dir, "tnsnames.ora");
                    if (File.Exists(p) && seen.Add(Path.GetFullPath(p))) found.Add(p);
                }
                catch { }
            }

            TryAdd(Environment.GetEnvironmentVariable("TNS_ADMIN"));

            foreach (var home in OracleHomes())
            {
                TryAdd(Path.Combine(home, "network", "admin"));
                TryAdd(Path.Combine(home, "NETWORK", "ADMIN"));
            }

            var oh = Environment.GetEnvironmentVariable("ORACLE_HOME");
            if (!string.IsNullOrWhiteSpace(oh)) TryAdd(Path.Combine(oh, "network", "admin"));

            return found;
        }

        /// <summary>레지스트리에서 ORACLE_HOME 경로들을 모은다(32/64 양쪽).</summary>
        public static IReadOnlyList<string> OracleHomes()
        {
            var homes = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var oracle = baseKey.OpenSubKey(@"SOFTWARE\ORACLE");
                    if (oracle == null) continue;
                    foreach (var sub in oracle.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase)) continue;
                        using var k = oracle.OpenSubKey(sub);
                        var home = Convert.ToString(k?.GetValue("ORACLE_HOME")) ?? "";
                        if (home.Length > 0 && Directory.Exists(home) && seen.Add(home)) homes.Add(home);
                    }
                }
                catch { }
            }
            return homes;
        }

        // [찾기:로컬오라클]
        /// <summary>
        /// 이 PC에 설치된 Oracle 인스턴스를 레지스트리 서비스 키에서 찾는다(서비스가 꺼져 있어도 잡힌다).
        /// 반환값은 (SID, 서비스 시작유형, 인스턴스 구동 중, 리스너 구동 중).
        ///
        /// 왜 필요한가: 리스너가 꺼져 있으면 포트 스캔·접속 판정으로는 아무것도 안 잡혀
        /// "이 PC 에 Oracle 이 없다"로 오해하게 된다. 실제로는 설치돼 있고 켜기만 하면 되는 경우가 많다.
        /// </summary>
        public static IReadOnlyList<LocalOracleInstance> LocalInstances()
        {
            var list = new List<LocalOracleInstance>();
            bool instanceUp = ProcessRunning("oracle");
            bool listenerUp = ProcessRunning("tnslsnr");

            try
            {
                using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (svc == null) return list;

                foreach (var name in svc.GetSubKeyNames())
                {
                    if (!name.StartsWith("OracleService", StringComparison.OrdinalIgnoreCase)) continue;
                    var sid = name["OracleService".Length..];
                    if (sid.Length == 0) continue;

                    int start = 3;
                    try
                    {
                        using var k = svc.OpenSubKey(name);
                        start = Convert.ToInt32(k?.GetValue("Start") ?? 3);
                    }
                    catch { }

                    list.Add(new LocalOracleInstance(
                        sid,
                        start switch { 2 => "자동", 3 => "수동", 4 => "사용 안 함", _ => "?" },
                        instanceUp, listenerUp,
                        "OracleService" + sid));
                }
            }
            catch { }
            return list;
        }

        /// <summary>listener.ora 에 적힌 수신 포트 목록(중복 제거). 없으면 빈 목록.</summary>
        public static IReadOnlyList<int> ListenerPorts()
        {
            var ports = new List<int>();
            foreach (var home in OracleHomes())
            {
                foreach (var dir in new[] { Path.Combine(home, "network", "admin"), Path.Combine(home, "NETWORK", "ADMIN") })
                {
                    try
                    {
                        var f = Path.Combine(dir, "listener.ora");
                        if (!File.Exists(f)) continue;
                        foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\(\s*PORT\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase))
                            if (int.TryParse(m.Groups[1].Value, out var p) && !ports.Contains(p)) ports.Add(p);
                    }
                    catch { }
                }
            }
            return ports;
        }

        private static bool ProcessRunning(string name)
        {
            try { return System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
            catch { return false; }
        }

        /// <summary>찾은 모든 tnsnames.ora 를 파싱해 항목 목록을 돌려준다.</summary>
        public static IReadOnlyList<TnsEntry> ReadAll()
        {
            var list = new List<TnsEntry>();
            foreach (var f in LocateFiles())
            {
                try { list.AddRange(Parse(File.ReadAllText(f), f)); }
                catch { /* 읽기 실패한 파일은 건너뛴다 */ }
            }
            return list;
        }

        private static readonly Regex HostRx    = new(@"\(\s*HOST\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PortRx    = new(@"\(\s*PORT\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ServiceRx = new(@"\(\s*(?:SERVICE_NAME|SID)\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// tnsnames.ora 본문을 파싱한다. 괄호 균형으로 항목 경계를 잡고, 각 항목에서
        /// HOST/PORT/SERVICE_NAME(또는 SID)을 정규식으로 뽑는다.
        /// 주석(#)과 IFILE 등 지시문은 무시한다.
        /// </summary>
        public static IReadOnlyList<TnsEntry> Parse(string text, string sourceFile = "")
        {
            var result = new List<TnsEntry>();
            if (string.IsNullOrEmpty(text)) return result;

            // 주석 제거 (줄 단위 '#')
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw;
                int h = line.IndexOf('#');
                if (h >= 0) line = line[..h];
                sb.Append(line).Append('\n');
            }
            var body = sb.ToString();

            int i = 0;
            while (i < body.Length)
            {
                // 별칭 = 여는 괄호 앞의 '=' 왼쪽 토큰
                int open = body.IndexOf('(', i);
                if (open < 0) break;

                int eq = body.LastIndexOf('=', open);
                if (eq < 0) { i = open + 1; continue; }

                int start = eq - 1;
                while (start >= 0 && body[start] != '\n' && body[start] != ')') start--;
                var alias = body[(start + 1)..eq].Trim();
                // 별칭에 콤마(여러 별칭)가 붙을 수 있다 — 첫 것만 쓴다.
                if (alias.Contains(',')) alias = alias.Split(',')[0].Trim();

                // 괄호 균형으로 항목 본문 끝을 찾는다.
                int depth = 0, j = open;
                for (; j < body.Length; j++)
                {
                    if (body[j] == '(') depth++;
                    else if (body[j] == ')') { depth--; if (depth == 0) { j++; break; } }
                }
                var entry = body[open..Math.Min(j, body.Length)];
                i = j > open ? j : open + 1;

                if (alias.Length == 0 || alias.Contains('(') || alias.Contains(')')) continue;
                if (alias.Equals("IFILE", StringComparison.OrdinalIgnoreCase)) continue;

                var hm = HostRx.Match(entry);
                if (!hm.Success) continue;   // ADDRESS 가 없는 항목(파라미터 블록 등)은 제외
                var pm = PortRx.Match(entry);
                var sm = ServiceRx.Match(entry);

                // [찾기:리스너정의제외] CONNECT_DATA 없이 ADDRESS 만 있는 항목은 "접속 대상"이 아니라
                //   리스너 자신의 수신 주소 정의다(예: LISTENER_NAIMS12 = (ADDRESS=...(HOST=0.0.0.0)(PORT=1522))).
                //   이걸 후보로 내보내면 0.0.0.0 같은 접속 불가 주소가 목록에 섞인다.
                if (!sm.Success) continue;

                var host = hm.Groups[1].Value;
                if (host is "0.0.0.0" or "::" ) continue;   // 바인드 전용 주소

                result.Add(new TnsEntry(
                    alias,
                    host,
                    pm.Success && int.TryParse(pm.Groups[1].Value, out var port) ? port : 1521,
                    sm.Groups[1].Value,
                    sourceFile));
            }
            return result;
        }
    }
}
