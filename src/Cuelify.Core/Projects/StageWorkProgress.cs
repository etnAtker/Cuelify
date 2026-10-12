namespace Cuelify.Core.Projects;

public sealed record StageWorkProgress(long Completed, long Total, int Running = 0, int Failed = 0, int PendingWords = 0)
{
    public string Unit { get; init; } = "";
    public bool ShowWaiting { get; init; }
    public double Percent => Total > 0 ? Math.Clamp(100d * Completed / Total, 0, 100) : 0;
}
