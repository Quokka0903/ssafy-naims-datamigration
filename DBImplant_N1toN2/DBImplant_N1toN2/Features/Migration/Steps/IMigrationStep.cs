using Microsoft.Data.SqlClient;
using DBImplant_N1toN2.Features.Migration.Models;

namespace DBImplant_N1toN2.Features.Migration.Steps
{
    public interface IMigrationStep
    {
        string Name { get; }
        int Order { get; }
        Task ExecuteAsync(SqlConnection conn, SqlTransaction tx, MigrationContext ctx,
                          IProgress<MigrationProgress> progress, CancellationToken ct);
    }
}
