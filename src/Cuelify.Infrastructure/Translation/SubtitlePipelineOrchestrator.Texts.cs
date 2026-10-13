using Cuelify.Core.Projects;
using Cuelify.Core.Speech;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Infrastructure.Translation;

public sealed partial class SubtitlePipelineOrchestrator
{
    private async Task<SubtitlePipelineResult> GenerateTextsAsync(IReadOnlyList<WordToken> words, TimeSpan duration,
        SubtitlePipelineProfile profile, TranslationSettings settings, IProgress<TranslationProgress>? progress,
        CancellationToken token, IReadOnlySet<string>? retryBlocks)
    {
        settings.Validate();
        PipelinePromptBuilder.ValidateInput(words, profile);
        var identity = AtomicFile.Hash(new { engine.CacheIdentity, Profile = profile.SegmentationIdentity,
            settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle, Words = words, Duration = duration, Schema = "segmentation-texts-v1" });
        var directory = Path.Combine(cacheRoot, identity); Directory.CreateDirectory(directory);
        await using var jobLock = new FileStream(Path.Combine(directory, "pipeline.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var accepted = new List<SourceWordRange>(); var next = 0; var calls = 0; var hits = 0; var pending = 0;
        const string remaining = "remaining-words";
        void Report(string unit, ProcessingState state, string stage = "Segmenting", int running = 0, int failed = 0) =>
            progress?.Report(new(stage, []) { UnitStates = new Dictionary<string, ProcessingState> { [unit] = state },
                WorkProgress = new(next, words.Count, running, failed, pending) });
        Report(remaining, words.Count == 0 ? ProcessingState.Completed : ProcessingState.NotStarted);
        while (next < words.Count)
        {
            token.ThrowIfCancellationRequested();
            var block = PipelinePromptBuilder.NextBlock(words, next, profile);
            var (processed, ranges) = await Process(block);
            // 最后一句暂不确认；下一块连同后文重新判断，容量缩块也不会冻结临时切点。
            var count = processed.Last == words.Count - 1 || ranges.Count == 1 ? ranges.Count : ranges.Count - 1;
            accepted.AddRange(ranges.Take(count));
            next = accepted[^1].Last + 1;
            pending = count < ranges.Count ? ranges[^1].Last - ranges[^1].First + 1 : 0;
            Report(remaining, next == words.Count ? ProcessingState.Completed : ProcessingState.NotStarted);
        }
        var display = PipelineDisplay.Build(words, accepted, profile, duration);
        var result = new SubtitlePipelineResult(display.Cues, display.Ranges, calls, hits);
        result.ValidateSource(words.Count);
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
        progress?.Report(new("CuesReady", []) { Cues = result.Cues, WorkProgress = new(words.Count, words.Count) });
        return result;

        async Task<(PipelineWordBlock Block, IReadOnlyList<SourceWordRange> Ranges)> Process(PipelineWordBlock block)
        {
            var request = PipelinePromptBuilder.Build(words, block, profile, settings);
            var unit = "block-" + AtomicFile.Hash(new { request.Messages, request.Parameters, request.OutputContract }) + ".json";
            var path = Path.Combine(directory, unit);
            var reduced = await Read<ReducedBlock>(unit + ".capacity.json");
            if (reduced is not null)
            {
                if (reduced.Last < block.First || reduced.Last >= block.Last) throw new InvalidDataException("分句容量记录的词范围无效。");
                Report(unit, ProcessingState.Completed);
                return await Process(PipelinePromptBuilder.CreateBlock(words, block.First, reduced.Last, profile));
            }
            var cached = await Read<CachedStage>(unit);
            if (cached is not null)
            {
                IReadOnlyList<SourceWordRange>? parsed = null;
                try { parsed = request.OutputContract!.ParseRanges(cached.Content); }
                catch (InvalidDataException) { /* 原文和时长同样验收，损坏缓存不当作成功。 */ }
                if (parsed is not null)
                {
                    await AtomicFile.WriteJsonAsync(path, cached, CancellationToken.None);
                    if (File.Exists(path + ".failed")) File.Delete(path + ".failed");
                    hits++; Report(unit, ProcessingState.Completed); return (block, parsed);
                }
            }
            if (retryBlocks is not null && !retryBlocks.Contains(unit))
                throw new InvalidOperationException("还有未处理的分句块，请选择继续未完成部分。");
            Report(remaining, block.Last == words.Count - 1 ? ProcessingState.Completed : ProcessingState.NotStarted);
            Report(unit, ProcessingState.Running, running: 1);
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        calls++;
                        var response = await engine.TranslateAsync(request, token);
                        var parsed = request.OutputContract!.ParseRanges(response.Content);
                        await AtomicFile.WriteJsonAsync(path, new CachedStage(response.Content), CancellationToken.None);
                        if (File.Exists(path + ".failed")) File.Delete(path + ".failed");
                        Report(unit, ProcessingState.Completed); return (block, parsed);
                    }
                    catch (PromptCapacityException) when (block.First < block.Last)
                    {
                        var last = block.First + (block.Last - block.First) / 2;
                        await AtomicFile.WriteJsonAsync(path + ".capacity.json", new ReducedBlock(last), CancellationToken.None);
                        Report(unit, ProcessingState.Completed);
                        return await Process(PipelinePromptBuilder.CreateBlock(words, block.First, last, profile));
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && attempt < settings.MaximumAttempts &&
                        (e is InvalidDataException or HttpRequestException or TimeoutException || e is TranslationServiceException { IsTransient: true }))
                    {
                        var delay = RetryDelay.Calculate(settings.RetryDelay, settings.MaximumRetryDelay, attempt,
                            (e as TranslationServiceException)?.RetryAfter, exponential: false);
                        if (delay is null) throw;
                        if (e is InvalidDataException) request = request with { Messages = request.Messages.Append(new PromptMessage("user",
                            "上次结果未通过校验：" + e.Message + "\n请逐字复制本块原文，不能增删标点；时间跨度包括停顿，返回完整 texts JSON。")).ToArray() };
                        Report(unit, ProcessingState.Running, "RetryWaiting", running: 1);
                        await Task.Delay(delay.Value, token);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { if (!File.Exists(path + ".capacity.json")) Report(unit, ProcessingState.Cancelled); throw; }
            catch (Exception)
            {
                // 容量记录仅描述缩块，真正失败归最小请求，避免父块污染失败重试名单。
                if (!File.Exists(path + ".capacity.json"))
                {
                    await AtomicFile.WriteTextAsync(path + ".failed", "阶段处理失败", true, CancellationToken.None);
                    Report(unit, ProcessingState.Failed, failed: 1);
                }
                throw;
            }
        }
        async Task<T?> Read<T>(string name) where T : class
        {
            var cached = await AtomicFile.ReadJsonAsync<T>(Path.Combine(directory, name), token);
            if (cached is null && fallbackCacheRoot is not null)
                cached = await AtomicFile.ReadJsonAsync<T>(Path.Combine(fallbackCacheRoot, identity, name), token);
            return cached;
        }
    }
    private sealed record ReducedBlock(int Last);
}
