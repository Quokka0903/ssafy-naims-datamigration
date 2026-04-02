using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using DBImplant_N1toN2.Features.DbBrowser;

namespace DBImplant_N1toN2.DataAccess
{
    public interface ILegacyDbConnection : IDisposable
    {
        string DbTypeName { get; }
        string ConnectionString { get; }

        Task<bool> TestConnectionAsync();
        DataTable QueryTable(string tableName, string? filterColumn, object? filterValue, int maxRows = 500);

        string[] GetRootTableNames();
        Dictionary<string, FkRelation[]> GetFkSchema();
    }
}
