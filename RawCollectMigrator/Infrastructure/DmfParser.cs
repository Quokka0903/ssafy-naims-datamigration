using System.Globalization;
using System.IO;
using System.Text;

namespace RawCollectMigrator.Infrastructure
{
    /// <summary>
    /// 자료관리파일.DMF / Signal_NN_NNN.DMF 파서 (CP949 INI).
    ///
    /// DMF 는 CDMU(USPASK/APU) 체계가 만드는 메타데이터 파일이다. 정의는 레거시
    /// USPASK_APU\DataManage\DataManageDefine.h:82(DMFFILE) 및 키 상수 102~171행,
    /// 생성/갱신은 DataManageAppFunction.cpp:1320 DmfuncMakeDMFfile 과
    /// DataManageInterface.cpp 의 MakeRawDMF / MakeSignalDMF / OnCreateQuick / UpdateRawDMF.
    ///
    /// 세 계층으로 놓인다:
    ///   ① 연동ID 폴더        \자료관리파일.DMF        [LIST][SUBDB]  (원음자료명 = "-")
    ///   ② 발췌자료(.sd/.nb)  \자료관리파일.DMF        [LIST][SUBDB]  (①을 복사 후 덮어씀)
    ///   ③ 원음신호(WAV)      \Signal_NN_NNN.DMF      [SIGNAL]
    /// 이관 항목은 ②에 해당하므로 항목 폴더의 것을 먼저 보고, 없으면 상위(①)로 올라간다.
    ///
    /// ※ AAS 는 DMF 를 쓰지 않는다 — AAS 소스의 DMF 코드는 전부 주석 처리되어 있고
    ///   같은 정보를 DB(UpdateRawDataImportInfoRecord)에 넣는다. 그래서 DMF 는
    ///   "있으면 필드를 채우고 없으면 넘어가는" 선택 입력이다(사용자 확정 2026-08-19).
    ///
    /// 미설정 값은 "-"(DMFKEY_DEFAULT) 또는 "0"(DMFKEY_DEFAULTCNT)으로 기록되므로
    /// "-" 는 빈 값으로 취급한다. 파싱 실패·파일 부재는 예외가 아니라 null 이다.
    /// </summary>
    public sealed class DmfInfo
    {
        /// <summary>읽어들인 DMF 파일의 전체 경로.</summary>
        public string SourcePath { get; init; } = "";

        /// <summary>
        /// 파싱한 전 섹션·전 키의 원문. 아래 타입 접근자가 다루지 않는 키도 여기 그대로 남는다
        /// — 매핑하지 않은 값이라도 "읽긴 읽었다"를 확인할 수 있어야 누락을 발견할 수 있다.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Sections { get; init; } = new();

        /// <summary>
        /// 섹션/키의 원문 값. 없거나 "-"(미설정)이면 빈 문자열.
        /// </summary>
        /// <param name="section">섹션명(LIST/SUBDB/SIGNAL). 대괄호 없이.</param>
        /// <param name="key">키명(한글 그대로).</param>
        /// <returns>정리된 값. 미설정·부재는 "".</returns>
        public string Get(string section, string key)
        {
            if (!Sections.TryGetValue(section, out var kv)) return "";
            if (!kv.TryGetValue(key, out var v)) return "";
            v = v.Trim();
            return v == "-" ? "" : v;
        }

        // ── [LIST] 수집원음/발췌자료 ──────────────────────────────────────

        /// <summary>연동ID('수집함_수집일시' 형태의 폴더명에서 유래).</summary>
        public string LinkedId => Get("LIST", "연동ID");
        /// <summary>수집체계. ★ .alg 에는 없는 값이라 DMF 가 유일한 출처다.</summary>
        public string RecordingSystem => Get("LIST", "수집체계");
        /// <summary>수집함(함정명). .alg szDetVesselName 에서 전사된 값.</summary>
        public string RecordingVessel => Get("LIST", "수집함");
        /// <summary>원음자료명(= .sd/.nb 폴더명). ① 계층에서는 빈 값.</summary>
        public string DatasetName => Get("LIST", "원음자료명");
        /// <summary>종류("협대역"/"단주기").</summary>
        public string DatasetType => Get("LIST", "종류");
        /// <summary>처리상태(연동완료/발췌완료/신호처리완료/CDMU통보완료).</summary>
        public string ProcessingState => Get("LIST", "처리상태");
        /// <summary>탐지소나(예: 214-CAS). MakeSignalDMF 가 (수집체계, 샘플율)로 산출한 값.</summary>
        public string DetSonar => Get("LIST", "탐지소나");
        /// <summary>연동경로(Online/Offline/AICS/ETC — 실데이터는 정의표와 어긋날 수 있다).</summary>
        public string LinkPath => Get("LIST", "연동경로");
        /// <summary>연동매체(Network/CF Memory/USB/MO …).</summary>
        public string LinkMedia => Get("LIST", "연동매체");
        /// <summary>발췌원음수(하위 Signal DMF 들의 누계).</summary>
        public int? ExtractCount => TryInt(Get("LIST", "발췌원음수"));
        /// <summary>샘플율(Hz).</summary>
        public int? SampleRate => TryInt(Get("LIST", "샘플율"));

