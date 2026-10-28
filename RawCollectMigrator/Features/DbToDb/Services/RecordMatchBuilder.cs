using System.Data;
using System.Globalization;

namespace RawCollectMigrator.Features.DbToDb
{
    // [찾기:레코드매칭]
    /// <summary>
    /// 레코드 단위 소스↔타겟 매칭표를 만든다.
    ///
    /// 왜 "정렬해서 맞춰보기"가 아니라 이 방식인가 —
    ///   이관기는 행을 넣을 때마다 (소스 PK → 타겟 RECORD_ID) 짝을 <see cref="IdRemapper"/> 에 적어 둔다.
    ///   그래서 <b>추정할 필요가 없다</b>. 키를 정렬해 맞추는 방식은 값이 같은 행이 여럿이거나
    ///   (레거시에 흔하다) 정렬 기준이 NULL 이면 조용히 어긋나는데, 여기서는 그럴 여지가 없다.
    ///   정렬은 "짝을 찾는 수단"이 아니라 "이미 확정된 짝을 사람이 보기 좋게 늘어놓는 수단"이다.
    ///
    /// 그래서 이 표가 검증하는 것은 "누가 누구로 갔나"(짝)가 아니라 <b>"가서 값이 보존됐나"</b> 다:
    ///   · 핵심 필드(테이블의 dedup 키 = 그 행을 식별하는 값)가 좌우 같은가
    ///   · FK 가 부모의 신규 RECORD_ID 로 제대로 치환됐는가
    ///
    /// 짝이 기록되지 않은 행(부모단위 스킵·링크 재사용)은 애초에 여기 나오지 않는다 — 없는 것을
    /// 있는 것처럼 보이지 않게 하려는 것이며, 건수는 요약에 그대로 드러낸다.
    /// </summary>
    public static class RecordMatchBuilder
    {
        /// <summary>판정값 — DataGrid 행 색상 트리거와 필터에 함께 쓴다.</summary>
        public const string VerdictOk = "일치";
        public const string VerdictBad = "불일치";
        public const string VerdictUnknown = "확인불가";

        public const string ColVerdict = "판정";
        public const string ColKind = "구분";

        // [찾기:불일치셀]
        /// <summary>
        /// 그 행에서 <b>어긋난 셀이 어느 열인지</b> 적어 두는 숨은 컬럼.
        /// 값은 <c>|열이름|열이름|</c> 꼴이라 <c>|헤더|</c> 로 그냥 찾으면 된다.
        ///
        /// 왜 표 안에 넣나 — 화면(셀 색)과 판정(차이 계산)이 <b>같은 한 번의 비교</b>에서 나와야
        /// 한다. 화면이 따로 다시 비교하면 규칙이 갈라져(공백패딩·부동소수 오차 등) "빨간데 차이는
        /// 비어 있음" 같은 모순이 생기고, 그 순간 사람은 색을 믿지 않게 된다.
        /// TSV 로 내보낼 때는 빼고 저장한다 — 사람이 읽을 열이 아니다.
        /// </summary>
        public const string ColDiffCells = "__불일치셀";

        // [찾기:레코드매칭] [NAIMS-ISSUE:UNREADABLE-NOT-MISMATCH]
        /// <summary>
        /// 소스에서 <b>읽지 못한</b> 컬럼을 골라낸다.
        ///
        /// 왜 따로 다루나 — 암호화 컬럼(VESSEL_NAME 등)은 복호화 컨텍스트가 없으면 조회에서
        /// 통째로 빠진다(암호문을 그대로 내보이지 않으려는 설계다). 그러면 소스 값이 빈 칸이 되고,
        /// 그대로 비교하면 <b>모든 행이 "값이 다르다"</b> 로 찍힌다 — 실측 2026-08-19:
        /// VESSEL_DATA 19건 전량이 그렇게 불일치로 나왔다. 실제로는 이관이 멀쩡했고,
        /// 대조하는 쪽이 값을 못 읽었을 뿐이다.
        ///
        /// <b>못 읽은 것과 다른 것은 다르다.</b> 섞어 세면 도구가 거짓 경보를 내고,
        /// 거짓 경보를 몇 번 보면 사람은 진짜 경보도 넘기게 된다.
        /// 그래서 읽히지 않은 컬럼은 비교에서 빼고, 뺐다는 사실을 요약에 적는다.
        /// </summary>
        private static List<string> Unreadable(
            DataTable srcDt, IEnumerable<string> targetCols, Dictionary<string, string> tgtToSrc) =>
            targetCols.Where(k => tgtToSrc.TryGetValue(k, out var sc) && !srcDt.Columns.Contains(sc))
                      .ToList();

        /// <summary>왜 못 읽었는지 사람이 조치할 수 있게 적는다.</summary>
        private static string UnreadableNote(
            IReadOnlyList<string> cols, Dictionary<string, string> tgtToSrc,
            DbProviderKind kind, string srcTable, bool hasCrypto)
        {
            if (cols.Count == 0) return "";
            bool enc = cols.Any(k => tgtToSrc.TryGetValue(k, out var sc)
                                     && CryptoContext.IsEncrypted(kind, srcTable, sc));
            string names = string.Join(", ", cols.Take(4));
            if (cols.Count > 4) names += $" 외 {cols.Count - 4}";
            return enc && !hasCrypto
                ? $"   ⚠ 암호화 컬럼({names})은 복호화 컨텍스트가 없어 <대조에서 제외>했습니다 — " +
                  "작업 화면의 [복호화 불러오기] 를 켜고 다시 대조하세요. 불일치가 아닙니다."
                : $"   ⚠ 소스에서 읽지 못한 컬럼({names})은 대조에서 제외했습니다 — 불일치가 아닙니다.";
        }

