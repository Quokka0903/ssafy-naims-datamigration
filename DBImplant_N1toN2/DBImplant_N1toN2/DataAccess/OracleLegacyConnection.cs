using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using DBImplant_N1toN2.Models;
using DBImplant_N1toN2.Features.DbBrowser;

namespace DBImplant_N1toN2.DataAccess
{
    public sealed class OracleLegacyConnection : ILegacyDbConnection
    {
        public string DbTypeName => "Oracle";
        public string ConnectionString { get; }
        private readonly LegacyConnectionMode _mode;
        private const string SchemaPrefix = "SYSTEM.";

        public OracleLegacyConnection(string connectionString, LegacyConnectionMode mode = LegacyConnectionMode.Odbc)
        {
            ConnectionString = connectionString;
            _mode = mode;
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();
                return true;
            }
            catch { return false; }
        }

        public DataTable QueryTable(string tableName, string? filterColumn, object? filterValue, int maxRows = 500)
        {
            using var conn = CreateConnection();
            conn.Open();

            // ODBC 는 위치 기반 '?', 다이렉트(관리형)는 명명 ':v'.
            string marker = _mode == LegacyConnectionMode.Odbc ? "?" : ":v";
            string fullTable = SchemaPrefix + tableName;
            string where = filterColumn != null
                ? $" WHERE {filterColumn} = {marker} AND ROWNUM <= {maxRows}"
                : $" WHERE ROWNUM <= {maxRows}";

            // CRYPTO.DECRYPT 대상 컬럼이 있으면 명시적 SELECT 구성
            string columns = BuildColumnList(tableName);
            string sql = $"SELECT {columns} FROM {fullTable}{where}";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (filterColumn != null)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = "v";
                param.Value = filterValue;
                cmd.Parameters.Add(param);
            }

            using var reader = cmd.ExecuteReader();
            var dt = new DataTable(tableName);
            dt.Load(reader);
            return dt;
        }

        public string[] GetRootTableNames() => new[]
        {
            "COUNTRY_CODES", "CLASSES", "VESSEL_DATA", "ACTIVE_SONAR",
            "SIGNATURES", "PULSES", "TRANSIENT", "WEAPON",
            "DB_DIST_GROUP", "DB_DIST_LOG",
        };

        public Dictionary<string, FkRelation[]> GetFkSchema()
        {
            var result = new Dictionary<string, FkRelation[]>();
            foreach (var (table, rels) in OracleTableSchema.FkRelations)
            {
                result[table] = rels.Select(r => new FkRelation(r.Pk, r.Child, r.ChildFk)).ToArray();
            }
            return result;
        }

        public void Dispose() { }

        // ── Private helpers ──────────────────────────────────────────────────

        private DbConnection CreateConnection() => _mode switch
        {
            // 폐쇄망 기본: Windows ODBC 드라이버/DSN 경유
            LegacyConnectionMode.Odbc => new System.Data.Odbc.OdbcConnection(ConnectionString),
            // 다이렉트: Oracle.ManagedDataAccess.Core (관리형, 클라이언트 설치 불필요)
            _ => new Oracle.ManagedDataAccess.Client.OracleConnection(ConnectionString),
        };

        /// <summary>CRYPTO.DECRYPT 래핑이 필요한 컬럼을 처리한 SELECT 컬럼 목록</summary>
        private static string BuildColumnList(string tableName)
        {
            if (!OracleTableSchema.CryptoFields.TryGetValue(tableName, out var cryptoCols))
                return "*";

            // 암호화 컬럼만 DECRYPT 래핑, 나머지는 * (Oracle에서 중복 컬럼 주의)
            // 실제 운용 시에는 ALL_TAB_COLUMNS에서 전체 컬럼 목록을 가져와 명시해야 함
            var decrypted = cryptoCols.Select(c => $"CRYPTO.DECRYPT({c}) AS {c}");
            return string.Join(", ", decrypted) + ", " + tableName + ".*";
        }
    }
}
