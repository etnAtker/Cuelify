using System.Runtime.InteropServices;
using System.Text;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using LLama;
using LLama.Common;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record LocalInferenceProgress(string CueId, int GeneratedTokens);
public sealed record LocalInferenceEvidence(string CueId, int GeneratedTokens, bool EndOfGeneration, bool ContextDisposed);
public sealed class LocalTranslationException(string category, string message) : InvalidOperationException(message)
{
    public string Category { get; } = category;
}

public sealed class HyMt2TranslationEngine : ITranslationEngine, IAsyncDisposable
{
    private readonly HyMt2ModelOptions _options;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IProgress<LocalInferenceProgress>? _progress;
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private ModelParams? _parameters;
    private bool _disposed;
    public TranslationOutputFormat OutputFormat => TranslationOutputFormat.PlainText;
    public string CacheIdentity { get; }
    public VulkanEvidence? Evidence { get; private set; }
    public int ModelLoads { get; private set; }
    public bool ModelDisposed { get; private set; }
    public bool CancellationObserved { get; private set; }
    public bool ContextsDisposed { get; private set; } = true;
    public List<LocalInferenceEvidence> Inferences { get; } = [];
    public HyMt2TranslationEngine(HyMt2ModelOptions options, IProgress<LocalInferenceProgress>? progress = null)
    {
        options.Validate();
        _options = options with { ModelPath = Path.GetFullPath(options.ModelPath) };
        _progress = progress;
        CacheIdentity = AtomicFile.Hash(new { Provider = "llamasharp-vulkan-hymt2", HyMt2ModelOptions.Sha256,
            options.GpuLayers, options.ContextSize, options.MaximumTokens, Sampling = "temperature=.7,top_p=.6,top_k=20,repeat=1.05,seed=42", Version = "0.27.0-v1" });
    }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (request.Cues.Count != 1 || request.Messages.Count == 0 || request.Messages.Any(message => message.Role is not ("system" or "user")))
            throw new ArgumentException("Hy-MT2 仅接受单条字幕的 system/user 提示词。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _lifetime.IsCancellationRequested, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_options.InferenceTimeout);
            return await Task.Run(async () =>
            {
                await LoadAsync(timeout.Token);
                var template = new LLamaTemplate(_weights!, strict: true) { AddAssistant = true };
                foreach (var message in request.Messages) template.Add(message.Role, message.Content);
                var prompt = Encoding.UTF8.GetString(template.Apply());
                var promptTokens = _weights!.Tokenize(prompt, add_bos: true, special: true, Encoding.UTF8);
                if (promptTokens.Length + _options.MaximumTokens > _options.ContextSize)
                    throw new InvalidDataException("提示词和参考前文过长，请减少参考前文条数，或调大上下文长度。");
                using var sampling = new TranslationSamplingPipeline();
                var output = new StringBuilder();
                try
                {
                    await foreach (var fragment in _executor!.InferAsync(prompt,
                        new InferenceParams { MaxTokens = _options.MaximumTokens, SamplingPipeline = sampling }, timeout.Token))
                    {
                        output.Append(fragment);
                        _progress?.Report(new(request.Cues[0].Id, sampling.GeneratedTokens));
                    }
                    timeout.Token.ThrowIfCancellationRequested();
                }
                finally
                {
                    var closed = _executor!.Context.NativeHandle.IsClosed;
                    ContextsDisposed &= closed;
                    Inferences.Add(new(request.Cues[0].Id, sampling.GeneratedTokens, sampling.EndOfGenerationObserved, closed));
                }
                if (!sampling.EndOfGenerationObserved || !ContextsDisposed || string.IsNullOrWhiteSpace(output.ToString()))
                    throw new InvalidDataException("本地译文为空或未完整生成，请调整提示词或最大输出长度后重试。");
                return new TranslationResponse(output.ToString().Trim());
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        { CancellationObserved = true; throw; }
        catch (OperationCanceledException) { throw new TimeoutException("Hy-MT2 Vulkan 推理超时。"); }
        catch (Exception exception) when (exception is not (LocalTranslationException or InvalidDataException or ArgumentException or TimeoutException or ObjectDisposedException))
        {
            var diagnostic = exception.Message + "\n" + VulkanRuntime.Snapshot();
            var category = exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ? "NativeLibrary" :
                diagnostic.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.Ordinal) ? "GpuMemory" :
                diagnostic.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase) ? "ModelArchitecture" : "VulkanOrInference";
            throw new LocalTranslationException(category, $"Hy-MT2 本地翻译不可用：{category}。请检查 Vulkan 驱动、原生 DLL、模型架构或显存；不自动回退。");
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null) return;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new LocalTranslationException("WindowsX64Required", "本地翻译仅支持 Windows x64。");
        try { await HyMt2ModelOptions.VerifyIdentityAsync(_options.ModelPath, cancellationToken); }
        catch (Exception exception) when (exception is IOException)
        { throw new LocalTranslationException("ModelIdentity", "指定 GGUF 缺失或 SHA-256/文件名不正确。"); }
        VulkanRuntime.Initialize();
        _parameters = new ModelParams(_options.ModelPath) { GpuLayerCount = _options.GpuLayers, ContextSize = _options.ContextSize, MainGpu = 0 };
        var weights = LLamaWeights.LoadFromFile(_parameters);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (weights.Metadata.GetValueOrDefault("general.architecture") != "hunyuan-dense")
                throw new LocalTranslationException("ModelArchitecture", "模型架构与指定 Hy-MT2 不一致。");
            _ = weights.NativeHandle.GetTemplate(null, strict: true);
            Evidence = VulkanEvidence.Parse(VulkanRuntime.Snapshot());
            if (!Evidence.HasGpuOffload) throw new LocalTranslationException("VulkanOffload", "缺少真实 Vulkan GPU 层卸载证据，拒绝继续生成。");
            _executor = new(weights, _parameters) { ApplyTemplate = false };
            _weights = weights;
            ModelLoads++;
        }
        catch { weights.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _weights?.Dispose();
            ModelDisposed = _weights is null || _weights.NativeHandle.IsClosed;
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