        /// <summary>어긋난 열 목록을 <see cref="ColDiffCells"/> 표기로 만든다.</summary>
        private static string Mark(IReadOnlyCollection<string> cols) =>
            cols.Count == 0 ? "" : "|" + string.Join("|", cols) + "|";

        public sealed class Result
        {
            public DataTable Table { get; init; } = new();
            public string Summary { get; init; } = "";
            public int Matched { get; init; }
            public int Mismatched { get; init; }
            public int Unknown { get; init; }
            /// <summary>비교에 실제로 쓴 핵심 필드(타겟 컬럼명).</summary>
            public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 한 테이블의 레코드 매칭표를 만든다(소스·타겟 DB 읽기 전용).
        /// </summary>
        /// <param name="r">이관 결과(짝 목록 IdPairs 포함).</param>
        /// <param name="plan">그 테이블의 이관 계획 — 핵심 필드(dedup 키)와 FK 정의를 여기서 얻는다.</param>
        /// <param name="opt">접속 정보 재사용용 실행 옵션.</param>
        /// <param name="translate">
        /// (부모 소스테이블, 소스 PK) → 타겟 RECORD_ID. FK 치환이 맞는지 대조하는 데 쓴다.
        /// 이번 실행의 전체 리포트에서 만들어 넘긴다.
        /// </param>
        /// <param name="crypto">소스 이름컬럼 복호화 컨텍스트(없으면 암호화 컬럼은 비교에서 빠진다).</param>
        /// <param name="limit">표시 상한(대용량 테이블 보호).</param>
        public static async Task<Result> BuildAsync(
            TableResult r, TablePlan? plan, DbToDbOptions opt,
            Func<string, long, long?> translate, CryptoContext? crypto,
            int limit, CancellationToken ct = default)
        {
            var pairs = r.IdPairs.Take(limit).ToList();
            if (pairs.Count == 0 || string.IsNullOrEmpty(r.SourcePk))
            {
                return new Result
                {
                    Summary = "이번 실행에서 (소스 PK → 타겟 RECORD_ID) 짝이 기록된 행이 없습니다. " +
                              "부모단위 스킵·링크 재사용 행은 타겟 RECORD_ID 를 몰라 짝을 남기지 않습니다.",
                };
            }

            var srcCols = await TableInspector.GetColumnsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, r.Source, ct);
            var tgtCols = await TableInspector.GetColumnsAsync(
                DbProviderKind.MsSql, DbConnectionMode.Direct, opt.TargetConnStr, r.Target, ct);
            if (srcCols.Count == 0 || tgtCols.Count == 0)
                return new Result { Summary = "소스 또는 타겟 스키마를 읽지 못해 매칭표를 만들 수 없습니다." };

            var maps = ColumnMapper.Build(srcCols, tgtCols, r.Source);

            // 타겟 컬럼명 → 소스 컬럼명 (실제 INSERT 에 쓰인 것과 같은 대응)
            var tgtToSrc = maps
                .Where(m => m.Status is MappingStatus.Exact or MappingStatus.Renamed)
                .Where(m => m.SourceName.Length > 0 && m.TargetName.Length > 0)
                .GroupBy(m => m.TargetName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SourceName, StringComparer.OrdinalIgnoreCase);

            var srcHas = srcCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tgtHas = tgtCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // [찾기:레코드매칭] 대조 컬럼은 <핵심 필드만> 이 아니라 대응이 확정된 것 전부다.
            //   예전에는 dedup 키 3개까지만 봤다. 그러면 "키는 맞는데 안의 값이 달라진 행" 을
            //   영영 못 잡는다 — 이관 검증에서 정작 찾아야 하는 것이 그건데.
            //   'DB 간 데이터 비교' 와 같은 규칙(PickCompareColumns)을 쓴다: 두 방식이 서로 다른 범위를
            //   보면, 한쪽에서 통과한 행이 다른 쪽에서 걸리는 이유를 설명할 수 없게 된다.
            var keyCols = PickKeyColumns(plan, maps, tgtToSrc, srcHas, tgtHas);
            var cmpCols = PickCompareColumns(
                maps, tgtToSrc, srcHas, tgtHas, keyCols, srcCols, tgtCols, r.SourcePk,
                opt.SourceKind, r.Source, crypto != null);

            var fks = (plan?.ForeignKeys ?? Array.Empty<ForeignKeyLink>())
                .Where(f => srcHas.Contains(f.SourceColumn) && tgtHas.Contains(f.TargetColumn))
                .ToList();

            // ── 양쪽에서 필요한 컬럼만 읽는다 ──
            var srcNeed = new List<string> { r.SourcePk };
            srcNeed.AddRange(fks.Select(f => f.SourceColumn));
            srcNeed.AddRange(cmpCols.Select(k => tgtToSrc[k]));

