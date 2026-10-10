using System.Diagnostics;

namespace Cuelify.Infrastructure.Translation.Local;

public enum LocalPreparationStage { CheckingRuntime, StartingService, LoadingModel, CheckingService, Ready, ReusingService, TestingTranslation }

public sealed record LocalPreparationProgress(LocalPreparationStage Stage)
{
    public long Timestamp { get; } = Stopwatch.GetTimestamp();
}
