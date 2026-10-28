using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace RawCollectMigrator.Features.DbToDb
{
    /// <summary>타겟(NAIMS2, MsSQL) 컬럼 한 개의 메타데이터.</summary>
    public sealed class TargetColumn
    {
        public string Name { get; init; } = "";
        public string DataType { get; init; } = "";     // int / varchar / bigint / varbinary / datetime2 ...
        public int MaxLength { get; init; }              // 문자/이진 최대길이 (-1 = max)
        public bool IsNullable { get; init; }
        public bool IsIdentity { get; init; }
        public bool IsComputed { get; init; }
        public bool HasDefault { get; init; }
        public int Ordinal { get; init; }

        /// <summary>활성 FK 제약이 참조하는 부모 테이블/컬럼. 제약이 없으면 빈 문자열.</summary>
        public string FkParentTable { get; set; } = "";
        public string FkParentColumn { get; set; } = "";

        /// <summary>활성화된 FK 제약이 걸린 컬럼인지.</summary>
        public bool HasForeignKey => FkParentTable.Length > 0;

        /// <summary>
        /// 부모 테이블에 키가 0 인 행이 실제로 있는지.
        /// NAIMS2 는 일부 참조 테이블에 RECORD_ID=0 "참조 없음" 센티넬 행을 두고 있어,
        /// 그런 경우에만 레거시 0 을 그대로 넣을 수 있다. 없으면 FK 위반이 된다.
        /// </summary>
        public bool FkParentHasZeroRow { get; set; }

        /// <summary>레거시 0("참조 없음")을 그대로 INSERT 해도 되는 컬럼인지.</summary>
        public bool AllowsZeroReference => !HasForeignKey || FkParentHasZeroRow;

        /// <summary>INSERT 목록에 포함할 수 있는 컬럼인지 (IDENTITY·계산 컬럼 제외).</summary>
        public bool IsInsertable => !IsIdentity && !IsComputed;
    }

    /// <summary>타겟 테이블 컬럼 메타를 실측으로 로드한다.</summary>
    public sealed class TargetSchema
    {
        private readonly Dictionary<string, List<TargetColumn>> _byTable =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TargetColumn> Columns(string table) =>
            _byTable.TryGetValue(table, out var c) ? c : (IReadOnlyList<TargetColumn>)Array.Empty<TargetColumn>();

        public TargetColumn? Column(string table, string col) =>
            Columns(table).FirstOrDefault(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase));

        public bool HasColumn(string table, string col) => Column(table, col) != null;

        /// <summary>지정한 타겟 테이블들의 컬럼 메타를 한 번에 로드한다.</summary>
        public static async Task<TargetSchema> LoadAsync(
            string targetConnStr, IEnumerable<string> tables, CancellationToken ct = default)
        {
            var schema = new TargetSchema();
            await using var cn = new SqlConnection(targetConnStr);
            await cn.OpenAsync(ct);

            foreach (var t in tables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cols = new List<TargetColumn>();
                await using var cmd = cn.CreateCommand();
                cmd.CommandText = @"
SELECT c.name, ty.name AS type_name, c.max_length, c.is_nullable,
       c.is_identity, c.is_computed, c.column_id,
       CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END AS has_default
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
JOIN sys.tables tb ON tb.object_id = c.object_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE tb.name = @t
ORDER BY c.column_id;";
                var p = cmd.CreateParameter();
                p.ParameterName = "@t";
                p.Value = t;
                cmd.Parameters.Add(p);

                // MARS 가 꺼져 있어 리더를 연 채로 같은 연결에 다른 질의를 보낼 수 없다.
                // 아래 FK 질의 전에 반드시 닫히도록 리더의 범위를 좁힌다.
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        var typeName = r.GetString(1);
                        int rawLen = r.GetInt16(2);
                        // nchar/nvarchar 는 byte 길이 → 문자수로 환산
                        int maxLen = rawLen;
                        if (rawLen > 0 && typeName is "nvarchar" or "nchar") maxLen = rawLen / 2;

                        cols.Add(new TargetColumn
                        {
                            Name = r.GetString(0),
                            DataType = typeName,
                            MaxLength = rawLen == -1 ? -1 : maxLen,
                            IsNullable = r.GetBoolean(3),
                            IsIdentity = r.GetBoolean(4),
                            IsComputed = r.GetBoolean(5),
                            Ordinal = r.GetInt32(6),
                            HasDefault = r.GetInt32(7) == 1,
                        });
                    }
                }

                // FK 제약이 걸린 컬럼에 부모 테이블/컬럼을 기록한다.
                await using (var fkCmd = cn.CreateCommand())
                {
                    fkCmd.CommandText = @"
SELECT c.name AS child_col, rt.name AS parent_tbl, rc.name AS parent_col
FROM sys.foreign_key_columns fkc
JOIN sys.columns c        ON c.object_id  = fkc.parent_object_id
                         AND c.column_id = fkc.parent_column_id
JOIN sys.tables  tb       ON tb.object_id = fkc.parent_object_id
JOIN sys.foreign_keys fk  ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables  rt       ON rt.object_id = fkc.referenced_object_id
JOIN sys.columns rc       ON rc.object_id  = fkc.referenced_object_id
                         AND rc.column_id = fkc.referenced_column_id
WHERE tb.name = @t AND fk.is_disabled = 0;";
                    var fp = fkCmd.CreateParameter();
                    fp.ParameterName = "@t";
                    fp.Value = t;
                    fkCmd.Parameters.Add(fp);

                    await using var fr = await fkCmd.ExecuteReaderAsync(ct);
                    while (await fr.ReadAsync(ct))
                    {
                        var col = cols.FirstOrDefault(c =>
                            c.Name.Equals(fr.GetString(0), StringComparison.OrdinalIgnoreCase));
                        if (col == null) continue;
                        col.FkParentTable = fr.GetString(1);
                        col.FkParentColumn = fr.GetString(2);
                    }
                }

                schema._byTable[t] = cols;
            }

            await schema.ProbeZeroSentinelRowsAsync(cn, ct);
            return schema;
        }

        /// <summary>
        /// FK 부모 테이블에 키가 0 인 "참조 없음" 센티넬 행이 있는지 실측한다.
        /// NAIMS2 는 이 관례가 테이블마다 달라서(STD_ACTV_SONAR 는 있고 STD_VESSEL_DATA 는 없음)
        /// 추측하지 않고 직접 확인해야 한다.
        /// </summary>
        private async Task ProbeZeroSentinelRowsAsync(SqlConnection cn, CancellationToken ct)
        {
            var fkCols = _byTable.Values.SelectMany(c => c).Where(c => c.HasForeignKey).ToList();
            var parents = fkCols
                .Select(c => (c.FkParentTable, c.FkParentColumn))
                .Distinct();

            var hasZero = new HashSet<(string, string)>();
            foreach (var (tbl, col) in parents)
            {
                await using var cmd = cn.CreateCommand();
                // 식별자는 파라미터로 못 넘기므로 대괄호로 감싼다. 이름은 sys 카탈로그에서 온 값이다.
                cmd.CommandText =
                    $"SELECT CASE WHEN EXISTS (SELECT 1 FROM [{tbl}] WHERE [{col}] = 0) THEN 1 ELSE 0 END;";
                try
                {
                    if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1)
                        hasZero.Add((tbl, col));
                }
                catch { /* 읽지 못하면 0 을 못 쓰는 것으로 본다(보수적) */ }
            }

            foreach (var c in fkCols)
                c.FkParentHasZeroRow = hasZero.Contains((c.FkParentTable, c.FkParentColumn));
        }
    }
}