            var tgtNeed = new List<string> { "RECORD_ID" };
            tgtNeed.AddRange(fks.Select(f => f.TargetColumn));
            tgtNeed.AddRange(cmpCols);

            var srcDt = await TableInspector.GetRowsByIdsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, r.Source,
                Pick(srcCols, srcNeed), r.SourcePk, pairs.Select(p => p.Src).ToList(), crypto, ct);
            var tgtDt = await TableInspector.GetRowsByIdsAsync(
                DbProviderKind.MsSql, DbConnectionMode.Direct, opt.TargetConnStr, r.Target,
                Pick(tgtCols, tgtNeed), "RECORD_ID", pairs.Select(p => p.Tgt).ToList(), null, ct);

            var srcById = IndexBy(srcDt, r.SourcePk);
            var tgtById = IndexBy(tgtDt, "RECORD_ID");

            // [NAIMS-ISSUE:UNREADABLE-NOT-MISMATCH] 소스에서 못 읽은 컬럼은 비교하지 않는다.
            var unread = Unreadable(srcDt, cmpCols, tgtToSrc);
            if (unread.Count > 0)
                cmpCols = cmpCols.Where(k => !unread.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            string unreadNote = UnreadableNote(unread, tgtToSrc, opt.SourceKind, r.Source, crypto != null);

            // ── 표 만들기 ──
            var dt = new DataTable();
            dt.Columns.Add(ColVerdict, typeof(string));
            dt.Columns.Add(ColKind, typeof(string));
            dt.Columns.Add($"소스 {r.SourcePk}", typeof(long));
            dt.Columns.Add("타겟 RECORD_ID", typeof(long));
            foreach (var f in fks)
            {
                dt.Columns.Add($"FK {f.SourceColumn}(소스)", typeof(string));
                dt.Columns.Add($"FK {f.TargetColumn}(타겟)", typeof(string));
            }
            foreach (var k in cmpCols)
            {
                dt.Columns.Add($"{tgtToSrc[k]}(소스)", typeof(string));
                dt.Columns.Add($"{k}(타겟)", typeof(string));
            }
            dt.Columns.Add("차이", typeof(string));
            dt.Columns.Add(ColDiffCells, typeof(string));   // [찾기:불일치셀] 화면용 숨은 표시

            int ok = 0, bad = 0, unk = 0;
            foreach (var (src, tgt) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                var row = dt.NewRow();
                var badCells = new List<string>();
                row[ColKind] = r.ReusedSourcePks.Contains(src) ? "재사용" : "신규";
                row[$"소스 {r.SourcePk}"] = src;
                row["타겟 RECORD_ID"] = tgt;

                srcById.TryGetValue(src, out var sRow);
                tgtById.TryGetValue(tgt, out var tRow);

                var diffs = new List<string>();
                bool unknown = sRow == null || tRow == null;
                if (sRow == null) diffs.Add("소스 행 없음(삭제됨?)");
                if (tRow == null) diffs.Add("타겟 행 없음");

                foreach (var f in fks)
                {
                    var sv = Val(sRow, f.SourceColumn);
                    var tv = Val(tRow, f.TargetColumn);
                    row[$"FK {f.SourceColumn}(소스)"] = Show(sv);
                    row[$"FK {f.TargetColumn}(타겟)"] = Show(tv);
                    if (unknown) continue;

                    // FK 는 값이 같아야 하는 게 아니라 "부모의 신규 RECORD_ID 로 바뀌어야" 맞다.
                    // [NAIMS-ISSUE:FK-ZERO-IS-NONE] 레거시 FK 0 은 <참조 없음> 이다(PULSES.*_SEG_ID 등).
                    //   부모 짝이 없는 게 당연하므로 '확인불가' 로 둘 일이 아니다 — 확인불가가 쌓이면
                    //   정말 확인해야 할 행이 그 속에 묻힌다. 타겟도 비었으면 그게 정답이다.
                    if (IsNone(sv))
                    {
                        if (!IsNone(tv) && !f.Optional)
                            diffs.Add($"{f.TargetColumn}: 소스 FK 가 '참조 없음'(0/NULL)인데 타겟에 값 있음");
                        continue;
                    }
                    if (!TryLong(sv, out var sFk)) { unknown = true; diffs.Add($"{f.SourceColumn}: FK 값 해석 불가"); continue; }
                    var expect = translate(f.ParentSourceTable, sFk);
                    if (expect == null) { unknown = true; diffs.Add($"{f.TargetColumn}: 부모 짝 기록 없음 — 대조 불가"); continue; }
                    if (!TryLong(tv, out var tFk) || tFk != expect.Value)
                    {
                        diffs.Add($"{f.TargetColumn}: 기대 {expect.Value} ≠ 실제 {Show(tv)}");
                        badCells.Add($"FK {f.SourceColumn}(소스)");
                        badCells.Add($"FK {f.TargetColumn}(타겟)");
                    }
                }

                foreach (var k in cmpCols)
                {
                    var sv = Val(sRow, tgtToSrc[k]);
                    var tv = Val(tRow, k);
                    row[$"{tgtToSrc[k]}(소스)"] = Show(sv);
                    row[$"{k}(타겟)"] = Show(tv);
                    if (unknown) continue;
                    if (!MeansSame(sv, tv, tgtToSrc[k], k))
                    {
                        diffs.Add($"{k}: '{Show(sv)}' → '{Show(tv)}'");
                        badCells.Add($"{tgtToSrc[k]}(소스)");
                        badCells.Add($"{k}(타겟)");
                    }
                }

                string verdict = unknown ? VerdictUnknown : diffs.Count == 0 ? VerdictOk : VerdictBad;
                if (verdict == VerdictOk) ok++;
                else if (verdict == VerdictBad) bad++;
                else unk++;

                row[ColVerdict] = verdict;
                row["차이"] = string.Join(" | ", diffs);
                row[ColDiffCells] = Mark(badCells);
                dt.Rows.Add(row);
            }

            string keyText = cmpCols.Count > 0
                ? $"대조 컬럼 {cmpCols.Count:N0}개" + (cmpCols.Count >= CompareColumnCap ? $"(상한 {CompareColumnCap})" : "")
                : "⚠ 대조할 컬럼을 찾지 못했습니다 — PK·FK 만 대조했습니다";
            string more = r.IdPairs.Count > pairs.Count
                ? $"   |   짝 {r.IdPairs.Count:N0}건 중 {pairs.Count:N0}건 표시(상한 {limit:N0})"
                : "";

            return new Result
            {
                Table = dt,
                Matched = ok,
                Mismatched = bad,
                Unknown = unk,
                KeyColumns = cmpCols,
                Summary = $"일치 {ok:N0} · 불일치 {bad:N0} · 확인불가 {unk:N0}   |   {keyText}{more}{unreadNote}",
            };
        }

