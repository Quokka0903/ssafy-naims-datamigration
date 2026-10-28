using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using RawCollectMigrator.Features.DbToDb;

namespace RawCollectMigrator.Infrastructure
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [가이드:접속설정] [GUIDE:APP-SESSION] [찾기:접속설정] [찾기:AppSession]
    //  ▣ 이 파일 = "이번 실행이 어디에 붙어 무엇을 읽는가"의 단일 원본.
    //
    //  ▶ 왜 만들었나: 예전에는 타겟(NAIMS2) 접속문자열이 수집원음 탭·발췌원음 탭·DB→DB 탭
    //    세 곳에 각각 하드코딩돼 있었다. 탭마다 다른 서버를 가리켜도 아무도 모르고,
    //    현장에서 서버를 바꾸려면 세 곳을 따로 고쳐야 했다. 실서버 오지정 사고의 씨앗이다.
    //    → 접속·경로는 여기 한 곳에만 두고, 모든 화면이 이 값을 읽는다.
    //
    //  ▶ 구동 흐름: 앱을 켜면 작업 화면이 아니라 [연결 설정] 화면이 먼저 뜬다.
    //    타겟 연결이 검증되기 전에는 작업 탭으로 넘어갈 수 없다(StartupView).
    //
    //  ▶ 비밀번호: 기본은 저장하지 않는다(SaveCredentials=false → 파일에 쓸 때 pwd 제거).
    //    저장을 켜면 Windows DPAPI(현재 사용자 키)로 암호화해 둔다 — 파일을 다른 계정·PC 로
    //    옮겨도 복호되지 않는다. 평문으로 남는 경로는 없다. [찾기:비밀번호저장]
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 앱 전역 접속·경로 설정. 화면들은 각자 입력칸을 갖지 않고 이 인스턴스를 읽는다.
    /// </summary>
    public sealed class AppSession : INotifyPropertyChanged
    {
        /// <summary>전역 인스턴스. 앱 시작 시 <see cref="Load"/> 로 채운다.</summary>
        public static AppSession Current { get; private set; } = new();

        // [찾기:설정판번호] [NAIMS-ISSUE:CRED-STRIPPED]
        //   비밀번호 저장의 기본값을 끔 → 켬 으로 바꿨는데(2026-08-20), 그 전에 저장된 파일에는
        //   <사용자가 고른 적 없는> SaveCredentials=false 가 박혀 있다. 그걸 그대로 읽으면
        //   새 기본값이 영영 적용되지 않고, 붙을 때마다 비밀번호가 계속 지워진다.
        //   → 판 번호가 없는(=옛) 파일의 SaveCredentials 는 <사용자의 선택이 아니라 옛 기본값>으로 보고 무시한다.
        //     한 번 저장하면 판 번호가 박히고, 그 뒤로는 사용자가 끈 것을 존중한다.
        private const int CurrentSettingsVersion = 2;

        /// <summary>설정 파일 경로 (exe 옆 config\connections.json).</summary>
        public static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "config", "connections.json");

        // ── 타겟 (NAIMS2, 항상 MsSQL 다이렉트) ──
        private string _targetConn =
            "Server=localhost;Database=NAIMS2;Trusted_Connection=yes;Encrypt=false;TrustServerCertificate=true;Connect Timeout=15;";
        /// <summary>적재 대상 NAIMS2 접속문자열. 세 트랙(수집·발췌·DB→DB)이 모두 이 값을 쓴다.</summary>
        public string TargetConn
        {
            get => _targetConn;
            set { if (Set(ref _targetConn, value)) { TargetVerified = false; Raise(nameof(Summary)); } }
        }

        private bool _targetVerified;
        /// <summary>타겟에 실제로 접속해 확인했는지. 작업 화면 진입 조건.</summary>
        public bool TargetVerified
        {
            get => _targetVerified;
            set { if (Set(ref _targetVerified, value)) { Raise(nameof(IsReady)); Raise(nameof(Summary)); } }
        }

        private string _targetInfo = "";
        /// <summary>타겟 확인 결과 문구(서버·버전 등).</summary>
        public string TargetInfo { get => _targetInfo; set => Set(ref _targetInfo, value); }

        // ── 소스 (구 NAIMS, DB→DB 트랙에서만 필요) ──
        private DbProviderKind _sourceKind = DbProviderKind.Oracle;
        public DbProviderKind SourceKind
        {
            get => _sourceKind;
            set { if (Set(ref _sourceKind, value)) { SourceVerified = false; Raise(nameof(Summary)); } }
        }

        // [NAIMS-ISSUE:CONN-MODE] 기본은 ODBC — 현장 운영 기본이 ODBC 이고 현장 버전은 12c 로 확정(2026-08-19).
        //   ODBC 는 DSN 등록·드라이버 비트수·프로세스 권한이 모두 맞아야 하고 어긋나면 IM002 로 막히므로,
        //   실패 시 [다이렉트로 전환] 을 폴백으로 둔다(관리형 드라이버는 그 의존이 없고 12c 접속 실증됨).
        private DbConnectionMode _sourceMode = DbConnectionMode.Odbc;
        public DbConnectionMode SourceMode
        {
            get => _sourceMode;
            set { if (Set(ref _sourceMode, value)) { SourceVerified = false; Raise(nameof(Summary)); } }
        }

        // 로컬 리그(이 저장소 스크립트가 만든 격리 DB) 기본값. 현장에서는 시작 화면에서 갈아끼운다.
        private string _sourceConn = "DSN=naims;Uid=LEGACYNAIMS;Pwd=naims2!;";
        /// <summary>구 NAIMS 접속문자열(DB→DB 트랙 전용). 파일 트랙에는 필요 없다.</summary>
        public string SourceConn
        {
            get => _sourceConn;
            set { if (Set(ref _sourceConn, value)) { SourceVerified = false; Raise(nameof(Summary)); } }
        }

        private bool _sourceVerified;
        public bool SourceVerified
        {
            get => _sourceVerified;
            set { if (Set(ref _sourceVerified, value)) { Raise(nameof(Summary)); } }
        }

        private string _sourceInfo = "";
        public string SourceInfo { get => _sourceInfo; set => Set(ref _sourceInfo, value); }

        // ── 파일 경로 ──
        private string _msuRoot = @"\\192.168.111.165\naims_share\MSU";
        /// <summary>MSU 공유 루트. 아래 4개 폴더를 여기서 유추한다(<see cref="DeriveFromMsu"/>).</summary>
        public string MsuRoot
        {
            get => _msuRoot;
            set => Set(ref _msuRoot, value);
        }

        // [NAIMS-ISSUE:SNIPPET-ROOTS] 스니펫·영상 파일은 CLASSIFICATIONDB 한 곳만 본다.
        private string _classificationRoot = @"\\192.168.111.165\naims_share\MSU\CLASSIFICATIONDB";
        /// <summary>
        /// 기준식별 파일 루트(CLASSIFICATIONDB). DB→DB 이관의 스니펫·영상 blob 이 여기서만 온다.
        /// 종류별 하위 폴더(SNIPPET / IMAGE / CTRYIMAGE)로 자동 분기한다.
        /// </summary>
        public string ClassificationRoot
        {
            get => _classificationRoot;
            set { if (Set(ref _classificationRoot, value)) Raise(nameof(Summary)); }
        }

        private string _collectRawRoot = @"\\192.168.111.165\naims_share\MSU\수집원음자료";
        /// <summary>수집 원음 자료 루트(탭 1).</summary>
        public string CollectRawRoot { get => _collectRawRoot; set => Set(ref _collectRawRoot, value); }

        private string _narrowBandRoot = @"\\192.168.111.165\naims_share\MSU\협대역원음자료";
        /// <summary>협대역 발췌 원음 루트(탭 2).</summary>
        public string NarrowBandRoot { get => _narrowBandRoot; set => Set(ref _narrowBandRoot, value); }

        private string _shortPeriodRoot = @"\\192.168.111.165\naims_share\MSU\단주기원음자료";
        /// <summary>단주기 발췌 원음 루트(탭 2).</summary>
        public string ShortPeriodRoot { get => _shortPeriodRoot; set => Set(ref _shortPeriodRoot, value); }

        // [찾기:비밀번호저장] 기본을 <켬>으로 둔다(2026-08-20 사용자 요청).
        //   근거: 저장 경로가 DPAPI 하나뿐이라 평문이 남을 자리가 없고, 키는 이 PC·이 사용자에
        //   묶여 파일을 옮겨도 풀리지 않는다. 반면 끄면 매 실행 비밀번호를 다시 치게 되는데,
        //   그 재입력이 오타·오지정의 입구다. 공용 PC 에서는 시작 화면에서 끌 수 있다.
        private bool _saveCredentials = true;
        /// <summary>
        /// 비밀번호까지 파일에 저장할지. 기본 true — 끄면 저장 시 pwd 를 지운다.
        /// 켜면 <b>평문이 아니라 Windows DPAPI(현재 사용자 키)로 암호화</b>해 저장하므로,
        /// 다른 계정·다른 PC 로 파일을 옮겨도 복호되지 않는다. [찾기:비밀번호저장]
        /// </summary>
        public bool SaveCredentials { get => _saveCredentials; set => Set(ref _saveCredentials, value); }

        // ── 접속문자열에서 계정/비밀번호 뽑기 (자동 입력용) ──────────

        /// <summary>접속문자열에서 계정(User Id / Uid)을 뽑는다. 없으면 빈 문자열.</summary>
        public static string UserOf(string conn) =>
            Regex.Match(conn ?? "", @"(?i)\b(?:user\s*id|uid|user)\s*=\s*([^;]*)").Groups[1].Value.Trim();

        /// <summary>접속문자열에서 비밀번호(Password / Pwd)를 뽑는다. 없으면 빈 문자열.</summary>
        public static string PasswordOf(string conn) =>
            Regex.Match(conn ?? "", @"(?i)\b(?:password|pwd)\s*=\s*([^;]*)").Groups[1].Value.Trim();

        /// <summary>작업 화면으로 넘어갈 수 있는 상태인지(타겟 검증 완료).</summary>
        public bool IsReady => TargetVerified;

        /// <summary>상단 바에 띄우는 한 줄 요약.</summary>
        public string Summary =>
            $"타겟 {ServerOf(TargetConn)}{(TargetVerified ? " ✓" : " (미확인)")}" +
            $"   |   소스 [{DbProvider.DisplayName(SourceKind)}·{DbProvider.ModeName(SourceMode)}] " +
            $"{ServerOf(SourceConn)}{(SourceVerified ? " ✓" : "")}" +
            $"   |   CLASSIFICATION {ShortPath(ClassificationRoot)}";

        /// <summary>MSU 루트에서 하위 4개 폴더 경로를 유추해 채운다(사용자가 개별 지정하면 그 값이 우선).</summary>
        public void DeriveFromMsu()
        {
            if (string.IsNullOrWhiteSpace(MsuRoot)) return;
            ClassificationRoot = Path.Combine(MsuRoot, "CLASSIFICATIONDB");
            CollectRawRoot = Path.Combine(MsuRoot, "수집원음자료");
            NarrowBandRoot = Path.Combine(MsuRoot, "협대역원음자료");
            ShortPeriodRoot = Path.Combine(MsuRoot, "단주기원음자료");
        }

        /// <summary>DB→DB 실행 옵션에 이 세션의 접속·경로를 적용한 새 옵션을 만든다.</summary>
        public DbToDbOptions ApplyTo(DbToDbOptions o) => new()
        {
            SourceKind = SourceKind,
            SourceMode = SourceMode,
            SourceConnStr = SourceConn,
            TargetConnStr = TargetConn,
            Mode = o.Mode,
            ExcludeCategories = o.ExcludeCategories,
            MaxRowsPerTable = o.MaxRowsPerTable,
            UseDecryption = o.UseDecryption,
            ClassFilter = o.ClassFilter,
            ForceReimportTables = o.ForceReimportTables,
            ClassificationRoot = ClassificationRoot,
        };

        // ── 저장 / 복원 ──────────────────────────────────────────────

        /// <summary>설정 파일을 읽어 전역 인스턴스를 채운다. 파일이 없거나 깨졌으면 기본값을 쓴다.</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(ConfigPath));
                if (dto == null) return;

                var s = new AppSession
                {
                    _targetConn = Unprotect(dto.TargetConn) ?? Current.TargetConn,
                    _sourceConn = Unprotect(dto.SourceConn) ?? Current.SourceConn,
                    _sourceKind = Enum.TryParse<DbProviderKind>(dto.SourceKind, out var k) ? k : DbProviderKind.Oracle,
                    _sourceMode = Enum.TryParse<DbConnectionMode>(dto.SourceMode, out var m) ? m : DbConnectionMode.Odbc,
                    _msuRoot = dto.MsuRoot ?? Current.MsuRoot,
                    _classificationRoot = dto.ClassificationRoot ?? Current.ClassificationRoot,
                    _collectRawRoot = dto.CollectRawRoot ?? Current.CollectRawRoot,
                    _narrowBandRoot = dto.NarrowBandRoot ?? Current.NarrowBandRoot,
                    _shortPeriodRoot = dto.ShortPeriodRoot ?? Current.ShortPeriodRoot,
                    // [찾기:설정판번호] 옛 파일의 false 는 사용자의 선택이 아니다 — 새 기본값을 쓴다.
                    _saveCredentials = dto.SettingsVersion >= CurrentSettingsVersion
                                       ? dto.SaveCredentials
                                       : true,
                };
                Current = s;   // 검증 상태는 복원하지 않는다 — 매 실행마다 다시 확인해야 한다.
                Current.CompleteFromVault();
            }
            catch { /* 설정 복원 실패는 기본값으로 계속 진행 */ }
        }

        // [찾기:접속완성]
        /// <summary>
        /// 비밀번호가 비어 있는 접속문자열을 <see cref="ConnectionVault"/> 의 기억으로 채운다.
        /// 설정 파일에 비밀번호가 없어도(저장을 껐던 적이 있어도) 한 번 붙어 본 대상이면
        /// 켜자마자 완성된 상태가 된다. 이미 채워져 있으면 손대지 않는다.
        /// </summary>
        public void CompleteFromVault()
        {
            var t = ConnectionVault.Complete(ConnectionRole.Target, DbProviderKind.MsSql,
                                             DbConnectionMode.Direct, TargetConn);
            if (t != TargetConn) { _targetConn = t; Raise(nameof(TargetConn)); }

            var c = ConnectionVault.Complete(ConnectionRole.Source, SourceKind, SourceMode, SourceConn);
            if (c != SourceConn) { _sourceConn = c; Raise(nameof(SourceConn)); }
        }

        /// <summary>현재 설정을 파일로 저장한다. SaveCredentials 가 꺼져 있으면 비밀번호를 지운다.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var dto = new Dto
                {
                    // [찾기:비밀번호저장] 저장하더라도 평문으로 두지 않는다 — DPAPI(현재 사용자)로 감싼다.
                    TargetConn = SaveCredentials ? Protect(TargetConn) : StripPassword(TargetConn),
                    SourceConn = SaveCredentials ? Protect(SourceConn) : StripPassword(SourceConn),
                    Protected = SaveCredentials,
                    SourceKind = SourceKind.ToString(),
                    SourceMode = SourceMode.ToString(),
                    MsuRoot = MsuRoot,
                    ClassificationRoot = ClassificationRoot,
                    CollectRawRoot = CollectRawRoot,
                    NarrowBandRoot = NarrowBandRoot,
                    ShortPeriodRoot = ShortPeriodRoot,
                    SaveCredentials = SaveCredentials,
                    SettingsVersion = CurrentSettingsVersion,
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(dto, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }),
                    new UTF8Encoding(false));
            }
            catch { /* 저장 실패가 작업을 막지 않는다 */ }
        }

        // [찾기:비밀번호저장] 암호화·복호화·비밀번호 제거는 SecretStore 한 곳에만 있다.
        //   여기 따로 두면 두 벌이 갈라지고, 갈라진 쪽에서 평문이 새어 나간다.
        private static string Protect(string plain) => SecretStore.Protect(plain);
        private static string? Unprotect(string? stored) => SecretStore.Unprotect(stored);

        /// <summary>접속문자열에서 비밀번호 값을 지운다(키는 남겨 사용자가 채우기 쉽게).</summary>
        public static string StripPassword(string conn) => SecretStore.StripPassword(conn);

        // [찾기:접속요약] 이 문자열은 상단 접속 바와 승인 창의 "적재 대상 확인"에 그대로 쓰인다.
        //   "(미지정)" 으로 뜨면 사람이 대상을 확인할 수 없어 안전장치가 무력해지므로,
        //   DSN-less(DBQ=) 같은 형태도 반드시 해석해야 한다.
        /// <summary>접속문자열에서 서버/DB(또는 DSN·DBQ)만 짧게 뽑는다(요약 표시용).</summary>
        public static string ServerOf(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return "(미지정)";

            var server = Regex.Match(conn, @"(?i)\b(?:server|data source)\s*=\s*([^;]+)").Groups[1].Value.Trim();
            var db = Regex.Match(conn, @"(?i)\b(?:database|initial catalog)\s*=\s*([^;]+)").Groups[1].Value.Trim();
            if (server.Length > 0 || db.Length > 0)
                return server + (db.Length > 0 ? "/" + db : "");

            // ODBC DSN-less: Driver={...};DBQ=host:port/service  ← 자동 폴백이 만드는 형태
            var dbq = Regex.Match(conn, @"(?i)\bDBQ\s*=\s*([^;]+)").Groups[1].Value.Trim();
            if (dbq.Length > 0) return dbq;

            var dsn = Regex.Match(conn, @"(?i)\bDSN\s*=\s*([^;]+)").Groups[1].Value.Trim();
            return dsn.Length > 0 ? "DSN=" + dsn : "(미지정)";
        }

        /// <summary>긴 경로를 뒤쪽 두 마디만 남겨 줄인다(식별성은 뒤에 있다).</summary>
        private static string ShortPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "(미지정)";
            var parts = p.TrimEnd('\\').Split('\\');
            return parts.Length <= 2 ? p : "…\\" + string.Join("\\", parts[^2..]);
        }

        /// <summary>설정 파일 직렬화 모델.</summary>
        private sealed class Dto
        {
            public string? TargetConn { get; set; }
            public string? SourceConn { get; set; }
            public string? SourceKind { get; set; }
            public string? SourceMode { get; set; }
            public string? MsuRoot { get; set; }
            public string? ClassificationRoot { get; set; }
            public string? CollectRawRoot { get; set; }
            public string? NarrowBandRoot { get; set; }
            public string? ShortPeriodRoot { get; set; }
            public bool SaveCredentials { get; set; }
            /// <summary>설정 판 번호. 없으면(0) 옛 파일 — SaveCredentials 를 사용자의 선택으로 보지 않는다.</summary>
            public int SettingsVersion { get; set; }
            /// <summary>접속문자열이 DPAPI 로 보호돼 있는지.</summary>
            public bool Protected { get; set; }
            public string? SavedAt { get; set; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
        {
            if (Equals(f, v)) return false;
            f = v; Raise(n!); return true;
        }
    }
}
