using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace RawCollectMigrator.Features.DbToDb
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:연결탐색] [GUIDE:ENDPOINT-DISCOVERY] [찾기:연결탐색] [찾기:포트스캔]
    //  ▣ 이 파일 = "이 망에 붙을 수 있는 DB 가 어디 있나" 를 4개 층으로 찾는다.
    //     A층 로컬 카탈로그 : ODBC DSN + tnsnames.ora        → 부작용 0
    //     B층 무설정 발견   : SQL Server Browser UDP 1434    → 브로드캐스트 1회
    //     C층 서브넷 스캔   : TCP 1433/1521 + 프로토콜 지문  → ★사전 승인 필요
    //     D층 접속 판정     : 자격증명 1회 시도 + 에러 분류  → ★계정 잠금 주의
    //
    //  ▶ "포트가 열렸다" ≠ "DB 다" ≠ "붙을 수 있다" — 세 가지를 ProbeState 로 구분한다.
    //  ▶ 에러코드가 곧 정보다: 로그인 실패(18456/ORA-01017)는 "서버 실재 확정"이라는 뜻이라
    //    Unreachable 이 아니라 AuthRequired 로 승격한다.
    //  ▶ [찾기:잠금가드] 계정 잠금(18486/ORA-28000)이 한 번이라도 나온 호스트는
    //    이후 시도를 전면 차단한다. 서비스 계정을 잠그면 현장 운영이 멈춘다.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>엔드포인트 판정 상태. 아래로 갈수록 확신이 강하다.</summary>
    public enum ProbeState
    {
        /// <summary>아직 확인하지 않음(카탈로그에서 이름만 읽은 상태).</summary>
        Unknown,
        /// <summary>TCP 연결 자체가 안 됨.</summary>
        Unreachable,
        /// <summary>설치돼 있으나 서비스가 정지 중 — 켜기만 하면 된다. [찾기:로컬오라클]</summary>
        ServiceStopped,
        /// <summary>포트는 열려 있으나 DB 프로토콜 확인 전.</summary>
        PortOpen,
        /// <summary>프로토콜 응답 확인 — DB 서버가 확실히 있다.</summary>
        Reachable,
        /// <summary>서버 실재 확정. 자격증명만 맞으면 붙는다.</summary>
        AuthRequired,
        /// <summary>로그인은 되나 지정한 DB/서비스가 없다.</summary>
        WrongDatabase,
        /// <summary>계정 잠김 — 더 시도하면 안 된다.</summary>
        Locked,
        /// <summary>실제 접속 성공.</summary>
        Connected,
    }

    /// <summary>탐색으로 찾은 DB 접속 후보 한 건.</summary>
    public sealed class DbEndpoint : INotifyPropertyChanged
    {
        /// <summary>DB 종류(MsSql/Oracle).</summary>
        public DbProviderKind Kind { get; init; }

        /// <summary>호스트명 또는 IP. DSN 만 있고 호스트를 모르면 빈 문자열.</summary>
        public string Host { get; init; } = "";

        /// <summary>TCP 포트. 모르면 0.</summary>
        public int Port { get; init; }

        /// <summary>MsSQL 명명 인스턴스명 또는 Oracle SERVICE_NAME/SID.</summary>
        public string Instance { get; init; } = "";

        /// <summary>DSN 별칭(ODBC 경유 후보일 때만).</summary>
        public string Dsn { get; init; } = "";

        /// <summary>어디서 알아냈는지("ODBC-DSN" / "tnsnames" / "UDP1434" / "PortScan" / "수동").</summary>
        public string Origin { get; init; } = "";

        private string _version = "";
        /// <summary>지문 채취로 알아낸 버전(모르면 빈 문자열).</summary>
        public string Version { get => _version; set => Set(ref _version, value); }

        private ProbeState _state = ProbeState.Unknown;
        public ProbeState State
        {
            get => _state;
            set { if (Set(ref _state, value)) { OnChanged(nameof(StateText)); OnChanged(nameof(IsUsable)); } }
        }

        private string _detail = "";
        /// <summary>판정 근거·에러 메시지.</summary>
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        /// <summary>이관에 곧바로 쓸 수 있는 상태인지(접속 성공).</summary>
        public bool IsUsable => State == ProbeState.Connected;

        public string KindText => Kind == DbProviderKind.Oracle ? "Oracle" : "MsSQL";

        public string Target => Dsn.Length > 0
            ? $"DSN={Dsn}" + (Host.Length > 0 ? $" ({Host}:{Port})" : "")
            : (Host.Length > 0 ? $"{Host}:{Port}" : "(불명)")
              + (Instance.Length > 0 ? "/" + Instance : "");

        public string StateText => State switch
        {
            ProbeState.Unknown       => "미확인",
            ProbeState.Unreachable   => "✗ 미도달",
            ProbeState.ServiceStopped => "⏻ 서비스 정지",
            ProbeState.PortOpen      => "· 포트 열림",
            ProbeState.Reachable     => "○ 서버 응답",
            ProbeState.AuthRequired  => "◐ 계정 필요",
            ProbeState.WrongDatabase => "◐ DB명 불일치",
            ProbeState.Locked        => "⚠ 계정 잠김",
            ProbeState.Connected     => "✅ 접속 성공",
            _ => "",
        };

        /// <summary>
        /// 이 엔드포인트로 접속하기 위한 접속문자열을 만든다.
        /// 비밀번호를 넘기지 않으면 자리만 비워 둔 템플릿을 돌려준다(화면 표시용).
        /// </summary>
        /// <param name="mode">ODBC/다이렉트. DSN 후보는 ODBC 만 의미가 있다.</param>
        /// <param name="user">계정(비우면 템플릿).</param>
        /// <param name="password">비밀번호(비우면 템플릿).</param>
        /// <param name="integrated">MsSQL 통합 인증 사용 여부.</param>
        public string BuildConnectionString(
            DbConnectionMode mode, string user = "", string password = "", bool integrated = false)
        {
            if (Kind == DbProviderKind.Oracle)
            {
                if (mode == DbConnectionMode.Odbc)
                {
                    var dsn = Dsn.Length > 0 ? Dsn : Instance;
                    return $"DSN={dsn};Uid={user};Pwd={password};";
                }
                var svc = Instance.Length > 0 ? Instance : "XE";
                return $"User Id={user};Password={password};Data Source={Host}:{(Port > 0 ? Port : 1521)}/{svc};";
            }

            // MsSQL
            string server = Instance.Length > 0 && Port == 0
                ? $"{Host}\\{Instance}"
                : $"{Host},{(Port > 0 ? Port : 1433)}";

            if (mode == DbConnectionMode.Odbc)
            {
                if (Dsn.Length > 0) return $"DSN={Dsn};Uid={user};Pwd={password};";
                var auth = integrated ? "Trusted_Connection=Yes;" : $"Uid={user};Pwd={password};";
                return $"Driver={{ODBC Driver 17 for SQL Server}};Server={server};Database=master;{auth}TrustServerCertificate=Yes;";
            }
            var a = integrated ? "Trusted_Connection=True;" : $"User Id={user};Password={password};";
            return $"Server=tcp:{server};Database=master;{a}Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(f, v)) return false;
            f = v; OnChanged(n); return true;
        }
    }

    /// <summary>A~D 4개 층으로 접속 가능한 DB 엔드포인트를 찾는다.</summary>
    public static class EndpointDiscovery
    {
        /// <summary>MsSQL 기본 포트.</summary>
        public const int MsSqlPort = 1433;
        /// <summary>Oracle 리스너 기본 포트.</summary>
        public const int OraclePort = 1521;

        /// <summary>스캔 기본 포트 집합(현장 관행상 쓰이는 대체 포트 포함).</summary>
        public static readonly int[] DefaultScanPorts = { 1433, 1521, 1522, 1526 };

        // [찾기:잠금가드] 계정 잠김이 관측된 호스트 — 이후 어떤 시도도 하지 않는다.
        private static readonly HashSet<string> LockedHosts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>잠금 가드에 걸린 호스트 목록(진단 표시용).</summary>
        public static IReadOnlyCollection<string> LockedOut => LockedHosts;

        /// <summary>잠금 가드를 해제한다(사용자가 계정 잠금을 푼 뒤 재시도할 때만).</summary>
        public static void ClearLockGuard() => LockedHosts.Clear();

        // ════════════════════════════════════════════════════════════════
        //  A층 — 로컬 카탈로그 (부작용 0)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 이 PC의 ODBC DSN 과 tnsnames.ora 를 합쳐 접속 후보를 만든다.
        /// Oracle DSN 의 TNS 별칭은 tnsnames 항목과 대조해 실제 호스트/포트/서비스로 전개한다.
        /// 네트워크를 전혀 건드리지 않는다.
        /// </summary>
        public static List<DbEndpoint> FromLocalCatalog()
        {
            // 설치돼 있으나 꺼져 있는 로컬 Oracle 도 후보에 올린다(없다고 오해하지 않게).
            var list = FromLocalOracleServices();
            list.AddRange(FromLocalSqlServerServices());   // [찾기:로컬MSSQL]
            var tns = TnsCatalog.ReadAll();
            var tnsByAlias = new Dictionary<string, TnsEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tns) tnsByAlias[t.Alias] = t;

            // ① 등록된 DSN
            foreach (var d in OdbcCatalog.DataSources())
            {
                var kind = d.GuessKind;
                if (kind == null) continue;   // DB 계열이 아닌 DSN(엑셀·텍스트 등)은 제외

                string host = "", instance = "";
                int port = 0;

                if (kind == DbProviderKind.Oracle)
                {
                    // [찾기:DBQ] Oracle DSN 의 대상은 두 형태다.
                    //   ① ServerName / DBQ 가 TNS 별칭  → tnsnames 로 전개
                    //   ② DBQ 가 EZConnect(host:port/service) → 그 자체가 접속 대상
                    var target = d.TnsAlias.Length > 0 ? d.TnsAlias : d.Dbq;

                    if (TryParseEzConnect(target, out var eh, out var ep, out var es))
                    {
                        host = eh; port = ep; instance = es;
                    }
                    else if (target.Length > 0 && tnsByAlias.TryGetValue(target, out var e))
                    {
                        host = e.Host; port = e.Port; instance = e.Service;
                    }
                    else instance = target;
                }
                else
                {
                    // Server 값은 "호스트,포트" 또는 "호스트\인스턴스" 형태일 수 있다.
                    (host, port, instance) = SplitSqlServerTarget(d.Server);
                }

                list.Add(new DbEndpoint
                {
                    Kind = kind.Value,
                    Host = host,
                    Port = port,
                    Instance = instance,
                    Dsn = d.Name,
                    Origin = "ODBC-DSN",
                    Detail = d.UsableHere
                        ? $"{d.Scope}·{d.Bitness} · {d.Driver}"
                        : $"⚠ {d.Bitness} DSN — 이 앱({OdbcCatalog.Bitness})에서는 사용 불가 · {d.Driver}",
                });
            }

            // ② tnsnames 항목 중 DSN 으로 안 잡힌 것
            var haveTns = new HashSet<string>(
                list.Where(x => x.Kind == DbProviderKind.Oracle && x.Host.Length > 0)
                    .Select(x => $"{x.Host}:{x.Port}/{x.Instance}"),
                StringComparer.OrdinalIgnoreCase);

            foreach (var t in tns)
            {
                var key = $"{t.Host}:{t.Port}/{t.Service}";
                if (!haveTns.Add(key)) continue;
                list.Add(new DbEndpoint
                {
                    Kind = DbProviderKind.Oracle,
                    Host = t.Host,
                    Port = t.Port,
                    Instance = t.Service,
                    Origin = "tnsnames",
                    Detail = $"별칭 {t.Alias} · {System.IO.Path.GetFileName(t.SourceFile)}",
                });
            }

            return list;
        }

        /// <summary>
        /// EZConnect 문자열 "host:port/service" (포트 생략 가능: "host/service")를 분해한다.
        /// Oracle ODBC 관리자가 만든 DSN 의 DBQ 가 이 형태인 경우가 많다.
        /// </summary>
        /// <returns>형태가 맞으면 true.</returns>
        public static bool TryParseEzConnect(string? s, out string host, out int port, out string service)
        {
            host = ""; port = 0; service = "";
            var t = (s ?? "").Trim();
            if (t.Length == 0) return false;

            int slash = t.IndexOf('/');
            if (slash <= 0 || slash == t.Length - 1) return false;   // 서비스명이 있어야 EZConnect 다

            var left = t[..slash];
            service = t[(slash + 1)..].Trim();

            int colon = left.IndexOf(':');
            if (colon > 0)
            {
                host = left[..colon].Trim();
                int.TryParse(left[(colon + 1)..].Trim(), out port);
            }
            else host = left.Trim();

            if (port <= 0) port = OraclePort;
            return host.Length > 0 && service.Length > 0;
        }

        // [찾기:로컬오라클]
        /// <summary>
        /// 이 PC에 설치된 Oracle 인스턴스를 서비스 등록 기준으로 후보에 올린다.
        /// <b>서비스가 꺼져 있어도 목록에 나온다</b> — 포트 스캔만으로는 "없다"로 오해하게 되기 때문이다.
        /// 리스너 포트는 listener.ora 에서 읽고, 없으면 1521 로 가정한다.
        /// </summary>
        public static List<DbEndpoint> FromLocalOracleServices()
        {
            var list = new List<DbEndpoint>();
            var instances = TnsCatalog.LocalInstances();
            if (instances.Count == 0) return list;

            var ports = TnsCatalog.ListenerPorts();
            int port = ports.Count > 0 ? ports[0] : OraclePort;

            foreach (var inst in instances)
            {
                list.Add(new DbEndpoint
                {
                    Kind = DbProviderKind.Oracle,
                    Host = "localhost",
                    Port = port,
                    Instance = inst.Sid,
                    Origin = "로컬서비스",
                    State = inst.Up ? ProbeState.Reachable : ProbeState.ServiceStopped,
                    Detail = inst.Up
                        ? $"로컬 인스턴스 구동 중 (서비스 {inst.ServiceName}, 시작 {inst.StartType})"
                        : $"⚠ {inst.StateText} — 설치돼 있으나 지금은 접속할 수 없습니다. " +
                          $"관리자 권한으로  net start {inst.ServiceName}  (리스너도 함께 시작) " +
                          $"[서비스 시작 유형: {inst.StartType}]",
                });
            }
            return list;
        }

        // [찾기:로컬MSSQL]
        /// <summary>
        /// 이 PC에 설치된 SQL Server 인스턴스를 서비스 등록 기준으로 후보에 올린다.
        ///
        /// 왜 필요한가 — 예전에는 로컬 MsSQL 이 목록에 아예 안 떴다. 발견 경로가
        /// ①등록된 DSN ②SSRP 브로드캐스트 ③서브넷 포트스캔 뿐이었는데,
        /// 로컬 SQL Server 는 보통 DSN 을 만들지 않고, 브로드캐스트에도 자기 자신은 잘 안 잡히며,
        /// 서브넷 스캔 대역(예: 192.168.200.0/24)에 자기 PC 가 없으면 그대로 누락된다.
        /// 정작 이관 타겟이 로컬인 경우가 많은데 도구가 그걸 못 보여주는 상태였다.
        /// Oracle 쪽은 이미 레지스트리 기반 <see cref="FromLocalOracleServices"/> 가 있었다 — 그 대칭이다.
        ///
        /// Oracle 과 같은 원칙: <b>서비스가 꺼져 있어도 목록에 낸다</b>.
        /// 포트 스캔만으로는 "설치 안 됨" 과 "지금 꺼져 있음" 을 구분할 수 없기 때문이다.
        /// </summary>
        public static List<DbEndpoint> FromLocalSqlServerServices()
        {
            var list = new List<DbEndpoint>();
            if (!OperatingSystem.IsWindows()) return list;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (key == null) return list;

                foreach (var instName in key.GetValueNames())
                {
                    // 기본 인스턴스는 MSSQLSERVER, 명명 인스턴스는 그 이름이 그대로 값 이름이다.
                    bool isDefault = instName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
                    string svcName = isDefault ? "MSSQLSERVER" : "MSSQL$" + instName;

                    var (up, startType, stateText) = SqlServiceState(svcName, isDefault);

                    list.Add(new DbEndpoint
                    {
                        Kind = DbProviderKind.MsSql,
                        Host = "localhost",
                        // 기본 인스턴스만 1433 을 가정한다. 명명 인스턴스는 동적 포트라 단정하지 않는다
                        // (0 이면 접속 시 "localhost\인스턴스" 표기를 쓰게 된다).
                        Port = isDefault ? MsSqlPort : 0,
                        Instance = isDefault ? "" : instName,
                        Origin = "로컬서비스",
                        State = up ? ProbeState.Reachable : ProbeState.ServiceStopped,
                        Detail = up
                            ? $"로컬 인스턴스 구동 중 (서비스 {svcName}, 시작 {startType})"
                            : $"⚠ {stateText} — 설치돼 있으나 지금은 접속할 수 없습니다. " +
                              $"관리자 권한으로  net start {svcName}  [서비스 시작 유형: {startType}]",
                    });
                }
            }
            catch { /* 레지스트리를 못 읽어도 나머지 발견 경로는 그대로 동작해야 한다 */ }
            return list;
        }

        // Oracle 쪽(OdbcCatalog.LocalInstances)과 같은 방식 — 프로세스 존재 + 레지스트리 시작유형.
        //   System.ServiceProcess.ServiceController 는 별도 NuGet 이 필요한데, 폐쇄망 반입 대상에
        //   런타임 의존을 하나라도 더 늘리지 않는 것이 이 저장소의 방침이라 쓰지 않는다.
        /// <summary>SQL Server 서비스의 구동 여부·시작 유형을 읽는다(서비스 키가 없으면 '미설치').</summary>
        private static (bool up, string startType, string stateText) SqlServiceState(string serviceName, bool isDefault)
        {
            string start = "알 수 없음";
            bool registered = false;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + serviceName);
                if (k != null)
                {
                    registered = true;
                    start = Convert.ToInt32(k.GetValue("Start") ?? 3) switch
                    {
                        2 => "자동", 3 => "수동", 4 => "사용 안 함", _ => "알 수 없음",
                    };
                }
            }
            catch { }

            if (!registered) return (false, "미설치", "서비스를 찾을 수 없음");

            // sqlservr.exe 는 기본·명명 인스턴스가 같은 이름으로 뜬다. 인스턴스별 구분은 못 하므로,
            // 여러 인스턴스가 설치된 PC 에서는 "구동 중" 이 과대 판정될 수 있다 —
            // 그래도 목록에서 빠지는 것보다 낫고, 실제 판정은 [선택 항목 접속 시험] 이 한다.
            bool up = ProcessRunning("sqlservr");
            return (up, start, up ? "서비스 실행 중" : "서비스 중지됨");
        }

        /// <summary>이름이 주어진 문자열로 시작하는 프로세스가 있는지.</summary>
        private static bool ProcessRunning(string namePrefix)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (p.ProcessName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>"host,1433" / "host\INSTANCE" / "host" 를 (호스트, 포트, 인스턴스)로 나눈다.</summary>
        private static (string host, int port, string instance) SplitSqlServerTarget(string server)
        {
            var s = (server ?? "").Trim();
            if (s.Length == 0) return ("", 0, "");
            if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s[4..];

            string instance = "";
            int slash = s.IndexOf('\\');
            if (slash >= 0) { instance = s[(slash + 1)..].Trim(); s = s[..slash]; }

            int port = 0;
            int comma = s.IndexOf(',');
            if (comma >= 0)
            {
                int.TryParse(s[(comma + 1)..].Trim(), out port);
                s = s[..comma];
            }
            return (s.Trim(), port, instance);
        }

        // ════════════════════════════════════════════════════════════════
        //  B층 — SQL Server Browser (UDP 1434, SSRP) 무설정 발견
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// UDP 1434 에 SSRP 열거 요청(0x02)을 브로드캐스트해 응답한 SQL Server 인스턴스를 모은다.
        /// 응답에는 호스트명·인스턴스명·버전·<b>실제 TCP 포트</b>가 들어 있어, 명명 인스턴스의
        /// 동적 포트까지 알 수 있다.
        ///
        /// ⚠ SQL Browser 서비스가 꺼져 있거나 UDP 1434 가 막히면 응답이 없다 — "없음"이 아니라
        /// "확인 불가"이므로, C층 포트 스캔과 병행해야 한다.
        /// </summary>
        /// <param name="timeoutMs">응답 수집 시간(기본 1.5초).</param>
        public static async Task<List<DbEndpoint>> DiscoverSqlBrowserAsync(
            int timeoutMs = 1500, CancellationToken ct = default)
        {
            var found = new Dictionary<string, DbEndpoint>(StringComparer.OrdinalIgnoreCase);

            var targets = new List<IPAddress> { IPAddress.Broadcast };
            targets.AddRange(DirectedBroadcasts());

            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); } catch { /* 이미 바인드됨 */ }

            var req = new byte[] { 0x02 };   // CLNT_UCAST_EX — 전체 인스턴스 열거
            foreach (var t in targets)
            {
                try { await udp.SendAsync(req, req.Length, new IPEndPoint(t, 1434)); }
                catch { /* 특정 인터페이스 실패는 무시 */ }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var res = await udp.ReceiveAsync(timeout.Token);
                    foreach (var ep in ParseSsrp(res.Buffer, res.RemoteEndPoint.Address.ToString()))
                        found[$"{ep.Host}|{ep.Instance}|{ep.Port}"] = ep;
                }
            }
            catch (OperationCanceledException) { /* 수집 시간 종료 — 정상 */ }
            catch (Exception) { /* 소켓 오류는 결과 없음으로 처리 */ }

            return found.Values.ToList();
        }

        /// <summary>
        /// SSRP 응답을 파싱한다.
        /// 형식: [0x05][길이 2바이트 LE][ASCII 본문]
        /// 본문: "ServerName;SRV;InstanceName;MSSQLSERVER;IsClustered;No;Version;15.0.2000.5;tcp;1433;;" 반복
        /// </summary>
        private static IEnumerable<DbEndpoint> ParseSsrp(byte[] buf, string fromIp)
        {
            var result = new List<DbEndpoint>();
            if (buf.Length < 3 || buf[0] != 0x05) return result;

            int len = buf[1] | (buf[2] << 8);
            len = Math.Min(len, buf.Length - 3);
            if (len <= 0) return result;

            var text = Encoding.ASCII.GetString(buf, 3, len);
            foreach (var block in text.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = block.Split(';');
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < parts.Length; i += 2)
                    map[parts[i].Trim()] = parts[i + 1].Trim();

                if (map.Count == 0) continue;
                map.TryGetValue("ServerName", out var srv);
                map.TryGetValue("InstanceName", out var inst);
                map.TryGetValue("Version", out var ver);
                map.TryGetValue("tcp", out var tcp);

                int.TryParse(tcp, out int port);
                bool isDefault = string.Equals(inst, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase);

                result.Add(new DbEndpoint
                {
                    Kind = DbProviderKind.MsSql,
                    Host = string.IsNullOrWhiteSpace(srv) ? fromIp : srv!,
                    Port = port,
                    Instance = isDefault ? "" : (inst ?? ""),
                    Origin = "UDP1434",
                    Version = ver ?? "",
                    State = ProbeState.Reachable,
                    Detail = $"SQL Browser 응답 ({fromIp})"
                           + (map.TryGetValue("IsClustered", out var cl) && cl.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                              ? " · 클러스터" : ""),
                });
            }
            return result;
        }

        /// <summary>로컬 IPv4 인터페이스의 디렉티드 브로드캐스트 주소 목록.</summary>
        private static List<IPAddress> DirectedBroadcasts()
        {
            var list = new List<IPAddress>();
            foreach (var (ip, mask) in LocalIpv4())
            {
                try
                {
                    var b = ip.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) b[i] = (byte)(b[i] | ~m[i]);
                    list.Add(new IPAddress(b));
                }
                catch { }
            }
            return list;
        }

        /// <summary>동작 중인 IPv4 인터페이스의 (주소, 넷마스크) 목록.</summary>
        public static List<(IPAddress Ip, IPAddress Mask)> LocalIpv4()
        {
            var list = new List<(IPAddress, IPAddress)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null || Equals(ua.IPv4Mask, IPAddress.Any)) continue;
                        list.Add((ua.Address, ua.IPv4Mask));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>이 PC가 속한 서브넷을 CIDR 문자열로 돌려준다(스캔 기본 대상 제안용).</summary>
        public static List<string> LocalSubnets()
        {
            var result = new List<string>();
            foreach (var (ip, mask) in LocalIpv4())
            {
                var mb = mask.GetAddressBytes();
                int bits = mb.Sum(b => CountBits(b));
                var ib = ip.GetAddressBytes();
                for (int i = 0; i < 4; i++) ib[i] = (byte)(ib[i] & mb[i]);
                var cidr = $"{new IPAddress(ib)}/{bits}";
                if (!result.Contains(cidr)) result.Add(cidr);
            }
            return result;
        }

        private static int CountBits(byte b)
        {
            int n = 0;
            for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) n++;
            return n;
        }

        // ════════════════════════════════════════════════════════════════
        //  C층 — 서브넷 TCP 스캔 + 프로토콜 지문
        // ════════════════════════════════════════════════════════════════

        /// <summary>스캔 진행 상황.</summary>
        public sealed record ScanProgress(int Done, int Total, string Current);

        /// <summary>
        /// CIDR 대역의 각 호스트에 지정 포트로 TCP 연결을 시도하고, 열린 포트는 프로토콜 지문으로
        /// MsSQL/Oracle 여부를 확정한다.
        ///
        /// ⚠ 이 동작은 망 스캔이다. 폐쇄망이라도 IDS/방화벽 로그에 남으므로 <b>사전 승인</b> 후 실행할 것.
        /// 호출부(UI)가 대상 대역을 명시하고 사용자 확인을 받는 것을 전제로 한다.
        /// </summary>
        /// <param name="cidr">"192.168.111.0/24" 형식. /16 보다 넓으면 거부한다.</param>
        /// <param name="ports">시도할 포트(기본 <see cref="DefaultScanPorts"/>).</param>
        /// <param name="connectTimeoutMs">TCP 연결 타임아웃(기본 400ms).</param>
        /// <param name="concurrency">동시 시도 수(기본 64).</param>
        public static async Task<List<DbEndpoint>> ScanSubnetAsync(
            string cidr,
            int[]? ports = null,
            int connectTimeoutMs = 400,
            int concurrency = 64,
            IProgress<ScanProgress>? progress = null,
            CancellationToken ct = default)
        {
            ports ??= DefaultScanPorts;
            var hosts = ExpandCidr(cidr);
            var results = new List<DbEndpoint>();
            var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            int done = 0, total = hosts.Count * ports.Length;
            var sync = new object();

            var tasks = new List<Task>();
            foreach (var host in hosts)
            {
                foreach (var port in ports)
                {
                    ct.ThrowIfCancellationRequested();
                    await gate.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var ep = await ProbePortAsync(host, port, connectTimeoutMs, ct);
                            if (ep != null) lock (sync) results.Add(ep);
                        }
                        catch { /* 개별 호스트 실패는 무시 */ }
                        finally
                        {
                            gate.Release();
                            int d = Interlocked.Increment(ref done);
                            if (d % 16 == 0 || d == total)
                                progress?.Report(new ScanProgress(d, total, $"{host}:{port}"));
                        }
                    }, ct));
                }
            }

            await Task.WhenAll(tasks);
            progress?.Report(new ScanProgress(total, total, "완료"));
            return results
                .OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Port)
                .ToList();
        }

        /// <summary>호스트:포트 하나를 열어보고, 열려 있으면 프로토콜 지문까지 확인한다.</summary>
        private static async Task<DbEndpoint?> ProbePortAsync(
            string host, int port, int timeoutMs, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try { await tcp.ConnectAsync(host, port, cts.Token); }
            catch { return null; }   // 닫힘/필터됨 — 결과에 넣지 않는다
            if (!tcp.Connected) return null;

            // 포트 번호로 1차 추정한 뒤, 해당 프로토콜 지문을 시도한다.
            bool oraclePort = port is 1521 or 1522 or 1526 or 1531;
            if (oraclePort)
            {
                var (ok, detail, ver) = await OracleTnsFingerprintAsync(tcp, host, port, timeoutMs, ct);
                return new DbEndpoint
                {
                    Kind = DbProviderKind.Oracle,
                    Host = host,
                    Port = port,
                    Origin = "PortScan",
                    Version = ver,
                    State = ok ? ProbeState.Reachable : ProbeState.PortOpen,
                    Detail = ok ? detail : "TCP 열림 — Oracle 리스너 응답 없음(다른 서비스일 수 있음)",
                };
            }

            var (mok, mver) = await MsSqlPreloginAsync(tcp, timeoutMs, ct);
            return new DbEndpoint
            {
                Kind = DbProviderKind.MsSql,
                Host = host,
                Port = port,
                Origin = "PortScan",
                Version = mver,
                State = mok ? ProbeState.Reachable : ProbeState.PortOpen,
                Detail = mok
                    ? "TDS PRELOGIN 응답 확인" + (mver.Length > 0 ? $" · {mver}" : "")
                    : "TCP 열림 — TDS 응답 없음(다른 서비스일 수 있음)",
            };
        }

        /// <summary>
        /// TDS PRELOGIN 패킷을 보내 SQL Server 인지 확인하고 버전을 회수한다.
        /// 패킷: 8바이트 TDS 헤더(type 0x12) + 옵션 토큰(VERSION=0) + 종료(0xFF) + 6바이트 버전 데이터.
        /// 응답이 type 0x04 면 SQL Server 확정이며, VERSION 토큰에서 major.minor.build 를 읽는다.
        /// </summary>
        private static async Task<(bool ok, string version)> MsSqlPreloginAsync(
            TcpClient tcp, int timeoutMs, CancellationToken ct)
        {
            try
            {
                // 옵션 스트림: [토큰0=VERSION][offset 2B BE][len 2B BE][0xFF] + 데이터 6B
                const int optionHeader = 5 + 1;   // 토큰 1개 + 종료자
                var payload = new byte[optionHeader + 6];
                payload[0] = 0x00;                              // VERSION
                payload[1] = 0x00; payload[2] = optionHeader;   // offset (BE)
                payload[3] = 0x00; payload[4] = 0x06;           // length (BE)
                payload[5] = 0xFF;                              // TERMINATOR
                // 데이터 6바이트는 0 으로 둔다(클라이언트 버전 미상 표기).

                var packet = new byte[8 + payload.Length];
                packet[0] = 0x12;                       // PRELOGIN
                packet[1] = 0x01;                       // EOM
                packet[2] = (byte)(packet.Length >> 8); // length (BE)
                packet[3] = (byte)(packet.Length & 0xFF);
                packet[6] = 0x01;                       // packet id
                Buffer.BlockCopy(payload, 0, packet, 8, payload.Length);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var ns = tcp.GetStream();
                await ns.WriteAsync(packet, cts.Token);
                await ns.FlushAsync(cts.Token);

                var buf = new byte[512];
                int n = await ns.ReadAsync(buf, cts.Token);
                if (n < 9 || buf[0] != 0x04) return (false, "");

                // 응답 옵션 스트림에서 VERSION 토큰을 찾는다(오프셋은 페이로드 시작 기준).
                int p = 8;
                while (p + 4 < n && buf[p] != 0xFF)
                {
                    byte token = buf[p];
                    int off = (buf[p + 1] << 8) | buf[p + 2];
                    int len = (buf[p + 3] << 8) | buf[p + 4];
                    if (token == 0x00 && len >= 6 && 8 + off + 6 <= n)
                    {
                        int d = 8 + off;
                        int major = buf[d], minor = buf[d + 1];
                        int build = (buf[d + 2] << 8) | buf[d + 3];
                        return (true, $"{major}.{minor}.{build}");
                    }
                    p += 5;
                }
                return (true, "");   // PRELOGIN 응답은 왔으나 버전 토큰 해석 실패
            }
            catch { return (false, ""); }
        }

        /// <summary>
        /// TNS CONNECT 패킷을 보내 Oracle 리스너인지 확인한다.
        /// 존재하지 않는 SERVICE_NAME 을 요청하므로 리스너는 REFUSE(타입 4)로 ORA-12514 를 돌려주는데,
        /// <b>그 응답 자체가 리스너 생존의 증거</b>다. ACCEPT/RESEND/REDIRECT 도 마찬가지로 인정한다.
        /// (서비스명 목록은 원격에서 얻을 수 없다 — lsnrctl services 는 로컬 OS 인증이 필요하다.)
        /// </summary>
        private static async Task<(bool ok, string detail, string version)> OracleTnsFingerprintAsync(
            TcpClient tcp, string host, int port, int timeoutMs, CancellationToken ct)
        {
            try
            {
                var connectData =
                    $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))" +
                    "(CONNECT_DATA=(SERVICE_NAME=__naims_probe__)(CID=(PROGRAM=)(HOST=)(USER=))))";
                var cd = Encoding.ASCII.GetBytes(connectData);

                const int headerLen = 58;   // 표준 CONNECT 헤더 크기(연결데이터 오프셋)
                var packet = new byte[headerLen + cd.Length];
                int total = packet.Length;

                packet[0] = (byte)(total >> 8); packet[1] = (byte)(total & 0xFF);  // 전체 길이
                packet[4] = 0x01;                                                   // 타입 = CONNECT
                packet[8] = 0x01; packet[9] = 0x3A;   // version 314
                packet[10] = 0x01; packet[11] = 0x2C; // version compatible 300
                packet[14] = 0x08; packet[15] = 0x00; // SDU 2048
                packet[16] = 0x7F; packet[17] = 0xFF; // TDU
                packet[18] = 0x4F; packet[19] = 0x98; // NT 프로토콜 특성
                packet[22] = 0x00; packet[23] = 0x01; // hardware 1
                packet[24] = (byte)(cd.Length >> 8); packet[25] = (byte)(cd.Length & 0xFF); // 연결데이터 길이
                packet[26] = 0x00; packet[27] = headerLen;                                   // 연결데이터 오프셋
                Buffer.BlockCopy(cd, 0, packet, headerLen, cd.Length);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var ns = tcp.GetStream();
                await ns.WriteAsync(packet, cts.Token);
                await ns.FlushAsync(cts.Token);

                var buf = new byte[2048];
                int n = await ns.ReadAsync(buf, cts.Token);
                if (n < 5) return (false, "", "");

                byte type = buf[4];
                string text = Encoding.ASCII.GetString(buf, 0, n);
                string ora = "";
                var m = System.Text.RegularExpressions.Regex.Match(text, @"ORA-\d{5}");
                if (m.Success) ora = m.Value;

                return type switch
                {
                    2 => (true, "TNS ACCEPT — 리스너 응답 확인", ""),
                    4 => (true, $"TNS REFUSE — 리스너 확인{(ora.Length > 0 ? $" ({ora})" : "")}", ""),
                    5 => (true, "TNS REDIRECT — 리스너 확인", ""),
                    11 => (true, "TNS RESEND — 리스너 확인", ""),
                    _ => (true, $"TNS 응답(타입 {type}) — 리스너 추정", ""),
                };
            }
            catch { return (false, "", ""); }
        }

        /// <summary>CIDR 문자열을 호스트 IP 문자열 목록으로 전개한다(네트워크·브로드캐스트 주소 제외).</summary>
        /// <exception cref="ArgumentException">형식이 틀렸거나 /16 보다 넓은 대역일 때.</exception>
        public static List<string> ExpandCidr(string cidr)
        {
            var parts = (cidr ?? "").Trim().Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) ||
                !int.TryParse(parts[1], out int bits) || bits is < 8 or > 32)
                throw new ArgumentException($"CIDR 형식이 아닙니다: '{cidr}' (예: 192.168.111.0/24)");

            if (bits < 16)
                throw new ArgumentException($"대역이 너무 넓습니다(/{bits}). /16 이상으로 좁혀 주세요 — " +
                                            "넓은 스캔은 시간도 오래 걸리고 망에 부담을 줍니다.");

            var b = baseIp.GetAddressBytes();
            uint start = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            uint net = start & mask;
            uint bcast = net | ~mask;

            var list = new List<string>();
            if (bits == 32) { list.Add(baseIp.ToString()); return list; }

            for (uint a = net + 1; a < bcast; a++)
                list.Add($"{(a >> 24) & 0xFF}.{(a >> 16) & 0xFF}.{(a >> 8) & 0xFF}.{a & 0xFF}");
            return list;
        }

        // ════════════════════════════════════════════════════════════════
        //  D층 — 자격증명 접속 판정
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 실제 자격증명으로 1회 접속을 시도해 상태를 확정한다.
        /// 에러 코드를 분류해, 로그인 실패도 "서버 실재 확정(AuthRequired)"이라는 정보로 승격한다.
        ///
        /// ⚠ [찾기:잠금가드] 계정 잠금이 관측된 호스트는 이후 시도를 전면 차단한다.
        /// 자격증명을 자동 조합해 여러 번 던지지 않는다 — 호출부가 사용자 입력 1세트만 넘겨야 한다.
        /// </summary>
        /// <param name="ep">판정할 엔드포인트(상태가 갱신된다).</param>
        /// <param name="mode">ODBC/다이렉트.</param>
        /// <param name="user">계정.</param>
        /// <param name="password">비밀번호.</param>
        /// <param name="integrated">MsSQL 통합 인증 사용 여부.</param>
        public static async Task ProbeCredentialsAsync(
            DbEndpoint ep, DbConnectionMode mode, string user, string password,
            bool integrated = false, CancellationToken ct = default)
        {
            var hostKey = ep.Host.Length > 0 ? ep.Host : ep.Dsn;
            if (LockedHosts.Contains(hostKey))
            {
                ep.State = ProbeState.Locked;
                ep.Detail = "계정 잠김이 관측된 호스트 — 추가 시도를 차단했습니다(잠금 해제 후 가드 초기화 필요).";
                return;
            }

            var connStr = ep.BuildConnectionString(mode, user, password, integrated);
            try
            {
                await using var cn = DbProvider.CreateConnection(ep.Kind, mode, connStr);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(8000);
                await cn.OpenAsync(cts.Token);

                using var cmd = cn.CreateCommand();
                cmd.CommandText = DbProvider.VersionSql(ep.Kind);
                var v = await cmd.ExecuteScalarAsync(cts.Token);
                ep.State = ProbeState.Connected;
                ep.Version = FirstLine(Convert.ToString(v) ?? "");
                ep.Detail = "접속 성공 · " + ep.Version;
            }
            catch (SqlException sx) { Classify(ep, hostKey, sx.Number, sx.Message); }
            catch (OracleException ox) { Classify(ep, hostKey, -ox.Number, ox.Message); }
            catch (System.Data.Odbc.OdbcException oe) { ClassifyText(ep, hostKey, oe.Message); }
            catch (OperationCanceledException)
            {
                ep.State = ProbeState.Unreachable;
                ep.Detail = "응답 시간 초과";
            }
            catch (Exception ex) { ClassifyText(ep, hostKey, ex.Message); }
        }

        /// <summary>SQL Server / Oracle 오류 번호를 상태로 분류한다(Oracle 은 음수로 전달).</summary>
        private static void Classify(DbEndpoint ep, string hostKey, int number, string message)
        {
            switch (number)
            {
                // ── MsSQL ──
                case 18456:  // 로그인 실패 → 서버는 확실히 있다
                    ep.State = ProbeState.AuthRequired;
                    ep.Detail = "서버 실재 확정 — 로그인 실패(18456). 계정/비밀번호만 맞추면 됩니다.";
                    return;
                case 4060:   // DB 열기 실패
                    ep.State = ProbeState.WrongDatabase;
                    ep.Detail = "로그인은 되었으나 지정 DB 접근 불가(4060) — DB명을 확인하세요.";
                    return;
                case 18486:  // 계정 잠김
                    LockedHosts.Add(hostKey);
                    ep.State = ProbeState.Locked;
                    ep.Detail = "⚠ 계정 잠김(18486) — 이 호스트에 대한 추가 시도를 중단했습니다.";
                    return;

                // ── Oracle (음수로 전달) ──
                case -1017:  // 잘못된 계정
                    ep.State = ProbeState.AuthRequired;
                    ep.Detail = "서버 실재 확정 — ORA-01017 잘못된 계정. 계정/비밀번호만 맞추면 됩니다.";
                    return;
                case -12514: // 리스너가 서비스명을 모름
                    ep.State = ProbeState.WrongDatabase;
                    ep.Detail = "리스너 정상, 서비스명 불일치(ORA-12514) — SERVICE_NAME 을 확인하세요.";
                    return;
                case -28000: // 계정 잠김
                    LockedHosts.Add(hostKey);
                    ep.State = ProbeState.Locked;
                    ep.Detail = "⚠ 계정 잠김(ORA-28000) — 이 호스트에 대한 추가 시도를 중단했습니다.";
                    return;
                case -12541: // 리스너 없음
                    ep.State = ProbeState.Unreachable;
                    ep.Detail = "리스너 없음(ORA-12541)";
                    return;
            }
            ClassifyText(ep, hostKey, message);
        }

        /// <summary>번호를 못 얻는 경로(ODBC 등)에서 메시지 텍스트로 분류한다.</summary>
        private static void ClassifyText(DbEndpoint ep, string hostKey, string message)
        {
            var m = message ?? "";
            if (m.Contains("ORA-28000") || m.Contains("18486") || m.Contains("locked", StringComparison.OrdinalIgnoreCase))
            {
                LockedHosts.Add(hostKey);
                ep.State = ProbeState.Locked;
                ep.Detail = "⚠ 계정 잠김 — 이 호스트에 대한 추가 시도를 중단했습니다. " + FirstLine(m);
                return;
            }
            if (m.Contains("ORA-01017") || m.Contains("18456") ||
                m.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            {
                ep.State = ProbeState.AuthRequired;
                ep.Detail = "서버 실재 확정 — 로그인 실패. 계정/비밀번호만 맞추면 됩니다. " + FirstLine(m);
                return;
            }
            if (m.Contains("ORA-12514") || m.Contains("4060"))
            {
                ep.State = ProbeState.WrongDatabase;
                ep.Detail = "서비스/DB 이름 불일치. " + FirstLine(m);
                return;
            }
            if (m.Contains("IM002"))   // ODBC: DSN 없음
            {
                ep.State = ProbeState.Unreachable;
                ep.Detail = "DSN 을 찾을 수 없음(IM002) — 비트수(32/64) 불일치일 수 있습니다. " + FirstLine(m);
                return;
            }
            ep.State = ProbeState.Unreachable;
            ep.Detail = FirstLine(m);
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var line = s.Split('\n')[0].Trim();
            return line.Length > 200 ? line[..200] + "…" : line;
        }

        /// <summary>같은 대상을 가리키는 중복 후보를 합친다(확신이 강한 상태·정보 많은 쪽을 남긴다).</summary>
        public static List<DbEndpoint> Merge(params IEnumerable<DbEndpoint>[] sets)
        {
            var byKey = new Dictionary<string, DbEndpoint>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in sets)
            {
                foreach (var e in set)
                {
                    var key = e.Dsn.Length > 0
                        ? $"dsn:{e.Dsn}"
                        : $"{e.Kind}:{e.Host}:{e.Port}:{e.Instance}";
                    if (!byKey.TryGetValue(key, out var cur)) { byKey[key] = e; continue; }
                    // 상태가 더 확실한 쪽을 남기되, 버전 정보는 있는 쪽에서 가져온다.
                    if (e.State > cur.State)
                    {
                        if (cur.Version.Length > 0 && e.Version.Length == 0) e.Version = cur.Version;
                        byKey[key] = e;
                    }
                    else if (cur.Version.Length == 0 && e.Version.Length > 0) cur.Version = e.Version;
                }
            }
            return byKey.Values
                .OrderBy(e => e.KindText)
                .ThenBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Port)
                .ToList();
        }
    }
}
