namespace DBImplant_N1toN2.Features.Migration.Models
{
    public record MigrationLogEntry(DateTime Timestamp, string Level, string Message);
}
