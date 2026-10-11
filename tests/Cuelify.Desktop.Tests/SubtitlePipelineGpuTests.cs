using System.Diagnostics;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class PipelineGpuFactAttribute : FactAttribute
{
    public PipelineGpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || new[] { "CUELIFY_LOCAL_MODEL", "CUELIFY_LOCAL_SERVER", "CUELIFY_PIPELINE_WORDS" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "需显式指定已有本地模型、Vulkan 程序及词级 ASR 缓存。";
    }
}

public sealed class SubtitlePipelineGpuTests
{
    [PipelineGpuFact]
    public async Task RealModelGeneratesTranslatesResumesAndRetranslatesFrozenCues()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/pipelines/gpu");
        Directory.CreateDirectory(root);
        var modelId = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL_ID") ?? "hy-mt2-7b-q4km";
        var pipelineId = Environment.GetEnvironmentVariable("CUELIFY_PIPELINE_ID") ?? SubtitlePipelinePresets.TwoStage.Id;
        var settings = new AppSettings { Provider = TranslationProvider.Local, LocalModelId = modelId,
            ModelPath = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")!, LlamaServerPath = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")!,
            ContextSize = 16384, LocalConcurrency = 1, LocalMaximumTokens = 4096, TaskPipelineId = pipelineId,
            SourceLanguage = "日语", TargetLanguage = "中文", TimeoutSeconds = 120 };
        var words = (await AtomicFile.ReadJsonAsync<WordToken[]>(Environment.GetEnvironmentVariable("CUELIFY_PIPELINE_WORDS")!, default))!;
        if (double.TryParse(Environment.GetEnvironmentVariable("CUELIFY_PIPELINE_SECONDS"), out var limit))
            words = words.TakeWhile(w => w.End.TotalSeconds <= limit).ToArray();
        Assert.NotEmpty(words);
        var duration = words.Max(w => w.End) + TimeSpan.FromSeconds(1);
        var source = new TranscriptionResult("existing-asr", duration, [], words, new CueBuilder().Build(words), 0, 1, "");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using var jobs = new DesktopJobService(root);
        var stages = new Stages(); var watch = Stopwatch.StartNew();
        var generated = await jobs.GenerateCuesAsync(source, settings, "", stages, timeout.Token);
        var generationSeconds = watch.Elapsed.TotalSeconds;
        Assert.Equal(string.Concat(words.Select(w => w.Text)), string.Concat(generated.Cues.Select(c => c.SourceText)));
        Assert.Equal(words.Length - 1, generated.Ranges[^1].Last);
        var translated = await jobs.TranslateAsync(generated.Cues, settings, "", null, stages, timeout.Token);
        Assert.True(translated.IsComplete, System.Text.Json.JsonSerializer.Serialize(translated.FailureReasons));
        var totalSeconds = watch.Elapsed.TotalSeconds;
        await AtomicFile.WriteTextAsync(Path.Combine(root, "translated.srt"), SrtSerializer.SerializeTranslated(translated.Cues), true, timeout.Token);
        var resumed = await jobs.GenerateCuesAsync(source, settings, "", stages, timeout.Token);
        Assert.Equal(0, resumed.EngineCalls); Assert.True(resumed.CacheHits > 0);
        var selected = new HashSet<string> { translated.Cues[0].Id };
        var retry = await jobs.TranslateAsync(translated.Cues, settings, "", selected, stages, timeout.Token);
        Assert.True(retry.IsComplete); Assert.Equal(translated.Cues.Skip(1), retry.Cues.Skip(1));
        Assert.Equal(translated.Cues[0].Start, retry.Cues[0].Start); Assert.Equal(translated.Cues[0].End, retry.Cues[0].End);
        // 服务测试额外真实生成一句，诊断来自本次服务的实际 GPU 加载记录。
        await jobs.TestEngineAsync(settings, "", timeout.Token);
        Assert.Contains("GPU 卸载", jobs.EngineDiagnostic);
        await jobs.ReleaseEmbeddedModelAsync();
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "evidence.json"), new { Model = modelId, WordCount = words.Length,
            Pipeline = pipelineId, CueCount = translated.Cues.Count, generated.EngineCalls, TranslationCalls = translated.EngineCalls,
            generated.CacheHits, ResumedCacheHits = resumed.CacheHits, RetryCalls = retry.EngineCalls,
            GenerationSeconds = generationSeconds, TotalSeconds = totalSeconds, Stages = stages.Events,
            Diagnostic = jobs.EngineDiagnostic, SourceCoverageExact = true, FrozenTimes = true, Released = true }, timeout.Token);
    }
    private sealed class Stages : IProgress<TranslationProgress>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();
        public void Report(TranslationProgress value) => Events.Enqueue(value.Stage);
    }
}
