using System.Diagnostics;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class PipelineCloudFactAttribute : FactAttribute
{
    public PipelineCloudFactAttribute()
    {
        if (new[] { "CUELIFY_PIPELINE_CLOUD_KEY", "CUELIFY_PIPELINE_WORDS" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "需显式授权云端费用并指定密钥和已有词缓存。";
    }
}

public sealed class SubtitlePipelineCloudTests
{
    [PipelineCloudFact]
    public async Task OfficialIndependentPipelineProducesRealSrtResumesAndKeepsRetranslationTimes()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/pipelines/cloud");
        var key = Environment.GetEnvironmentVariable("CUELIFY_PIPELINE_CLOUD_KEY")!;
        var words = (await AtomicFile.ReadJsonAsync<WordToken[]>(Environment.GetEnvironmentVariable("CUELIFY_PIPELINE_WORDS")!, default))!
            .TakeWhile(w => w.End.TotalSeconds <= 60).ToArray();
        var settings = new AppSettings { Provider = TranslationProvider.DeepSeek, CloudModel = "deepseek-flash", ThinkingEnabled = false,
            TaskPipelineId = SubtitlePipelinePresets.TwoStage.Id, SourceLanguage = "日语", TargetLanguage = "中文", TranslationConcurrency = 1 };
        var source = new TranscriptionResult("existing-asr", words.Max(w => w.End) + TimeSpan.FromSeconds(1), [], words, new CueBuilder().Build(words), 0, 1, "");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var jobs = new DesktopJobService(root);
        var progress = new Progress<TranslationProgress>(); var watch = Stopwatch.StartNew();
        using var http = new HttpClient();
        var engine = new RecordingEngine(new DeepSeekOfficialProvider(http, () => key, settings.CloudOptions(), settings.ThinkingOptions()), root);
        var generated = await new SubtitlePipelineOrchestrator(engine, Path.Combine(root, "Pipelines")).GenerateAsync(words,
            source.Duration, settings.GetPipeline(), settings.TranslationSettings(), progress, timeout.Token);
        Assert.Equal(string.Concat(words.Select(w => w.Text)), string.Concat(generated.Cues.Select(c => c.SourceText)));
        var translated = await jobs.TranslateAsync(generated.Cues, settings, key, null, progress, timeout.Token);
        Assert.True(translated.IsComplete, System.Text.Json.JsonSerializer.Serialize(translated.FailureReasons));
        var elapsed = watch.Elapsed.TotalSeconds;
        await AtomicFile.WriteTextAsync(Path.Combine(root, "translated.srt"), SrtSerializer.SerializeTranslated(translated.Cues), true, timeout.Token);
        var resumed = await jobs.GenerateCuesAsync(source, settings, key, progress, timeout.Token);
        Assert.Equal(0, resumed.EngineCalls); Assert.True(resumed.CacheHits > 0);
        var retry = await jobs.TranslateAsync(translated.Cues, settings, key, new HashSet<string> { translated.Cues[0].Id }, progress, timeout.Token);
        Assert.True(retry.IsComplete); Assert.Equal(translated.Cues.Skip(1), retry.Cues.Skip(1));
        Assert.Equal(translated.Cues[0].Start, retry.Cues[0].Start); Assert.Equal(translated.Cues[0].End, retry.Cues[0].End);
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "evidence.json"), new { Provider = "DeepSeek", Model = settings.CloudModel,
            WordCount = words.Length, CueCount = translated.Cues.Count, generated.EngineCalls, TranslationCalls = translated.EngineCalls,
            ResumeRequests = resumed.EngineCalls, ResumedCacheHits = resumed.CacheHits, RetryCalls = retry.EngineCalls,
            Seconds = elapsed, SourceCoverageExact = true, FrozenTimes = true }, timeout.Token);
    }
    private sealed class RecordingEngine(ITranslationEngine inner, string root) : ITranslationEngine
    {
        private int _sequence;
        public string CacheIdentity => inner.CacheIdentity;
        public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            var response = await inner.TranslateAsync(request, cancellationToken);
            await AtomicFile.WriteJsonAsync(Path.Combine(root, $"stage-response-{Interlocked.Increment(ref _sequence)}.json"),
                new { request, response }, cancellationToken);
            return response;
        }
    }
}
