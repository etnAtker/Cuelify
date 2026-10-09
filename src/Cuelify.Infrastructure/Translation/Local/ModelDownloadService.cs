using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record ModelDownloadProgress(string Stage, long Bytes = 0, long? TotalBytes = null);
public sealed record ModelDownloadArtifact(string Repository, string Revision, string FileName, long? Size, string? Sha256)
{
    public string Url => $"https://huggingface.co/{Repository}/resolve/{Uri.EscapeDataString(Revision)}/{string.Join('/', FileName.Split('/').Select(Uri.EscapeDataString))}";
}
public interface IModelDownloadService
{
    Task<EmbeddedModelFile> DownloadAsync(EmbeddedModel model, string root, IProgress<ModelDownloadProgress> progress, CancellationToken token);
    Task<EmbeddedModelFile> VerifyAsync(EmbeddedModel model, string path, IProgress<ModelDownloadProgress> progress, CancellationToken token);
}

public sealed class ModelDownloadService(HttpClient http) : IModelDownloadService
{
    public async Task<ModelDownloadArtifact> ResolveAsync(EmbeddedModel model, CancellationToken token)
    {
        using var response = await http.GetAsync($"https://huggingface.co/api/models/{model.Repository}?blobs=true", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var revision = json.RootElement.GetProperty("sha").GetString();
        var files = json.RootElement.GetProperty("siblings").EnumerateArray().Where(file =>
        {
            var name = file.GetProperty("rfilename").GetString() ?? "";
            return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(name, $@"(?:^|[-._]){model.Quantization}(?:\.gguf)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }).ToArray();
        if (string.IsNullOrWhiteSpace(revision) || files.Length != 1)
            throw new InvalidDataException("官方仓库中的模型文件无法确定，请稍后重试或手动选择模型文件。");
        var selected = files[0];
        var fileName = selected.GetProperty("rfilename").GetString()!;
        long? size = selected.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var length) ? length : null;
        string? hash = null;
        if (selected.TryGetProperty("lfs", out var lfs))
        {
            if (lfs.TryGetProperty("size", out bytes) && bytes.TryGetInt64(out length)) size = length;
            if (lfs.TryGetProperty("sha256", out var digest)) hash = digest.GetString()?.ToLowerInvariant();
        }
        if (size is <= 0 || (hash is not null && !Regex.IsMatch(hash, "^[0-9a-f]{64}$")))
            throw new InvalidDataException("官方模型信息无效，请稍后重试。");
        return new(model.Repository, revision, fileName, size, hash);
    }

    public async Task<EmbeddedModelFile> VerifyAsync(EmbeddedModel model, string path, IProgress<ModelDownloadProgress> progress, CancellationToken token)
    {
        progress.Report(new("查询官方模型信息"));
        var artifact = await ResolveAsync(model, token);
        progress.Report(new("校验模型"));
        return new(Path.GetFullPath(path), await VerifyFileAsync(path, artifact, token));
    }

    public async Task<EmbeddedModelFile> DownloadAsync(EmbeddedModel model, string root, IProgress<ModelDownloadProgress> progress, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, model.DefaultFileName);
        await using var downloadLock = new FileStream(target + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress.Report(new("查询官方模型信息"));
        var artifact = await ResolveAsync(model, token);
        if (File.Exists(target))
        {
            progress.Report(new("校验已有模型"));
            try { return new(target, await VerifyFileAsync(target, artifact, token)); }
            catch (InvalidDataException) { /* 新版本写入临时文件，保留当前正式模型。 */ }
        }
        var partial = target + ".partial";
        var metadata = partial + ".json";
        var previous = await AtomicFile.ReadJsonAsync<ModelDownloadArtifact>(metadata, token);
        var offset = previous == artifact && File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (artifact.Size is { } expected && offset > expected) offset = 0;
        if (offset == 0)
        {
            await using (var reset = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)) { }
            await AtomicFile.WriteJsonAsync(metadata, artifact, token);
        }
        if (artifact.Size is not { } total || offset != total)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset || range.To is null || (artifact.Size is { } remoteSize && range.Length != remoteSize))
                    throw new InvalidDataException("服务器返回的续传位置不正确，请重试下载。");
            }
            else offset = 0; // 未支持 Range 时从头写入，不能将完整响应追加到旧片段。
            var totalBytes = artifact.Size ?? response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is { } contentLength ? offset + contentLength : null);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                var received = offset;
                progress.Report(new("下载中", received, totalBytes));
                var lastReport = Environment.TickCount64;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    received += count;
                    if (totalBytes is { } maximum && received > maximum) throw new InvalidDataException("下载内容超过官方模型大小，请重试。");
                    if (Environment.TickCount64 - lastReport >= 200)
                    { progress.Report(new("下载中", received, totalBytes)); lastReport = Environment.TickCount64; }
                }
                await output.FlushAsync(token);
                output.Flush(true);
                if (totalBytes is { } finalSize && received != finalSize) throw new IOException("下载尚未完成，请继续下载。");
            }
        }
        progress.Report(new("校验模型"));
        string sha256;
        try { sha256 = await VerifyFileAsync(partial, artifact, token); }
        catch (InvalidDataException)
        {
            // 坏片段无法续传；只移除本服务持有的临时文件，不覆盖正式模型。
            File.Delete(partial); File.Delete(metadata); throw;
        }
        token.ThrowIfCancellationRequested();
        File.Move(partial, target, true);
        File.Delete(metadata);
        return new(target, sha256);
    }

    private static async Task<string> VerifyFileAsync(string path, ModelDownloadArtifact artifact, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到模型文件。", path);
        if (artifact.Size is { } size && info.Length != size) throw new InvalidDataException("模型大小与官方文件不符，请重新下载。");
        return await EmbeddedModelOptions.VerifyIdentityAsync(path, token, artifact.Sha256 ?? "");
    }
}
