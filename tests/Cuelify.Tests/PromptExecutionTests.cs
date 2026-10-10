using System.Collections.Concurrent;
using System.Text.Json;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class PromptExecutionTests
{
    private static EmbeddedModelOptions LocalOptions() => new() { FileVersion = new("fixture-model.gguf", 100, DateTime.UnixEpoch) };
    private static readonly SubtitleCue[] Cues = Enumerable.Range(1, 4)
        .Select(index => TranslationPromptTests.Cue("cue-" + index, "Source " + index, index * 2)).ToArray();
    private static PromptProfile Profile(bool batch) => new("独立协议", "", batch ? "仅输出 ID 对应译文 JSON\n{cues_json}" : "只输出译文\n{source_text}", batch)
        { BatchSize = 2, MaximumBatchCharacters = 6000 };
    private static string Output(TranslationRequest request) => request.PromptContext!.Profile.BatchTranslation
        ? JsonSerializer.Serialize(request.Cues.ToDictionary(cue => cue.Id, _ => "测试译文")) : "测试译文";

    [Theory]
    [InlineData("compatible", false)]
    [InlineData("compatible", true)]
    [InlineData("deepseek", false)]
    [InlineData("deepseek", true)]
    [InlineData("local", false)]
    [InlineData("local", true)]
    public async Task ProductionBackendsAcceptSingleAndBatchProtocolsFromSameTemplateLibrary(string provider, bool batch)
    {
        using var directory = new TestDirectory();
        var session = new Session(); var sent = new ConcurrentBag<int>();
        using var handler = new CloudTranslationTests.Handler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var content = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            if (!batch) { sent.Add(1); return CloudTranslationTests.Success("测试译文"); }
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(content[(content.LastIndexOf('\n') + 1)..])!;
            sent.Add(entries.Count); return CloudTranslationTests.Success(JsonSerializer.Serialize(entries.ToDictionary(item => item.Key, _ => "测试译文")));
        });
        using var http = new HttpClient(handler);
        var options = new CloudTranslationOptions { BaseUrl = provider == "deepseek" ? "https://api.deepseek.com" : "https://provider.example/v1", Model = "model" };
        await using var local = new LocalTranslationEngine(LocalOptions(), new("server.exe", "test", "test", "Vulkan0"), session);
        ITranslationEngine engine = provider switch
        {
            "local" => local,
            "deepseek" => new DeepSeekOfficialProvider(http, () => "fixture-key", options, new()),
            _ => new OpenAiCompatibleTranslationEngine(http, () => "fixture-key", options)
        };
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues, Profile(batch), new() { BatchSize = 99, Concurrency = 2 });
        Assert.True(result.IsComplete); Assert.Equal(batch ? 2 : 4, result.EngineCalls);
        var sizes = provider == "local" ? session.Requests.Select(request => request.Cues.Count) : sent;
        Assert.All(sizes, size => Assert.Equal(batch ? 2 : 1, size));
        Assert.Equal(Cues.Select(cue => (cue.Id, cue.Start, cue.End)), result.Cues.Select(cue => (cue.Id, cue.Start, cue.End)));
        Assert.All(result.Cues, cue => Assert.Equal("测试译文", cue.TranslatedText));
    }

    [Fact]
    public async Task LocalCapacitySplitsBatchesWithoutDiscardingCuesOrChangingJsonProtocol()
    {
        using var directory = new TestDirectory(); var session = new Session { Capacity = 1 };
        await using var engine = new LocalTranslationEngine(LocalOptions() with { ContextSize = 512, MaximumTokens = 64 }, new("server.exe", "test", "test", "Vulkan0"), session);
        var profile = Profile(true) with { BatchSize = 4 };
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues, profile, new() { Concurrency = 1 });
        Assert.True(result.IsComplete); Assert.Equal(4, session.Completions);
        Assert.All(session.Requests, request => Assert.True(request.PromptContext!.Profile.BatchTranslation));
        Assert.Equal(Cues.Select(cue => cue.Id), result.Cues.Select(cue => cue.Id));
        var cached = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues, profile, new());
        Assert.Equal(4, cached.CacheHits); Assert.Equal(4, session.Completions);
    }

    [Fact]
    public async Task SingleCueOverCapacityFailsExplicitlyWithoutGeneratingOrSubstitutingOriginalText()
    {
        using var directory = new TestDirectory(); var session = new Session { Capacity = 0 };
        await using var engine = new LocalTranslationEngine(LocalOptions(), new("server.exe", "test", "test", "Vulkan0"), session);
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync([Cues[0]], Profile(true), new());
        Assert.False(result.IsComplete); Assert.Null(result.Cues[0].TranslatedText); Assert.Equal(0, session.Completions);
        Assert.Equal("本地上下文不足", result.FailureReasons[Cues[0].Id]);
    }

    [Fact]
    public async Task BatchRetryKeepsJsonAndOnlyResendsMissingId()
    {
        using var directory = new TestDirectory(); var session = new Session { MissingFirstId = true };
        await using var engine = new LocalTranslationEngine(LocalOptions(), new("server.exe", "test", "test", "Vulkan0"), session);
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(Cues.Take(2).ToArray(), Profile(true), new() { Concurrency = 1 });
        Assert.True(result.IsComplete); Assert.Equal(2, session.Completions);
        var requests = session.Requests.ToArray(); Assert.Equal(2, requests[0].Cues.Count);
        Assert.Equal("cue-1", Assert.Single(requests[1].Cues).Id);
        Assert.All(requests, request => Assert.Equal(TranslationOutputFormat.CueIdJson, request.PromptContext!.Profile.OutputFormat));
    }

    [Fact]
    public async Task RenamingAndDescribingTemplateKeepsCacheButChangingModeOrContentDoesNot()
    {
        using var directory = new TestDirectory(); var session = new Session();
        await using var engine = new LocalTranslationEngine(LocalOptions(), new("server.exe", "test", "test", "Vulkan0"), session);
        var orchestrator = new TranslationOrchestrator(engine, directory.File("cache")); var original = Profile(true);
        await orchestrator.TranslateAsync(Cues, original, new());
        var renamed = await orchestrator.TranslateAsync(Cues, original with { Name = "改名", Description = "备注", Id = "另一个相同模板" }, new());
        Assert.Equal(0, renamed.EngineCalls); Assert.Equal(4, renamed.CacheHits);
        var edited = await orchestrator.TranslateAsync(Cues, original with { UserTemplate = "更自然的译文\n{cues_json}" }, new());
        Assert.Equal(2, edited.EngineCalls);
        var single = await orchestrator.TranslateAsync(Cues, Profile(false), new()); Assert.Equal(4, single.EngineCalls);
    }

    [Theory]
    [InlineData(true, "{source_text}")]
    [InlineData(false, "{cues_json}")]
    public void ProtocolMismatchIsRejectedWithoutRewritingTemplate(bool batch, string content) =>
        Assert.Throws<ArgumentException>(() => PromptBuilder.Validate(Profile(batch) with { UserTemplate = content }));

    private sealed class Session : ILlamaServerSession
    {
        private readonly ConcurrentDictionary<string, TranslationRequest> _prepared = new();
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public int Capacity = 100;
        public int Completions;
        public bool MissingFirstId;
        public VulkanEvidence? Evidence => null;
        public Task EnsureStartedAsync(CancellationToken token) => Task.CompletedTask;
        public Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            // 经生产预算逻辑产生容量异常，而不是在引擎中硬编码分批结果。
            return EmbeddedPromptBudget.PrepareAsync(request, (messages, _) =>
            {
                var key = Guid.NewGuid().ToString("N"); _prepared[key] = request;
                return Task.FromResult((key, request.Cues.Count > Capacity ? 10000 : 20));
            }, 512, 64, token);
        }
        public Task<LocalCompletion> CompleteAsync(string prompt, IProgress<int>? progress, CancellationToken token)
        {
            var count = Interlocked.Increment(ref Completions); var request = _prepared[prompt];
            var content = MissingFirstId && count == 1 ? JsonSerializer.Serialize(request.Cues.Skip(1).ToDictionary(cue => cue.Id, _ => "测试译文")) : Output(request);
            return Task.FromResult(new LocalCompletion(content, 8, "eos", false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
