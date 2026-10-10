using System.Collections.Concurrent;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed class LocalTranslationException(string category, string message, string diagnostic = "") : InvalidOperationException(message)
{
    public string Category { get; } = category;
    public string Diagnostic { get; } = diagnostic;
}
public sealed record PreparedInferencePrompt(IReadOnlyList<PromptMessage> Messages, string Prompt, int Tokens, int RemovedContextCues);
public sealed record LocalInferenceProgress(IReadOnlyList<string> CueIds, int GeneratedTokens);
public sealed record LocalInferenceEvidence(IReadOnlyList<string> CueIds, int GeneratedTokens, string StopType);
public sealed record LocalCompletion(string Content, int GeneratedTokens, string StopType, bool Truncated);

public interface ILlamaServerSession : IAsyncDisposable
{
    VulkanEvidence? Evidence { get; }
    Task EnsureStartedAsync(CancellationToken token);
    Task<bool> IsReadyAsync(CancellationToken token) => Task.FromResult(false);
    Task EnsureStartedAsync(CancellationToken token, IProgress<LocalPreparationProgress>? progress) => EnsureStartedAsync(token);
    Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token);
    Task<LocalCompletion> CompleteAsync(string prompt, IProgress<int>? progress, CancellationToken token);
}

public sealed class LocalTranslationEngine : ITranslationEngine, IAsyncDisposable
{
    private readonly EmbeddedModelOptions _options;
    private readonly ILlamaServerSession _session;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IProgress<LocalInferenceProgress>? _progress;
    private readonly ConcurrentQueue<LocalInferenceEvidence> _inferences = new();
    private readonly object _disposeSync = new();
    private Task? _disposal;
    public string CacheIdentity { get; }
    public VulkanEvidence? Evidence => _session.Evidence;
    public IReadOnlyCollection<LocalInferenceEvidence> Inferences => _inferences.ToArray();

    public LocalTranslationEngine(EmbeddedModelOptions options, LlamaServerBinary binary, ILlamaServerSession? session = null,
        IProgress<LocalInferenceProgress>? progress = null)
    {
        options.Validate();
        var fileVersion = options.FileVersion ?? ModelFileVersion.Read(options.ModelPath);
        _options = options with { FileVersion = fileVersion };
        _session = session ?? new LlamaServerSession(_options, binary);
        _slots = new(options.Concurrency);
        _progress = progress;
        CacheIdentity = AtomicFile.Hash(new { Provider = "llama.cpp-vulkan-server", Runtime = binary.Identity,
            options.ModelId, FileVersion = fileVersion, options.GpuLayers, options.ContextSize, options.MaximumTokens, options.Concurrency,
            Sampling = "temperature=.7,top_p=.6,top_k=20,repeat=1.05,min_p=0,seed=42", Protocol = "local-completion-file-version-v3" });
    }

    public Task WarmupAsync(CancellationToken token, IProgress<LocalPreparationProgress>? progress = null) => PrepareOperationAsync(token, progress);
    public Task<bool> IsReadyAsync(CancellationToken token) => _session.IsReadyAsync(token);

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _slots.WaitAsync(linked.Token);
        try
        {
            // 冷启动单独计时，等待其他请求不消耗本条的生成预算。
            await _session.EnsureStartedAsync(linked.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(_options.InferenceTimeout);
            try
            {
                var prepared = await _session.PrepareAsync(request, timeout.Token);
                var progress = _progress is null ? null : new Progress<int>(count => _progress.Report(new(request.Cues.Select(cue => cue.Id).ToArray(), count)));
                var completion = await _session.CompleteAsync(prepared.Prompt, progress, timeout.Token);
                _inferences.Enqueue(new(request.Cues.Select(cue => cue.Id).ToArray(), completion.GeneratedTokens, completion.StopType));
                timeout.Token.ThrowIfCancellationRequested();
                if (completion.Truncated || completion.StopType != "eos" || completion.GeneratedTokens <= 0 || string.IsNullOrWhiteSpace(completion.Content))
                    throw new InvalidDataException("本地译文为空或未完整生成，请调整提示词或最大输出长度后重试。");
                return new(completion.Content.Trim());
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            { throw new TimeoutException("本地模型推理超时。"); }
        }
        finally { _slots.Release(); }
    }

    public async Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token)
    {
        ValidateRequest(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _slots.WaitAsync(linked.Token);
        try
        {
            await _session.EnsureStartedAsync(linked.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(_options.InferenceTimeout);
            try { return await _session.PrepareAsync(request, timeout.Token); }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            { throw new TimeoutException("本地模型请求预览超时。"); }
        }
        finally { _slots.Release(); }
    }

    private async Task PrepareOperationAsync(CancellationToken token, IProgress<LocalPreparationProgress>? progress)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _slots.WaitAsync(linked.Token);
        try { await _session.EnsureStartedAsync(linked.Token, progress); }
        finally { _slots.Release(); }
    }

    private static void ValidateRequest(TranslationRequest request)
    {
        if (request.Cues.Count == 0 || (request.PromptContext?.Profile.BatchTranslation != true && request.Cues.Count != 1) || request.Messages.Count == 0 || request.Messages.Any(message => message.Role is not ("system" or "user")))
            throw new ArgumentException("本地模型请求的字幕条数或 system/user 提示词无效。");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync();
        // 等待预览和所有并发请求结束，再关闭服务。
        for (var slot = 0; slot < _options.Concurrency; slot++) await _slots.WaitAsync();
        await _session.DisposeAsync();
    }
}
