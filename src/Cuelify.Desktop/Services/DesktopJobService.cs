using System.Text.Json;
using Cuelify.Core.Media;
using Cuelify.Core.Projects;
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
    Task<ChunkingResult> PrepareChunksAsync(string input, AppSettings settings, string directory, bool restart, IProgress<TranscriptionProgress> progress, CancellationToken token);
    Task<TranscriptionResult> RecognizeChunksAsync(ChunkingResult prepared, AppSettings settings, string key, string directory, bool reuseShared,
        IReadOnlySet<int>? retryChunks, IProgress<TranscriptionProgress> progress, CancellationToken token);
    Task<SubtitlePipelineResult> GenerateStageAsync(TranscriptionResult source, AppSettings settings, string key, string directory, bool reuseShared,
        IReadOnlySet<string>? retryBlocks, IProgress<TranslationProgress> progress, CancellationToken token);
    Task<TranslationResult> TranslateStageAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, string directory, bool reuseShared,
        IReadOnlySet<string>? selectedIds, IProgress<TranslationProgress> progress, CancellationToken token);
    Task<TranscriptionResult> TranscribeAsync(string input, AppSettings settings, string key, IProgress<TranscriptionProgress> progress, CancellationToken token);
    Task<TranslationResult> TranslateAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, IReadOnlySet<string>? force, IProgress<TranslationProgress> progress, CancellationToken token);
    Task<SubtitlePipelineResult> GenerateCuesAsync(TranscriptionResult source, AppSettings settings, string key,
        IProgress<TranslationProgress> progress, CancellationToken token);
    Task<string> TestEngineAsync(AppSettings settings, string key, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null);
    Task ValidateLocalAsync(AppSettings settings, CancellationToken token, IProgress<LocalPreparationProgress>? progress = null);
    string Preview(IReadOnlyList<SubtitleCue> cues, AppSettings settings);
    Task<string> PreviewAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, CancellationToken token) => Task.FromResult(Preview(cues, settings));
    Task<string> PreviewPipelineAsync(TranscriptionResult source, IReadOnlyList<SubtitleCue> cues, AppSettings settings, CancellationToken token) => PreviewAsync(cues, settings, token);
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

    private static ElevenLabsAsrOptions AsrOptions(AppSettings settings) => new() { ModelId = settings.AsrModel,
        LanguageCode = string.IsNullOrWhiteSpace(settings.SourceCode) ? null : settings.SourceCode, MaximumUploadBytes = settings.MaximumUploadBytes };
    private static TranscriptionOptions RecognitionOptions(AppSettings settings) => new()
    {
        Cues = new CueBuilderOptions { ZeroDurationTolerance = TimeSpan.FromMilliseconds(settings.ZeroDurationToleranceMs) },
        Concurrency = settings.AsrConcurrency, MaximumAttempts = settings.MaximumAttempts
    };
    public Task<ChunkingResult> PrepareChunksAsync(string input, AppSettings settings, string directory, bool restart,
        IProgress<TranscriptionProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        var options = new SileroVadOptions { Threshold = (float)settings.VadThreshold,
            MinimumSpeech = TimeSpan.FromMilliseconds(settings.MinimumSpeechMs), MinimumSilence = TimeSpan.FromMilliseconds(settings.MinimumSilenceMs) };
        var hash = await AtomicFile.HashFileAsync(settings.VadPath, token);
        var signature = AtomicFile.Hash(new { modelHash = hash, vadOptions = options, Version = "silero-16k-v1" });
        var chunks = new ChunkPlannerOptions { TargetDuration = TimeSpan.FromSeconds(settings.ChunkTargetSeconds),
            MaximumDuration = TimeSpan.FromSeconds(settings.ChunkMaximumSeconds), SearchRadius = TimeSpan.FromSeconds(settings.ChunkSearchSeconds),
            ForcedOverlap = TimeSpan.FromMilliseconds(settings.OverlapMs), MaximumUploadBytes = settings.MaximumUploadBytes, AlwaysChunk = settings.AlwaysChunk };
        return await new ChunkingPipeline(Audio(settings), new SileroVoiceActivityDetector(settings.VadPath, options), signature, Path.Combine(_root, "Jobs"))
            .RunAsync(input, chunks, directory, progress, token, restart);
    }, token);
    public Task<TranscriptionResult> RecognizeChunksAsync(ChunkingResult prepared, AppSettings settings, string key, string directory,
        bool reuseShared, IReadOnlySet<int>? retryChunks, IProgress<TranscriptionProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        var options = AsrOptions(settings);
        // 识别入口不加载 VAD；它只使用已经验证并持久化的分片成果。
        var pipeline = new TranscriptionPipeline(Audio(settings), null, new ElevenLabsAsrClient(_http, () => key, options),
            "prepared-chunks", options, Path.Combine(_root, "Jobs"));
        return await pipeline.TranscribePreparedAsync(prepared, RecognitionOptions(settings), directory, retryChunks, progress, token, reuseShared);
    }, token);
    public Task<SubtitlePipelineResult> GenerateStageAsync(TranscriptionResult source, AppSettings settings, string key, string directory,
        bool reuseShared, IReadOnlySet<string>? retryBlocks, IProgress<TranslationProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        if (settings.GetPipeline().Method == SegmentationMethod.Program)
            return ProgramSegmentation.Build(source.Words, source.Duration, settings.GetPipeline(), TimeSpan.FromMilliseconds(settings.ZeroDurationToleranceMs));
        var engine = await EngineAsync(settings, key, token);
        var generated = await new SubtitlePipelineOrchestrator(engine, directory, reuseShared ? Path.Combine(_root, "Pipelines") : null)
            .GenerateAsync(source.Words, source.Cues, source.Duration, settings.GetPipeline(), settings.SegmentationSettings(), progress, token, retryBlocks);
        generated.ValidateSource(source.Words.Count); return generated;
    }, token);
    public Task<TranslationResult> TranslateStageAsync(IReadOnlyList<SubtitleCue> cues, AppSettings settings, string key, string directory,
        bool reuseShared, IReadOnlySet<string>? selectedIds, IProgress<TranslationProgress> progress, CancellationToken token) => Task.Run(async () =>
    {
        var engine = await EngineAsync(settings, key, token);
        var result = await new TranslationOrchestrator(engine, directory, reuseShared ? Path.Combine(_root, "Translations") : null)
            .TranslateAsync(cues, settings.GetProfile(), settings.TranslationSettings(), selectedIds, progress, token, previousCues: cues);
        return result with { Cues = result.Cues.Select(c => c.TranslatedText is null ? c : c with
            { TranslatedText = PipelineDisplay.Wrap(c.TranslatedText, settings.GetPipeline().LineCharacters) }).ToArray() };
    }, token);

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
        var result = await new TranslationOrchestrator(engine, Path.Combine(_root, "Translations")).TranslateAsync(cues, settings.GetProfile(), settings.TranslationSettings(), force, progress, token,
            previousCues: force is not null ? cues : null);
        return result with { Cues = result.Cues.Select(c => c.TranslatedText is null ? c : c with { TranslatedText = PipelineDisplay.Wrap(c.TranslatedText, settings.GetPipeline().LineCharacters) }).ToArray() };
    }, token);

    public Task<SubtitlePipelineResult> GenerateCuesAsync(TranscriptionResult source, AppSettings settings, string key,
        IProgress<TranslationProgress> progress, CancellationToken token) =>
        GenerateStageAsync(source, settings, key, Path.Combine(_root, "Pipelines"), true, null, progress, token);

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
            : CloudRequestBuilder.Build(settings.CloudOptions(), request, settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
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
            TranslationRequest request;
            while (true)
            {
                request = new PromptBuilder().Build(settings.GetProfile(), settings.TranslationSettings(), source, [], all.Skip(source.Count).ToArray());
                try { prepared = await engine.PrepareAsync(request, token); break; }
                catch (PromptCapacityException) when (source.Count > 1) { source = source.Take(source.Count / 2).ToArray(); }
            }
            return JsonSerializer.Serialize(new { Prompt = prepared, StageParameters = request.Parameters,
                ModelDefaults = EmbeddedModelCatalog.Get(settings.LocalModelId).DefaultParameters,
                MaximumTokens = request.Parameters.MaximumTokens ?? settings.LocalMaximumTokens },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }, token);
    }
    public async Task<string> PreviewPipelineAsync(TranscriptionResult source, IReadOnlyList<SubtitleCue> cues, AppSettings settings, CancellationToken token)
    {
        var profile = settings.GetPipeline();
        var previews = new List<object>();
        if (profile.Method != SegmentationMethod.Program && source.Words.Count > 0)
        {
            var block = PipelinePromptBuilder.Plan(source.Words, profile)[0];
            var engine = await EngineAsync(settings, "", token);
            while (true)
            {
                var request = PipelinePromptBuilder.Build(source.Words, block, profile, settings.SegmentationSettings());
                try
                {
                    var value = engine is LocalTranslationEngine local ? (object)new { Prompt = await local.PrepareAsync(request, token), request.Parameters, request.OutputContract } :
                        CloudRequestBuilder.Build(settings.CloudOptions(), request, settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
                    previews.Add(new { Stage = "分句", Request = value });
                    break;
                }
                catch (PromptCapacityException) when (block.Boundaries.Count > 1)
                {
                    var last = block.Boundaries[(block.Boundaries.Count - 1) / 2];
                    block = PipelinePromptBuilder.CreateBlock(source.Words, block.First, last, profile);
                }
            }
        }
        previews.Add(new { Stage = "翻译", Request = await PreviewAsync(cues, settings, token) });
        return JsonSerializer.Serialize(previews, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
