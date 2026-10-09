using System.Net;

namespace Cuelify.Infrastructure.Speech;

public sealed class AsrServiceException(HttpStatusCode statusCode, TimeSpan? retryAfter) : Exception(
    $"ElevenLabs 转写失败，HTTP {(int)statusCode}；{(statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "请检查 API Key 和端点权限。" : "请检查请求参数或服务状态。")}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsTransient { get; } = statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
