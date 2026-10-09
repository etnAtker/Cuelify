using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Tests;

public sealed class CloudTranslationTests
{
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    internal static HttpResponseMessage Success(string content = "{\"cue-1\":\"你好。\"}", string finish = "stop", string? reasoning = "不能作为字幕") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject
        {
            ["finish_reason"] = finish, ["message"] = new JsonObject { ["content"] = content, ["reasoning_content"] = reasoning }
        }) }.ToJsonString(), Encoding.UTF8, "application/json")
    };
    private static CloudTranslationOptions Options => new() { BaseUrl = "https://provider.example/v1", Model = "user-model" };
    private static TranslationRequest Request => new PromptBuilder().Build(PromptPresets.Cloud, new(), [TranslationPromptTests.Cue()], []);

    [Theory]
    [InlineData("https://provider.example/v1", "https://provider.example/v1/chat/completions")]
    [InlineData("https://provider.example/v1/", "https://provider.example/v1/chat/completions")]
    [InlineData("https://provider.example/v1/chat/completions", "https://provider.example/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    public void EndpointDoesNotDuplicateV1(string value, string expected) => Assert.Equal(expected, (Options with { BaseUrl = value }).Endpoint().AbsoluteUri);

    [Fact]
    public async Task GenericRequestUsesUserModelUnsetParametersAndFinalContentOnly()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-secret-key", request.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("user-model", body["model"]!.GetValue<string>());
            Assert.False(body.ContainsKey("temperature"));
            Assert.False(body.ContainsKey("reasoning_effort"));
            Assert.False(body.ContainsKey("thinking"));
            Assert.False(body["stream"]!.GetValue<bool>());
            return Success();
        });
        using var http = new HttpClient(handler);
        var engine = new OpenAiCompatibleTranslationEngine(http, () => "fake-secret-key", Options);
        Assert.DoesNotContain("fake-secret-key", engine.Preview(Request));
        Assert.DoesNotContain("fake-secret-key", engine.CacheIdentity);
        Assert.Equal("{\"cue-1\":\"你好。\"}", (await engine.TranslateAsync(Request, CancellationToken.None)).Content);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DeepSeekOnlySendsEffectiveParameters(bool thinking)
    {
        var options = Options with { BaseUrl = "https://api.deepseek.com", Temperature = .3, TopP = .95, PresencePenalty = 1, FrequencyPenalty = 1 };
        var body = CloudRequestBuilder.Build(options, Request.Messages, new(thinking, thinking ? "low" : null));
        Assert.Equal(thinking ? "enabled" : "disabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(thinking, body.ContainsKey("reasoning_effort"));
        Assert.Equal(!thinking, body.ContainsKey("temperature"));
        Assert.Equal(!thinking, body.ContainsKey("presence_penalty"));
        Assert.Equal(thinking, body.ContainsKey("top_p"));
    }

    [Theory]
    [InlineData("{\"model\":\"override\"}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"stream\":true}")]
    [InlineData("{\"enable_thinking\":true}")]
    [InlineData("{\"extra\":{\"API_KEY\":\"must-not-save\"}}")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public void InvalidAdvancedParametersAreRejectedBeforeSending(string parameters) => Assert.Throws<ArgumentException>(() =>
        new OpenAiCompatibleTranslationEngine(new HttpClient(), () => "unused", Options with { AdditionalParametersJson = parameters }));

    [Fact]
    public void DeepSeekUnsupportedThinkingSettingsFail()
    {
        Assert.Throws<ArgumentException>(() => CloudRequestBuilder.Build(Options with { BaseUrl = "https://api.deepseek.com", TopP = .6 }, Request.Messages, new(true)));
        Assert.Throws<ArgumentException>(() => new DeepSeekThinkingOptions(false, "high").Validate());
        Assert.Throws<ArgumentException>(() => new DeepSeekThinkingOptions(true, "medium").Validate());
        Assert.Throws<ArgumentException>(() => CloudRequestBuilder.Build(Options, Request.Messages, new(false)));
    }

    [Theory]
    [InlineData(401, false)] [InlineData(400, false)] [InlineData(429, true)] [InlineData(503, true)]
    public async Task ServiceErrorsAreClassifiedWithoutLeakingKey(int status, bool transient)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("echo fake-secret-key"), Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) }
        }));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TranslationServiceException>(() => new OpenAiCompatibleTranslationEngine(http, () => "fake-secret-key", Options).TranslateAsync(Request, CancellationToken.None));
        Assert.Equal(transient, exception.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.RetryAfter);
        Assert.DoesNotContain("fake-secret-key", exception.ToString());
    }

    [Theory]
    [InlineData("length")] [InlineData("content_filter")]
    public async Task IncompleteResponseIsNeverAccepted(string reason)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Success(finish: reason)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleTranslationEngine(http, () => "fake-secret-key", Options).TranslateAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutAndCancellationAreDistinct()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Success(); });
        using var http = new HttpClient(handler);
        var engine = new OpenAiCompatibleTranslationEngine(http, () => "fake-secret-key", Options with { Timeout = TimeSpan.FromMilliseconds(50) });
        await Assert.ThrowsAsync<TimeoutException>(() => engine.TranslateAsync(Request, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync(Request, cancellation.Token));
    }
}
