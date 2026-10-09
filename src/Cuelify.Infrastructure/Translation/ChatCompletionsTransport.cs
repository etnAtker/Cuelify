using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation;

public sealed class TranslationServiceException(HttpStatusCode status, TimeSpan? retryAfter) : Exception(
    $"翻译服务请求失败，HTTP {(int)status}；请检查认证、参数或服务状态。")
{
    public HttpStatusCode StatusCode { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsTransient => StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode >= 500;
}

public sealed class ChatCompletionsTransport(HttpClient http, Func<string?> apiKeyProvider)
{
    public async Task<TranslationResponse> SendAsync(Uri endpoint, JsonObject body, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n')) throw new ServiceCredentialException("翻译服务");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
                    retryAfter = date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
                throw new TranslationServiceException(response.StatusCode, retryAfter);
            }
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (json.Contains(key, StringComparison.Ordinal)) throw new InvalidDataException("服务响应包含认证信息，拒绝输出或缓存。");
            try
            {
                using var document = JsonDocument.Parse(json);
                var choices = document.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() != 1 || choices[0].GetProperty("finish_reason").GetString() != "stop")
                    throw new InvalidDataException("翻译响应未正常结束，不能缓存截断或被过滤的译文。");
                var content = choices[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("服务未返回译文，请检查模型设置后重试。");
                return new(content);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException("翻译响应结构无效，不能导出或缓存。");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("翻译服务请求超时。"); }
    }
}

public sealed class OpenAiCompatibleTranslationEngine : ITranslationEngine
{
    private readonly ChatCompletionsTransport _transport;
    private readonly CloudTranslationOptions _options;
    public TranslationOutputFormat OutputFormat => TranslationOutputFormat.CueIdJson;
    public string CacheIdentity { get; }
    public OpenAiCompatibleTranslationEngine(HttpClient http, Func<string?> apiKeyProvider, CloudTranslationOptions options)
    {
        _options = options;
        CacheIdentity = CloudRequestBuilder.Identity(options);
        _transport = new(http, apiKeyProvider);
    }
    public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        _transport.SendAsync(_options.Endpoint(), CloudRequestBuilder.Build(_options, request.Messages), _options.Timeout, cancellationToken);
    public string Preview(TranslationRequest request) => CloudRequestBuilder.Build(_options, request.Messages).ToJsonString();
}

public sealed class DeepSeekOfficialProvider : ITranslationEngine
{
    private readonly ChatCompletionsTransport _transport;
    private readonly CloudTranslationOptions _options;
    private readonly DeepSeekThinkingOptions _thinking;
    public TranslationOutputFormat OutputFormat => TranslationOutputFormat.CueIdJson;
    public string CacheIdentity { get; }
    public DeepSeekOfficialProvider(HttpClient http, Func<string?> apiKeyProvider, CloudTranslationOptions options, DeepSeekThinkingOptions thinking)
    {
        _options = options;
        _thinking = thinking;
        CacheIdentity = CloudRequestBuilder.Identity(options, thinking);
        _transport = new(http, apiKeyProvider);
    }
    public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        _transport.SendAsync(_options.Endpoint(), CloudRequestBuilder.Build(_options, request.Messages, _thinking), _options.Timeout, cancellationToken);
    public string Preview(TranslationRequest request) => CloudRequestBuilder.Build(_options, request.Messages, _thinking).ToJsonString();
    public Task<TranslationResponse> TestConnectionAsync(CancellationToken cancellationToken) =>
        _transport.SendAsync(_options.Endpoint(), CloudRequestBuilder.Build(_options with { MaximumTokens = 128 }, [new("user", "请只回复 OK。")], _thinking), _options.Timeout, cancellationToken);
}
