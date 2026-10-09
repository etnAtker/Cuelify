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

public sealed record PreparedInferencePrompt(IReadOnlyList<PromptMessage> Messages, string Prompt, int Tokens, int RemovedContextCues);
public sealed class EmbeddedTranslationEngine : ITranslationEngine, IAsyncDisposable
{
    private readonly EmbeddedModelOptions _options;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IProgress<LocalInferenceProgress>? _progress;
    private LLamaWeights? _weights;
    private FileStream? _modelFile;
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
    public EmbeddedTranslationEngine(EmbeddedModelOptions options, IProgress<LocalInferenceProgress>? progress = null)
    {
        options.Validate();
        _options = options with { ModelPath = Path.GetFullPath(options.ModelPath) };
        _progress = progress;
        CacheIdentity = AtomicFile.Hash(new { Provider = "llamasharp-vulkan-embedded", options.ModelId, options.ExpectedSha256,
            options.GpuLayers, options.ContextSize, options.MaximumTokens, Sampling = "temperature=.7,top_p=.6,top_k=20,repeat=1.05,seed=42", Version = "0.27.0-v2" });
    }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (request.Cues.Count != 1 || request.Messages.Count == 0 || request.Messages.Any(message => message.Role is not ("system" or "user")))
            throw new ArgumentException("内嵌模型仅接受单条字幕的 system/user 提示词。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _lifetime.IsCancellationRequested, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_options.InferenceTimeout);
            return await Task.Run(async () =>
            {
                await LoadAsync(timeout.Token);
                var prepared = Prepare(request);
                using var sampling = new TranslationSamplingPipeline();
                var output = new StringBuilder();
                try
                {
                    await foreach (var fragment in _executor!.InferAsync(prepared.Prompt,
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
        catch (OperationCanceledException) { throw new TimeoutException("内嵌模型推理超时。"); }
        catch (Exception exception) when (exception is not (LocalTranslationException or InvalidDataException or ArgumentException or TimeoutException or ObjectDisposedException))
        {
            var diagnostic = exception.Message + "\n" + VulkanRuntime.Snapshot();
            var category = exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ? "NativeLibrary" :
                diagnostic.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.Ordinal) ? "GpuMemory" :
                diagnostic.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase) ? "ModelArchitecture" : "VulkanOrInference";
            throw new LocalTranslationException(category, $"内嵌模型翻译不可用：{category}。请检查 Vulkan 驱动、原生 DLL、模型架构或显存。");
        }
        finally { _gate.Release(); }
    }

    public async Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await Task.Run(async () => { await LoadAsync(linked.Token); return Prepare(request); }, linked.Token);
        }
        finally { _gate.Release(); }
    }

    private PreparedInferencePrompt Prepare(TranslationRequest request)
    {
        return EmbeddedPromptBudget.Prepare(request, text => _weights!.Tokenize(text, add_bos: true, special: true, Encoding.UTF8).Length,
            messages =>
            {
                var template = new LLamaTemplate(_weights!, strict: true) { AddAssistant = true };
                foreach (var message in messages) template.Add(message.Role, message.Content);
                return Encoding.UTF8.GetString(template.Apply());
            }, _options.ContextSize, _options.MaximumTokens);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null) return;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new LocalTranslationException("WindowsX64Required", "本地翻译仅支持 Windows x64。");
        try { await EmbeddedModelOptions.VerifyIdentityAsync(_options.ModelPath, cancellationToken, _options.ExpectedSha256); }
        catch (Exception exception) when (exception is IOException)
        { throw new LocalTranslationException("ModelIdentity", "指定 GGUF 缺失、已改变或不完整。"); }
        VulkanRuntime.Initialize();
        _parameters = new ModelParams(_options.ModelPath) { GpuLayerCount = _options.GpuLayers, ContextSize = _options.ContextSize, MainGpu = 0 };
        var logStart = VulkanRuntime.Snapshot().Length;
        var modelFile = new FileStream(_options.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        LLamaWeights weights;
        try { weights = LLamaWeights.LoadFromFile(_parameters); }
        catch { modelFile.Dispose(); throw; }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (weights.Metadata.GetValueOrDefault("general.architecture") != _options.Model.Architecture ||
                !int.TryParse(weights.Metadata.GetValueOrDefault("general.file_type"), out var fileType) || fileType % 1024 != _options.Model.FileType)
                throw new LocalTranslationException("ModelArchitecture", "模型架构或量化与所选模型不一致，请重新选择文件。");
            _ = weights.NativeHandle.GetTemplate(null, strict: true);
            var log = VulkanRuntime.Snapshot();
            Evidence = VulkanEvidence.Parse(log[logStart..]) with { DeviceLines = VulkanEvidence.Parse(log).DeviceLines };
            if (!Evidence.HasGpuOffload) throw new LocalTranslationException("VulkanOffload", "缺少真实 Vulkan GPU 层卸载证据，拒绝继续生成。");
            _executor = new(weights, _parameters) { ApplyTemplate = false };
            _weights = weights;
            _modelFile = modelFile;
            ModelLoads++;
        }
        catch { weights.Dispose(); modelFile.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _weights?.Dispose();
            _modelFile?.Dispose();
            ModelDisposed = _weights is null || _weights.NativeHandle.IsClosed;
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