        public const string VerdictSrcOnly = "소스에만";
        public const string VerdictTgtOnly = "타겟에만";
        public const string VerdictAmbiguous = "키중복";

        // [찾기:레코드매칭]
        /// <summary>
        /// "DB 간 데이터 비교" 모드 — 이관 세션 기록 없이 <b>자연키(파일명 등)만으로</b>
        /// 소스↔타겟 레코드를 맞춘다.
        ///
        /// 언제 쓰나 — 예전에 이관해 둔 DB 를 나중에 검증할 때. 그때는 (소스 PK → 타겟 RECORD_ID)
        /// 짝이 남아 있지 않으므로 값으로 맞추는 수밖에 없다.
        ///
        /// 왜 "정렬해서 나란히 놓기"가 아니라 키 사전인가 —
        ///   좌우를 각각 정렬해 같은 인덱스끼리 짝지으면, 한쪽에 스킵된 행이 하나만 있어도
        ///   그 아래가 전부 한 칸씩 밀린 채 "전부 불일치"로 보인다(실측: TR_SNIPPET 4건 전량 스킵).
        ///   키로 사전을 만들면 빠진 행은 "소스에만" 한 줄로 정확히 드러나고 나머지는 멀쩡히 맞는다.
        ///   정렬은 사람이 보기 위한 것이지 짝을 찾는 수단이 아니다.
        ///
        /// 한계는 숨기지 않는다 — 같은 키가 여러 행에 있으면 그 행들은 <see cref="VerdictAmbiguous"/>
        /// 로 남기고 짝짓지 않는다. 레거시 파일명은 실제로 여러 행이 공유하는 경우가 있다.
        /// </summary>
        public static async Task<Result> BuildByKeyAsync(
            TableResult r, TablePlan? plan, DbToDbOptions opt,
            CryptoContext? crypto, int limit, CancellationToken ct = default)
        {
            var srcCols = await TableInspector.GetColumnsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, r.Source, ct);
            var tgtCols = await TableInspector.GetColumnsAsync(
                DbProviderKind.MsSql, DbConnectionMode.Direct, opt.TargetConnStr, r.Target, ct);
            if (srcCols.Count == 0 || tgtCols.Count == 0)
                return new Result { Summary = "소스 또는 타겟 스키마를 읽지 못했습니다." };

            var maps = ColumnMapper.Build(srcCols, tgtCols, r.Source);
            var tgtToSrc = maps
                .Where(m => m.Status is MappingStatus.Exact or MappingStatus.Renamed)
                .Where(m => m.SourceName.Length > 0 && m.TargetName.Length > 0)
                .GroupBy(m => m.TargetName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SourceName, StringComparer.OrdinalIgnoreCase);

            var srcHas = srcCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tgtHas = tgtCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keyCols = PickKeyColumns(plan, maps, tgtToSrc, srcHas, tgtHas);
            if (keyCols.Count == 0)
                return new Result
                {
                    Summary = "이 테이블에는 좌우를 맞출 자연키(파일명·명칭 등)가 없습니다. " +
                              "'이번 실행 기록' 방식으로 보세요.",
                };

