using System.Data.Common;
using System.IO;
using System.Text;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>
    /// 소스 PK → 타겟 RECORD_ID 재매핑 저장소.
    ///
    /// 타겟 STD_* 테이블은 전부 RECORD_ID 가 IDENTITY 라 소스의 CLASS_ID 등을
    /// 그대로 쓸 수 없다. 부모를 INSERT 하면서 얻은 새 RECORD_ID 를 여기에 적어 두고,
    /// 자식 테이블의 FK(REF_STD_*_ID)를 채울 때 조회한다.
    ///
    /// 재실행 안전성을 위해 이관 후 파일로 덤프/복원할 수 있다.
    /// </summary>
    public sealed class IdRemapper
    {
        // 소스 테이블명 → (소스 PK → 타겟 RECORD_ID)
        private readonly Dictionary<string, Dictionary<long, long>> _maps =
            new(StringComparer.OrdinalIgnoreCase);

        // 이번 실행에서 "이미 타겟에 있어 재사용"으로 판정된 소스 행 (신규 INSERT 와 구분).
        // 부모 단위 멱등: 부모가 여기 있으면 자식도 이미 이관된 것으로 보고 재삽입을 막는다.
        private readonly HashSet<(string Table, long Id)> _reused = new();

        public void Add(string sourceTable, long sourceId, long targetRecordId)
        {
            if (!_maps.TryGetValue(sourceTable, out var m))
                _maps[sourceTable] = m = new Dictionary<long, long>();
            m[sourceId] = targetRecordId;
        }

        /// <summary>기존 타겟 행을 재사용한 소스 행으로 표시한다(타겟 RECORD_ID 를 알면 함께 등록).</summary>
        public void MarkReused(string sourceTable, long sourceId, long? targetRecordId = null)
        {
            _reused.Add((sourceTable, sourceId));
            if (targetRecordId is { } t) Add(sourceTable, sourceId, t);
        }

        /// <summary>이 소스 행이 이번 실행에서 "재사용(기존)"으로 판정됐는지.</summary>
        public bool WasReused(string sourceTable, long sourceId) =>
            _reused.Contains((sourceTable, sourceId));

        public bool TryGet(string sourceTable, long sourceId, out long targetRecordId)
        {
            targetRecordId = 0;
            return _maps.TryGetValue(sourceTable, out var m) && m.TryGetValue(sourceId, out targetRecordId);
        }

        /// <summary>
        /// FK 값을 변환한다. 매핑이 없으면 예외 대신 null 을 돌려주고 호출부가 판단하게 한다.
        /// (레거시에는 부모가 지워진 고아 FK 가 존재한다 — 실측으로 확인됨)
        /// </summary>
        public long? Translate(string parentSourceTable, object? rawFk)
        {
            if (rawFk is null || rawFk is DBNull) return null;
            if (!long.TryParse(Convert.ToString(rawFk), out var id)) return null;
            if (id < 0) return id;                       // -1 등 의도적 sentinel 은 보존
            return TryGet(parentSourceTable, id, out var t) ? t : null;
        }

        /// <summary>전체 매핑을 (소스테이블, 소스PK, 타겟RECORD_ID) 로 열거한다(보고서용).</summary>
        public IEnumerable<(string Table, long SourceId, long TargetId)> Entries()
        {
            foreach (var (table, m) in _maps.OrderBy(k => k.Key))
                foreach (var (src, tgt) in m.OrderBy(k => k.Key))
                    yield return (table, src, tgt);
        }

        public int Count(string sourceTable) =>
            _maps.TryGetValue(sourceTable, out var m) ? m.Count : 0;

        public int TotalCount => _maps.Values.Sum(m => m.Count);

        public IEnumerable<string> Tables => _maps.Keys;

        /// <summary>
        /// 기존에 이미 이관된 행을 자연키로 되찾아 매핑에 채운다.
        /// 재실행 시 중복 INSERT 를 막고, "이미 있는 국가는 재사용" 요구를 구현하는 통로다.
        /// </summary>
        /// <param name="pairs">(소스 PK, 자연키 값) 목록</param>
        /// <param name="targetKeyColumn">타겟에서 자연키에 해당하는 컬럼</param>
        public async Task<int> SeedFromTargetAsync(
            DbConnection targetCn,
            string sourceTable,
            string targetTable,
            string targetKeyColumn,
            IEnumerable<(long SourceId, string Key)> pairs,
            CancellationToken ct = default,
            DbTransaction? tx = null)
        {
            // 타겟의 (자연키 → RECORD_ID) 를 한 번에 읽어 메모리에서 대조한다.
            var existing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = targetCn.CreateCommand())
            {
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText =
                    $"SELECT [{targetKeyColumn}], RECORD_ID FROM [{targetTable}]";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (r.IsDBNull(0)) continue;
                    var k = NormalizeKey(Convert.ToString(r.GetValue(0)) ?? "");
                    if (k.Length == 0) continue;
                    existing[k] = Convert.ToInt64(r.GetValue(1));
                }
            }

            int matched = 0;
            foreach (var (srcId, key) in pairs)
            {
                var k = NormalizeKey(key);
                if (k.Length == 0) continue;

                if (existing.TryGetValue(k, out var rec))
                {
                    MarkReused(sourceTable, srcId, rec);
                    matched++;
                    continue;
                }

                // 잘린 이름 구제: 레거시는 char(30) 이라 30자에서 끊겨 있다.
                //   소스 "Sejong the Great-class destroy" ↔ 타겟 "Sejong the Great-class destroyer"
                // 완전일치만 보면 중복 INSERT 가 되므로 접두 일치를 허용한다.
                if (IsLikelyTruncated(key))
                {
                    var hits = existing.Where(e => e.Key.StartsWith(k, StringComparison.Ordinal))
                                       .Take(2).ToList();
                    if (hits.Count == 1)          // 후보가 하나일 때만 — 모호하면 신규로 넘긴다
                    {
                        MarkReused(sourceTable, srcId, hits[0].Value);
                        matched++;
                    }
                }
            }
            return matched;
        }

        /// <summary>레거시 고정폭(char) 경계에 딱 맞게 차 있으면 잘렸을 가능성이 높다.</summary>
        public static bool IsLikelyTruncated(string raw, int width = LegacyNameWidth) =>
            raw.TrimEnd().Length >= width;

        /// <summary>구 NAIMS 이름 컬럼의 고정폭. 2026-07-22 복호화 실측으로 확인.</summary>
        public const int LegacyNameWidth = 30;

        /// <summary>자연키 대조용 정규화 — 공백/대소문자 차이로 중복 생성되는 것을 막는다.</summary>
        public static string NormalizeKey(string s) =>
            string.Join(' ', s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

        // ── 영속화 ──────────────────────────────────────────────

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# sourceTable,sourceId,targetRecordId");
            foreach (var (table, m) in _maps.OrderBy(k => k.Key))
                foreach (var (src, tgt) in m.OrderBy(k => k.Key))
                    sb.Append(table).Append(',').Append(src).Append(',').Append(tgt).AppendLine();
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static IdRemapper Load(string path)
        {
            var r = new IdRemapper();
            if (!File.Exists(path)) return r;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(',');
                if (p.Length != 3) continue;
                if (long.TryParse(p[1], out var s) && long.TryParse(p[2], out var t))
                    r.Add(p[0], s, t);
            }
            return r;
        }
    }
}
