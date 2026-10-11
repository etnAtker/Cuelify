using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Infrastructure.Translation;

public sealed class SubtitlePipelineOrchestrator(ITranslationEngine engine, string cacheRoot)
{
    public async Task<SubtitlePipelineResult> GenerateAsync(IReadOnlyList<WordToken> words, IReadOnlyList<SubtitleCue> originalCues,
        TimeSpan duration, SubtitlePipelineProfile profile, TranslationSettings settings,
        IProgress<TranslationProgress>? progress = null, CancellationToken token = default)
    {
        profile.Validate(); settings.Validate();
        if (profile.Mode == SubtitlePipelineMode.DirectTranslation) return new(originalCues, [], 0, 0);
        var blocks = PipelinePromptBuilder.Plan(words, profile);
        var identity = AtomicFile.Hash(new { engine.CacheIdentity, Profile = profile.ExecutionIdentity,
            settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle, Words = words, Duration = duration, Schema = "subtitle-pipeline-v1" });
        var directory = Path.Combine(cacheRoot, identity); Directory.CreateDirectory(directory);
        await using var jobLock = new FileStream(Path.Combine(directory, "pipeline.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var results = new IReadOnlyList<(SourceWordRange Range, string? Translation)>[blocks.Count];
        int calls = 0, hits = 0;
        for (var offset = 0; offset < blocks.Count; offset += settings.Concurrency)
        {
            token.ThrowIfCancellationRequested();
            await Task.WhenAll(Enumerable.Range(offset, Math.Min(settings.Concurrency, blocks.Count - offset)).Select(async i =>
                results[i] = await Process(blocks[i])));
        }
        var (cues, ranges) = PipelineDisplay.Build(words, results.SelectMany(r => r).ToArray(), profile, duration);
        _ = SrtSerializer.SerializeSource(cues);
        var result = new SubtitlePipelineResult(cues, ranges, calls, hits);
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
        progress?.Report(new("CuesReady", []) { Cues = cues });
        return result;

        async Task<IReadOnlyList<(SourceWordRange Range, string? Translation)>> Process(PipelineWordBlock block)
        {
            var request = PipelinePromptBuilder.Build(words, block, profile, settings);
            var path = Path.Combine(directory, "block-" + AtomicFile.Hash(new { request.Messages, request.Parameters, request.Grammar }) + ".json");
            var cached = await AtomicFile.ReadJsonAsync<CachedStage>(path, token);
            if (cached is not null)
            {
                try { var parsed = PipelinePromptBuilder.Parse(cached.Content, words, block, profile); Interlocked.Increment(ref hits); return parsed; }
                catch (InvalidDataException) { /* 缓存同样需要通过完整覆盖校验。 */ }
            }
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(profile.Mode == SubtitlePipelineMode.SegmentThenTranslate ? "Segmenting" : "JointTranslating", []));
                try
                {
                    Interlocked.Increment(ref calls);
                    var response = await engine.TranslateAsync(request, token);
                    var parsed = PipelinePromptBuilder.Parse(response.Content, words, block, profile);
                    await AtomicFile.WriteJsonAsync(path, new CachedStage(response.Content), CancellationToken.None);
                    return parsed;
                }
                catch (PromptCapacityException) when (block.Boundaries.Count > 1)
                {
                    // 按已有自然候选拆小请求，继续复用成功的子块；不丢字或强切词。
                    var end = block.Boundaries[(block.Boundaries.Count - 1) / 2];
                    var left = block with { Last = end, Boundaries = block.Boundaries.Where(b => b <= end).ToArray() };
                    var right = block with { First = end + 1, Boundaries = block.Boundaries.Where(b => b > end).ToArray() };
                    return (await Process(left)).Concat(await Process(right)).ToArray();
                }
                catch (Exception e) when (!token.IsCancellationRequested && attempt < settings.MaximumAttempts &&
                    (e is InvalidDataException or HttpRequestException or TimeoutException || e is TranslationServiceException { IsTransient: true }))
                {
                    if (e is InvalidDataException)
                        request = request with { Messages = request.Messages.Append(new PromptMessage("user",
                            "上次结果未通过校验：" + e.Message + "\n请重新处理同一块源文，严格遵循映射和格式，返回完整 JSON。")).ToArray() };
                    progress?.Report(new("RetryWaiting", []));
                    var delay = e is TranslationServiceException { RetryAfter: { } wait } ? wait : settings.RetryDelay;
                    if (delay > settings.MaximumRetryDelay) throw;
                    await Task.Delay(delay, token);
                }
            }
        }
    }
    private sealed record CachedStage(string Content);
}
