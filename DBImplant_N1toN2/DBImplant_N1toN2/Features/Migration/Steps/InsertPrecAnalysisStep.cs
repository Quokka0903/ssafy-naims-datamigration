using Microsoft.Data.SqlClient;
using DBImplant_N1toN2.Features.Migration.Models;

namespace DBImplant_N1toN2.Features.Migration.Steps
{
    /// <summary>
    /// PREC_ANALYSIS 테이블에 단주기 파싱 데이터를 INSERT하는 스텝.
    /// </summary>
    public sealed class InsertPrecAnalysisStep : IMigrationStep
    {
        public string Name => "PREC_ANALYSIS INSERT";
        public int Order => 1;

        public async Task ExecuteAsync(
            SqlConnection conn, SqlTransaction tx, MigrationContext ctx,
            IProgress<MigrationProgress> progress, CancellationToken ct)
        {
            var sdTab = ctx.FileBrowser.SdTab;
            var packages = sdTab.Packages;

            if (packages.Count == 0)
            {
                progress.Report(new MigrationProgress(1, 1, Name, "파싱된 패키지가 없습니다."));
                return;
            }

            for (int i = 0; i < packages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var pkg = packages[i];
                progress.Report(new MigrationProgress(1, 1, Name,
                    $"패키지 {i + 1}/{packages.Count}: {pkg.PreciseName}"));

                // TODO: 실제 INSERT SQL 구현
                // var sql = @"
                //     INSERT INTO PREC_ANALYSIS
                //         (STATION_CODE, EVENT_TIME, LATITUDE, LONGITUDE, DEPTH, MAGNITUDE, DELETED, IS_TRAINING)
                //     VALUES
                //         (@StationCode, @EventTime, @Latitude, @Longitude, @Depth, @Magnitude, 0, 0);
                //     SELECT SCOPE_IDENTITY();";
                //
                // await using var cmd = new SqlCommand(sql, conn, tx);
                // cmd.Parameters.AddWithValue("@StationCode", pkg.StationCode ?? "");
                // cmd.Parameters.AddWithValue("@EventTime",   pkg.EventTime ?? (object)DBNull.Value);
                // cmd.Parameters.AddWithValue("@Latitude",    pkg.Latitude ?? (object)DBNull.Value);
                // cmd.Parameters.AddWithValue("@Longitude",   pkg.Longitude ?? (object)DBNull.Value);
                // cmd.Parameters.AddWithValue("@Depth",       pkg.Depth ?? (object)DBNull.Value);
                // cmd.Parameters.AddWithValue("@Magnitude",   pkg.Magnitude ?? (object)DBNull.Value);
                //
                // var analysisId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                // ctx.Bag[$"AnalysisId_{i}"] = analysisId;

                await Task.Delay(10, ct); // placeholder
            }
        }
    }
}
