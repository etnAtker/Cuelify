using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Core.Projects;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using System.Collections.Concurrent;

namespace Cuelify.Infrastructure.Translation;

public sealed partial class SubtitlePipelineOrchestrator(ITranslationEngine engine, string cacheRoot, string? fallbackCacheRoot = null)
{
    public Task<SubtitlePipelineResult> GenerateAsync(IReadOnlyList<WordToken> words, IReadOnlyList<SubtitleCue> originalCues,
        TimeSpan duration, SubtitlePipelineProfile profile, TranslationSettings settings,
        IProgress<TranslationProgress>? progress = null, CancellationToken token = default, IReadOnlySet<string>? retryBlocks = null) =>
        profile.Method == SegmentationMethod.Model && profile.SegmentationFormat == SegmentationOutputFormat.SourceTexts
            ? GenerateTextsAsync(words, duration, profile, settings, progress, token, retryBlocks)
            : GenerateLegacyAsync(words, originalCues, duration, profile, settings, progress, token, retryBlocks);

    private async Task<SubtitlePipelineResult> GenerateLegacyAsync(IReadOnlyList<WordToken> words, IReadOnlyList<SubtitleCue> originalCues,
        TimeSpan duration, SubtitlePipelineProfile profile, TranslationSettings settings,
        IProgress<TranslationProgress>? progress = null, CancellationToken token = default, IReadOnlySet<string>? retryBlocks = null)
    {
        if (profile.Method == SegmentationMethod.Program)
        {
            token.ThrowIfCancellationRequested(); progress?.Report(new("BuildingCues", []));
            var generated = ProgramSegmentation.Build(words, duration, profile);
            progress?.Report(new("CuesReady", []) { Cues = generated.Cues, WorkProgress = new(words.Count, words.Count) { Unit = "词" } });
            return generated;
        }
        settings.Validate();
        var blocks = PipelinePromptBuilder.Plan(words, profile);
        var identity = AtomicFile.Hash(new { engine.CacheIdentity, Profile = profile.SegmentationIdentity,
            settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle, Words = words, Duration = duration, Schema = "segmentation-v3" });
        var directory = Path.Combine(cacheRoot, identity); Directory.CreateDirectory(directory);
        await using var jobLock = new FileStream(Path.Combine(directory, "pipeline.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var results = new IReadOnlyList<SourceWordRange>[blocks.Count];
        var progressGate = new object(); var completedWords = 0;
        var processing = "Segmenting";
        var requests = new ConcurrentDictionary<PipelineWordBlock, TranslationRequest>();
        var unitIds = new ConcurrentDictionary<PipelineWordBlock, string>();
        TranslationRequest Request(PipelineWordBlock block) => requests.GetOrAdd(block, b => PipelinePromptBuilder.Build(words, b, profile, settings));
        string UnitId(PipelineWordBlock block)
        {
            return unitIds.GetOrAdd(block, b =>
            {
                var request = Request(b);
                return "block-" + AtomicFile.Hash(new { request.Messages, request.Parameters, request.OutputContract }) + ".json";
            });
        }
        void State(PipelineWordBlock block, ProcessingState state) => progress?.Report(new(processing, [])
        { UnitStates = new Dictionary<string, ProcessingState> { [UnitId(block)] = state } });
        progress?.Report(new(processing, []) { UnitStates = blocks.ToDictionary(UnitId, _ => ProcessingState.NotStarted) });
        int calls = 0, hits = 0;
        for (var offset = 0; offset < blocks.Count; offset += settings.Concurrency)
        {
            token.ThrowIfCancellationRequested();
            await Task.WhenAll(Enumerable.Range(offset, Math.Min(settings.Concurrency, blocks.Count - offset)).Select(async i =>
            {
                results[i] = await Process(blocks[i]);
                lock (progressGate)
                {
                    completedWords += blocks[i].Last - blocks[i].First + 1;
                    progress?.Report(new("Segmenting", []) { WorkProgress = new(completedWords, words.Count) });
                }
            }));
        }
        var (cues, ranges) = PipelineDisplay.Build(words, results.SelectMany(r => r).ToArray(), profile, duration);
        _ = SrtSerializer.SerializeSource(cues);
        var result = new SubtitlePipelineResult(cues, ranges, calls, hits);
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
        progress?.Report(new("CuesReady", []) { Cues = cues, WorkProgress = new(words.Count, words.Count) });
        return result;

        async Task<IReadOnlyList<SourceWordRange>> Process(PipelineWordBlock block)
        {
            var request = Request(block);
            var path = Path.Combine(directory, UnitId(block));
            var split = await AtomicFile.ReadJsonAsync<SplitStage>(path + ".split.json", token);
            if (split is null && fallbackCacheRoot is not null)
                split = await AtomicFile.ReadJsonAsync<SplitStage>(Path.Combine(fallbackCacheRoot, identity, Path.GetFileName(path) + ".split.json"), token);
            if (split is not null)
            {
                PlanSplit(block, split.Left, split.Right);
                return (await Process(split.Left)).Concat(await Process(split.Right)).ToArray();
            }
            var cached = await AtomicFile.ReadJsonAsync<CachedStage>(path, token);
            if (cached is null && fallbackCacheRoot is not null)
                cached = await AtomicFile.ReadJsonAsync<CachedStage>(Path.Combine(fallbackCacheRoot, identity, Path.GetFileName(path)), token);
            if (cached is not null)
            {
                try
                {
                    var parsed = request.OutputContract!.ParseRanges(cached.Content);
                    await AtomicFile.WriteJsonAsync(path, cached, CancellationToken.None);
                    if (File.Exists(path + ".failed")) File.Delete(path + ".failed");
                    Interlocked.Increment(ref hits); State(block, ProcessingState.Completed); return parsed;
                }
                catch (InvalidDataException) { /* 缓存同样需要通过完整覆盖校验。 */ }
            }
            var failedPath = path + ".failed";
            if (retryBlocks is not null && !retryBlocks.Contains(Path.GetFileName(path)))
                throw new InvalidOperationException("还有未处理的分句块，请选择继续未完成部分。");
            State(block, ProcessingState.Running);
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("Segmenting", []));
                try
                {
                    Interlocked.Increment(ref calls);
                    var response = await engine.TranslateAsync(request, token);
                    var parsed = request.OutputContract!.ParseRanges(response.Content);
                    await AtomicFile.WriteJsonAsync(path, new CachedStage(response.Content), CancellationToken.None);
                    if (File.Exists(failedPath)) File.Delete(failedPath);
                    State(block, ProcessingState.Completed); return parsed;
                }
                catch (PromptCapacityException) when (block.Boundaries.Count > 1)
                {
                    // 按原词范围拆小请求，继续复用成功的子块。
                    var end = block.Boundaries[(block.Boundaries.Count - 1) / 2];
                    var left = PipelinePromptBuilder.CreateBlock(words, block.First, end, profile);
                    var right = PipelinePromptBuilder.CreateBlock(words, end + 1, block.Last, profile);
                    await AtomicFile.WriteJsonAsync(path + ".split.json", new SplitStage(left, right), CancellationToken.None);
                    PlanSplit(block, left, right);
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
                    if (delay > settings.MaximumRetryDelay)
                    {
                        await AtomicFile.WriteTextAsync(failedPath, "阶段处理失败", true, CancellationToken.None);
                        State(block, ProcessingState.Failed);
                        throw;
                    }
                    await Task.Delay(delay, token);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    await AtomicFile.WriteTextAsync(failedPath, "阶段处理失败", true, CancellationToken.None);
                    State(block, ProcessingState.Failed);
                    throw;
                }
            }
        }
        void PlanSplit(PipelineWordBlock parent, PipelineWordBlock left, PipelineWordBlock right) => progress?.Report(new(processing, [])
        { UnitStates = new Dictionary<string, ProcessingState> { [UnitId(parent)] = ProcessingState.Completed,
            [UnitId(left)] = ProcessingState.NotStarted, [UnitId(right)] = ProcessingState.NotStarted } });
    }
    private sealed record CachedStage(string Content);
    private sealed record SplitStage(PipelineWordBlock Left, PipelineWordBlock Right);
}
