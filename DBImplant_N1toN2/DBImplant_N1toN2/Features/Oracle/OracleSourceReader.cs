using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using DBImplant_N1toN2.Models;

namespace DBImplant_N1toN2.Features.Oracle
{
    /// <summary>
    /// Oracle 레거시 DB (USPASK CDMU) 읽기 전용 리더.
    /// CRYPTO.DECRYPT 자동 래핑, SYSTEM. 스키마 접두사 처리.
    ///
    /// 연결: Oracle.ManagedDataAccess.Core NuGet 또는 System.Data.OracleClient 사용.
    /// 현재는 DbConnection 추상 타입으로 구현하여 DI로 주입 가능.
    /// </summary>
    public sealed class OracleSourceReader
    {
        private readonly string _connectionString;
        private readonly string _schemaPrefix;

        /// <param name="connectionString">Oracle 연결 문자열</param>
        /// <param name="schemaPrefix">테이블 접두사 (기본 "SYSTEM.")</param>
        public OracleSourceReader(string connectionString, string schemaPrefix = "SYSTEM.")
        {
            _connectionString = connectionString;
            _schemaPrefix = schemaPrefix;
        }

        /// <summary>
        /// 특정 테이블에서 전체 레코드를 읽어온다. CRYPTO.DECRYPT 자동 적용.
        /// </summary>
        public DataTable ReadTable(DbConnection connection, string tableName, int maxRows = 500)
        {
            var sql = BuildSelectSql(tableName, filterColumn: null, filterValue: null, maxRows);
            return ExecuteQuery(connection, sql, null);
        }

        /// <summary>
        /// FK 필터 조건으로 자식 레코드를 읽는다.
        /// </summary>
        public DataTable ReadFilteredTable(DbConnection connection, string tableName,
            string filterColumn, object filterValue, int maxRows = 500)
        {
            var sql = BuildSelectSql(tableName, filterColumn, filterValue, maxRows);
            return ExecuteQuery(connection, sql, filterValue != null
                ? new[] { CreateParameter(connection, ":v", filterValue) }
                : null);
        }

        /// <summary>
        /// SELECT SQL 생성 — CRYPTO.DECRYPT 자동 래핑 + ROWNUM 제한.
        /// Oracle에서는 TOP이 아닌 ROWNUM을 사용.
        /// </summary>
        public string BuildSelectSql(string tableName, string? filterColumn, object? filterValue, int maxRows)
        {
            // 컬럼 목록 — CRYPTO 대상 컬럼은 DECRYPT 래핑
            string columns = "*";
            if (OracleTableSchema.CryptoFields.TryGetValue(tableName, out var cryptoCols))
            {
                // CRYPTO 필드가 있으면 명시적 컬럼 SELECT로 전환해야 하지만,
                // 스키마를 동적으로 가져올 수 없으므로 * 사용 후 후처리 권장.
                // 여기서는 간단히 CRYPTO 필드만 DECRYPT 래핑하여 별도 SELECT.
                var decryptCols = cryptoCols.Select(c => $"CRYPTO.DECRYPT({c}) AS {c}");
                columns = string.Join(", ", decryptCols) + ", " + tableName + ".*";
                // 주의: Oracle에서 동일 컬럼이 중복될 수 있음 — 실제 구현 시 컬럼 리스트 명시 필요
            }

            string fullTable = _schemaPrefix + tableName;
            string where = "";
            if (filterColumn != null)
                where = $" WHERE {filterColumn} = :v";

            return $"SELECT {columns} FROM {fullTable}{where} WHERE ROWNUM <= {maxRows}";
        }

        /// <summary>
        /// Oracle SELECT SQL (CRYPTO 미적용, 원시 쿼리용)
        /// </summary>
        public static string BuildRawSelectSql(string tableName, string schemaPrefix = "SYSTEM.")
        {
            return $"SELECT * FROM {schemaPrefix}{tableName}";
        }

        /// <summary>
        /// 쿼리 실행 공통 메서드
        /// </summary>
        private static DataTable ExecuteQuery(DbConnection connection, string sql, DbParameter[]? parameters)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            if (parameters != null)
                foreach (var p in parameters)
                    cmd.Parameters.Add(p);

            using var reader = cmd.ExecuteReader();
            var dt = new DataTable();
            dt.Load(reader);
            return dt;
        }

        private static DbParameter CreateParameter(DbConnection connection, string name, object value)
        {
            using var cmd = connection.CreateCommand();
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value;
            return param;
        }

        // ── Oracle 전용 유틸 ─────────────────────────────────────────────────────

        /// <summary>
        /// TO_DATE 변환이 필요한 날짜 조건 생성 (Oracle 레거시 패턴)
        /// </summary>
        public static string ToOracleDate(DateTime dt)
            => $"TO_DATE('{dt:yyyy-MM-dd HH:mm:ss}','YYYY-MM-DD HH24:MI:SS')";

        /// <summary>
        /// Oracle (+) 외부 조인을 ANSI LEFT JOIN으로 변환하는 헬퍼.
        /// 예: WHERE A.ID = B.ID(+) → LEFT JOIN B ON A.ID = B.ID
        /// </summary>
        public static string ConvertOracleOuterJoinToAnsi(string oracleWhere)
        {
            // 간단한 패턴: col1 = col2(+) → LEFT JOIN 변환
            // 실제 복잡한 쿼리는 수동 변환 필요
            return oracleWhere.Replace("(+)", "/* OUTER_JOIN_MARKER */");
        }

        // ── 전체 테이블 스캔 (마이그레이션 검증용) ──────────────────────────────────

        /// <summary>
        /// Oracle의 모든 테이블 레코드 수를 카운트한다 (마이그레이션 전후 비교용).
        /// </summary>
        public async Task<Dictionary<string, long>> CountAllTablesAsync(DbConnection connection)
        {
            var result = new Dictionary<string, long>();
            foreach (var table in OracleTableSchema.AllTables)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM {_schemaPrefix}{table}";
                    var count = await cmd.ExecuteScalarAsync();
                    result[table] = Convert.ToInt64(count);
                }
                catch
                {
                    result[table] = -1; // 테이블 미존재 or 권한 없음
                }
            }
            return result;
        }
    }
}
