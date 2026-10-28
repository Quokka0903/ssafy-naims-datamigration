using System.IO;
using RawCollectMigrator.Features.DbToDb;
using Xunit;

namespace RawCollectMigrator.Tests
{
    /// <summary>
    /// ColumnMapper.Build 순수 단위 테스트 (DB 불필요).
    ///
    /// 이 매핑 결과는 UI 전시뿐 아니라 DbToDbExecutor 의 실제 INSERT 컬럼 결정에
    /// 쓰이므로, 여기서 검증하는 결정성(순서 무관)·정확 일치 우선 규칙이 곧
    /// 데이터 정합성 안전장치다.
    /// </summary>
    public sealed class ColumnMapperTests
    {
        static ColumnMapperTests()
        {
            // 테스트 실행 폴더에 mapping_rules.json 이 전이 복사되지 않은 환경 대비:
            // 소스 트리의 JSON 을 직접 가리킨다 (MigrationRules 최초 접근 전에 설정).
            if (!File.Exists(MigrationRules.RulesPath))
            {
                var src = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..",
                    "RawCollectMigrator", "Features", "DbToDb", "mapping_rules.json"));
                if (File.Exists(src)) MigrationRules.RulesPath = src;
            }
        }

        /// <summary>테스트용 컬럼 생성 헬퍼.</summary>
        private static ColumnInfo Col(string name, string type = "int", int? len = null) =>
            new() { Name = name, DataType = type, MaxLength = len };

        private static ColumnMapping ForSource(List<ColumnMapping> maps, string sourceName) =>
            Assert.Single(maps, m => m.SourceName == sourceName);

        // ── 1) FK 결정성: 타겟 컬럼 순서와 무관하게 같은 후보에 매핑 ──────────

        [Fact]
        public void Fk_matches_correct_candidate_regardless_of_target_order()
        {
            // PULSE_SEGMENTS.PULSE_ID: 어간 "PULSE" 가
            //   REF_STD_PULSES_ID(어간 길이차 1) / REF_STD_PULSE_SEGMENTS_ID(길이차 큼)
            // 둘 다 접두 매치하던 실사례 — 길이차 최소 후보가 순서와 무관하게 선택돼야 한다.
            var source = new List<ColumnInfo>
            {
                Col("PULSE_SEGMENTS_ID"),   // 첫 컬럼 → PK 규칙으로 RECORD_ID
                Col("PULSE_ID"),
                Col("SEGMENT_DATA", "varbinary"),
            };
            var targetsA = new List<ColumnInfo>
            {
                Col("RECORD_ID"),
                Col("REF_STD_PULSES_ID"),
                Col("REF_STD_PULSE_SEGMENTS_ID"),
                Col("SEGMENT_DATA", "varbinary"),
            };
            var targetsB = new List<ColumnInfo>(targetsA);
            targetsB.Reverse();   // 타겟 컬럼 순서를 뒤집어도 결과가 같아야 한다

            var mapsA = ColumnMapper.Build(source, targetsA, "PULSE_SEGMENTS");
            var mapsB = ColumnMapper.Build(source, targetsB, "PULSE_SEGMENTS");

            foreach (var maps in new[] { mapsA, mapsB })
            {
                var fk = ForSource(maps, "PULSE_ID");
                Assert.Equal("REF_STD_PULSES_ID", fk.TargetName);
                Assert.Equal(MappingStatus.Renamed, fk.Status);
                Assert.Equal("FK→REF_*_ID", fk.Rule);
            }

            // 소스 컬럼 전체의 (소스→타겟) 대응도 두 순서에서 동일해야 한다
            var pairsA = mapsA.Where(m => m.SourceName != "").Select(m => (m.SourceName, m.TargetName));
            var pairsB = mapsB.Where(m => m.SourceName != "").Select(m => (m.SourceName, m.TargetName));
            Assert.Equal(pairsA, pairsB);
        }

        [Fact]
        public void Fk_prefers_exact_core_over_prefix_candidate()
        {
            // 어간 정확 일치(VESSEL==VESSEL) 후보가 있으면 접두 후보(VESSELDATA)보다 우선.
            var source = new List<ColumnInfo> { Col("SIG_ID"), Col("VESSEL_ID") };
            var targets = new List<ColumnInfo>
            {
                Col("RECORD_ID"),
                Col("REF_STD_VESSEL_DATA_ID"),   // 접두 후보를 앞에 둬도
                Col("REF_STD_VESSELS_ID"),       // 정확 후보가 이겨야 한다
            };

            var maps = ColumnMapper.Build(source, targets, "SIGNATURES");

            Assert.Equal("REF_STD_VESSELS_ID", ForSource(maps, "VESSEL_ID").TargetName);
        }

        [Fact]
        public void Fk_ambiguous_tie_gives_up_instead_of_order_dependent_pick()
        {
            // 길이차 동률(모호) — 틀린 FK 로 이관하느니 미발견 처리해야 한다.
            var source = new List<ColumnInfo> { Col("SEG_ID"), Col("PULSE_ID") };
            var targets = new List<ColumnInfo>
            {
                Col("RECORD_ID"),
                Col("REF_STD_PULSEA_ID"),   // 어간 PULSEA — 길이차 1
                Col("REF_STD_PULSEB_ID"),   // 어간 PULSEB — 길이차 1 (동률)
            };

            var maps = ColumnMapper.Build(source, targets, "SEGMENTS");

            var fk = ForSource(maps, "PULSE_ID");
            Assert.Equal(MappingStatus.SourceOnly, fk.Status);
            Assert.Equal("", fk.TargetName);
        }

        // ── 2) 정확 일치 선점: 별칭보다 이름 정확 일치가 우선 ────────────────

        [Fact]
        public void Exact_name_match_is_reserved_before_alias_rule()
        {
            // 소스에 COMMENTS 와 COMMENT 가 함께 있을 때:
            //   구현이 탐욕 순회이면 COMMENTS 가 별칭(COMMENTS→COMMENT)으로 타겟
            //   COMMENT 를 선점해 소스 COMMENT 가 미발견 처리된다.
            //   2패스 예약으로 정확 일치(COMMENT↔COMMENT)가 항상 이겨야 한다.
            var source = new List<ColumnInfo>
            {
                Col("DATA_ID"),
                Col("COMMENTS", "varchar", 200),   // 별칭 규칙 대상 (소스 순서상 먼저)
                Col("COMMENT",  "varchar", 200),   // 정확 일치 대상 (뒤에 있어도 이겨야 함)
            };
            var targets = new List<ColumnInfo>
            {
                Col("RECORD_ID"),
                Col("COMMENT", "varchar", 200),
            };

            var maps = ColumnMapper.Build(source, targets, "DATA");

            var exact = ForSource(maps, "COMMENT");
            Assert.Equal("COMMENT", exact.TargetName);
            Assert.Equal(MappingStatus.Exact, exact.Status);

            // COMMENTS 는 타겟이 이미 예약돼 미발견 처리 (수동 확인 대상)
            var alias = ForSource(maps, "COMMENTS");
            Assert.Equal(MappingStatus.SourceOnly, alias.Status);

            // 결과 순서 불변식: 소스 컬럼 순 + 타겟 전용은 뒤
            Assert.Equal(new[] { "DATA_ID", "COMMENTS", "COMMENT" },
                         maps.Where(m => m.SourceName != "").Select(m => m.SourceName));
            Assert.All(maps.Skip(source.Count), m => Assert.Equal(MappingStatus.TargetOnly, m.Status));
        }

        // ── 3) PK 규칙: 첫 컬럼 <TABLE>_ID → RECORD_ID ──────────────────────

        [Fact]
        public void Pk_first_column_maps_to_record_id()
        {
            var source = new List<ColumnInfo> { Col("CLASSES_ID"), Col("CLASS_NAME", "varchar", 50) };
            var targets = new List<ColumnInfo> { Col("RECORD_ID"), Col("CLASS_NAME", "varchar", 50) };

            var maps = ColumnMapper.Build(source, targets, "CLASSES");

            var pk = ForSource(maps, "CLASSES_ID");
            Assert.Equal("RECORD_ID", pk.TargetName);
            Assert.Equal(MappingStatus.Renamed, pk.Status);
            Assert.Equal("PK→RECORD_ID", pk.Rule);
        }

        // ── 4) 길이 축소 / 타입 변경 Note ───────────────────────────────────

        [Fact]
        public void Type_note_reports_length_shrink_and_type_change()
        {
            var source = new List<ColumnInfo>
            {
                Col("TITLE", "varchar", 100),   // 길이 축소 대상
                Col("SCORE", "float"),          // 타입 변경 대상
            };
            var targets = new List<ColumnInfo>
            {
                Col("TITLE", "varchar", 50),
                Col("SCORE", "int"),
            };

            var maps = ColumnMapper.Build(source, targets, "SAMPLE");

            var title = ForSource(maps, "TITLE");
            Assert.Equal("길이 축소 100→50 — 잘림 주의.", title.Note);

            var score = ForSource(maps, "SCORE");
            Assert.Equal("타입 변경 float → int", score.Note);
            Assert.True(score.TypeChanged);
        }
    }
}
