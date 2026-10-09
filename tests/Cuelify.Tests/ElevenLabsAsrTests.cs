using System.Net;
using System.Net.Http.Headers;
using Cuelify.Infrastructure.Speech;
using Xunit;

namespace Cuelify.Tests;

public sealed class ElevenLabsAsrTests
{
    private const string FakeKey = "test-only-secret";
    private const string ResponseJson = """
        {"language_code":"en","words":[
          {"text":"world","start":1.25,"end":1.75,"type":"word","speaker_id":"speaker_1"},
          {"text":" ","start":1.0,"end":1.2,"type":"spacing"},
          {"text":"(music)","type":"audio_event"},
          {"text":"Hello","start":0.2,"end":0.8,"type":"word"}]}
        """;

    [Fact]
    public async Task SendsOfficialMultipartAndPreservesLocalWordTimes()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            using var handler = new FakeHandler(async (request, ct) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", request.RequestUri!.AbsoluteUri);
                Assert.Equal(FakeKey, Assert.Single(request.Headers.GetValues("xi-api-key")));
                var parts = Assert.IsType<MultipartFormDataContent>(request.Content);
                var fields = new Dictionary<string, string>();
                foreach (var part in parts)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "file") Assert.Equal(new byte[] { 1, 2, 3, 4 }, await part.ReadAsByteArrayAsync(ct));
                    else fields[name] = await part.ReadAsStringAsync(ct);
                }
                Assert.Equal("scribe_v2", fields["model_id"]);
                Assert.Equal("word", fields["timestamps_granularity"]);
                Assert.Equal("false", fields["diarize"]);
                Assert.Equal("false", fields["use_multi_channel"]);
                Assert.Equal("false", fields["tag_audio_events"]);
                Assert.Equal("en", fields["language_code"]);
                Assert.Equal("other", fields["file_format"]);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseJson) };
            });
            using var http = new HttpClient(handler);
            var result = await new ElevenLabsAsrClient(http, () => FakeKey, new() { LanguageCode = "en" }).TranscribeAsync(file, CancellationToken.None);
            Assert.Equal("en", result.LanguageCode);
            Assert.Equal(2, result.Words.Count);
            Assert.Equal("Hello", result.Words[0].Text);
            Assert.Equal(TimeSpan.FromSeconds(1.25), result.Words[1].Start);
            Assert.Equal("speaker_1", result.Words[1].SpeakerId);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task ClassifiesFailuresWithoutLeakingSecretsOrRetrying(HttpStatusCode status, bool transient)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2]);
            using var handler = new FakeHandler((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent($"server echoed {FakeKey}") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var exception = await Assert.ThrowsAsync<AsrServiceException>(() => new ElevenLabsAsrClient(http, () => FakeKey).TranscribeAsync(file, CancellationToken.None));
            Assert.Equal(transient, exception.IsTransient);
            Assert.Equal(TimeSpan.FromSeconds(3), exception.RetryAfter);
            Assert.DoesNotContain(FakeKey, exception.ToString());
            Assert.Equal(1, handler.Calls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task MissingKeyAndOversizedUploadNeverSendRequest()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("不应发送请求"));
        using var http = new HttpClient(handler);
        var missing = await Assert.ThrowsAsync<Cuelify.Infrastructure.ServiceCredentialException>(() => new ElevenLabsAsrClient(http, () => null).TranscribeAsync("not-needed.wav", CancellationToken.None));
        Assert.Contains("ElevenLabs", missing.Message);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            await Assert.ThrowsAsync<InvalidDataException>(() => new ElevenLabsAsrClient(http, () => FakeKey, new() { MaximumUploadBytes = 2 }).TranscribeAsync(file, CancellationToken.None));
            Assert.Equal(0, handler.Calls);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"transcripts\":[]}")]
    [InlineData("{\"words\":[{\"type\":\"word\",\"text\":\"hi\",\"start\":-1,\"end\":1}]}")]
    [InlineData("{\"words\":[{\"type\":\"word\",\"text\":\"hi\",\"start\":2,\"end\":1}]}")]
    public void RejectsMalformedResponsesAndInvalidTimestamps(string json) => Assert.Throws<InvalidDataException>(() => ElevenLabsTranscriptParser.Parse(json));

    [Fact]
    public void PreservesZeroDurationWordsForCueMerging()
    {
        var result = ElevenLabsTranscriptParser.Parse("""
            {"language_code":"jpn","words":[
              {"type":"word","text":"你","start":1,"end":1},
              {"type":"word","text":"吃饭了吗","start":1,"end":2.5}]}
            """);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal(result.Words[0].Start, result.Words[0].End);
        Assert.Equal("你", result.Words[0].Text);
    }

    [Fact]
    public async Task CancellationPropagatesToHttpHandler()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2]);
            using var cancellation = new CancellationTokenSource();
            using var handler = new FakeHandler(async (_, ct) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException();
            });
            using var http = new HttpClient(handler);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ElevenLabsAsrClient(http, () => FakeKey).TranscribeAsync(file, cancellation.Token));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task TimeoutStopsRequestWithoutAutomaticRetry()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2]);
            using var handler = new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException();
            });
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ElevenLabsAsrClient(http, () => FakeKey,
                new() { Timeout = TimeSpan.FromMilliseconds(50) }).TranscribeAsync(file, CancellationToken.None));
            Assert.Equal(1, handler.Calls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void InvalidConfigurationIsRejectedBeforeNetwork()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("不应发送请求"));
        using var http = new HttpClient(handler);
        Assert.Throws<ArgumentException>(() => new ElevenLabsAsrClient(http, () => FakeKey, new() { FileFormat = "invalid" }));
        Assert.Throws<ArgumentException>(() => new ElevenLabsAsrClient(http, () => FakeKey, new() { LanguageCode = "english" }));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }
}
