namespace DBImplant_N1toN2.Features.Migration.Models
{
    public record MigrationProgress(int CurrentStep, int TotalSteps, string StepName, string? Detail = null);
}
