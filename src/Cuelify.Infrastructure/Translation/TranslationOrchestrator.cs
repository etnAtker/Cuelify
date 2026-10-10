using System.Collections.Concurrent;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation;

public sealed record TranslationProgress(string Stage, IReadOnlyList<string> CueIds)
{
    public IReadOnlyList<SubtitleCue>? Cues { get; init; }
    public IReadOnlyList<string>? FailedIds { get; init; }
}
public sealed class TranslationOrchestrator(ITranslationEngine engine, string? cacheRoot = null)
{
    private readonly string _cacheRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify", "Translations");

    public async Task<TranslationResult> TranslateAsync(IReadOnlyList<SubtitleCue> cues, PromptProfile profile, TranslationSettings settings,
        IReadOnlySet<string>? forceCueIds = null, IProgress<TranslationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        PromptBuilder.Validate(profile);
        settings = settings with { BatchSize = profile.BatchTranslation ? profile.BatchSize : 1,
            MaximumBatchCharacters = profile.BatchTranslation ? profile.MaximumBatchCharacters : 50000 };
        settings.Validate();
        // 已显示的旧译文不属于新请求输入，也不能改变缓存键或混入其他语言上下文。
        cues = cues.Select(cue => cue with { TranslatedText = null }).ToArray();
        _ = SrtSerializer.SerializeSource(cues); // 付费前校验输入 ID、顺序及时间戳。
        var indices = cues.Select((cue, index) => (cue.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        if (forceCueIds?.Any(id => !indices.ContainsKey(id)) == true) throw new ArgumentException("定向重翻包含未知 cue ID。");
        var identity = AtomicFile.Hash(new { engine.CacheIdentity, Prompt = profile.ExecutionIdentity, settings.BatchSize, settings.MaximumBatchCharacters, settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle,
            Input = cues.Select(cue => new { cue.Id, cue.Start, cue.End, cue.SourceText }).ToArray(), Schema = "translation-v2" });
        var directory = Path.Combine(_cacheRoot, identity);
        Directory.CreateDirectory(directory);
        await using var jobLock = new FileStream(Path.Combine(directory, "translation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var translations = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        int calls = 0, hits = 0;
        var builder = new PromptBuilder();
        var batches = Plan(cues, settings);
        if (forceCueIds?.Count == 0) forceCueIds = null;
        try
        {
            if (forceCueIds is not null)
            {
                var previous = await AtomicFile.ReadJsonAsync<TranslationResult>(Path.Combine(directory, "result.json"), cancellationToken)
                    ?? throw new InvalidOperationException("定向重翻需要当前翻译配置的已有结果；首次处理请执行正常翻译。");
                if (previous.Cues is null || previous.Cues.Count != cues.Count || previous.Cues.Where((cue, index) =>
                    cue.Id != cues[index].Id || cue.Start != cues[index].Start || cue.End != cues[index].End || cue.SourceText != cues[index].SourceText).Any())
                    throw new InvalidDataException("定向重翻的原有结果与输入字幕不一致。");
                foreach (var cue in previous.Cues.Where(cue => !forceCueIds.Contains(cue.Id)))
                {
                    if (cue.TranslatedText is not null && AlignmentValidator.IsValid(cue.TranslatedText))
                    { translations[cue.Id] = cue.TranslatedText; hits++; }
                    else failures[cue.Id] = "原有译文未完成，本次未选中重翻";
                }
                // 失效所有请求版本中的选中 ID，防止重翻失败/取消后恢复到旧成功值。
                foreach (var cachePath in Directory.EnumerateFiles(directory, "batch-*.json"))
                {
                    var cache = await ReadCache(cachePath, cues);
                    if (cache.Keys.Any(forceCueIds.Contains))
                    {
                        foreach (var id in forceCueIds) cache.Remove(id);
                        await AtomicFile.WriteJsonAsync(cachePath, new CachedTranslations(cache), CancellationToken.None);
                    }
                }
            }
            // 以固定批次波次并行；上下文取该波次开始前的快照，避免线程完成顺序改变提示词/缓存键。
            for (var offset = 0; offset < batches.Count; offset += settings.Concurrency)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = cues.Select(cue => cue with { TranslatedText = translations.GetValueOrDefault(cue.Id) }).ToArray();
                await Task.WhenAll(batches.Skip(offset).Take(settings.Concurrency).Select(batch => ProcessAndReport(batch, snapshot)));
                await Status("Translating", cancellationToken);
            }
            var result = new TranslationResult(cues.Select(cue => cue with { TranslatedText = translations.GetValueOrDefault(cue.Id) }).ToArray(),
                cues.Where(cue => !translations.ContainsKey(cue.Id)).Select(cue => cue.Id).ToArray(), calls, hits)
            { FailureReasons = failures.Where(item => !translations.ContainsKey(item.Key)).ToDictionary(item => item.Key, item => item.Value) };
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
            await Status(result.IsComplete ? "Completed" : "PartialFailure", CancellationToken.None);
            return result;
        }
        catch (Exception exception)
        {
            failures["job"] = Category(exception);
            await Status(cancellationToken.IsCancellationRequested ? "Cancelled" : "Failed", CancellationToken.None);
            throw;
        }

        IReadOnlyList<SubtitleCue> Context(SubtitleCue cue, IReadOnlyList<SubtitleCue> snapshot) =>
            snapshot.Take(indices[cue.Id]).TakeLast(settings.ContextCues).ToArray();
        IReadOnlyList<SubtitleCue> Following(IReadOnlyList<SubtitleCue> batch, IReadOnlyList<SubtitleCue> snapshot) =>
            snapshot.Skip(indices[batch[^1].Id] + 1).Take(settings.FollowingContextCues).Select(cue => cue with { TranslatedText = null }).ToArray();
        string RequestPath(TranslationRequest request) => Path.Combine(directory, "batch-" + AtomicFile.Hash(new { request.Cues, request.Messages }) + ".json");
        bool Forced(string id) => forceCueIds?.Contains(id) == true;

        async Task ProcessAndReport(IReadOnlyList<SubtitleCue> batch, IReadOnlyList<SubtitleCue> snapshot)
        {
            try { await ProcessBatch(batch, snapshot); }
            finally
            {
                progress?.Report(new("Translating", [])
                {
                    Cues = cues.Select(cue => cue with { TranslatedText = translations.GetValueOrDefault(cue.Id) }).ToArray(),
                    FailedIds = failures.Keys.Where(id => !translations.ContainsKey(id)).ToArray()
                });
            }
        }

        async Task ProcessBatch(IReadOnlyList<SubtitleCue> batch, IReadOnlyList<SubtitleCue> snapshot)
        {
            if (forceCueIds is not null) batch = batch.Where(cue => Forced(cue.Id)).ToArray();
            if (batch.Count == 0) return;
            var request = builder.Build(profile, settings, batch, Context(batch[0], snapshot), Following(batch, snapshot));
            var path = RequestPath(request);
            var accepted = await ReadCache(path, batch);
            foreach (var cue in batch.Where(cue => Forced(cue.Id))) accepted.Remove(cue.Id);
            foreach (var item in accepted) translations[item.Key] = item.Value;
            Interlocked.Add(ref hits, accepted.Count);
            if (accepted.Count == batch.Count) return;
            var pending = batch.Where(cue => !accepted.ContainsKey(cue.Id)).ToArray();
            // 有有效缓存或显式定向重翻时，不再发送整批，避免重付已完成条目。
            if (accepted.Count == 0 && forceCueIds is null)
            {
                try
                {
                    progress?.Report(new("Translating", batch.Select(cue => cue.Id).ToArray()));
                    var response = await SendWithRetry(request);
                    var aligned = AlignmentValidator.Parse(response.Content, batch, profile.OutputFormat);
                    foreach (var item in aligned.Translations) { accepted[item.Key] = item.Value; translations[item.Key] = item.Value; }
                    await AtomicFile.WriteJsonAsync(path, new CachedTranslations(accepted), CancellationToken.None);
                    pending = batch.Where(cue => !accepted.ContainsKey(cue.Id)).ToArray();
                    // 本地/云端单条直接补翻剩余尝试，不重复无意义的批次降级。
                    if (batch.Count == 1 && pending.Length == 1)
                    {
                        await TranslateOne(pending[0], snapshot, Math.Max(0, settings.MaximumAttempts - 1));
                        SaveRecovered(pending[0]);
                        await AtomicFile.WriteJsonAsync(path, new CachedTranslations(accepted), CancellationToken.None);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is TranslationServiceException or HttpRequestException or TimeoutException or InvalidOperationException)
                {
                    foreach (var cue in pending) failures[cue.Id] = Category(exception);
                    return;
                }
                catch (Local.PromptCapacityException) when (batch.Count > 1)
                {
                    var middle = batch.Count / 2;
                    await ProcessBatch(batch.Take(middle).ToArray(), snapshot);
                    await ProcessBatch(batch.Skip(middle).ToArray(), snapshot);
                    return;
                }
                catch (Local.PromptCapacityException)
                { foreach (var cue in pending) failures[cue.Id] = "本地上下文不足"; return; }
                catch (InvalidDataException) { /* 结构/截断错误按 cue 缩小请求重新验证。 */ }
            }
            foreach (var cue in pending)
            {
                var current = snapshot.Select(item => item with { TranslatedText = translations.GetValueOrDefault(item.Id) ?? item.TranslatedText }).ToArray();
                await TranslateOne(cue, current, settings.MaximumAttempts);
                SaveRecovered(cue);
                await AtomicFile.WriteJsonAsync(path, new CachedTranslations(accepted), CancellationToken.None);
            }

            void SaveRecovered(SubtitleCue cue)
            {
                if (translations.TryGetValue(cue.Id, out var text)) accepted[cue.Id] = text;
            }
        }

        async Task TranslateOne(SubtitleCue cue, IReadOnlyList<SubtitleCue> snapshot, int attempts)
        {
            var request = builder.Build(profile, settings, [cue], Context(cue, snapshot), Following([cue], snapshot));
            var path = RequestPath(request);
            if (!Forced(cue.Id))
            {
                var cache = await ReadCache(path, [cue]);
                if (cache.TryGetValue(cue.Id, out var text)) { translations[cue.Id] = text; Interlocked.Increment(ref hits); return; }
            }
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new("TargetedRetry", [cue.Id]));
                    // 单条失败最多 attempts 次；transport 暂时失败也占此预算。
                    Interlocked.Increment(ref calls);
                    var response = await engine.TranslateAsync(request, cancellationToken);
                    var aligned = AlignmentValidator.Parse(response.Content, [cue], profile.OutputFormat);
                    if (aligned.Translations.TryGetValue(cue.Id, out var text))
                    {
                        await AtomicFile.WriteJsonAsync(path, new CachedTranslations(new Dictionary<string, string> { [cue.Id] = text }), CancellationToken.None);
                        translations[cue.Id] = text;
                        failures.TryRemove(cue.Id, out _);
                        return;
                    }
                    failures[cue.Id] = "译文或 ID 校验失败";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failures[cue.Id] = Category(exception);
                    if (!Transient(exception) && exception is not InvalidDataException) return;
                    if (attempt + 1 < attempts && !await Delay(exception, attempt + 1)) return;
                }
            }
            failures.TryAdd(cue.Id, "译文或 ID 校验失败");
        }

        async Task<TranslationResponse> SendWithRetry(TranslationRequest request)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { Interlocked.Increment(ref calls); return await engine.TranslateAsync(request, cancellationToken); }
                catch (Exception exception) when (Transient(exception) && !cancellationToken.IsCancellationRequested && attempt < settings.MaximumAttempts)
                {
                    if (!await Delay(exception, attempt)) throw;
                }
            }
        }
        async Task<bool> Delay(Exception exception, int attempt)
        {
            var delay = TimeSpan.FromTicks((long)Math.Min(settings.MaximumRetryDelay.Ticks, settings.RetryDelay.Ticks * Math.Pow(2, attempt - 1)));
            if (exception is TranslationServiceException { RetryAfter: { } retryAfter } && retryAfter > delay) delay = retryAfter;
            if (delay > settings.MaximumRetryDelay) return false;
            progress?.Report(new("RetryWaiting", []));
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        async Task<Dictionary<string, string>> ReadCache(string path, IReadOnlyList<SubtitleCue> batch)
        {
            var cached = await AtomicFile.ReadJsonAsync<CachedTranslations>(path, cancellationToken);
            if (cached is null) return new(StringComparer.Ordinal);
            var expected = batch.ToDictionary(cue => cue.Id, StringComparer.Ordinal);
            if (cached.Translations is null || cached.Translations.Any(item => !expected.ContainsKey(item.Key) || !AlignmentValidator.IsValid(item.Value)))
                throw new InvalidDataException("翻译缓存内容无效，请检查该批次缓存。");
            return new(cached.Translations, StringComparer.Ordinal);
        }
        Task Status(string stage, CancellationToken token) => AtomicFile.WriteJsonAsync(Path.Combine(directory, "status.json"),
            new { Stage = stage, EngineCalls = calls, CacheHits = hits, Failures = failures.OrderBy(item => item.Key).ToArray() }, token);
    }

