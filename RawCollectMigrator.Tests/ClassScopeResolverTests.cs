using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// CLASS(함정급) 단위 선택 이관의 스코프 계산(ClassScopeResolver) 통합 테스트.
    ///
    /// 로컬 Oracle XE(localhost:1521/XEPDB1, NAIMS)에 레거시 데이터가 적재돼 있고
    /// XEPDB1 이 OPEN 상태여야 한다. 연결이 안 되면 각 테스트는 Skip 된다(실패 아님).
    /// 검증은 특정 행수 하드코딩 대신 "환경에 독립적인 구조적 불변식" 위주로 한다.
    /// </summary>
    public sealed class ClassScopeResolverTests
    {
        private const DbProviderKind Kind = DbProviderKind.Oracle;
        private const DbConnectionMode Mode = DbConnectionMode.Direct;   // 로컬 XE 통합 테스트는 관리형 다이렉트 사용
        private static string Conn => DbProvider.DefaultOracleSource;

        private static async Task<bool> CanConnectAsync()
        {
            try
            {
                await using var cn = DbProvider.CreateConnection(Kind, Conn);
                await cn.OpenAsync();
                return true;
            }
            catch { return false; }
        }

        private static ClassScopeResolver NewResolver() => new(Kind, Mode, Conn);

        [SkippableFact]
        public async Task LoadClassList_returns_distinct_classes()
        {
            Skip.IfNot(await CanConnectAsync(), "로컬 Oracle XE(XEPDB1) 연결 불가 — 통합 테스트 건너뜀.");

            var list = await NewResolver().LoadClassListAsync();

            Assert.NotEmpty(list);
            Assert.All(list, x => Assert.False(string.IsNullOrWhiteSpace(x.Label)));
            var ids = list.Select(x => x.ClassId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count()); // CLASS_ID 중복 없음
        }

        [SkippableFact]
        public async Task SingleClass_scope_narrows_and_stays_subset()
        {
            Skip.IfNot(await CanConnectAsync(), "로컬 Oracle XE(XEPDB1) 연결 불가 — 통합 테스트 건너뜀.");

            var resolver = NewResolver();
            var first = (await resolver.LoadClassListAsync()).First().ClassId;

            var scope = await resolver.ComputeAsync(new[] { first });

            // 최상위: 선택한 CLASS 그 자체만
            Assert.True(scope.ContainsKey("CLASSES"));
            Assert.Equal(new[] { first }, scope["CLASSES"].OrderBy(x => x));

            // 서브트리 핵심 테이블은 항상 계산된다(비어 있어도 키는 존재)
            Assert.True(scope.ContainsKey("VESSEL_DATA"));
            Assert.True(scope.ContainsKey("SIGNATURES"));

            // 필터가 실제로 좁힌다: 허용 VESSEL 수 ≤ 전체 VESSEL 수
            var totalVessel = await TableInspector.TryGetRowCountAsync(Kind, Mode, Conn, "VESSEL_DATA") ?? 0;
            Assert.True(scope["VESSEL_DATA"].Count <= totalVessel,
                $"허용 VESSEL {scope["VESSEL_DATA"].Count} > 전체 {totalVessel}");
        }

        [SkippableFact]
        public async Task MultiClass_scope_is_superset_of_each_single()
        {
            Skip.IfNot(await CanConnectAsync(), "로컬 Oracle XE(XEPDB1) 연결 불가 — 통합 테스트 건너뜀.");

            var resolver = NewResolver();
            var ids = (await resolver.LoadClassListAsync()).Take(3).Select(x => x.ClassId).ToArray();
            Skip.If(ids.Length < 2, "CLASS 가 2개 미만이라 다중 선택 검증 불가.");

            var multi = await resolver.ComputeAsync(ids);

            // 각 단일 CLASS 스코프는 다중 스코프의 부분집합이어야 한다(단조성).
            foreach (var id in ids)
            {
                var single = await resolver.ComputeAsync(new[] { id });
                foreach (var (table, pks) in single)
                {
                    Assert.True(multi.ContainsKey(table),
                        $"다중 스코프에 '{table}' 누락 (단일 CLASS {id} 에는 존재)");
                    Assert.Subset(multi[table], pks); // pks ⊆ multi[table]
                }
            }

            // 다중 CLASSES 키는 선택한 집합과 정확히 일치
            Assert.Equal(ids.OrderBy(x => x), multi["CLASSES"].OrderBy(x => x));
        }

        [SkippableFact]
        public async Task ReferencedOnly_shared_tables_imply_a_referencing_parent()
        {
            Skip.IfNot(await CanConnectAsync(), "로컬 Oracle XE(XEPDB1) 연결 불가 — 통합 테스트 건너뜀.");

            var resolver = NewResolver();
            var ids = (await resolver.LoadClassListAsync()).Select(x => x.ClassId).ToArray();

            var scope = await resolver.ComputeAsync(ids);

            // "참조된 것만 포함" 설계: 공유 참조 테이블 키가 있으면 이를 참조하는 부모가 비어있지 않다.
            if (scope.ContainsKey("WEAPON"))
                Assert.NotEmpty(scope["WEAPON_CARRIER"]);          // 무기는 WEAPON_CARRIER 를 통해서만 참조
            if (scope.ContainsKey("COUNTRY_CODES"))
                Assert.NotEmpty(scope["VESSEL_DATA"]);             // 국가코드는 VESSEL 을 통해서만 참조
            if (scope.ContainsKey("NB_SNIPPET"))
                Assert.NotEmpty(scope["SIG_SNIPPET"]);             // 협대역 스니펫은 SIG_SNIPPET 을 통해서만
            if (scope.ContainsKey("TR_SNIPPET"))
                Assert.NotEmpty(scope["FRAME_SNIPPET"]);           // 순간소음 스니펫은 FRAME_SNIPPET 을 통해서만
        }

        [SkippableFact]
        public async Task EmptyFilter_is_out_of_scope_signal()
        {
            Skip.IfNot(await CanConnectAsync(), "로컬 Oracle XE(XEPDB1) 연결 불가 — 통합 테스트 건너뜀.");

            // 빈 선택으로 계산하면 CLASS 도메인 밖 = 어떤 실데이터 PK 도 허용되지 않는다.
            // (UI 는 빈 선택 시 ClassFilter 를 아예 넘기지 않아 전체 이관으로 동작한다.)
            var scope = await NewResolver().ComputeAsync(Array.Empty<long>());
            Assert.All(scope, kv => Assert.Empty(kv.Value));
        }
    }
}
