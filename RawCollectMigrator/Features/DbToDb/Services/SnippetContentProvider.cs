using System.Data;
using System.IO;
using RawCollectMigrator.Features.DbToDb.Spectrogram;
using RawCollectMigrator.Infrastructure;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 스니펫 테이블(STD_NB/AI/TR_SNIPPET)의 blob·산출 컬럼을 파일에서 채운다.
    ///
    ///   · GRAM blob   : 레거시 .grm 을 R8 66B 로 변환 (GramConverter)
    ///   · AUDIO blob  : .wav 원본 바이트
    ///   · 파일명       : 소스 GRAM_FILENAME / AUDIO_FILENAME
    ///   · BIN_NUM / FRAME_NUM / FREQ / GRAM_TYPE : 그램 헤더에서 산출
    ///   · SNIPPET_*_DATETIME / SAMPLING_RATE     : wav 헤더에서 산출
    ///   · TR SPECTROGRAM_* : 그램이 없으므로 빈 byte / -1 / 계산값
    ///
    /// 한 소스 행에 여러 blob·산출 컬럼이 있으므로, "파일에서만 파생되는 값"은 파일명 조합
    /// 단위로 한 번만 로드해 캐시한다. DET_DATE/NO_FRAME 등 행마다 달라지는 입력에서
    /// 파생되는 컬럼(SNIPPET_*_DATETIME, SPECTROGRAM_FRAME_PER_SEC)은 캐시하지 않고
    /// 행마다 다시 계산한다 — 같은 파일을 참조하는 다른 행이 이전 행의 시각·프레임 값을
    /// 재사용하지 않게 하기 위함이다.
    /// 필수 파일이 없으면 예외를 던져 해당 행을 건너뛰게 한다(실행기가 catch).
    /// </summary>
    public sealed class SnippetContentProvider
    {
        private readonly SnippetFileResolver _resolver;
        private readonly Action<string> _log;

        // [찾기:스펙트로그램] TR 스니펫은 소스에 .grm 이 없어 원음 WAV 에서 스펙트로그램을 생성한다.
        //   구현은 인터페이스 뒤에 두어, 실제 산출물(.dll)이 확정되면 이 한 줄만 바꾸면 된다.
        private readonly ISpectrogramGenerator _spectro = new ManagedStftSpectrogramGenerator();
        private bool _spectroNoticeLogged;

        /// <summary>현재 캐시 조합의 원음 WAV 실제 경로(스펙트로그램 생성 입력).</summary>
        private string _audioPath = "";

        // 이 컬럼들은 이 제공자가 값을 만든다 (테이블별).
        private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
        {
            // NB
            "CLASSIFI_NB_GRAM_SNIPPET_FILE", "CLASSIFI_NB_AUDIO_SNIPPET_FILE",
            "STD_NB_GRAM_SNIPPET_FILE_NAME", "STD_NB_AUDIO_SNIPPET_FILE_NAME",
            // AI
            "GRAM_FILE", "AUDIO_FILE", "GRAM_FILE_NAME", "AUDIO_FILE_NAME",
            // TR
            "SPECTROGRAM_DATA", "SPECTROGRAM_START_FREQ", "SPECTROGRAM_END_FREQ", "SPECTROGRAM_FRAME_PER_SEC",
            // 공통 산출
            "BIN_NUM", "FRAME_NUM", "FREQ_MIN", "FREQ_MAX", "RESOLUTION", "GRAM_TYPE",
            "SNIPPET_START_DATETIME", "SNIPPET_END_DATETIME", "SAMPLING_RATE",
            // 영상자료(STD_REFERENCE_DATA): IMG_FILENAME 이 가리키는 실제 이미지 바이트
            "FILE_DATA",
            // [NAIMS-ISSUE:FLAG-FILE] 국가 국기(STD_COUNTRY_CODES): FLAG_FILENAME 이 가리키는 이미지 바이트.
            //   소스는 파일명(FLAG_FILENAME), 타겟은 blob(FLAG_FILE) 이라 이름이 안 맞아 자동 대응이 안 됐고,
            //   컬럼이 nullable 이라 조용히 NULL 로 들어가고 있었다 — 어떤 게이트에도 걸리지 않았다.
            "FLAG_FILE",
        };

        // [NAIMS-ISSUE:EMPTY-ON-MISSING] 파일이 없어도 행은 넣는다(사용자 결정, 2026-08-19).
        //   blob 은 빈 byte[] 로 두고, 그 파일에서 파생되던 NOT NULL 컬럼은 0/기본값으로 채운다.
        //   ⚠ 0 은 "값이 0" 이 아니라 "자료 없음" 이다. 그럴듯한 값을 지어내지 않는 이유가 그것이다 —
        //   나중에 파일을 확보해 재구축할 때 무엇이 비어 있었는지 눈으로 가려낼 수 있어야 한다.
        private readonly bool _emptyOnMissing;
        private readonly Action<string, string>? _onMissing;

        // [찾기:파일부재해결]
        /// <summary>사람이 리허설에서 정한 파일별 결정(파일명 → 결정). 없으면 종전 동작.</summary>
        private readonly IReadOnlyDictionary<string, DbToDbOptions.MissingFileDecision> _manual;

        /// <summary>사용자가 '이 파일이 없으면 행을 버린다' 고 정한 경우.</summary>
        public sealed class ManualFileDiscard : Exception
        {
            public ManualFileDiscard(string message) : base(message) { }
        }

        /// <param name="emptyOnMissing">true 면 파일이 없어도 빈 blob 으로 행을 적재한다(false 면 그 행을 스킵).</param>
        /// <param name="onMissing">(종류, 파일명) 통지 — 호출부가 건수를 집계한다.</param>
        /// <param name="manual">파일별 사람의 결정(버림/대체). 없으면 전부 기본(빈 값 적재).</param>
        public SnippetContentProvider(IReadOnlyList<string> roots, Action<string> log,
            bool emptyOnMissing = false, Action<string, string>? onMissing = null,
            IReadOnlyDictionary<string, DbToDbOptions.MissingFileDecision>? manual = null)
        {
            _log = log ?? (_ => { });
            _emptyOnMissing = emptyOnMissing;
            _onMissing = onMissing;
            _manual = manual ?? new Dictionary<string, DbToDbOptions.MissingFileDecision>(StringComparer.OrdinalIgnoreCase);
            var specs = roots.Select((r, i) => new SnippetFileResolver.RootSpec($"우선{i + 1}", r));
            _resolver = new SnippetFileResolver(specs, _log);
        }

        /// <summary>파일 부재를 빈 값으로 대체했음을 기록한다(종류별 1회 로그 + 호출부 집계).</summary>
        // [NAIMS-ISSUE:EMPTY-ON-MISSING] 부재 통지는 '파일 조합' 단위로만 한 번 일어난다(캐시 때문).
        //   그래서 통지만으로는 "몇 행이 영향받았는지" 를 셀 수 없다 — 같은 파일을 여러 행이 참조하면
        //   두 번째 행부터는 캐시에 걸려 통지가 없다. 행 단위 목록은 호출부가 이 속성을 매 행 읽어서 만든다.
        /// <summary>지금 캐시된 파일 조합에서 없던 파일들(종류, 파일명). 행마다 유효하다.</summary>
        public IReadOnlyList<(string Kind, string File)> CurrentMissing => _missingForKey;
        private List<(string Kind, string File)> _missingForKey = new();

        private void NoteMissing(string kind, string fileName)
        {
            _missingForKey.Add((kind, fileName));
            _onMissing?.Invoke(kind, fileName);
            if (_missingLogged.Add(kind))
                _log($"  · {kind} 파일 부재 → 빈 값으로 적재합니다 (예: {fileName}). " +
                     "파일 확보 후 전량 재구축하면 채워집니다.");
        }
        private readonly HashSet<string> _missingLogged = new(StringComparer.Ordinal);

        // [찾기:파일부재해결]
        /// <summary>
        /// 리졸버가 못 찾은 파일에 대해 사람의 결정을 적용한다.
        /// </summary>
        /// <returns>대체할 파일의 전체 경로. null 이면 결정이 없다는 뜻이라 호출부가 종전대로 처리한다.</returns>
        /// <exception cref="ManualFileDiscard">사용자가 '행 버림' 으로 정한 경우.</exception>
        private string? ManualPath(string kind, string fileName)
        {
            if (!_manual.TryGetValue(fileName, out var d)) return null;

            if (d.Action == DbToDbOptions.MissingFileDecision.Discard)
                throw new ManualFileDiscard($"{kind} 파일 없음({fileName}) — 사용자가 '행 버림' 으로 지정한 행");

            if (d.Action == DbToDbOptions.MissingFileDecision.Substitute
                && !string.IsNullOrWhiteSpace(d.SubstitutePath))
            {
                if (File.Exists(d.SubstitutePath)) return d.SubstitutePath;
                // 지정한 대체 파일마저 없으면 조용히 빈 값으로 내려가지 않고 사실을 남긴다.
                _log($"  · {kind} 대체 파일이 지정된 경로에 없습니다: {d.SubstitutePath}");
            }
            return null;
        }

        // [NAIMS-ISSUE:SNIPPET-ABS-TIME]
        /// <summary>
        /// 스니펫의 <b>절대 시작 시각</b> = DET_DATE 의 <b>날짜</b> + A_START_TIME <b>초</b>.
        ///
        /// 예전에는 DET_DATE 만 썼다. 레거시 DET_DATE 에는 시:분:초가 없어(자정) 스니펫 시각이
        /// 전부 <c>00:00:00</c> 으로 들어갔다 — 실측: TR 스펙트로그램 축이 00:00:00~00:00:07.
        /// 하루 중 언제 잡힌 신호인지가 사라지면 시각축은 있으나 마나다.
        ///
        /// 단위 근거(AAS Library_AASBase/UtilTime.cpp):
        ///   · <c>ConvertSTimeToLong</c>   = 자정 이후 <b>밀리초</b> (시×3600000 + 분×60000 + 초×1000 + ms)
        ///   · <c>ConvertSTimeToSecond</c> = 자정 이후 <b>초</b>
        ///   · <c>ConvertLongToSTime2(lTime, y, m, d)</c> — 날짜 + 자정이후시간으로 절대시각을 되만드는 정본 방식
        /// TR_SNIPPET 실측값이 44035·63664 인데, 초로 읽어야 12:13:55·17:41:04 이 되고
        /// A_END−A_START = 4초로 스니펫 길이와 맞는다. 밀리초로 읽으면 0.004초라 7초짜리 원음과 모순이다.
        /// 그래서 <b>초</b> 다. (실행기의 A_START_MILLISEC 매핑이 ×1000 하는 것과도 일치한다.)
        /// </summary>
        private DateTime SnippetStart(IReadOnlyDictionary<string, object?> row)
        {
            var day = SnippetDate(row);

            // ① NDID — 원음 자체가 적어 둔 구간이다. 가장 구체적이고, 이 blob 이 실제로 담고 있는 소리의 시각이다.
            if (_ndid is { } nd)
            {
                if (nd.AbsoluteStart is { } abs) return abs;            // 날짜까지 들어 있으면 그대로
                if (nd.StartSecOfDay >= 0) return day.AddSeconds(nd.StartSecOfDay);
            }

            // ② DB 의 A_START_TIME(자정 이후 초). 사람이 운용하며 적어 넣은 값이다.
            //    int.MaxValue 는 레거시의 "값 없음" 센티넬이다.
            long sec = ToLong(row.GetValueOrDefault("A_START_TIME"));
            if (sec > 0 && sec < 24 * 60 * 60) return day.AddSeconds(sec);

            // ③ 그램 헤더(자정 이후 ms). 그램은 원음보다 넓은 구간일 수 있어 뒤에 둔다.
            if (_gramStartMs >= 0 && _gramStartMs < MsPerDayL) return day.AddMilliseconds(_gramStartMs);

            // ④ 날짜만 — 이때는 자정이 사실이다. 지어내지 않는다.
            return day;
        }

        private const long MsPerDayL = 24L * 60 * 60 * 1000;

        // [NAIMS-ISSUE:SNIPPET-NO-DATE]
        /// <summary>
        /// 스니펫의 <b>날짜</b>를 정한다. 시각(NDID/A_START_TIME)과 달리 날짜는 어디에도 잘 없다.
        ///
        /// 실측 2026-08-20 — 레거시 DB 의 DET_DATE 대부분이 <b>자리표시자</b>다:
        ///   NB_SNIPPET 30행 중 29행이 1970-01-01 (00:00:00 15건 · 01:01:01 14건)
        ///   AI 10행 중 1건, TR 7행 중 3건도 1970-01-01 01:01:01
        /// 1970-01-01 은 <c>default(DateTime)</c>(0001-01-01)이 아니라서 그냥 두면 <b>유효한 날짜로 통과</b>하고,
        /// "1970-01-01 10:14:56" 같은 <b>지어낸 시각</b>이 만들어진다. 그래서 명시적으로 걸러낸다.
        ///
        /// 순서는 두 갈래뿐이다:
        ///   ① DET_DATE 가 자리표시자가 아니면 그것 — 운용자가 적은 탐지일이다.
        ///   ② 없으면 자리표시자를 <b>그대로 둔다</b>.
        ///
        /// bext OriginationDate 는 <b>쓰지 않는다</b>(2026-08-20 결정). 그건 파일을 <b>자른 날</b>이지
        /// 소리가 <b>잡힌 날</b>이 아니다 — OriginationTime 이 NDID 시각보다 항상 뒤이고,
        /// nb_33082886 은 17:46 녹음을 다음날 02:31 에 잘라 <b>날짜 자체가 다르다</b>.
        /// 대개 같은 날이라는 이유로 채워 넣으면, 맞을 때는 티가 안 나고 틀릴 때는 알아낼 방법이 없다.
        /// 모르는 것은 모르는 채로 두어야 "이 행은 날짜를 모른다" 를 나중에 가려낼 수 있다.
        /// </summary>
        private DateTime SnippetDate(IReadOnlyDictionary<string, object?> row) =>
            row.GetValueOrDefault("DET_DATE") is DateTime d && !IsPlaceholderDate(d) ? d.Date : NoDate;

        /// <summary>레거시가 "값 없음" 으로 쓰는 날짜. 1970-01-01 계열은 전부 자리표시자다.</summary>
        private static readonly DateTime NoDate = new(1970, 1, 1);

        private static bool IsPlaceholderDate(DateTime d) =>
            d == default || d.Year <= 1970;

        /// <summary>
        /// 스니펫 길이(초). 원음이 있으면 <b>실제 wav 길이</b>, 없으면 A_END−A_START(초).
        /// 원음을 우선하는 이유 — 길이는 파일에 실측으로 들어 있고, DB 값은 사람이 적은 값이다.
        /// </summary>
        private double SnippetLengthSec(IReadOnlyDictionary<string, object?> row)
        {
            if (_wav.IsValid && _wav.AudioLengthSec > 0) return _wav.AudioLengthSec;
            if (_ndid is { } nd && nd.EndSecOfDay > nd.StartSecOfDay && nd.StartSecOfDay >= 0)
                return nd.EndSecOfDay - nd.StartSecOfDay;
            if (_gramStartMs >= 0 && _gramEndMs > _gramStartMs) return (_gramEndMs - _gramStartMs) / 1000.0;
            long a = ToLong(row.GetValueOrDefault("A_START_TIME"));
            long b = ToLong(row.GetValueOrDefault("A_END_TIME"));
            double d = b - a;
            return d > 0 && d < 24 * 60 * 60 ? d : 0;
        }

        // [NAIMS-ISSUE:SNIPPET-ABS-TIME]
        /// <summary>
        /// 이 행의 그램 blob(R8 66B)을 만든다. 헤더에 시각이 없으면 <b>행의 시각</b>을 넣는다.
        /// 이미 R8 인 파일은 제 시각을 갖고 있으므로 그대로 통과시킨다.
        /// </summary>
        private byte[] BuildGramBlob(IReadOnlyDictionary<string, object?> row, bool isAi, out string? error)
        {
            error = null;
            if (_gramPassthrough.Length > 0) return _gramPassthrough;
            if (_gramLegacy is not { Ok: true } g || g.Amplitude.Length == 0) return Array.Empty<byte>();

            var start = SnippetStart(row);
            var end = start.AddSeconds(SnippetLengthSec(row));

            var r = GramConverter.ToR8(g, null, forceTag: isAi ? "A0" : null,
                                       fallbackStart: start, fallbackEnd: end);
            if (r is not { Ok: true })
            {
                error = $"그램 R8 변환 실패: {r?.Message ?? "변환 불가"}";
                return Array.Empty<byte>();
            }
            if (!isAi && !VerifyNbR8(r.Data, r.FrameCount, r.BinCount))
            {
                error = "협대역 그램 R8 변환 검증 실패(본문 길이 불일치)";
                return Array.Empty<byte>();
            }
            return r.Data;
        }

        // [NAIMS-ISSUE:NB-GRAM-R8]
        /// <summary>
        /// 만든 66B 바이트를 <b>정본 파서와 같은 방식으로</b> 되읽어 본다.
        ///
        /// 정본 마이그레이터(R8SnippetMigrator)도 "변환 후 즉시 66B 재파싱에 성공한 행만" 갱신한다.
        /// 길이가 어긋나면 NbGrmFileParser 의 TryDetectLayout 이 실패해 <조용히 구 raw-float 폴백>으로
        /// 떨어진다 — 화면에는 뭔가 그려지므로 사람은 정상이라고 믿는다. 그래서 여기서 먼저 막는다.
        /// 검사는 파서와 같은 계산이다: 본문 = 프레임수 × (TS16 + float[bin]).
        /// </summary>
        private static bool VerifyNbR8(byte[] data, int frames, int bins)
        {
            if (data == null || frames <= 0 || bins <= 0) return false;
            if (data.Length < GramHeader66.TotalSize) return false;
            long body = data.Length - GramHeader66.TotalSize;
            long expect = (long)frames * (GramHeader66.TimestampSize + (long)bins * sizeof(float));
            if (body != expect) return false;
            var h = GramHeader66.TryRead(data);
            return h != null && h.FrameCount == frames;
        }

        public bool Handles(string targetTable, string column) =>
            (IsSnippetTable(targetTable) && Handled.Contains(column))
            || (IsRefDataTable(targetTable) && column.Equals("FILE_DATA", StringComparison.OrdinalIgnoreCase))
            || (IsCountryTable(targetTable) && column.Equals("FLAG_FILE", StringComparison.OrdinalIgnoreCase));

        private static bool IsSnippetTable(string t) => t.ToUpperInvariant() is
            "STD_NB_SNIPPET" or "STD_AI_SNIPPET" or "STD_TR_SNIPPET";

        private static bool IsRefDataTable(string t) =>
            t.Equals("STD_REFERENCE_DATA", StringComparison.OrdinalIgnoreCase);

        /// <summary>국가표 — 국기 이미지(CTRYIMAGE)를 blob 으로 싣는다.</summary>
        private static bool IsCountryTable(string t) =>
            t.Equals("STD_COUNTRY_CODES", StringComparison.OrdinalIgnoreCase);

        /// <summary>능동식별 스니펫 — 그램 blob 규약이 NB 와 달라(R8 66B) 별도 분기가 필요하다.</summary>
        private static bool IsAiSnippetTable(string t) =>
            t.Equals("STD_AI_SNIPPET", StringComparison.OrdinalIgnoreCase);

        // ── 파일 단위 캐시 ──
        // ⚠ 단일 스레드 전제: 실행기(DbToDbExecutor)가 행을 순차 처리하므로 잠금 없이
        //   마지막 파일 조합 하나만 캐시한다. 병렬 처리로 바꾸면 이 캐시(그리고 리졸버의
        //   인덱스 캐시)부터 재설계해야 한다.
        // 캐시 키는 "타겟 테이블 + 파일명 조합"이며, 캐시 값도 파일에서만 파생된 것만 담는다.
        // (과거: DET_DATE/NO_FRAME 파생 값까지 이 키로 캐시해, 같은 파일을 참조하는
        //  다른 행이 이전 행의 SNIPPET_*_DATETIME·FRAME_PER_SEC 를 재사용하는 버그가 있었다.)
        private string _fileKey = "";
        private Dictionary<string, object?> _fileCache = new(StringComparer.OrdinalIgnoreCase);
        private WavHeaderInfo _wav;                 // 행 파생 계산에 쓰는 wav 헤더 (파일 파생)
        private bool _rawFloatNoticeLogged;         // RawFloat 안내 로그는 1회만 (로그 홍수 방지)
        private bool _aiTagRewriteLogged;           // AI 태그 교정 안내도 1회만

        // [NAIMS-ISSUE:TR-SPECTRO-NONDETERMINISTIC] TR 스펙트로그램 산출물(파일 파생)만 캐시하고,
        //   blob 직렬화는 행마다 한다 — blob 안에 들어가는 시각이 행의 DET_DATE 에서 나오기 때문.
        //   예전에는 DateTime.UtcNow 로 직렬화해서 (ㄱ) 같은 wav 를 쓰는 다른 행이 첫 행의 시각을
        //   물려받고 (ㄴ) 실행할 때마다 바이트가 달라져 재실행 대조 자체가 불가능했다.
        private SpectrogramResult? _trSpectro;

        // [NAIMS-ISSUE:SNIPPET-ABS-TIME] 그램 헤더의 자정 이후 밀리초(있으면).
        //   NB 는 DB 의 A_START_TIME 이 <전부 0> 이다(실측 30/30) — 시각을 .grm 헤더에 갖고
        //   DB 에는 안 넣는 체계다. DB 만 보면 스니펫 시각이 자정이 되어, 같은 행인데
        //   그램 축은 10:14 인데 SNIPPET_START_DATETIME 은 00:00 인 모순이 생긴다.
        private long _gramStartMs = -1, _gramEndMs = -1;

        // [NAIMS-ISSUE:SNIPPET-ABS-TIME] 그램 blob 은 <행 파생> 이다 — 파일 캐시에 담지 않는다.
        //   blob 안의 시각이 행의 A_START_TIME 에서 나올 수 있기 때문이다. 캐시에 담으면 같은 .grm 을
        //   참조하는 두 번째 행이 첫 행의 시각을 물려받는다(TR 스펙트로그램에서 이미 겪은 버그).
        //   그래서 <파싱 결과>만 캐시하고(파일 파생), 바이트 직렬화는 행마다 한다.
        // [찾기:NDID] 원음 자체가 적어 둔 구간 시각(파일 파생).
        private NdidChunk.Times? _ndid;

        private LegacyGrm? _gramLegacy;          // 파싱 결과(파일 파생)
        private byte[] _gramPassthrough = Array.Empty<byte>();   // 이미 R8 인 파일은 그대로 통과

        // [NAIMS-ISSUE:EMPTY-ON-MISSING] AI 그램이 빈 상태에서 STD_AI_SPECTROGRAM 자식행을 만들면
        //   전시 측이 예외를 던진다. AIATUSW StdDBAISnippetItemViewModel.Init 은
        //   (U8GramFile != null && ISpectrogram != null) 일 때만 그리는데, 빈 배열은 null 이 아니라
        //   이 관문을 통과하고 ArGramCodec 역직렬화에서 "AR Gram 역직렬화 실패" 로 터진다.
        //   자식행을 만들지 않으면 ISpectrogram 이 null 이라 관문에서 걸려 빈 화면으로 지나간다.
        //   자료가 없을 때 목표는 "예쁘게 보이기" 가 아니라 "터지지 않기" 다.
        private bool _aiGramEmpty;

        public bool Resolve(TablePlan plan, TargetColumn tc,
            IReadOnlyDictionary<string, object?> row, out object? value)
        {
            EnsureFiles(plan, row);

            // 행 파생 컬럼: 캐시하지 않고 이 행의 DET_DATE/NO_FRAME 으로 매번 계산
            switch (tc.Name.ToUpperInvariant())
            {
                case "SNIPPET_START_DATETIME":
                case "SNIPPET_END_DATETIME":
                case "SPECTROGRAM_FRAME_PER_SEC":
                case "SPECTROGRAM_DATA":
                // [NAIMS-ISSUE:SNIPPET-ABS-TIME] 그램 blob 안의 시각이 행에서 나올 수 있다 → 행 파생.
                case "CLASSIFI_NB_GRAM_SNIPPET_FILE":
                case "GRAM_FILE":
                    return TryRowDerived(plan.TargetTable, tc.Name, row, out value);
            }

            return _fileCache.TryGetValue(tc.Name, out value);
        }

        /// <summary>행마다 달라지는 입력(DET_DATE/NO_FRAME)에서 파생되는 값을 계산한다.</summary>
        private bool TryRowDerived(string target, string name,
            IReadOnlyDictionary<string, object?> row, out object? value)
        {
            value = null;
            DateTime start = SnippetStart(row);

            switch (name.ToUpperInvariant())
            {
                // 원음이 없으면 길이를 모른다 → 시작=끝(길이 0)으로 두되 컬럼은 채운다(NOT NULL).
                case "SNIPPET_START_DATETIME":
                    if (!_wav.IsValid && !_emptyOnMissing) return false;
                    value = start;
                    return true;

                case "SNIPPET_END_DATETIME":
                    if (!_wav.IsValid && !_emptyOnMissing) return false;
                    value = start.AddSeconds(SnippetLengthSec(row));
                    return true;

                case "CLASSIFI_NB_GRAM_SNIPPET_FILE":
                case "GRAM_FILE":
                {
                    bool ai = name.Equals("GRAM_FILE", StringComparison.OrdinalIgnoreCase);
                    value = BuildGramBlob(row, ai, out string? why);
                    if (value is byte[] { Length: 0 } && !_emptyOnMissing && why != null)
                        throw new InvalidDataException(why);
                    return true;
                }

                case "SPECTROGRAM_DATA":
                    // TR 전용. 시각은 이 행의 DET_DATE 기준 — AI 스펙트로그램과 같은 규약.
                    if (!target.Equals("STD_TR_SNIPPET", StringComparison.OrdinalIgnoreCase)) return false;
                    if (_trSpectro is not { } trSp)
                    {
                        if (!_emptyOnMissing) return false;
                        value = Array.Empty<byte>();
                        return true;
                    }
                    var spStart = DateTime.SpecifyKind(start, DateTimeKind.Utc);
                    var spEnd = spStart.AddSeconds(SnippetLengthSec(row));
                    value = SpectrogramS0Codec.Serialize(trSp, spStart, spEnd);
                    return true;

                case "SPECTROGRAM_FRAME_PER_SEC":
                    // TR 전용 — 다른 테이블이면 기존처럼 생략
                    if (!target.Equals("STD_TR_SNIPPET", StringComparison.OrdinalIgnoreCase)) return false;
                    value = _wav.IsValid && _wav.AudioLengthSec > 0
                        ? ToInt(row.GetValueOrDefault("NO_FRAME")) / Math.Max(_wav.AudioLengthSec, 0.001f)
                        : 0f;
                    return true;
            }
            return false;
        }

        /// <summary>이 행이 참조하는 파일 조합의 "파일 파생" 캐시를 준비한다.</summary>
        private void EnsureFiles(TablePlan plan, IReadOnlyDictionary<string, object?> row)
        {
            string audioName = Str(row.GetValueOrDefault("AUDIO_FILENAME"));
            string gramName = Str(row.GetValueOrDefault("GRAM_FILENAME"));
            string imgName = Str(row.GetValueOrDefault("IMG_FILENAME"));
            string flagName = Str(row.GetValueOrDefault("FLAG_FILENAME"));
            string key = plan.TargetTable + "|" + audioName + "|" + gramName + "|" + imgName + "|" + flagName;
            if (key == _fileKey) return;

            // 구축 실패(파일 없음 등 예외) 시 이전 조합의 캐시가 이 키로 재사용되지 않도록
            // 먼저 비우고, 성공했을 때만 키를 확정한다.
            _fileKey = "";
            _wav = default;
            _trSpectro = null;
            _gramStartMs = -1; _gramEndMs = -1;
            _gramLegacy = null; _gramPassthrough = Array.Empty<byte>();
            _ndid = null;
            _aiGramEmpty = false;
            _missingForKey = new List<(string, string)>();
            _fileCache = BuildFileCache(plan.TargetTable, audioName, gramName, imgName, flagName);
            _fileKey = key;
        }

        /// <summary>
        /// 파일에서만 파생되는 값을 로드·산출한다. 행 파생 값(시각·프레임률)은 여기서 만들지 않는다.
        /// </summary>
        private Dictionary<string, object?> BuildFileCache(
            string target, string audioName, string gramName, string imgName, string flagName)
        {
            var c = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // ── 국가 국기(STD_COUNTRY_CODES): FLAG_FILENAME → CTRYIMAGE 의 이미지 원본 ──
            // [NAIMS-ISSUE:FLAG-FILE] 파일명이 비어 있는 국가는 국기가 없는 것이 정상이므로
            //   '파일 부재' 로 세지 않는다 — 참조 자체가 없는 것과 참조가 깨진 것은 다르다.
            if (IsCountryTable(target))
            {
                if (flagName.Length == 0) { Put(c, "FLAG_FILE", DBNull.Value); return c; }
                var flag = _resolver.Resolve(flagName);
                string? flagPath = flag.Found ? flag.FullPath : ManualPath("국기", flagName);
                if (flagPath == null)
                {
                    if (!_emptyOnMissing)
                        throw new IOException($"국기 파일 없음: {flagName}" + PartialNote(flag));
                    NoteMissing("국기", flagName);
                    Put(c, "FLAG_FILE", Array.Empty<byte>());
                    return c;
                }
                Put(c, "FLAG_FILE", File.ReadAllBytes(flagPath));
                return c;
            }

            // ── 영상자료(STD_REFERENCE_DATA): IMG_FILENAME 이 가리키는 이미지 원본을 FILE_DATA 로 ──
            if (IsRefDataTable(target))
            {
                if (string.IsNullOrWhiteSpace(imgName))
                    throw new IOException("IMG_FILENAME 이 비어 있음");
                var img = _resolver.Resolve(imgName);
                string? imgPath = img.Found ? img.FullPath : ManualPath("영상", imgName);
                if (imgPath == null)
                {
                    if (!_emptyOnMissing)
                        throw new IOException($"이미지 파일 없음: {imgName}" + PartialNote(img));
                    NoteMissing("영상", imgName);
                    Put(c, "FILE_DATA", Array.Empty<byte>());
                    return c;
                }
                Put(c, "FILE_DATA", File.ReadAllBytes(imgPath));
                return c;
            }

            bool isTr = target.Equals("STD_TR_SNIPPET", StringComparison.OrdinalIgnoreCase);
            bool isAi = IsAiSnippetTable(target);

            // ── AUDIO (wav) ──
            byte[] audioBytes = Array.Empty<byte>();
            _audioPath = "";
            if (!string.IsNullOrWhiteSpace(audioName))
            {
                var a = _resolver.Resolve(audioName);
                string? aPath = a.Found ? a.FullPath : ManualPath("원음(wav)", audioName);
                if (aPath == null)
                {
                    if (!_emptyOnMissing)
                        throw new IOException($"wav 파일 없음: {audioName}" + PartialNote(a));
                    NoteMissing("원음(wav)", audioName);
                    // audioBytes 는 빈 채로 둔다. _wav 무효 · _audioPath 빈 문자열 →
                    // 아래에서 파생 컬럼을 0 으로 채우고 스펙트로그램 생성도 건너뛴다.
                }
                else
                {
                    audioBytes = File.ReadAllBytes(aPath);
                    _wav = WavHeader.TryRead(aPath);
                    _audioPath = aPath;
                    _ndid = NdidChunk.TryRead(audioBytes);   // [찾기:NDID] 원음이 적어 둔 구간 시각
                }
            }

            // ── GRAM (.grm → R8) : TR 은 그램 없음 ──
            GramConverter.ConvertResult? gram = null;
            LegacyGrm? legacy = null;
            byte[] r8Bytes = Array.Empty<byte>();   // R8 66B 헤더 포함 바이너리 (AI 스니펫용)
            if (!isTr && !string.IsNullOrWhiteSpace(gramName))
            {
                var g = _resolver.Resolve(gramName);
                string? gPath = g.Found ? g.FullPath : ManualPath("그램(grm)", gramName);
                if (gPath == null && _emptyOnMissing)
                {
                    NoteMissing("그램(grm)", gramName);
                    goto afterGram;   // legacy 는 null 로 두어 아래 기본값 경로를 탄다
                }
                if (gPath == null) throw new IOException($"grm 파일 없음: {gramName}" + PartialNote(g));
                legacy = LegacyGrmReader.Read(gPath);
                if (legacy is { Ok: true } && legacy.EndTimeMs > legacy.StartTimeMs)
                {
                    _gramStartMs = legacy.StartTimeMs;
                    _gramEndMs = legacy.EndTimeMs;
                }

                // [NAIMS-ISSUE:AI-GRAM-R8] 이미 R8(66B) 인 파일은 변환 대상이 아니라 "그대로 통과" 다.
                //   예전에는 ToR8 이 Ok=false("이미 R8 포맷")를 돌려주는 것을 변환 실패로 보고
                //   행 전체를 스킵해, 최신 양식 그램일수록 이관되지 않는 역전이 있었다.
                _gramLegacy = legacy;
                if (legacy.Kind == LegacyGrmKind.R8)
                {
                    r8Bytes = File.ReadAllBytes(gPath);
                    // [NAIMS-ISSUE:AI-GRAM-A0] 이미 R8 이어도 AI 컬럼이 요구하는 태그는 "A0" 다.
                    //   본문 레이아웃(GramHeader66 + 프레임[TS16+float]) 은 A0·S0·L0 가 모두 같으므로
                    //   선두 2바이트만 고쳐 쓰면 무손실이다. 그대로 두면 전시 측이 legacy 213B 로
                    //   오해해 오류 없이 잘못 그린다.
                    if (isAi && !IsA0(r8Bytes))
                    {
                        r8Bytes = (byte[])r8Bytes.Clone();
                        r8Bytes[0] = (byte)'A';
                        r8Bytes[1] = (byte)'0';
                        WarnOnce(ref _aiTagRewriteLogged,
                            $"  · AI 스니펫 그램 태그를 'A0' 로 교정 (예: {gramName})");
                    }
                    _gramPassthrough = r8Bytes;   // 이미 R8 — 제 시각을 갖고 있으니 손대지 않는다
                }
                else
                {
                    // 기준일(baseDate)은 태그·성공 여부에 영향이 없으므로 행의 DET_DATE 를 넘기지 않는다
                    // — 이 캐시를 순수 파일 파생으로 유지하기 위함.
                    // AI 는 원본 종류와 무관하게 "A0" 강제 — 타겟 컬럼 규약이 원본보다 우선한다.
                    gram = GramConverter.ToR8(legacy, null, forceTag: isAi ? "A0" : null);
                    if (gram is { Ok: false })
                        throw new InvalidDataException($"grm 변환 실패({gramName}): {gram.Message}");
                    if (gram != null) r8Bytes = gram.Data;
                }
            }
            afterGram:

            // ── 컬럼별 값 ──
            // 파일명
            Put(c, "STD_NB_AUDIO_SNIPPET_FILE_NAME", audioName);
            Put(c, "AUDIO_FILE_NAME", audioName);
            Put(c, "STD_NB_GRAM_SNIPPET_FILE_NAME", gramName);
            Put(c, "GRAM_FILE_NAME", gramName);

            // blob
            Put(c, "CLASSIFI_NB_AUDIO_SNIPPET_FILE", audioBytes);
            Put(c, "AUDIO_FILE", audioBytes);

            // 그램 blob(CLASSIFI_NB_GRAM_SNIPPET_FILE / GRAM_FILE)은 여기서 넣지 않는다 —
            // 행마다 시각이 달라질 수 있어 <행 파생>(TryRowDerived → BuildGramBlob)으로 옮겼다.
            // 여기 남겨 두면 파일 캐시 값이 먼저 잡혀 같은 .grm 을 쓰는 행끼리 시각이 물려진다.

            // [NAIMS-ISSUE:AI-GRAM-R8] AI 스니펫(STD_AI_SNIPPET.GRAM_FILE)은 NB 와 규약이 다르다.
            //   NB  : NAIMS-II NBSnippet.LoadSnippetInfo 가 blob 전체를 float[] 로 BlockCopy
            //         (byteSnippetData.Length / 4) → 순수 float, 헤더 금지. 66B 를 붙이면 길이가
            //         4의 배수가 아니라 BlockCopy 가 범위를 넘는다.
            //   AI  : AR 그램 정본 = R8 66B("A0") 헤더 포함 바이너리 (ArGramCodec.Serialize)
            //   예전에는 둘 다 순수 float 을 넣어, AI 쪽이 R8 로 적재되지 않았다.
            if (isAi)
            {
                _aiGramEmpty = r8Bytes.Length == 0;
                if (r8Bytes.Length == 0 && !_emptyOnMissing)
                    throw new InvalidDataException(
                        $"AI 스니펫 그램(R8 66B) 생성 실패: {gramName} — GRAM_FILE 은 NOT NULL 이라 적재할 수 없음");
                Put(c, "GRAM_FILE", r8Bytes);
            }
            else if (legacy is { Ok: true } && legacy.Amplitude.Length > 0)
            {
                var ampBytes = new byte[(long)legacy.Amplitude.Length * sizeof(float)];
                Buffer.BlockCopy(legacy.Amplitude, 0, ampBytes, 0, ampBytes.Length);
                Put(c, "GRAM_FILE", ampBytes);
            }
            else if (_emptyOnMissing)
            {
                Put(c, "GRAM_FILE", Array.Empty<byte>());
            }

            // 그램 헤더 산출값
            if (legacy is { Ok: true })
            {
                Put(c, "BIN_NUM", legacy.BinCount > 0 ? legacy.BinCount : 1601);
                Put(c, "FRAME_NUM", legacy.FrameCount);

                // [NAIMS-ISSUE:RAWFLOAT-FREQ] 헤더 없는 그램(RawFloat)은 StartFreq/EndFreq/Resolution 이
                // 전부 0 이다. 0 을 그대로 기록하면 FREQ_MIN/MAX·RESOLUTION 을 0 으로 덮어쓰므로,
                // 헤더가 실제 값을 가질 때만 기록하고 아니면 컬럼을 생략해 DB 기본 경로를 유지한다.
                // (GramConverter 의 "주파수·시각 정보는 DB에서 채워야 함" 주석과 정합)
                if (legacy.Kind != LegacyGrmKind.RawFloat)
                {
                    Put(c, "FREQ_MIN", legacy.StartFreq);
                    Put(c, "FREQ_MAX", legacy.EndFreq);
                    // 레거시 Resolution 이 비어 있으면 GramConverter 와 같은 식으로 범위에서 산출
                    float res = legacy.Resolution > 0
                        ? legacy.Resolution
                        : (legacy.BinCount > 1 && legacy.EndFreq > legacy.StartFreq
                            ? (legacy.EndFreq - legacy.StartFreq) / (legacy.BinCount - 1)
                            : 0f);
                    if (res > 0) Put(c, "RESOLUTION", res);
                }
                else if (!_rawFloatNoticeLogged)
                {
                    _rawFloatNoticeLogged = true;
                    _log($"  · 헤더 없는 그램(원시 float) 감지 (예: {gramName}) — " +
                         "FREQ_MIN/MAX·RESOLUTION 은 기록하지 않음(DB 기본 경로 유지)");
                }

                // [NAIMS-ISSUE:GRAM-TYPE] NOT NULL 방어·태그 미매핑 시 기본값
                // GRAM_TYPE 은 타겟 NOT NULL. 레거시 태그가 매핑표에 없으면(예: 소문자/변형)
                // 변환기가 만든 R8 태그(L0/D0/V1~4 — 항상 유효)로 재산출하고, 그래도 없으면 L(101).
                int gt = MigrationRules.GramTypeCode(legacy.GramTag)
                       ?? (gram != null ? MigrationRules.GramTypeCode(gram.Tag) : null)
                       ?? 101;
                Put(c, "GRAM_TYPE", gt);
            }
            else
            {
                // [NAIMS-ISSUE:EMPTY-ON-MISSING] 그램이 없으면 그램에서 나오던 NOT NULL 컬럼을
                //   여기서 전부 채워야 한다 — 안 채우면 'NOTNULL-값없음' 으로 그 행이 또 스킵된다.
                //   FRAME_NUM=0 · 빈 blob 은 서로 정합한다(0프레임 × 1601빈 = 0바이트).
                Put(c, "BIN_NUM", 1601);   // 표준 빈 수
                Put(c, "GRAM_TYPE", 101);  // NOT NULL 방어 기본값
                if (_emptyOnMissing)
                {
                    Put(c, "FRAME_NUM", 0);
                    Put(c, "FREQ_MIN", 0f);
                    Put(c, "FREQ_MAX", 0f);
                    Put(c, "RESOLUTION", 0f);
                }
            }

            // wav 헤더 산출값 — 파일 파생인 샘플레이트만 여기서.
            // (SNIPPET_*_DATETIME 은 DET_DATE 파생이라 TryRowDerived 에서 행마다 계산)
            if (_wav.IsValid) Put(c, "SAMPLING_RATE", _wav.SampleRate);
            else if (_emptyOnMissing) Put(c, "SAMPLING_RATE", 0);   // 0 = 자료 없음

            // ── TR 전용: SPECTROGRAM ──────────────────────────────────────
            // [NAIMS-ISSUE:TR-SPECTROGRAM] 예전에는 빈 byte[] 와 -1 을 넣어, NOT NULL 을 형식만
            //   맞춘 가짜 값이 적재됐다(기준식별에서 열리지 않음). 이제 원음 WAV 에서 실제로 생성한다.
            //   소스 TR_SNIPPET 은 .grm 이 없고 .wav 만 있으므로 생성이 유일한 경로다.
            if (isTr)
            {
                if (_audioPath.Length == 0 && _emptyOnMissing)
                {
                    // 원음이 없으면 스펙트로그램을 만들 근거가 없다. 지어내지 않고 비운다.
                    Put(c, "SPECTROGRAM_START_FREQ", 0f);
                    Put(c, "SPECTROGRAM_END_FREQ", 0f);
                    Put(c, "SPECTROGRAM_FRAME_PER_SEC", 0f);
                    Put(c, "BIN_NUM", 0);
                    Put(c, "FRAME_NUM", 0);
                    return c;   // SPECTROGRAM_DATA 는 행 파생 경로가 빈 배열을 돌려준다
                }
                if (_audioPath.Length == 0)
                    throw new InvalidDataException(
                        "TR 스니펫 스펙트로그램 생성 불가: AUDIO_FILENAME 이 비어 원음 WAV 가 없음 " +
                        "(SPECTROGRAM_DATA 는 NOT NULL)");

                if (!_spectroNoticeLogged)
                {
                    _spectroNoticeLogged = true;
                    _log($"  · TR 스펙트로그램 생성기: {_spectro.Name}");
                }

                var sp = _spectro.Generate(_audioPath);
                if (sp.IsEmpty)
                    throw new InvalidDataException(
                        $"TR 스니펫 스펙트로그램 생성 실패: {audioName} — 프레임을 만들 수 없음(원음이 너무 짧음)");

                // [NAIMS-ISSUE:TR-SPECTROGRAM] blob 은 순수 float 이 아니라 S0(R8 66B) 자기기술 포맷이다.
                //   전시 측(AIATUSW SpectrogramS0Codec.TryDeserialize)이 선두 2바이트를 "S0" 로 검사하므로
                //   float 만 넣으면 열리지 않는다.
                // blob 은 여기서 만들지 않는다 — 시각이 행의 DET_DATE 파생이라 TryRowDerived 담당.
                _trSpectro = sp;
                Put(c, "SPECTROGRAM_START_FREQ", sp.StartFreq);
                Put(c, "SPECTROGRAM_END_FREQ", sp.EndFreq);
                Put(c, "SPECTROGRAM_FRAME_PER_SEC", sp.FramePerSec);
                Put(c, "BIN_NUM", sp.BinCount);
                Put(c, "FRAME_NUM", sp.FrameCount);
            }

            return c;
        }

        // [찾기:스펙트로그램] [NAIMS-ISSUE:DERIVED-CHILD]
        /// <summary>
        /// STD_AI_SPECTROGRAM 자식행의 컬럼값을 만든다(부모 STD_AI_SNIPPET INSERT 직후 호출).
        ///
        /// 레거시에는 스펙트로그램이 없다. 현행 체계는 정밀신호처리 때 서버가 만든 전체 스펙트로그램에서
        /// 스니펫 구간을 발췌해 쓰지만(PREC_SS_SPECTROGRAM → 그대로 이전), 레거시 이관에는 그 원본이
        /// 없으므로 <b>스니펫 원음(.wav) 자체가 이미 발췌본</b>이라는 점을 이용해 거기서 생성한다.
        /// (사용자 확정: 레거시 이관은 이 방식. 현행 파이프라인이 붙이는 ±0.5초 패딩만큼 구간이 짧다.)
        ///
        /// blob 은 순수 float 이 아니라 S0(R8 66B) 자기기술 포맷이어야 전시 측이 읽는다.
        /// </summary>
        /// <returns>컬럼→값 사전. 원음이 없어 만들 수 없으면 null.</returns>
        public Dictionary<string, object?>? BuildAiSpectrogramRow(
            IReadOnlyDictionary<string, object?> row, DateTime chngDateTime)
        {
            if (_audioPath.Length == 0) return null;
            if (_aiGramEmpty) return null;   // 위 주석 참조 — 반쪽짜리 자식행이 전시를 터뜨린다

            if (!_spectroNoticeLogged)
            {
                _spectroNoticeLogged = true;
                _log($"  · 스펙트로그램 생성기: {_spectro.Name}");
            }

            var sp = _spectro.Generate(_audioPath);
            if (sp.IsEmpty) return null;

            // 그램 구간 시각: 소스 DET_DATE 를 시작점으로 삼고 원음 길이만큼 이어붙인다.
            var start = row.GetValueOrDefault("DET_DATE") is DateTime d && d != default
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
                : DateTime.UtcNow;
            var end = _wav.IsValid && _wav.AudioLengthSec > 0
                ? start.AddSeconds(_wav.AudioLengthSec)
                : start;

            double binRes = sp.BinCount > 1 ? (sp.EndFreq - sp.StartFreq) / (sp.BinCount - 1) : 0.0;

            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FRAME_NUM"]    = sp.FrameCount,
                ["BIN_RES"]      = (float)binRes,
                ["START_FREQ"]   = sp.StartFreq,
                ["END_FREQ"]     = sp.EndFreq,
                ["CHNG_DATETIME"] = chngDateTime,
                ["IS_TRAINING"]  = 0,
                ["GRAM_DATA"]    = SpectrogramS0Codec.Serialize(sp, start, end),
            };
        }

        /// <summary>선두 2바이트가 AR 그램 태그 "A0" 인지.</summary>
        private static bool IsA0(byte[] b) => b.Length >= 2 && b[0] == (byte)'A' && b[1] == (byte)'0';

        /// <summary>같은 안내를 이관 1회당 한 번만 남긴다(로그 홍수 방지).</summary>
        private void WarnOnce(ref bool flag, string message)
        {
            if (flag) return;
            flag = true;
            _log(message);
        }

        /// <summary>파일 인덱스가 부분 실패였으면 "없음" 판정이 불확실함을 예외 문구에 덧붙인다.</summary>
        private static string PartialNote(SnippetFileResolver.Resolved r)
            => r.IndexIncomplete ? " (⚠ 파일 인덱스 부분 실패 — 실제로는 존재할 수 있음)" : "";

        private static void Put(Dictionary<string, object?> c, string key, object? v)
        {
            if (!Handled.Contains(key)) return;   // 안전
            c[key] = v ?? (object)DBNull.Value;
        }

        // [NAIMS-ISSUE:FILENAME-PAD] CHAR(80) 공백패딩 미Trim 시 전체 스킵
        // 파일명 컬럼이 레거시 CHAR(80) 공백 패딩인 경우가 있어(Oracle 소스 실측) 반드시 Trim 한다.
        // 패딩된 이름으로는 리졸버가 파일을 못 찾아 스니펫이 전부 스킵된다.
        private static string Str(object? v) => (v == null || v is DBNull) ? "" : (Convert.ToString(v) ?? "").Trim();
        private static long ToLong(object? v)
        {
            if (v == null || v is DBNull) return 0;
            return long.TryParse(Convert.ToString(v), out var l) ? l : 0;
        }

        private static int ToInt(object? v)
        {
            if (v == null || v is DBNull) return 0;
            return int.TryParse(Convert.ToString(v), out var i) ? i : 0;
        }
    }
}