    public static IReadOnlyList<IReadOnlyList<SubtitleCue>> Plan(IReadOnlyList<SubtitleCue> cues, TranslationSettings settings)
    {
        settings.Validate();
        var result = new List<IReadOnlyList<SubtitleCue>>();
        var batch = new List<SubtitleCue>();
        var characters = 0;
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var length = cue.Id.Length + cue.SourceText.Length + 8;
            if (length > settings.MaximumBatchCharacters) throw new ArgumentException($"第 {index + 1} 条字幕过长，请调大“批次字符上限”。");
            if (batch.Count > 0 && (batch.Count >= settings.BatchSize || characters + length > settings.MaximumBatchCharacters))
            { result.Add(batch); batch = []; characters = 0; }
            batch.Add(cue);
            characters += length;
        }
        if (batch.Count > 0) result.Add(batch);
        return result;
    }
    private static bool Transient(Exception exception) => exception is TranslationServiceException { IsTransient: true } or HttpRequestException or TimeoutException;
    private static string Category(Exception exception) => exception switch
    {
        ServiceCredentialException credential => credential.Message,
        Local.LocalTranslationException local => $"Local:{local.Category}",
        TranslationServiceException service => $"HTTP {(int)service.StatusCode}", InvalidDataException => "译文或缓存校验失败",
        Local.PromptCapacityException => "本地上下文不足",
        InvalidOperationException => "配置或凭据无效", HttpRequestException => "网络失败", TimeoutException => "请求超时",
        OperationCanceledException => "取消", _ => "翻译处理失败"
    };
    private sealed record CachedTranslations(Dictionary<string, string> Translations);
}
