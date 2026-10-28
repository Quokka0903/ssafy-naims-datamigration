using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using DBImplant_N1toN2.Features.DbBrowser;
using Microsoft.Data.SqlClient;

namespace DBImplant_N1toN2.DataAccess
{
    public sealed class MsSqlLegacyConnection : ILegacyDbConnection
    {
        public string DbTypeName => "MsSQL";
        public string ConnectionString { get; }
        private readonly LegacyConnectionMode _mode;

        public MsSqlLegacyConnection(string connectionString, LegacyConnectionMode mode = LegacyConnectionMode.Odbc)
        {
            ConnectionString = connectionString;
            _mode = mode;
        }

        private DbConnection CreateConnection() => _mode == LegacyConnectionMode.Odbc
            ? new System.Data.Odbc.OdbcConnection(ConnectionString)   // 폐쇄망 기본: ODBC 경유
            : new SqlConnection(ConnectionString);                    // 다이렉트: SqlClient

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

            // ODBC 는 위치 기반 '?', 다이렉트(SqlClient)는 명명 '@v'.
            string marker = _mode == LegacyConnectionMode.Odbc ? "?" : "@v";
            string sql = filterColumn != null
                ? $"SELECT TOP {maxRows} * FROM [{tableName}] WHERE [{filterColumn}] = {marker}"
                : $"SELECT TOP {maxRows} * FROM [{tableName}]";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (filterColumn != null)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "v";
                p.Value = filterValue ?? DBNull.Value;
                cmd.Parameters.Add(p);
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

        public Dictionary<string, FkRelation[]> GetFkSchema() => new()
        {
            ["COUNTRY_CODES"] = new[] { new FkRelation("COUNTRY_ID", "VESSEL_DATA", "COUNTRY_ID") },
            ["CLASSES"] = new[]
            {
                new FkRelation("CLASS_ID", "VESSEL_DATA", "CLASS_ID"),
                new FkRelation("CLASS_ID", "WEAPON_CARRIER", "CLASS_ID"),
            },
            ["ACTIVE_SONAR"] = new[]
            {
                new FkRelation("SONAR_ID", "PULSES", "SONAR_ID"),
                new FkRelation("SONAR_ID", "PULSE_SEGMENTS", "SONAR_ID"),
                new FkRelation("SONAR_ID", "PULSE_FREQUENCY_ISUS90", "SONAR_ID"),
            },
            ["WEAPON"] = new[] { new FkRelation("WEAPON_ID", "WEAPON_CARRIER", "WEAPON_ID") },
            ["VESSEL_DATA"] = new[]
            {
                new FkRelation("VESSEL_ID", "SIGNATURES", "VESSEL_ID"),
                new FkRelation("VESSEL_ID", "IMAGE_DATA", "VESSEL_ID"),
                new FkRelation("VESSEL_ID", "CHARACTERISTICS", "VESSEL_ID"),
                new FkRelation("VESSEL_ID", "TRANSIENT", "VESSEL_ID"),
            },
            ["PULSES"] = new[] { new FkRelation("PULSE_ID", "AI_SNIPPET", "PULSE_ID") },
            ["PULSE_FREQUENCY_ISUS90"] = new[]
            {
                new FkRelation("PULSE_F_ID", "PULSE_SEG_SWING_ISUS90", "PULSE_F_ID"),
                new FkRelation("PULSE_F_ID", "PULSE_SEG_LENGTH_ISUS90", "PULSE_F_ID"),
            },
            ["SIGNATURES"] = new[]
            {
                new FkRelation("SIGNATURE_ID", "HARMONICS", "SIGNATURES_ID"),
                new FkRelation("SIGNATURE_ID", "SIG_SNIPPET", "SIGNATURE_ID"),
            },
            ["TRANSIENT"] = new[] { new FkRelation("TR_ID", "TR_FRAME", "TR_ID") },
            ["SIG_SNIPPET"] = new[] { new FkRelation("NB_SNIPPET_ID", "NB_SNIPPET", "NB_SNIPPET_ID") },
            ["TR_FRAME"] = new[] { new FkRelation("FRAME_ID", "FRAME_SNIPPET", "FRAME_ID") },
            ["FRAME_SNIPPET"] = new[] { new FkRelation("TR_SNIPPET_ID", "TR_SNIPPET", "TR_SNIPPET_ID") },
            ["DB_DIST_GROUP"] = new[] { new FkRelation("GROUP_ID", "DIST_LIST", "GROUP_ID") },
            ["DB_DIST_LOG"] = new[] { new FkRelation("LOG_ID", "DIST_TO", "LOG_ID") },
        };

        public void Dispose() { }
    }
}
