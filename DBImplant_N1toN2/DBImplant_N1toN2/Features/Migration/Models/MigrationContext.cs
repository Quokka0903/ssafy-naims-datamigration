using DBImplant_N1toN2.DataAccess;
using DBImplant_N1toN2.Features.FileBrowser;

namespace DBImplant_N1toN2.Features.Migration.Models
{
    public sealed class MigrationContext
    {
        public required ILegacyDbConnection LegacyDb { get; init; }
        public required FileBrowserViewModel FileBrowser { get; init; }
        public required string TargetConnectionString { get; init; }
        public Dictionary<string, object> Bag { get; } = new();
    }
}