        /// <summary>
        /// 수집일시([LIST]수집일시). ★ 폴더명 스탬프는 <b>수신</b>일시라 이것과 다르다
        /// (실측: 폴더 ISUS90_20191126151223 ↔ 수집일시 20191017001900).
        /// .alg 는 날짜(YYYYMMDD)만 주므로 시분초까지 가진 이 값이 가장 정확하다.
        /// </summary>
        public DateTime? CollectDateTime => TryStamp(Get("LIST", "수집일시"));

        // ── [SUBDB] 보조DB ───────────────────────────────────────────────

        /// <summary>수집함정급(예: 214). .alg szDetVesselClass 원문 — 체계명이 아니다.</summary>
        public string VesselClass => Get("SUBDB", "수집함정급");
        /// <summary>수신일시(폴더명 스탬프와 같은 값).</summary>
        public DateTime? ReceiptDateTime => TryStamp(Get("SUBDB", "수신일시"));
        /// <summary>위도("36:24:54 N" 형식).</summary>
        public string Latitude => Get("SUBDB", "위도");
        /// <summary>경도("130:39:48 E" 형식).</summary>
        public string Longitude => Get("SUBDB", "경도");
        /// <summary>임무 요약.</summary>
        public string Mission => Get("SUBDB", "임무");
        /// <summary>Quick 분석 장치(APU1/APU2/APU3).</summary>
        public string QaUnit => Get("SUBDB", "Quick분석장치");
        /// <summary>Quick 분석자(사람 이름).</summary>
        public string QaOperator => Get("SUBDB", "Quick분석자");
        /// <summary>Quick 분석 일시(= 실제 발췌 시각).</summary>
        public DateTime? QaDateTime => TryStamp(Get("SUBDB", "Quick일시"));

        // ── [SIGNAL] 원음신호(Signal_NN_NNN.DMF) ─────────────────────────

        /// <summary>원음신호명(Signal_NN_NNN).</summary>
        public string SignalName => Get("SIGNAL", "원음신호명");
        /// <summary>발췌종류("협대역"/"단주기"/"협대역/단주기").</summary>
        public string SignalType => Get("SIGNAL", "발췌종류");
        /// <summary>이벤트 개수.</summary>
        public int? SignalEventCount => TryInt(Get("SIGNAL", "이벤트개수"));
        /// <summary>원음신호 길이("00:19:18").</summary>
        public string SignalLength => Get("SIGNAL", "원음신호길이");
        /// <summary>연동파일명 — 개명 전 원본 WAV 파일명. DMF 에만 남는 값이다.</summary>
        public string LinkedFileName => Get("SIGNAL", "연동파일명");

        /// <summary>이 DMF 가 [LIST] 를 가진 자료관리파일인지(아니면 [SIGNAL] 전용 Signal DMF).</summary>
        public bool HasListSection => Sections.ContainsKey("LIST");

        /// <summary>
        /// 값이 채워져 있는데 <b>DB 어느 컬럼에도 반영되지 않는</b> 키들("섹션/키=값").
        ///
        /// 판정 기준은 "파서가 읽었는가"가 아니라 "이관 결과에 남는가"다 — 읽어만 놓고 버리는 값이
        /// 조용히 사라지지 않게 하기 위한 것이므로, 타입 접근자가 있어도 DB 로 가지 않으면 여기 나온다.
        /// 미설정("-")·0 은 애초에 값이 없는 것이라 누락이 아니다.
        /// </summary>
        public IEnumerable<string> PendingKeys()
        {
            foreach (var (sec, kv) in Sections)
            {
                if (!Persisted.TryGetValue(sec, out var done)) done = EmptySet;
                foreach (var (k, raw) in kv)
                {
                    if (done.Contains(k)) continue;
                    var v = raw.Trim();
                    if (v.Length == 0 || v == "-" || v == "0") continue;
                    yield return $"{sec}/{k}={v}";
                }
            }
        }

        private static readonly HashSet<string> EmptySet = new();

        /// <summary>
        /// 이관 결과에 실제로 남는 키. 직접 반영(수집체계·수집일시·수집함)과
        /// 같은 값이 다른 경로로 이미 들어가는 것(원음자료명=PREC_SRC_SIG_NAME,
        /// 종류=PREC_SRC_SIG_TYPE, 샘플율=SAMPLING_RATE)을 함께 센다.
        /// 여기 없는 키는 전부 PendingKeys 로 드러난다.
        /// </summary>
        private static readonly Dictionary<string, HashSet<string>> Persisted = new()
        {
            ["LIST"] = new HashSet<string> { "수집체계", "수집일시", "수집함", "원음자료명", "종류", "샘플율" },
            ["SUBDB"] = new HashSet<string>(),
            ["SIGNAL"] = new HashSet<string> { "원음신호명", "샘플율", "이벤트개수" },
        };

