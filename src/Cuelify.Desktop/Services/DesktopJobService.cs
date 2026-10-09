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
    Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token);
    string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings);
}

public sealed class DesktopJobService(string? root = null) : IDesktopJobService
{
    public string EngineDiagnostic { get; private set; } = "";
    private readonly string _root = root ?? new ConfigurationStore().Root;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private HyMt2TranslationEngine? _local;
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
        var engine = await EngineAsync(settings, key);
        var result = await new TranslationOrchestrator(engine, Path.Combine(_root, "Translations")).TranslateAsync(cues, settings.GetProfile(), settings.TranslationSettings(), force, progress, token);
        return result;
    }, token);

    private async Task<ITranslationEngine> EngineAsync(AppSettings settings, string key)
    {
        if (settings.Provider != TranslationProvider.Local)
            return settings.Provider == TranslationProvider.DeepSeek
                ? new DeepSeekOfficialProvider(_http, () => key, settings.CloudOptions(), settings.ThinkingOptions())
                : new OpenAiCompatibleTranslationEngine(_http, () => key, settings.CloudOptions());
        var identity = AtomicFile.Hash(settings.LocalOptions());
        if (_localIdentity != identity)
        {
            if (_local is not null) await _local.DisposeAsync();
            _local = new(settings.LocalOptions());
            _localIdentity = identity;
        }
        return _local!;
    }

    public Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token) => Task.Run(async () =>
    {
        EngineDiagnostic = "";
        var cue = new SubtitleCue("connection-test", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null);
        var request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), [cue], []);
        var engine = await EngineAsync(settings, key);
        var response = await engine.TranslateAsync(request, token);
        var aligned = AlignmentValidator.Parse(response.Content, [cue], engine.OutputFormat, false);
        if (aligned.FailedIds.Count > 0) throw new InvalidDataException("服务已响应，但译文不完整或格式不正确。请检查提示词后重试。");
        if (engine is HyMt2TranslationEngine local)
        {
            EngineDiagnostic = $"Vulkan：{string.Join("; ", local.Evidence?.DeviceLines ?? [])}；GPU 卸载 {local.Evidence?.OffloadedLayers}/{local.Evidence?.TotalLayers} 层。";
            return "本地翻译测试成功";
        }
        return "连接成功，翻译测试通过";
    }, token);

    public string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings)
    {
        var batch = cues.Take(settings.TranslationSettings().BatchSize).ToArray();
        var request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), batch, []);
        var value = settings.Provider == TranslationProvider.Local ? (object)request.Messages
            : CloudRequestBuilder.Build(settings.CloudOptions(), request.Messages, settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    public async ValueTask DisposeAsync()
    {
        if (_local is not null) await _local.DisposeAsync();
        _http.Dispose();
    }
}
