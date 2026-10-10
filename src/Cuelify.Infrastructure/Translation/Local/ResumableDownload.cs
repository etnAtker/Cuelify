using System.Net;
using System.Net.Http.Headers;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

internal static class ResumableDownload
{
    public static async Task DownloadAsync<T>(HttpClient http, string partial, T artifact, string url, long? expectedSize,
        IProgress<ModelDownloadProgress> progress, CancellationToken token) where T : class
    {
        var metadata = partial + ".json";
        var previous = await AtomicFile.ReadJsonAsync<T>(metadata, token);
        var offset = EqualityComparer<T>.Default.Equals(previous, artifact) && File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expectedSize is { } expected && offset > expected) offset = 0;
        if (offset == 0)
        {
            await using (var reset = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            await AtomicFile.WriteJsonAsync(metadata, artifact, token);
        }
        if (expectedSize is { } completeSize && offset == completeSize) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.To is null || (expectedSize is { } remoteSize && range.Length != remoteSize))
                throw new InvalidDataException("服务器返回的续传位置不正确，请重试下载。");
        }
        else offset = 0;
        var total = expectedSize ?? response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is { } length ? offset + length : null);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        var received = offset;
        progress.Report(new("下载中", received, total));
        var lastReport = Environment.TickCount64;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            received += count;
            if (total is { } maximum && received > maximum) throw new InvalidDataException("下载内容超过官方文件大小，请重试。");
            if (Environment.TickCount64 - lastReport >= 200)
            { progress.Report(new("下载中", received, total)); lastReport = Environment.TickCount64; }
        }
        await output.FlushAsync(token);
        output.Flush(true);
        if (total is { } finalSize && received != finalSize) throw new IOException("下载尚未完成，请继续下载。");
        progress.Report(new("下载中", received, total));
    }
}
