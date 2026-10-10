using System.Text.Json;
using Cuelify.Core.Media;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Media;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.Services;

public interface IDesktopJobService : IAsyncDisposable
{
    string EngineDiagnostic => "";
    Task<MediaInfo> ProbeAsync(string input, AppSettings settings, CancellationToken token);
    Task<TranscriptionResult> TranscribeAsync(string input, AppSettings settings, string key, IProgress<TranscriptionProgress> progress, CancellationToken token);
    Task<TranslationResult> TranslateAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, IReadOnlySet<string>? force, IProgress<TranslationProgress> progress, CancellationToken token);
    Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null);
    Task ValidateLocalAsync(AppSettings settings, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null);
    string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings);
    Task<string> PreviewAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, CancellationToken token) => Task.FromResult(Preview(cues, settings));
    ValueTask ReleaseEmbeddedModelAsync() => ValueTask.CompletedTask;
}

public sealed class DesktopJobService(string? root = null) : IDesktopJobService
{
    public string EngineDiagnostic { get; private set; } = "";
    private readonly string _root = root ?? new ConfigurationStore().Root;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private LocalTranslationEngine? _local;
    private LlamaServerBinary? _localBinary;
    private string? _localIdentity;
    private IAudioProcessor Audio(AppSettings settings) => new FfmpegAudioProcessor(new ProcessCommandRunner(), settings.FfmpegPath, settings.FfprobePath);
    public Task<MediaInfo> ProbeAsync(string input, AppSettings settings, CancellationToken token) => Audio(settings).ProbeAsync(input, token);

    public Task<TranscriptionResult> TranscribeAsync(string input, AppSettings settings, string key, IProgress<TranscriptionProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        var vadOptions = new SileroVadOptions { Threshold = (float)settings.VadThreshold, MinimumSpeech = TimeSpan.FromMilliseconds(settings.MinimumSpeechMs), MinimumSilence = TimeSpan.FromMilliseconds(settings.MinimumSilenceMs) };
        var hash = await AtomicFile.HashFileAsync(settings.VadPath, token);
        var signature = AtomicFile.Hash(new { modelHash = hash, vadOptions, Version = "silero-16k-v1" });
        var asrOptions = new ElevenLabsAsrOptions { ModelId = settings.AsrModel, LanguageCode = string.IsNullOrWhiteSpace(settings.SourceCode) ? null : settings.SourceCode, MaximumUploadBytes = settings.MaximumUploadBytes };
        var options = new TranscriptionOptions
        {
            Cues = new CueBuilderOptions { ZeroDurationTolerance = TimeSpan.FromMilliseconds(settings.ZeroDurationToleranceMs) },
            Chunks = new ChunkPlannerOptions { TargetDuration = TimeSpan.FromSeconds(settings.ChunkTargetSeconds), MaximumDuration = TimeSpan.FromSeconds(settings.ChunkMaximumSeconds), SearchRadius = TimeSpan.FromSeconds(settings.ChunkSearchSeconds), ForcedOverlap = TimeSpan.FromMilliseconds(settings.OverlapMs), MaximumUploadBytes = settings.MaximumUploadBytes, AlwaysChunk = settings.AlwaysChunk },
            Concurrency = settings.AsrConcurrency, MaximumAttempts = settings.MaximumAttempts
        };
        var pipeline = new TranscriptionPipeline(Audio(settings), new SileroVoiceActivityDetector(settings.VadPath, vadOptions),
            new ElevenLabsAsrClient(_http, () => key, asrOptions), signature, asrOptions, Path.Combine(_root, "Jobs"));
        var temporary = Path.Combine(_root, "Working", Guid.NewGuid().ToString("N") + ".srt");
        try { return await pipeline.RunAsync(input, temporary, options, progress: progress, cancellationToken: token); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    public Task<TranslationResult> TranslateAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, IReadOnlySet<string>? force, IProgress<TranslationProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        var engine = await EngineAsync(settings, key, token);
        var result = await new TranslationOrchestrator(engine, Path.Combine(_root, "Translations")).TranslateAsync(cues, settings.GetProfile(), settings.TranslationSettings(), force, progress, token);
        return result;
    }, token);