        /// <summary>"0" 을 포함한 정수 문자열 파싱. 빈 값·비정수는 null.</summary>
        /// <param name="s">원문.</param>
        /// <returns>파싱된 정수 또는 null.</returns>
        private static int? TryInt(string s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        /// <summary>
        /// DMF 시각 문자열을 DateTime 으로. yyyyMMddHHmmss(14) 우선, yyyyMMdd(8) 도 받는다.
        /// 레거시 실데이터에 '82557828' 같은 오염값이 있어(참고자료 번들 실측) 파싱 실패는 조용히 null.
        /// </summary>
        /// <param name="s">DMF 원문 값.</param>
        /// <returns>파싱된 시각 또는 null.</returns>
        private static DateTime? TryStamp(string s)
        {
            if (s.Length == 14 && DateTime.TryParseExact(s, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
            if (s.Length == 8 && DateTime.TryParseExact(s, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }
    }

    /// <summary>DMF(CP949 INI) 읽기 도우미.</summary>
    public static class DmfParser
    {
        /// <summary>정적 생성자. CP949 를 쓰기 위해 .NET 8 기본 미포함 인코딩 공급자를 등록한다.</summary>
        static DmfParser() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        /// <summary>자료관리파일의 고정 이름.</summary>
        public const string DmfFileName = "자료관리파일.DMF";

        /// <summary>
        /// 이관 항목 폴더에 대응하는 자료관리파일.DMF 경로를 찾는다.
        /// ② 계층(항목 폴더 안)을 먼저 보고, 없으면 ① 계층(바로 위 연동ID 폴더)으로 한 단계만 올라간다.
        /// 하위 전체 탐색은 하지 않는다 — 다른 발췌자료의 DMF 를 잘못 집어올 수 있기 때문이다.
        /// </summary>
        /// <param name="itemDir">발췌자료 폴더(.sd/.nb 또는 협대역 항목 폴더).</param>
        /// <returns>찾은 DMF 경로, 없으면 null.</returns>
        public static string? FindForItem(string itemDir)
        {
            try
            {
                var own = Path.Combine(itemDir, DmfFileName);
                if (File.Exists(own)) return own;

                var parent = Directory.GetParent(itemDir.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
                if (parent == null) return null;

                var up = Path.Combine(parent, DmfFileName);
                return File.Exists(up) ? up : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// DMF 파일을 읽어 섹션/키 사전으로 만든다. 인코딩은 UTF-8 BOM 이면 UTF-8, 아니면 CP949.
        /// 형식이 INI 라 관대하게 읽는다: 빈 줄·';'/'#' 주석은 건너뛰고, '=' 없는 줄은 무시하며,
        /// 섹션 밖의 키는 "" 섹션에 담는다. 중복 키는 마지막 값이 이긴다(WritePrivateProfileString 동작).
        /// 파일이 없거나 읽기에 실패하면 예외 대신 null — DMF 는 선택 입력이다.
        /// </summary>
        /// <param name="dmfPath">읽을 DMF 파일 경로.</param>
        /// <returns>파싱 결과. 파일 부재·읽기 실패·섹션 0개면 null.</returns>
        public static DmfInfo? TryParse(string? dmfPath)
        {
            if (string.IsNullOrEmpty(dmfPath) || !File.Exists(dmfPath)) return null;

            try
            {
                var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var current = "";

                foreach (var raw in File.ReadAllLines(dmfPath, DetectEncoding(dmfPath)))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                    if (line[0] == '[' && line[^1] == ']')
                    {
                        current = line[1..^1].Trim();
                        if (!sections.ContainsKey(current))
                            sections[current] = new Dictionary<string, string>(StringComparer.Ordinal);
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (!sections.TryGetValue(current, out var kv))
                        sections[current] = kv = new Dictionary<string, string>(StringComparer.Ordinal);

                    kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }

                if (sections.Count == 0) return null;
                return new DmfInfo { SourcePath = dmfPath, Sections = sections };
            }
            catch { return null; }
        }

        /// <summary>UTF-8 BOM 이 있으면 UTF-8, 아니면 CP949(국내 레거시 관례).</summary>
        /// <param name="path">검사할 파일 경로.</param>
        /// <returns>사용할 인코딩.</returns>
        private static Encoding DetectEncoding(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> bom = stackalloc byte[3];
                if (fs.Length >= 3 && fs.Read(bom) == 3 &&
                    bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    return Encoding.UTF8;
            }
            catch { }
            try { return Encoding.GetEncoding(949); }
            catch { return Encoding.UTF8; }
        }
    }
}