            // [찾기:불일치셀] 자연키로 짝을 찾은 뒤, 짝지어진 두 행의 <b>값이 실제로 같은가</b> 를 넓게 본다.
            //   키 컬럼만 비교하면 키로 맞춘 이상 언제나 같아서 불일치가 영영 나오지 않는다 —
            //   그건 검증이 아니라 자기 확인이다. 사후 검증에서 알고 싶은 것은 정확히
            //   "키는 맞는데 안의 값이 달라진 행" 이므로, 대응이 확정된 컬럼을 모두 대조한다.
            var cmpCols = PickCompareColumns(
                maps, tgtToSrc, srcHas, tgtHas, keyCols, srcCols, tgtCols, r.SourcePk,
                opt.SourceKind, r.Source, crypto != null);
            bool capped = cmpCols.Count >= CompareColumnCap;

            var srcNeed = new List<string> { r.SourcePk };
            srcNeed.AddRange(cmpCols.Select(k => tgtToSrc[k]));
            var tgtNeed = new List<string> { "RECORD_ID" };
            tgtNeed.AddRange(cmpCols);

            var srcDt = await TableInspector.GetTopRowsAsync(
                opt.SourceKind, opt.SourceMode, opt.SourceConnStr, r.Source,
                Pick(srcCols, srcNeed), limit, crypto, ct);
            var tgtDt = await TableInspector.GetTopRowsAsync(
                DbProviderKind.MsSql, DbConnectionMode.Direct, opt.TargetConnStr, r.Target,
                Pick(tgtCols, tgtNeed), limit, null, ct);

            // [NAIMS-ISSUE:UNREADABLE-NOT-MISMATCH]
            //   자연키를 못 읽으면 짝을 못 찾아 <전부 '소스에만'> 이 된다. 그건 대조 결과가 아니라
            //   대조를 못 했다는 뜻이므로, 결과처럼 보여 주지 않고 그 사실만 돌려준다.
            var unreadKey = Unreadable(srcDt, keyCols, tgtToSrc);
            if (unreadKey.Count > 0)
                return new Result
                {
                    Summary = "자연키를 소스에서 읽지 못해 대조할 수 없습니다." +
                              UnreadableNote(unreadKey, tgtToSrc, opt.SourceKind, r.Source, crypto != null),
                };

            var unreadCmp = Unreadable(srcDt, cmpCols, tgtToSrc);
            if (unreadCmp.Count > 0)
                cmpCols = cmpCols.Where(k => !unreadCmp.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            string unreadNote = UnreadableNote(unreadCmp, tgtToSrc, opt.SourceKind, r.Source, crypto != null);

            // 키 → 행. 같은 키가 둘 이상이면 짝짓지 않고 '키중복' 으로 남긴다.
            var tgtByKey = new Dictionary<string, DataRow>(StringComparer.Ordinal);
            var tgtDup = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataRow row in tgtDt.Rows)
            {
                var k = KeyOf(row, keyCols);
                if (k.Length == 0) continue;
                if (!tgtByKey.TryAdd(k, row)) tgtDup.Add(k);
            }

            var dt = new DataTable();
            dt.Columns.Add(ColVerdict, typeof(string));
            dt.Columns.Add("자연키", typeof(string));
            dt.Columns.Add($"소스 {r.SourcePk}", typeof(string));
            dt.Columns.Add("타겟 RECORD_ID", typeof(string));
            foreach (var k in cmpCols)
            {
                dt.Columns.Add($"{tgtToSrc[k]}(소스)", typeof(string));
                dt.Columns.Add($"{k}(타겟)", typeof(string));
            }
            dt.Columns.Add("차이", typeof(string));
            dt.Columns.Add(ColDiffCells, typeof(string));   // [찾기:불일치셀]

            int ok = 0, bad = 0, unk = 0, onlyOne = 0;
            var usedTgt = new HashSet<string>(StringComparer.Ordinal);

            foreach (DataRow sRow in srcDt.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var key = KeyOf(sRow, keyCols.Select(k => tgtToSrc[k]).ToList());
                var row = dt.NewRow();
                row["자연키"] = key;
                row[$"소스 {r.SourcePk}"] = Show(Val(sRow, r.SourcePk));
                foreach (var k in cmpCols)
                    row[$"{tgtToSrc[k]}(소스)"] = Show(Val(sRow, tgtToSrc[k]));

                if (key.Length == 0 || tgtDup.Contains(key))
                {
                    row[ColVerdict] = key.Length == 0 ? VerdictUnknown : VerdictAmbiguous;
                    row["차이"] = key.Length == 0
                        ? "자연키가 비어 있어 짝을 찾을 수 없습니다"
                        : "타겟에 같은 자연키가 여럿이라 짝짓지 않았습니다";
                    unk++;
                }
                else if (tgtByKey.TryGetValue(key, out var tRow))
                {
                    usedTgt.Add(key);
                    row["타겟 RECORD_ID"] = Show(Val(tRow, "RECORD_ID"));

                    var diffs = new List<string>();
                    var badCells = new List<string>();
                    foreach (var k in cmpCols)
                    {
                        var sv = Val(sRow, tgtToSrc[k]);
                        var tv = Val(tRow, k);
                        row[$"{k}(타겟)"] = Show(tv);
                        if (MeansSame(sv, tv, tgtToSrc[k], k)) continue;
                        diffs.Add($"{k}: '{Show(sv)}' → '{Show(tv)}'");
                        badCells.Add($"{tgtToSrc[k]}(소스)");
                        badCells.Add($"{k}(타겟)");
                    }
                    row["차이"] = string.Join(" | ", diffs);
                    row[ColDiffCells] = Mark(badCells);
                    if (diffs.Count == 0) { row[ColVerdict] = VerdictOk; ok++; }
                    else { row[ColVerdict] = VerdictBad; bad++; }
                }
                else
                {
                    row[ColVerdict] = VerdictSrcOnly;
                    row["차이"] = "타겟에 같은 자연키의 행이 없습니다";
                    onlyOne++;
                }

                dt.Rows.Add(row);
            }