    private async Task<ITranslationEngine> EngineAsync(AppSettings settings, string key, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null)
    {
        if (settings.Provider != TranslationProvider.Local)
            return settings.Provider == TranslationProvider.DeepSeek
                ? new DeepSeekOfficialProvider(_http, () => key, settings.CloudOptions(), settings.ThinkingOptions())
                : new OpenAiCompatibleTranslationEngine(_http, () => key, settings.CloudOptions());
        var options = settings.LocalOptions();
        options = options with { FileVersion = ModelFileVersion.Read(options.ModelPath) };
        var runtimeIdentity = LlamaServerBinary.FileVersionIdentity(options.ServerPath);
        var identity = AtomicFile.Hash(new { options, Runtime = runtimeIdentity });
        if (_localIdentity == identity && _local is not null && await _local.IsReadyAsync(token)) return _local;
        if (_local is not null) await _local.DisposeAsync();
        _local = null; _localIdentity = null; _localBinary = null;
        progress?.Report(new(LocalPreparationStage.CheckingRuntime));
        _localBinary = await LlamaServerBinary.InspectAsync(options.ServerPath, token);
        if (ModelFileVersion.Read(options.ModelPath) != options.FileVersion || LlamaServerBinary.FileVersionIdentity(options.ServerPath) != runtimeIdentity)
        {
            throw new IOException("本地运行文件在准备过程中发生变化，请重新开始处理。");
        }
        _local = new(options, _localBinary);
        _localIdentity = identity;
        return _local!;
    }

    public Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null) => Task.Run(async () =>
    {
        EngineDiagnostic = "";
        var cue = new SubtitleCue("connection-test", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null);
        var request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), [cue], []);
        var engine = await EngineAsync(settings, key, token, progress);
        if (engine is LocalTranslationEngine prepared) await prepared.WarmupAsync(token, progress);
        progress?.Report(new(LocalPreparationStage.TestingTranslation));
        var response = await engine.TranslateAsync(request, token);
        var aligned = AlignmentValidator.Parse(response.Content, [cue], settings.GetProfile().OutputFormat);
        if (aligned.FailedIds.Count > 0) throw new InvalidDataException("服务已响应，但译文不完整或格式不正确。请检查提示词后重试。");
        if (engine is LocalTranslationEngine local)
        {
            EngineDiagnostic = $"{_localBinary?.Version}；Vulkan：{string.Join("; ", local.Evidence?.DeviceLines ?? [])}；GPU 卸载 {local.Evidence?.OffloadedLayers}/{local.Evidence?.TotalLayers} 层；并发 {settings.LocalConcurrency}。";
            return "本地翻译测试成功";
        }
        return "连接成功，翻译测试通过";
    }, token);

    public string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings)
    {
        var batch = TranslationOrchestrator.Plan(cues, settings.TranslationSettings()).FirstOrDefault() ?? [];
        var request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), batch, [], cues.Skip(batch.Count).ToArray());
        var value = settings.Provider == TranslationProvider.Local ? (object)request.Messages
            : CloudRequestBuilder.Build(settings.CloudOptions(), request.Messages, settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    public async Task<string> PreviewAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, CancellationToken token)
    {
        if (settings.Provider != TranslationProvider.Local) return Preview(cues, settings);
        return await Task.Run(async () =>
        {
            var all = cues.Select(cue => cue with { TranslatedText = null }).ToArray();
            var source = TranslationOrchestrator.Plan(all, settings.TranslationSettings()).FirstOrDefault() ?? [];
            var engine = (LocalTranslationEngine)await EngineAsync(settings, "", token);
            PreparedInferencePrompt prepared;
            while (true)
            {
                var request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), source, [], all.Skip(source.Count).ToArray());
                try { prepared = await engine.PrepareAsync(request, token); break; }
                catch (PromptCapacityException) when (source.Count > 1) { source = source.Take(source.Count / 2).ToArray(); }
            }
            return JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }, token);
    }
    public Task ValidateLocalAsync(AppSettings settings, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null) => Task.Run(async () =>
    {
        var engine = (LocalTranslationEngine)await EngineAsync(settings, "", token, progress);
        await engine.WarmupAsync(token, progress);
    }, token);
    public async ValueTask ReleaseEmbeddedModelAsync()
    {
        if (_local is not null) await _local.DisposeAsync();
        _local = null; _localIdentity = null; _localBinary = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_local is not null) await _local.DisposeAsync();
        _http.Dispose();
    }
}
