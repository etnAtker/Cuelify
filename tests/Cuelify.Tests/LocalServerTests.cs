using System.Net;
using System.Text;
using System.Text.Json;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class LocalServerTests
{
    private static readonly ModelFileVersion FileVersion = new("fixture-model.gguf", 100, DateTime.UnixEpoch);
    private static readonly LlamaServerBinary Binary = new("llama-server.exe", "test-version", "test-runtime", "Vulkan0");
    private static TranslationRequest Request(string id = "a") => new PromptBuilder().Build(PromptPresets.SingleSimple,
        new() { BatchSize = 1 }, [TranslationPromptTests.Cue(id)], []);

    [Fact]
    public async Task ConcurrentRequestsShareSessionAndDisposalCancelsActiveAndQueuedWork()
    {
        var session = new Session { Hold = true };
        var engine = new LocalTranslationEngine(new() { FileVersion = FileVersion, Concurrency = 2 }, Binary, session);
        var first = engine.TranslateAsync(Request("a"), default);
        var second = engine.TranslateAsync(Request("b"), default);
        await session.TwoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = engine.TranslateAsync(Request("c"), default);
        Assert.Equal(2, session.Calls);
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var task in new[] { first, second, queued }) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(2, session.MaximumActive); Assert.Equal(0, session.Active); Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("limit", false, 2, "译文")]
    [InlineData("eos", true, 2, "译文")]
    [InlineData("eos", false, 0, "")]
    public async Task RejectsTruncationAndNonEosOutput(string stop, bool truncated, int tokens, string content)
    {
        await using var engine = new LocalTranslationEngine(new() { FileVersion = FileVersion }, Binary, new Session { Result = new(content, tokens, stop, truncated) });
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranslateAsync(Request(), default));
    }

    [Fact]
    public async Task InferenceTimeoutReleasesSlotForNextRequest()
    {
        var session = new Session { Hold = true };
        await using var engine = new LocalTranslationEngine(new() { FileVersion = FileVersion, Concurrency = 1, InferenceTimeout = TimeSpan.FromMilliseconds(100) }, Binary, session);
        await Assert.ThrowsAsync<TimeoutException>(() => engine.TranslateAsync(Request(), default));
        session.Hold = false;
        Assert.Equal("你好。", (await engine.TranslateAsync(Request("b"), default)).Content);
    }

    [Fact]
    public void RuntimeIdentityChangesCacheButBinaryLocationDoesNot()
    {
        var options = new EmbeddedModelOptions { FileVersion = FileVersion };
        var first = new LocalTranslationEngine(options, Binary, new Session());
        var changed = new LocalTranslationEngine(options, Binary with { Identity = "new-runtime" }, new Session());
        var moved = new LocalTranslationEngine(options, Binary with { Path = "其他目录/llama-server.exe" }, new Session());
        Assert.NotEqual(first.CacheIdentity, changed.CacheIdentity); Assert.Equal(first.CacheIdentity, moved.CacheIdentity);
    }

    [Fact]
    public async Task FileVersionsReuseCachesAndSeparateChangedOrReplacedModelsWithoutReadingContent()
    {
        using var directory = new TestDirectory(); var path = directory.File("自定义.gguf");
        await File.WriteAllTextAsync(path, "任意内容");
        var options = new EmbeddedModelOptions { ModelPath = path };
        var cues = new[] { TranslationPromptTests.Cue("a") };
        await using (var first = new LocalTranslationEngine(options, Binary, new Session()))
        {
            var result = await new TranslationOrchestrator(first, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleSimple, new());
            Assert.Equal(1, result.EngineCalls);
        }
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await using var reused = new LocalTranslationEngine(options, Binary, new Session());
            var result = await new TranslationOrchestrator(reused, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleSimple, new());
            Assert.Equal(0, result.EngineCalls); Assert.Equal(1, result.CacheHits);
        }
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await using (var changed = new LocalTranslationEngine(options, Binary, new Session()))
        {
            var result = await new TranslationOrchestrator(changed, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleSimple, new());
            Assert.Equal(1, result.EngineCalls); Assert.Equal(0, result.CacheHits);
        }
        var other = directory.File("另外模型.gguf"); File.Copy(path, other);
        await using var moved = new LocalTranslationEngine(options with { ModelPath = other }, Binary, new Session());
        var fresh = await new TranslationOrchestrator(moved, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleSimple, new());
        Assert.Equal(1, fresh.EngineCalls); Assert.Equal(0, fresh.CacheHits);
    }

    [Fact]
    public void ServerUsesLoopbackVulkanAndPerRequestContextBudget()
    {
        var arguments = LlamaServerSession.Arguments(new() { ModelPath = "字幕 模型.gguf", Concurrency = 4, ContextSize = 4096 }, Binary);
        Assert.Equal("127.0.0.1", arguments[arguments.ToList().IndexOf("--host") + 1]);
        Assert.Equal("0", arguments[arguments.ToList().IndexOf("--port") + 1]);
        Assert.Equal("Vulkan0", arguments[arguments.ToList().IndexOf("--device") + 1]);
        Assert.Equal("16384", arguments[arguments.ToList().IndexOf("--ctx-size") + 1]);
        Assert.Equal(Path.GetFullPath("字幕 模型.gguf"), arguments[1]);
        Assert.Contains("--no-webui", arguments); Assert.Contains("--no-kv-unified", arguments);
    }

    [Fact]
    public async Task LocalWavesUseStableContextAndPreserveCueOrderDespiteCompletionOrder()
    {
        using var directory = new TestDirectory();
        var cues = Enumerable.Range(0, 6).Select(index => TranslationPromptTests.Cue(index.ToString(), "SOURCE" + index, index * 2)).ToArray();
        var engine = new ConcurrentEngine();
        var settings = new TranslationSettings { BatchSize = 1, Concurrency = 4 };
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleContext, settings);
        Assert.True(result.IsComplete); Assert.Equal(4, engine.MaximumActive);
        Assert.Equal(cues.Select(cue => (cue.Id, cue.Start, cue.End)), result.Cues.Select(cue => (cue.Id, cue.Start, cue.End)));
        foreach (var request in engine.Requests.Where(item => int.Parse(item.Cues[0].Id) < 4))
            foreach (var index in Enumerable.Range(0, 4)) Assert.DoesNotContain("译文" + index, request.Messages[0].Content);
        Assert.Contains("译文0", engine.Requests.Single(item => item.Cues[0].Id == "4").Messages[0].Content);
        var cached = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(cues, PromptPresets.SingleContext, settings);
        Assert.Equal(0, cached.EngineCalls); Assert.Equal(6, cached.CacheHits);
    }

    [Fact]
    public async Task ProtocolUsesExactPreparedPromptAndChecksStreamCompletion()
    {
        string? sentPrompt = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            return request.RequestUri!.AbsolutePath switch
            {
                "/apply-template" => Json(new { prompt = "<chat>当前字幕<assistant>" }),
                "/tokenize" => Json(new { tokens = new[] { 1, 2, 3 } }),
                "/completion" => Completion(json.RootElement.GetProperty("prompt").GetString()!),
                _ => throw new InvalidOperationException()
            };
            HttpResponseMessage Completion(string prompt)
            {
                sentPrompt = prompt;
                Assert.Equal(.7, json.RootElement.GetProperty("temperature").GetDouble());
                Assert.False(json.RootElement.GetProperty("cache_prompt").GetBoolean());
                return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"content\":\"你\",\"stop\":false}\n\ndata: {\"content\":\"好。\",\"stop\":false}\n\ndata: {\"content\":\"\",\"stop\":true,\"stop_type\":\"eos\",\"tokens_predicted\":3,\"truncated\":false}\n\n", Encoding.UTF8, "text/event-stream") };
            }
        })) { BaseAddress = new("http://127.0.0.1/") };
        var protocol = new LlamaServerProtocol(http, 512, 100);
        var prepared = await protocol.PrepareAsync(Request(), default);
        var completion = await protocol.CompleteAsync(prepared.Prompt, null, default);
        Assert.Equal(prepared.Prompt, sentPrompt); Assert.Equal(3, prepared.Tokens);
        Assert.Equal("你好。", completion.Content); Assert.Equal("eos", completion.StopType);
    }

    [Fact]
    public async Task ProtocolRejectsInsufficientPerSlotContext()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new { total_slots = 2, default_generation_settings = new { n_ctx = 2048 } }))))
        { BaseAddress = new("http://127.0.0.1/") };
        var error = await Assert.ThrowsAsync<LocalTranslationException>(() => new LlamaServerProtocol(http, 4096, 256).ValidateCapacityAsync(2, default));
        Assert.Equal("ServerCapacity", error.Category);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Session : ILlamaServerSession
    {
        public VulkanEvidence? Evidence => null;
        public bool Hold; public bool Disposed; public int Calls; public int Active; public int MaximumActive;
        public LocalCompletion Result = new("你好。", 3, "eos", false);
        public TaskCompletionSource TwoActive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnsureStartedAsync(CancellationToken token) => Task.CompletedTask;
        public Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token) =>
            Task.FromResult(new PreparedInferencePrompt(request.Messages, request.Cues[0].Id, 10, 0));
        public async Task<LocalCompletion> CompleteAsync(string prompt, IProgress<int>? progress, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active); MaximumActive = Math.Max(MaximumActive, active);
            if (active == 2) TwoActive.TrySetResult();
            try { if (Hold) await Task.Delay(Timeout.Infinite, token); return Result; }
            finally { Interlocked.Decrement(ref Active); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ConcurrentEngine : ITranslationEngine
    {
        private int _active;
        public int MaximumActive;
        public System.Collections.Concurrent.ConcurrentBag<TranslationRequest> Requests { get; } = [];
        public TranslationOutputFormat OutputFormat => TranslationOutputFormat.PlainText;
        public string CacheIdentity => "parallel-local-test";
        public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            Requests.Add(request);
            var active = Interlocked.Increment(ref _active); MaximumActive = Math.Max(MaximumActive, active);
            try { await Task.Delay(30 + (6 - int.Parse(request.Cues[0].Id)) * 10, token); return new("译文" + request.Cues[0].Id); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
