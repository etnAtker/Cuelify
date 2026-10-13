using Cuelify.Core.Speech;
using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation;

public sealed partial class SubtitlePipelineOrchestrator(ITranslationEngine engine, string cacheRoot, string? fallbackCacheRoot = null)
{
    public Task<SubtitlePipelineResult> GenerateAsync(IReadOnlyList<WordToken> words, TimeSpan duration,
        SubtitlePipelineProfile profile, TranslationSettings settings, IProgress<TranslationProgress>? progress = null,
        CancellationToken token = default, IReadOnlySet<string>? retryBlocks = null)
    {
        token.ThrowIfCancellationRequested();
        profile.ValidateSegmentation();
        if (profile.Method == SegmentationMethod.Model)
            return GenerateTextsAsync(words, duration, profile, settings, progress, token, retryBlocks);
        progress?.Report(new("BuildingCues", []));
        var result = ProgramSegmentation.Build(words, duration, profile);
        progress?.Report(new("CuesReady", []) { Cues = result.Cues, WorkProgress = new(words.Count, words.Count) { Unit = "词" } });
        return Task.FromResult(result);
    }
    private sealed record CachedStage(string Content);
}
