using System.Data.Common;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 기준식별 트리(CLASS→VESSEL→SIGNATURES/… )에서, 선택한 CLASS 서브트리에 속한
    /// 소스 PK 집합을 테이블별로 계산한다. 이관 실행기는 이 집합으로 각 테이블 행을 거른다.
    ///
    /// 설계(사용자 확정): 참조된 공유데이터만 포함(국가·무장·능동소나), 다중 CLASS 선택.
    /// 클래스 도메인 밖 테이블(PULSES·배포·이력 등)은 CLASS 필터 시 이관 대상에서 제외한다.
    /// </summary>
    public sealed class ClassScopeResolver
    {
        private readonly DbProviderKind _kind;
        private readonly DbConnectionMode _mode;
        private readonly string _connStr;

        public ClassScopeResolver(DbProviderKind kind, DbConnectionMode mode, string connStr)
        {
            _kind = kind;
            _mode = mode;
            _connStr = connStr;
        }

        /// <summary>
        /// 선택 CLASS_ID 로부터 테이블별 허용 소스 PK 집합을 만든다.
        /// 반환 딕셔너리에 없는 테이블은 CLASS 범위 밖(=이관 제외).
        /// </summary>
        public async Task<Dictionary<string, HashSet<long>>> ComputeAsync(
            IReadOnlyCollection<long> classIds, CancellationToken ct = default)
        {
            var classes = new HashSet<long>(classIds);
            var allow = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

            await using var cn = DbProvider.CreateConnection(_kind, _mode, _connStr);
            await cn.OpenAsync(ct);

            // 최상위
            allow["CLASSES"] = new HashSet<long>(classes);

            // VESSEL_DATA: CLASS_ID ∈ 선택 → 허용 VESSEL, 참조 COUNTRY
            var vessels = new HashSet<long>();
            var countries = new HashSet<long>();
            await foreach (var (pk, cls, ctry) in ReadTripleAsync(cn, "VESSEL_DATA", "VESSEL_ID", "CLASS_ID", "COUNTRY_ID", ct))
                if (classes.Contains(cls)) { vessels.Add(pk); if (ctry > 0) countries.Add(ctry); }
            allow["VESSEL_DATA"] = vessels;

            // 참조 능동소나: CLASSES.ACTIVE_SONAR1 / ACTIVE_SONAR2
            var sonars = new HashSet<long>();
            await foreach (var (pk, s1, s2) in ReadTripleAsync(cn, "CLASSES", "CLASS_ID", "ACTIVE_SONAR1", "ACTIVE_SONAR2", ct))
                if (classes.Contains(pk)) { if (s1 > 0) sonars.Add(s1); if (s2 > 0) sonars.Add(s2); }
            if (sonars.Count > 0) allow["ACTIVE_SONAR"] = sonars;
            if (countries.Count > 0) allow["COUNTRY_CODES"] = countries;

            // SIGNATURES: VESSEL_ID ∈ 허용
            var signatures = await ChildByFkAsync(cn, "SIGNATURES", "SIGNATURE_ID", "VESSEL_ID", vessels, ct);
            allow["SIGNATURES"] = signatures;

            // CHARACTERISTICS / IMAGE_DATA: VESSEL_ID ∈ 허용
            allow["CHARACTERISTICS"] = await ChildByFkAsync(cn, "CHARACTERISTICS", "CHAR_ID", "VESSEL_ID", vessels, ct);
            allow["IMAGE_DATA"] = await ChildByFkAsync(cn, "IMAGE_DATA", "IMAGE_ID", "VESSEL_ID", vessels, ct);

            // HARMONICS: SIGNATURES_ID ∈ 허용
            allow["HARMONICS"] = await ChildByFkAsync(cn, "HARMONICS", "HARM_ID", "SIGNATURES_ID", signatures, ct);

            // SIG_SNIPPET: SIGNATURE_ID ∈ 허용 → 허용 링크, 참조 NB_SNIPPET
            var sigSnips = new HashSet<long>();
            var nbSnips = new HashSet<long>();
            await foreach (var (pk, sig, nb) in ReadTripleAsync(cn, "SIG_SNIPPET", "SIG_SNP_ID", "SIGNATURE_ID", "NB_SNIPPET_ID", ct))
                if (signatures.Contains(sig)) { sigSnips.Add(pk); if (nb > 0) nbSnips.Add(nb); }
            allow["SIG_SNIPPET"] = sigSnips;
            if (nbSnips.Count > 0) allow["NB_SNIPPET"] = nbSnips;

            // TRANSIENT: CLASS_ID ∈ 선택 또는 VESSEL_ID ∈ 허용
            var transients = new HashSet<long>();
            await foreach (var (pk, cls, ves) in ReadTripleAsync(cn, "TRANSIENT", "TR_ID", "CLASS_ID", "VESSEL_ID", ct))
                if (classes.Contains(cls) || vessels.Contains(ves)) transients.Add(pk);
            allow["TRANSIENT"] = transients;

            // TR_FRAME: TR_ID ∈ 허용
            var trFrames = await ChildByFkAsync(cn, "TR_FRAME", "FRAME_ID", "TR_ID", transients, ct);
            allow["TR_FRAME"] = trFrames;

            // FRAME_SNIPPET: FRAME_ID ∈ 허용 → 허용 링크, 참조 TR_SNIPPET
            var frameSnips = new HashSet<long>();
            var trSnips = new HashSet<long>();
            await foreach (var (pk, frm, trs) in ReadTripleAsync(cn, "FRAME_SNIPPET", "FRM_SNP_ID", "FRAME_ID", "TR_SNIPPET_ID", ct))
                if (trFrames.Contains(frm)) { frameSnips.Add(pk); if (trs > 0) trSnips.Add(trs); }
            allow["FRAME_SNIPPET"] = frameSnips;
            if (trSnips.Count > 0) allow["TR_SNIPPET"] = trSnips;

            // WEAPON_CARRIER: CLASS_ID ∈ 선택 → 허용 링크, 참조 WEAPON
            var carriers = new HashSet<long>();
            var weapons = new HashSet<long>();
            await foreach (var (pk, cls, wpn) in ReadTripleAsync(cn, "WEAPON_CARRIER", "WCARRIER_ID", "CLASS_ID", "WEAPON_ID", ct))
                if (classes.Contains(cls)) { carriers.Add(pk); if (wpn > 0) weapons.Add(wpn); }
            allow["WEAPON_CARRIER"] = carriers;
            if (weapons.Count > 0) allow["WEAPON"] = weapons;

            return allow;
        }

        /// <summary>CLASS 목록(CLASS_ID + 표시명 후보)을 읽어 UI 선택지로 돌려준다.</summary>
        public async Task<List<(long ClassId, string Label)>> LoadClassListAsync(CancellationToken ct = default)
        {
            var list = new List<(long, string)>();
            await using var cn = DbProvider.CreateConnection(_kind, _mode, _connStr);
            await cn.OpenAsync(ct);
            // 이름은 암호화(MsSQL varbinary / Oracle RAW)라 여기서는 CLASS_ID + 평문 후보만.
            // CLASS_TYPE / SHIP_SUB 등 비암호화 힌트를 함께 보여 준다.
            string q = _kind == DbProviderKind.Oracle
                ? "SELECT CLASS_ID, CLASS_TYPE FROM CLASSES ORDER BY CLASS_ID"
                : "SELECT CLASS_ID, CLASS_TYPE FROM CLASSES ORDER BY CLASS_ID";
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = q;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long id = ToLong(r.GetValue(0));
                string typ = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1)) ?? "";
                list.Add((id, $"CLASS {id}" + (typ.Length > 0 ? $" ({typ})" : "")));
            }
            return list;
        }

        // ── 보조 ──────────────────────────────────────────────
        private async Task<HashSet<long>> ChildByFkAsync(
            DbConnection cn, string table, string pkCol, string fkCol, HashSet<long> allowedParents, CancellationToken ct)
        {
            var set = new HashSet<long>();
            if (allowedParents.Count == 0) return set;
            await foreach (var (pk, fk, _) in ReadTripleAsync(cn, table, pkCol, fkCol, null, ct))
                if (allowedParents.Contains(fk)) set.Add(pk);
            return set;
        }

        /// <summary>(pk, colA, colB) 를 읽는다. colB 가 null 이면 0 을 돌려준다.
        /// ※ 규모 한계: WHERE 없이 전 행을 읽어 클라이언트에서 거르는 구조다 —
        /// 현행 데이터(수천 행 급)에는 충분하지만, 수백만 행 급 테이블에는 서버측 필터(IN/조인)로
        /// 바꿔야 한다. 기본 30초 타임아웃으로는 대형 테이블 전 행 스캔이 끊길 수 있어 120초로 늘린다.</summary>
        private async IAsyncEnumerable<(long Pk, long A, long B)> ReadTripleAsync(
            DbConnection cn, string table, string pkCol, string colA, string? colB,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            string pk = DbProvider.QuoteColumn(_kind, pkCol);
            string a = DbProvider.QuoteColumn(_kind, colA);
            string sel = colB == null ? $"{pk}, {a}" : $"{pk}, {a}, {DbProvider.QuoteColumn(_kind, colB)}";
            string tq = DbProvider.QuoteIdent(_kind, table);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT {sel} FROM {tq}";
            cmd.CommandTimeout = 120;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long p = r.IsDBNull(0) ? 0 : ToLong(r.GetValue(0));
                long va = r.IsDBNull(1) ? 0 : ToLong(r.GetValue(1));
                long vb = (colB != null && !r.IsDBNull(2)) ? ToLong(r.GetValue(2)) : 0;
                yield return (p, va, vb);
            }
        }

        private static long ToLong(object? v)
        {
            if (v == null || v is DBNull) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
    }
}
