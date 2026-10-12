using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Cuelify.Core.Media;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Tests;

public sealed class UnifiedProgressTests
{
    [Fact]
    public async Task NativeVadReportsAnalyzedBytesAndCancelsWithoutKeepingTheModelSession()
    {
        using var directory = new TestDirectory(); var input = await NativeMediaTests.Fixture(directory);
        var audio = NativeMediaTests.Processor(); var info = await audio.ProbeAsync(input, CancellationToken.None);
        var prepared = await audio.PrepareAsync(input, info, directory.File("audio.pcm"), CancellationToken.None);
        var modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vad/silero_vad.onnx"));
        var vad = new SileroVoiceActivityDetector(modelPath); var reports = new List<StageWorkProgress>();
        await using var pcm = File.OpenRead(prepared.PcmPath);
        await vad.DetectAsync(pcm, CancellationToken.None, new InlineProgress<StageWorkProgress>(reports.Add));
        Assert.Equal(pcm.Length, reports[^1].Completed); Assert.Equal(100, reports[^1].Percent);
        pcm.Position = 0; using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vad.DetectAsync(pcm, cancellation.Token,
            new InlineProgress<StageWorkProgress>(work => { if (work.Completed > 0) cancellation.Cancel(); })));
        pcm.Position = 0;
        await vad.DetectAsync(pcm, CancellationToken.None);
    }
    [Fact]
    public async Task FileHashReportsActualBytesWithoutChangingIdentityAndHonorsCancellation()
    {
        using var directory = new TestDirectory(); var path = directory.File("校验.bin");
        var data = RandomNumberGenerator.GetBytes(2_000_000); await File.WriteAllBytesAsync(path, data);
        var reports = new List<StageWorkProgress>();
        var hash = await AtomicFile.HashFileAsync(path, CancellationToken.None, new InlineProgress<StageWorkProgress>(reports.Add));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hash);
        Assert.Equal(0, reports[0].Completed); Assert.Equal(data.Length, reports[^1].Completed);
        Assert.All(reports, value => Assert.Equal(data.Length, value.Total));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.HashFileAsync(path, cancellation.Token,
            new InlineProgress<StageWorkProgress>(_ => cancellation.Cancel())));
    }

    [Fact]
    public async Task NativeAudioAndCachedChunksPublishTheirOwnTotals()
    {
        using var directory = new TestDirectory(); var input = await NativeMediaTests.Fixture(directory);
        var reports = new List<TranscriptionProgress>();
        var pipeline = new ChunkingPipeline(NativeMediaTests.Processor(), new TranscriptionPipelineTests.FakeVad(), "test-vad", directory.File("cache"));
        var options = new ChunkPlannerOptions { TargetDuration = TimeSpan.FromSeconds(1), MaximumDuration = TimeSpan.FromSeconds(1), ForcedOverlap = TimeSpan.Zero, AlwaysChunk = true };
        var result = await pipeline.RunAsync(input, options, progress: new InlineProgress<TranscriptionProgress>(reports.Add));
        Assert.Contains(reports, value => value.Stage == "SourceChecking" && value.WorkProgress?.Percent == 100);
        Assert.Contains(reports, value => value.Stage == "AudioPreparing" && value.WorkProgress?.Completed == 64_000);
        Assert.Equal(result.Files.Count, reports.Last(value => value.Stage == "ChunkPlanning").WorkProgress!.Completed);
        reports.Clear();
        await pipeline.RunAsync(input, options, progress: new InlineProgress<TranscriptionProgress>(reports.Add));
        Assert.Equal(100, reports.Last(value => value.Stage == "ChunkPlanning").WorkProgress!.Percent);
        Assert.DoesNotContain(reports, value => value.Stage == "VadAnalyzing");
    }

    [Fact]
    public async Task TranslationCountsValidatedCuesAndSelectedRetryScope()
    {
        using var directory = new TestDirectory(); var reports = new List<TranslationProgress>();
        SubtitleCue[] cues = [new("a", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null), new("b", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "Bye.", null)];
        var engine = new Engine { FailSecond = true }; var orchestrator = new TranslationOrchestrator(engine, directory.File("cache"));
        var profile = PromptPresets.BatchSubtitles with { BatchSize = 1 };
        var settings = new TranslationSettings { Concurrency = 2, MaximumAttempts = 1 };
        var failed = await orchestrator.TranslateAsync(cues, profile, settings, progress: new InlineProgress<TranslationProgress>(reports.Add));
        var work = reports[^1].WorkProgress!;
        Assert.Equal(2, work.Total); Assert.Equal(1, work.Completed); Assert.Equal(1, work.Failed); Assert.Equal(0, work.Running); Assert.Equal(50, work.Percent);
        Assert.Equal("PartialFailure", reports[^1].Stage);
        engine.FailSecond = false; reports.Clear();
        var retried = await orchestrator.TranslateAsync(cues, profile, settings, new HashSet<string> { "b" },
            new InlineProgress<TranslationProgress>(reports.Add), previousCues: failed.Cues);
        Assert.True(retried.IsComplete);
        Assert.All(reports, value => Assert.Equal(1, value.WorkProgress!.Total));
        Assert.Equal(1, reports[^1].WorkProgress!.Completed); Assert.Equal(100, reports[^1].WorkProgress!.Percent);
        Assert.Equal(failed.Cues[0], retried.Cues[0]);
    }

    private sealed class Engine : ITranslationEngine
    {
        public bool FailSecond;
        public string CacheIdentity => "progress-test";
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken token) =>
            FailSecond && request.Cues.Any(c => c.Id == "b")
                ? Task.FromException<TranslationResponse>(new TranslationServiceException(HttpStatusCode.Unauthorized, null))
                : Task.FromResult(new TranslationResponse(JsonSerializer.Serialize(request.Cues.ToDictionary(c => c.Id, _ => "测试译文"))));
    }
}
