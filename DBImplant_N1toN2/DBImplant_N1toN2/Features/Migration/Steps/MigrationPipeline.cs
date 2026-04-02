using Microsoft.Data.SqlClient;
using DBImplant_N1toN2.Features.Migration.Models;

namespace DBImplant_N1toN2.Features.Migration.Steps
{
    /// <summary>
    /// IMigrationStep 구현체를 Order 순서대로 실행하는 오케스트레이터.
    /// 전체를 하나의 트랜잭션으로 감싸며, 실패 시 자동 롤백된다.
    /// </summary>
    public sealed class MigrationPipeline
    {
        private readonly MigrationContext _ctx;
        private readonly List<IMigrationStep> _steps;

        public MigrationPipeline(MigrationContext ctx)
        {
            _ctx = ctx;
            _steps = new List<IMigrationStep>
            {
                new InsertPrecAnalysisStep(),
                // TODO: 추가 스텝 등록
            };
            _steps.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        /// <summary>
        /// 마이그레이션 파이프라인을 실행한다.
        /// </summary>
        /// <returns>성공 여부</returns>
        public async Task<bool> RunAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_ctx.TargetConnectionString);
            await conn.OpenAsync(ct);

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

            try
            {
                for (int i = 0; i < _steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var step = _steps[i];
                    progress.Report(new MigrationProgress(i + 1, _steps.Count, step.Name));

                    await step.ExecuteAsync(conn, tx, _ctx, progress, ct);
                }

                await tx.CommitAsync(ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                // 트랜잭션은 Dispose 시 자동 롤백
                return false;
            }
            catch
            {
                // 트랜잭션은 Dispose 시 자동 롤백
                throw;
            }
        }
    }
}
