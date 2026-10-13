namespace Cuelify.Infrastructure;

internal static class RetryDelay
{
    public static TimeSpan? Calculate(TimeSpan initial, TimeSpan maximum, int attempt, TimeSpan? retryAfter, bool exponential)
    {
        var delay = exponential
            ? TimeSpan.FromTicks((long)Math.Min(maximum.Ticks, initial.Ticks * Math.Pow(2, attempt - 1)))
            : initial;
        // 分句沿用服务指定延迟；识别/翻译把 Retry-After 作为最低等待时间。
        if (retryAfter is { } specified && (!exponential || specified > delay)) delay = specified;
        return delay > maximum ? null : delay;
    }
}