            // 타겟에만 있는 행도 반드시 드러낸다 — 안 보이면 "과잉 적재"를 영영 모른다.
            foreach (var (k, tRow) in tgtByKey)
            {
                if (usedTgt.Contains(k)) continue;
                var row = dt.NewRow();
                row[ColVerdict] = VerdictTgtOnly;
                row["자연키"] = k;
                row["타겟 RECORD_ID"] = Show(Val(tRow, "RECORD_ID"));
                foreach (var kc in cmpCols) row[$"{kc}(타겟)"] = Show(Val(tRow, kc));
                row["차이"] = "소스에 같은 자연키의 행이 없습니다";
                dt.Rows.Add(row);
                onlyOne++;
            }

            return new Result
            {
                Table = dt,
                Matched = ok,
                Mismatched = bad + onlyOne,
                Unknown = unk,
                KeyColumns = keyCols,
                Summary = $"값까지 일치 {ok:N0} · 값 불일치 {bad:N0} · 한쪽에만 {onlyOne:N0} · 키중복/키없음 {unk:N0}" +
                          $"   |   자연키 {string.Join(" + ", keyCols)}" +
                          $"   |   대조 컬럼 {cmpCols.Count:N0}개" + (capped ? $"(상한 {CompareColumnCap})" : "") +
                          $"   |   각 {limit:N0}행 상한으로 읽었습니다" +
                          (tgtDup.Count > 0 ? $"   ⚠ 타겟에 중복 키 {tgtDup.Count}종" : "") + unreadNote,
            };
        }

        /// <summary>사후 비교에서 한 번에 다룰 대조 컬럼 수 상한 — 표가 가로로 무한히 길어지면 읽힐 수 없다.</summary>
        private const int CompareColumnCap = 40;

