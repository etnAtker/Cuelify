using System.Net;
using System.Text.Json;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Tests;

public sealed class TranslationOrchestratorTests
{
    private sealed class ProgressSink(Action<TranslationProgress> report) : IProgress<TranslationProgress>
    { public void Report(TranslationProgress value) => report(value); }

    [Fact]
    public async Task ProgressPublishesValidatedFirstBatchBeforeNextRequest()
    {
        using var directory = new TestDirectory();
        TranslationProgress? completed = null;
        var engine = new Engine { Behavior = (request, call, _) =>
        {
            if (call == 2)
            {
                Assert.NotNull(completed?.Cues);
                Assert.Equal("模拟译文a", completed.Cues[0].TranslatedText);
                Assert.Null(completed.Cues[1].TranslatedText);
                Assert.Equal(Cues[1].Start, completed.Cues[1].Start);
            }
            return Task.FromResult(Response(request));
        } };
        var result = await Orchestrator(directory, engine).TranslateAsync(Cues, PromptPresets.BatchSubtitles with { BatchSize = 1 },
            Settings with { BatchSize = 1, Concurrency = 1 }, progress: new ProgressSink(value => { if (value.Cues is not null) completed = value; }));
        Assert.True(result.IsComplete); Assert.Equal(2, engine.Calls);
        Assert.Equal(result.Cues, completed!.Cues);
    }
    private sealed class Engine(string identity = "fake-A") : ITranslationEngine
    {
        public TranslationOutputFormat OutputFormat => TranslationOutputFormat.CueIdJson;
        public string CacheIdentity => identity;
        public int Calls;
        public Func<TranslationRequest, int, CancellationToken, Task<TranslationResponse>>? Behavior;
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls);
            return Behavior?.Invoke(request, call, token) ?? Task.FromResult(Response(request));
        }
    }
    private static TranslationResponse Response(TranslationRequest request) => new(JsonSerializer.Serialize(request.Cues.ToDictionary(cue => cue.Id, cue => "模拟译文" + cue.Id)));
    private static SubtitleCue[] Cues => [TranslationPromptTests.Cue("a"), TranslationPromptTests.Cue("b", "Goodbye.", 2)];
    private static TranslationSettings Settings => new() { RetryDelay = TimeSpan.Zero, MaximumRetryDelay = TimeSpan.Zero };
    private static TranslationOrchestrator Orchestrator(TestDirectory directory, Engine engine) => new(engine, directory.File("translation-cache"));

    [Fact]
    public async Task FailedHttpRequestsKeepSafeReasonsForTheDesktop()
    {
        using var directory = new TestDirectory();
        using var handler = new CloudTranslationTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("echo fake-secret-key") }));
        using var http = new HttpClient(handler);
        var engine = new OpenAiCompatibleTranslationEngine(http, () => "fake-secret-key", new() { BaseUrl = "https://provider.example/v1", Model = "test" });
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.False(result.IsComplete);
        Assert.All(result.FailedIds, id => Assert.Equal("HTTP 401", result.FailureReasons[id]));
        Assert.DoesNotContain("fake-secret-key", JsonSerializer.Serialize(result));
        var saved = Directory.GetFiles(directory.File("cache"), "result.json", SearchOption.AllDirectories).Single();
        Assert.Equal(result.FailureReasons.Count, JsonSerializer.Deserialize<TranslationResult>(await File.ReadAllTextAsync(saved))!.FailureReasons.Count);
    }

    [Fact]
    public async Task MissingCloudKeyKeepsAnActionableReasonWithoutSendingHttp()
    {
        using var directory = new TestDirectory();
        var calls = 0;
        using var handler = new CloudTranslationTests.Handler((_, _) => { calls++; return Task.FromResult(CloudTranslationTests.Success()); });
        using var http = new HttpClient(handler);
        var engine = new OpenAiCompatibleTranslationEngine(http, () => null, new() { BaseUrl = "https://provider.example/v1", Model = "test" });
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(0, calls);
        Assert.All(result.FailedIds, id => Assert.Contains("请填写有效的翻译服务 API 密钥", result.FailureReasons[id]));
    }

    [Fact]
    public void PreviousResultJsonWithoutFailureReasonsStillLoads()
    {
        var previous = JsonSerializer.Deserialize<TranslationResult>("{\"Cues\":[],\"FailedIds\":[],\"EngineCalls\":0,\"CacheHits\":0}")!;
        Assert.Empty(previous.FailureReasons);
        Assert.True(previous.IsComplete);
    }

    [Fact]
    public async Task MissingCueUsesTargetedRetryAndStableTimeline()
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (request, call, _) =>
        {
            if (call == 1) return Task.FromResult(new TranslationResponse("{\"a\":\"你好。\"}"));
            Assert.Equal("b", Assert.Single(request.Cues).Id);
            return Task.FromResult(Response(request));
        } };
        var orchestrator = Orchestrator(directory, engine);
        var result = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EngineCalls);
        Assert.Equal(Cues.Select(cue => (cue.Id, cue.Start, cue.End)), result.Cues.Select(cue => (cue.Id, cue.Start, cue.End)));
        Assert.Equal("你好。", result.Cues[0].TranslatedText);
        var again = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(0, again.EngineCalls);
        Assert.Equal(2, again.CacheHits);
    }

    [Fact]
    public async Task PartialFailureCanResumeOnlyMissingCueAndCannotExportIncompleteTranslation()
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (_, call, _) => Task.FromResult(new TranslationResponse(call == 1 ? "{\"a\":\"你好\"}" : "{\"b\":\"Goodbye.\"}")) };
        var orchestrator = Orchestrator(directory, engine);
        var partial = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.False(partial.IsComplete);
        Assert.Equal(["b"], partial.FailedIds);
        Assert.Null(partial.Cues[1].TranslatedText);
        Assert.Throws<InvalidDataException>(() => SrtSerializer.SerializeTranslated(partial.Cues));
        engine.Behavior = (request, _, _) => { Assert.Equal("b", Assert.Single(request.Cues).Id); return Task.FromResult(Response(request)); };
        var resumed = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.True(resumed.IsComplete);
        Assert.Equal(1, resumed.EngineCalls);
        Assert.Equal(1, resumed.CacheHits);
    }

    [Fact]
    public async Task PassingDisplayedTranslatedCuesBackDoesNotChangeCacheOrChargeAgain()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var orchestrator = Orchestrator(directory, engine);
        var first = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        var displayed = await orchestrator.TranslateAsync(first.Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(0, displayed.EngineCalls);
        Assert.Equal(2, displayed.CacheHits);
        Assert.Equal(first.Cues, displayed.Cues);
    }

    [Fact]
    public async Task ExplicitRetranslationOnlyCallsSelectedCue()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var orchestrator = Orchestrator(directory, engine);
        await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        engine.Behavior = (request, _, _) => { Assert.Equal("b", Assert.Single(request.Cues).Id); return Task.FromResult(new TranslationResponse("{\"b\":\"新的译文\"}")); };
        var result = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings, new HashSet<string> { "b" });
        Assert.Equal(1, result.EngineCalls);
        Assert.Equal("新的译文", result.Cues[1].TranslatedText);
        Assert.Equal("模拟译文a", result.Cues[0].TranslatedText);
    }

    [Fact]
    public async Task RetranslationFailureInvalidatesOldSelectedCache()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var orchestrator = Orchestrator(directory, engine);
        await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        engine.Behavior = (_, _, _) => Task.FromException<TranslationResponse>(new TranslationServiceException(HttpStatusCode.Unauthorized, null));
        var failed = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings, new HashSet<string> { "b" });
        Assert.False(failed.IsComplete);
        Assert.Null(failed.Cues[1].TranslatedText);
        engine.Behavior = (request, _, _) => { Assert.Equal("b", Assert.Single(request.Cues).Id); return Task.FromResult(new TranslationResponse("{\"b\":\"恢复的新译文\"}")); };
        var recovered = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(1, recovered.EngineCalls);
        Assert.Equal("恢复的新译文", recovered.Cues[1].TranslatedText);
    }

    [Fact]
    public async Task RetranslatingEarlyCueDoesNotRetranslateLaterContextDependentCues()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var orchestrator = Orchestrator(directory, engine);
        await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles with { BatchSize = 1 }, Settings with { BatchSize = 1 });
        engine.Behavior = (request, _, _) => { Assert.Equal("a", Assert.Single(request.Cues).Id); return Task.FromResult(new TranslationResponse("{\"a\":\"新的上文译文\"}")); };
        var changed = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles with { BatchSize = 1 }, Settings with { BatchSize = 1 }, new HashSet<string> { "a" });
        Assert.True(changed.IsComplete);
        Assert.Equal(1, changed.EngineCalls);
        Assert.Equal("模拟译文b", changed.Cues[1].TranslatedText);
    }

    [Fact]
    public async Task ParallelBatchesUseStableContextSnapshot()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var inputs = Cues.Concat([TranslationPromptTests.Cue("c", "Third.", 4), TranslationPromptTests.Cue("d", "Fourth.", 6)]).ToArray();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        engine.Behavior = async (request, _, _) => { var cue = Assert.Single(request.Cues); seen[cue.Id] = request.Messages[^1].Content; await Task.Delay(cue.Id == "a" ? 20 : 1); return Response(request); };
        var result = await Orchestrator(directory, engine).TranslateAsync(inputs, PromptPresets.BatchSubtitles with { BatchSize = 1 }, Settings with { BatchSize = 1, Concurrency = 2 });
        Assert.True(result.IsComplete);
        Assert.Contains("（无）", seen["a"]);
        Assert.Contains("Translation", seen["c"]);
        var repeat = await Orchestrator(directory, engine).TranslateAsync(inputs, PromptPresets.BatchSubtitles with { BatchSize = 1 }, Settings with { BatchSize = 1, Concurrency = 2 });
        Assert.Equal(0, repeat.EngineCalls);
    }

    [Fact]
    public async Task InvalidJsonFallsBackToSinglesWithoutCopyingSource()
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (request, call, _) => Task.FromResult(call == 1 ? new TranslationResponse("说明 {bad}") : Response(request)) };
        var result = await Orchestrator(directory, engine).TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.EngineCalls);
    }

    [Fact]
    public async Task PersistentUntranslatedSingleIsBounded()
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (_, _, _) => Task.FromResult(new TranslationResponse("{\"a\":\"Hello.\"}")) };
        var result = await Orchestrator(directory, engine).TranslateAsync([Cues[0]], PromptPresets.BatchSubtitles, Settings);
        Assert.False(result.IsComplete);
        Assert.Equal(3, result.EngineCalls);
        Assert.Null(result.Cues[0].TranslatedText);
    }

    [Theory]
    [InlineData(401, 1)] [InlineData(503, 3)]
    public async Task ServiceFailuresRespectRetryBudget(int status, int expectedCalls)
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (_, _, _) => Task.FromException<TranslationResponse>(new TranslationServiceException((HttpStatusCode)status, null)) };
        var result = await Orchestrator(directory, engine).TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.False(result.IsComplete);
        Assert.Equal(expectedCalls, result.EngineCalls);
    }

    [Fact]
    public async Task LongRetryAfterStopsRatherThanRetryingEarly()
    {
        using var directory = new TestDirectory();
        var engine = new Engine { Behavior = (_, _, _) => Task.FromException<TranslationResponse>(new TranslationServiceException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10))) };
        var result = await Orchestrator(directory, engine).TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(1, result.EngineCalls);
    }

    [Fact]
    public async Task CancellationAfterResponsePreservesValidatedPaidResults()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine { Behavior = (request, _, _) => { cancellation.Cancel(); return Task.FromResult(Response(request)); } };
        var orchestrator = Orchestrator(directory, engine);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings, cancellationToken: cancellation.Token));
        engine.Behavior = null;
        var recovered = await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        Assert.Equal(0, recovered.EngineCalls);
        Assert.Equal(2, recovered.CacheHits);
    }

    [Fact]
    public async Task CorruptTranslationCacheNeverTriggersAutomaticRebilling()
    {
        using var directory = new TestDirectory();
        var engine = new Engine();
        var orchestrator = Orchestrator(directory, engine);
        await orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings);
        var cache = Directory.GetFiles(directory.File("translation-cache"), "batch-*.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(cache, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => orchestrator.TranslateAsync(Cues, PromptPresets.BatchSubtitles, Settings));
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ChangingTranslationModelLanguageOrPromptNeverInvalidatesAsr()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory);
        var asr = new TranscriptionPipelineTests.FakeAsr();
        var transcription = new TranscriptionPipeline(NativeMediaTests.Processor(), new TranscriptionPipelineTests.FakeVad(), asr,
            "fake-vad", new ElevenLabsAsrOptions(), directory.File("asr-cache"));
        var recognized = await transcription.RunAsync(input, directory.File("source.srt"));
        await Orchestrator(directory, new()).TranslateAsync(recognized.Cues, PromptPresets.BatchSubtitles, Settings);
        await Orchestrator(directory, new("fake-B")).TranslateAsync(recognized.Cues, PromptPresets.BatchSubtitles with { SystemTemplate = PromptPresets.BatchSubtitles.SystemTemplate + "简洁表达" }, Settings with { TargetLanguage = "日语" });
        var repeated = await transcription.RunAsync(input, directory.File("source-again.srt"));
        Assert.Equal(1, asr.Calls);
        Assert.Equal(0, repeated.AsrRequests);
    }

    [Fact]
    public void BatchPlannerLimitsCountAndCharacterBudget()
    {
        var batches = TranslationOrchestrator.Plan([TranslationPromptTests.Cue("a", new string('a', 70)), TranslationPromptTests.Cue("b", new string('b', 70))], Settings with { MaximumBatchCharacters = 100 });
        Assert.Equal(2, batches.Count);
        Assert.Throws<ArgumentException>(() => TranslationOrchestrator.Plan([TranslationPromptTests.Cue(text: new string('a', 200))], Settings with { MaximumBatchCharacters = 100 }));
    }
}