        // [찾기:불일치셀]
        /// <summary>
        /// 사후 비교에서 <b>값을 대조할</b> 컬럼(타겟 이름)을 고른다. 자연키가 맨 앞에 온다.
        ///
        /// 일부러 뺀 것들 —
        ///   · RECORD_ID / 소스 PK : 신규 DB 가 새로 매기는 번호라 다른 것이 정상이다.
        ///   · REF_* (FK)          : 부모의 신규 번호로 치환되는 게 정상이라 값이 같으면 오히려 이상하다.
        ///                           (FK 치환이 맞는지는 '이번 실행 기록' 방식에서 따로 대조한다.)
        ///   · BLOB/RAW 등 대용량   : 표에 늘어놓을 수 없다. 그램·영상은 [미리보기] 로 눈으로 본다.
        ///     다만 암호화된 이름 컬럼(RAW)은 복호화되어 들어오므로, 복호 컨텍스트가 있을 때만 남긴다.
        /// </summary>
        private static List<string> PickCompareColumns(
            List<ColumnMapping> maps, Dictionary<string, string> tgtToSrc,
            IReadOnlySet<string> srcHas, IReadOnlySet<string> tgtHas,
            IReadOnlyList<string> keyCols,
            IReadOnlyList<ColumnInfo> srcCols, IReadOnlyList<ColumnInfo> tgtCols,
            string sourcePk, DbProviderKind srcKind, string srcTable, bool canDecrypt)
        {
            var srcType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in srcCols) srcType[c.Name] = c.DataType;
            var tgtType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in tgtCols) tgtType[c.Name] = c.DataType;

            var picked = new List<string>(keyCols);
            var seen = new HashSet<string>(keyCols, StringComparer.OrdinalIgnoreCase);

            foreach (var m in maps)
            {
                if (picked.Count >= CompareColumnCap) break;
                if (m.Status is not (MappingStatus.Exact or MappingStatus.Renamed)) continue;

                string t = m.TargetName, sc = m.SourceName;
                if (t.Length == 0 || sc.Length == 0) continue;
                if (!seen.Add(t)) continue;
                if (!tgtHas.Contains(t) || !srcHas.Contains(sc)) continue;
                // 실제 INSERT 에 쓴 대응과 같은 것만 대조한다(별칭이 1:다 일 수 있다).
                if (!tgtToSrc.TryGetValue(t, out var mapped)
                    || !mapped.Equals(sc, StringComparison.OrdinalIgnoreCase)) continue;

                if (t.Equals("RECORD_ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (sc.Equals(sourcePk, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.StartsWith("REF_", StringComparison.OrdinalIgnoreCase)) continue;

                bool bulky = IsBulky(srcType.GetValueOrDefault(sc, "")) || IsBulky(tgtType.GetValueOrDefault(t, ""));
                if (bulky && !(canDecrypt && CryptoContext.IsEncrypted(srcKind, srcTable, sc))) continue;

                picked.Add(t);
            }
            return picked;
        }

        /// <summary>표에 늘어놓을 수 없고 문자열로 비교해도 의미 없는 큰 자료형인가.</summary>
        private static bool IsBulky(string dataType)
        {
            var t = (dataType ?? "").ToUpperInvariant();
            return t.Contains("BLOB") || t.Contains("CLOB") || t.Contains("RAW")
                || t.Contains("BINARY") || t.Contains("IMAGE") || t.Contains("LONG")
                || t.Contains("TEXT") || t.Contains("XML");
        }

        /// <summary>여러 키 컬럼을 하나의 대조 문자열로 정규화한다(공백패딩·대소문자 무시).</summary>
        private static string KeyOf(DataRow row, IReadOnlyList<string> cols)
        {
            var parts = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                var v = Val(row, c);
                if (v is null or DBNull) return "";        // 키가 비면 대조 불가 — 빈 키끼리 뭉치지 않게
                var t = Show(v);
                if (t.Length == 0) return "";
                parts.Add(t.ToUpperInvariant());
            }
            return string.Join("", parts);
        }

        /// <summary>
        /// 비교에 쓸 핵심 필드를 고른다. dedup 키가 곧 "그 행을 식별하는 값"이라 1순위다.
        /// 아무것도 없으면 이름·명칭류 텍스트 컬럼을 최대 3개까지 추정해 쓴다(없으면 빈 목록).
        /// </summary>
        private static List<string> PickKeyColumns(
            TablePlan? plan, List<ColumnMapping> maps, Dictionary<string, string> tgtToSrc,
            IReadOnlySet<string> srcHas, IReadOnlySet<string> tgtHas)
        {
            bool Usable(string tgtCol) =>
                tgtHas.Contains(tgtCol)
                && tgtToSrc.TryGetValue(tgtCol, out var sc)
                && srcHas.Contains(sc);

            var picked = new List<string>();
            void TryAdd(string? c)
            {
                if (string.IsNullOrWhiteSpace(c)) return;
                if (picked.Contains(c, StringComparer.OrdinalIgnoreCase)) return;
                if (Usable(c)) picked.Add(c);
            }

            foreach (var k in plan?.DedupKeyColumns ?? Array.Empty<string>()) TryAdd(k);
            TryAdd(plan?.TargetKeyColumn);
            if (picked.Count > 0) return picked.Take(3).ToList();

            // 추정: 이름/명칭류를 우선하고, 없으면 대응이 확정된 비-FK 컬럼 앞에서부터.
            foreach (var m in maps.Where(m => m.Status is MappingStatus.Exact or MappingStatus.Renamed))
            {
                var t = m.TargetName;
                if (t.StartsWith("REF_", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Equals("RECORD_ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Contains("NAME", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("CODE", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("_NO", StringComparison.OrdinalIgnoreCase))
                    TryAdd(t);
                if (picked.Count >= 3) break;
            }
            return picked;
        }

        // ── 값 비교 ──────────────────────────────────────────────────────
        // 레거시는 CHAR 고정폭 공백패딩·대소문자 혼용이 흔하다. 그것까지 "불일치"로 세면
        // 표가 온통 빨개져서 진짜 불일치가 묻힌다. 의미가 같으면 같은 것으로 본다.
        // 실수는 float(7자리) 왕복 오차가 있어 상대오차로 본다.

        // [찾기:의미비교] [NAIMS-ISSUE:COMPARE-SEMANTICS]
        /// <summary>
        /// 소스 값이 <b>이관 규칙을 거치면</b> 타겟 값이 되는지 본다.
        ///
        /// 원값끼리 맞대면 안 된다 — 이관은 값을 <b>일부러 바꾼다</b>. 바뀐 것을 "다르다" 고 세면
        /// 정상 이관이 전부 빨갛게 찍히고, 그 속에서 진짜 불일치는 보이지 않는다.
        /// 실측(2026-08-20): COMMENT 말머리와 시각 단위 환산 때문에 멀쩡한 행이 불일치로 나왔다.
        ///
        /// 반영하는 규칙(실행기와 같은 것):
        ///   · COMMENT 계열   : 말머리 <c>[NAIMS-I]</c> 를 붙여 넣는다 → 붙인 것과 비교
        ///   · 시각 단위      : A_START_TIME(자정 이후 <b>초</b>) → *_MILLISEC ×1,000 / *_MICROSEC ×1,000,000
        ///                      "숫자가 같나" 가 아니라 "<b>같은 시각을 가리키나</b>" 를 본다
        ///   · 거리 단위      : DISTANCE_KM → *_NM  (÷1.852)
        ///   · 값 없음 센티넬 : 레거시 0 / int.MaxValue 는 "없음" 이다 → 타겟 NULL·0 과 같은 뜻
        /// </summary>
        /// <param name="srcCol">소스 컬럼명(규칙 판정용).</param>
        /// <param name="tgtCol">타겟 컬럼명(규칙 판정용) — 단위는 이름에 적혀 있다.</param>
        private static bool MeansSame(object? a, object? b, string srcCol, string tgtCol)
        {
            // [NAIMS-ISSUE:COMMENT-EMPTY] COMMENT 는 '없음' 판정보다 <먼저> 본다.
            //   이관은 소스가 비어 있어도 말머리를 붙인다(ApplyCommentPrefix: 빈 값 → "[NAIMS-I]").
            //   '없음' 을 먼저 걸러 버리면 빈 소스 vs "[NAIMS-I]" 타겟이 <불일치>로 찍힌다 —
            //   실측 2026-08-20: 멀쩡히 이관된 행이 그 이유로 빨갛게 나왔다.
            //   둘 다 비었을 때만 같다고 보고, 그 밖에는 말머리를 붙여서 맞대 본다.
            if (MigrationRules.IsCommentColumn(tgtCol))
            {
                if (IsNone(a) && IsNone(b)) return true;
                var expect = MigrationRules.ApplyCommentPrefix(Convert.ToString(a) ?? "") ?? "";
                return string.Equals(expect.Trim(), (Convert.ToString(b) ?? "").Trim(),
                                     StringComparison.OrdinalIgnoreCase);
            }

            // 값 없음끼리는 같다. 레거시의 0·int.MaxValue 도 "없음" 이다.
            bool na = IsNone(a), nb = IsNone(b);
            if (na || nb) return na && nb;

            if (UnitScale(srcCol, tgtCol) is { } scale && IsNum(a) && IsNum(b))
                return ValueEquals(Convert.ToDouble(a, CultureInfo.InvariantCulture) * scale, b);

            return ValueEquals(a, b);
        }

        /// <summary>레거시의 "값 없음" 표현들. 0 은 참조·시각 어느 쪽에서도 '없음' 이다.</summary>
        private static bool IsNone(object? v)
        {
            if (v is null or DBNull) return true;
            if (v is string s0) return s0.Trim().Length == 0;
            if (!IsNum(v)) return false;
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return d == 0 || d == int.MaxValue;
        }

        /// <summary>소스→타겟 단위 환산 계수. 없으면 null. 계수는 <b>타겟 컬럼 이름</b>이 정한다.</summary>
        private static double? UnitScale(string srcCol, string tgtCol)
        {
            var t = tgtCol.ToUpperInvariant();
            var s = srcCol.ToUpperInvariant();

            // 자정 이후 초 → µs / ms. µs 를 먼저 본다(MICROSEC 안에 SEC 가 들어 있다).
            if (s.EndsWith("_TIME", StringComparison.Ordinal))
            {
                if (t.Contains("MICROSEC", StringComparison.Ordinal)) return 1_000_000d;
                if (t.Contains("MILLISEC", StringComparison.Ordinal)) return 1_000d;
            }
            // km → 해리
            if (s.Contains("DISTANCE_KM", StringComparison.Ordinal) && t.Contains("_NM", StringComparison.Ordinal))
                return 1d / 1.852d;
            return null;
        }

        /// <summary>소스 값과 타겟 값이 "의미상 같은지" 본다(단위·말머리는 위에서 이미 맞춰 온다).</summary>
        private static bool ValueEquals(object? a, object? b)
        {
            bool na = a is null or DBNull, nb = b is null or DBNull;
            if (na || nb) return na && nb;

            if (IsNum(a) && IsNum(b))
            {
                double da = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                double db = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                if (da == db) return true;
                double scale = Math.Max(Math.Abs(da), Math.Abs(db));
                return Math.Abs(da - db) <= Math.Max(1e-6, scale * 1e-6);
            }

            if (a is DateTime dta && b is DateTime dtb)
                return Math.Abs((dta - dtb).TotalSeconds) < 1.0;   // 초 미만 차이는 표기 차

            var sa = Convert.ToString(a, CultureInfo.InvariantCulture)?.Trim() ?? "";
            var sb = Convert.ToString(b, CultureInfo.InvariantCulture)?.Trim() ?? "";
            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNum(object v) =>
            v is byte or sbyte or short or ushort or int or uint or long or ulong
              or float or double or decimal;

        private static bool TryLong(object? v, out long r)
        {
            r = 0;
            if (v is null or DBNull) return false;
            try { r = Convert.ToInt64(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        private static object? Val(DataRow? row, string col) =>
            row != null && row.Table.Columns.Contains(col) ? row[col] : null;

        private static string Show(object? v) =>
            v is null or DBNull ? "" : (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim();

        /// <summary>요청 컬럼 중 실제로 존재하는 것만, 중복 없이 골라낸다.</summary>
        private static List<ColumnInfo> Pick(IReadOnlyList<ColumnInfo> all, IEnumerable<string> want)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outp = new List<ColumnInfo>();
            foreach (var w in want)
            {
                if (!seen.Add(w)) continue;
                var c = all.FirstOrDefault(x => x.Name.Equals(w, StringComparison.OrdinalIgnoreCase));
                if (c != null) outp.Add(c);
            }
            return outp;
        }

        /// <summary>조회 결과를 ID 로 색인한다(행 순서는 DB 가 보장하지 않으므로 인덱스로 맞추지 않는다).</summary>
        private static Dictionary<long, DataRow> IndexBy(DataTable dt, string idColumn)
        {
            var map = new Dictionary<long, DataRow>();
            if (!dt.Columns.Contains(idColumn)) return map;
            foreach (DataRow row in dt.Rows)
                if (TryLong(row[idColumn], out var id)) map[id] = row;
            return map;
        }
    }
}
