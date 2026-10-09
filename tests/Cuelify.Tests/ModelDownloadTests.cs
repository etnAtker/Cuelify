using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class ModelDownloadTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("GGUF模型下载测试数据");
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static EmbeddedModel Model => EmbeddedModelCatalog.Default;
    private static HttpResponseMessage Metadata(string revision = "revision-a", string name = "renamed-Q6_K.gguf", byte[]? payload = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { sha = revision, siblings = new[] { new { rfilename = name, size = (payload ?? Payload).Length, lfs = new { sha256 = Hash(payload ?? Payload), size = (payload ?? Payload).Length } } } }))
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Sink : IProgress<ModelDownloadProgress> { public void Report(ModelDownloadProgress value) { } }

    [Fact]
    public async Task DiscoversOfficialRenameAndRevisionAndValidatesRealBytes()
    {
        using var directory = new TestDirectory();
        var version = "new-version";
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/")) return Task.FromResult(Metadata(version, "new-name-Q6_K.gguf"));
            Assert.Contains("/resolve/new-version/new-name-Q6_K.gguf", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        });
        using var http = new HttpClient(handler);
        var result = await new ModelDownloadService(http).DownloadAsync(Model, directory.File("模型 配置"), new Sink(), default);
        Assert.Equal(Hash(Payload), result.Sha256); Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal(Path.Combine(directory.File("模型 配置"), Model.DefaultFileName), result.Path);
        Assert.False(File.Exists(result.Path + ".partial"));
        version = "next-version";
        Assert.Equal(version, (await new ModelDownloadService(http).ResolveAsync(Model, default)).Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumesAtVerifiedOffsetOrRestartsIfServerIgnoresRange(bool range)
    {
        using var directory = new TestDirectory();
        var path = directory.File(Model.DefaultFileName);
        await File.WriteAllBytesAsync(path + ".partial", Payload[..4]);
        await AtomicFile.WriteJsonAsync(path + ".partial.json", new ModelDownloadArtifact(Model.Repository, "revision-a", "renamed-Q6_K.gguf", Payload.Length, Hash(Payload)), default);
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/")) return Task.FromResult(Metadata());
            Assert.Equal(4, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(range ? Payload[4..] : Payload) };
            if (range) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, Payload.Length - 1, Payload.Length);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var file = await new ModelDownloadService(http).DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task NewOfficialVersionNeverAppendsOldPartialBytes()
    {
        using var directory = new TestDirectory();
        var path = directory.File(Model.DefaultFileName);
        await File.WriteAllBytesAsync(path + ".partial", [1, 2, 3, 4]);
        await AtomicFile.WriteJsonAsync(path + ".partial.json", new ModelDownloadArtifact(Model.Repository, "old", "old-Q6_K.gguf", Payload.Length, Hash(Payload)), default);
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/")) return Task.FromResult(Metadata());
            Assert.Null(request.Headers.Range);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        });
        using var http = new HttpClient(handler);
        await new ModelDownloadService(http).DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CorruptedDownloadPreservesOldModelAndRemovesUnusablePartial()
    {
        using var directory = new TestDirectory();
        var path = directory.File(Model.DefaultFileName);
        var old = Encoding.UTF8.GetBytes("GGUF旧模型"); await File.WriteAllBytesAsync(path, old);
        var damaged = Payload.ToArray(); damaged[^1] ^= 1;
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/api/models/") ? Metadata() :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(damaged) }));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModelDownloadService(http).DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), default));
        Assert.Equal(old, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task ExistingCurrentFileNeedsNoDownloadAndCustomFilenameCanBeVerified()
    {
        using var directory = new TestDirectory();
        var path = directory.File(Model.DefaultFileName); await File.WriteAllBytesAsync(path, Payload);
        var calls = 0;
        using var handler = new Handler((request, _) => { calls++; Assert.Contains("/api/models/", request.RequestUri!.AbsolutePath); return Task.FromResult(Metadata()); });
        using var http = new HttpClient(handler); var service = new ModelDownloadService(http);
        await service.DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), default);
        var custom = directory.File("重命名.gguf"); await File.WriteAllBytesAsync(custom, Payload);
        Assert.Equal(Hash(Payload), (await service.VerifyAsync(Model, custom, new Sink(), default)).Sha256);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationRetainsPartialAndDoesNotReplaceExistingModel()
    {
        using var directory = new TestDirectory();
        var path = directory.File(Model.DefaultFileName);
        var old = Encoding.UTF8.GetBytes("GGUF旧模型"); await File.WriteAllBytesAsync(path, old);
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/")) return Task.FromResult(Metadata());
            cancellation.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception();
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelDownloadService(http).DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), cancellation.Token));
        Assert.Equal(old, await File.ReadAllBytesAsync(path)); Assert.True(File.Exists(path + ".partial.json"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task HttpFailuresAreReportedWithoutSavingACompletedFile(int status)
    {
        using var directory = new TestDirectory();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new ModelDownloadService(http).DownloadAsync(Model, directory.File("root"), new Sink(), default));
        Assert.False(File.Exists(directory.File(Path.Combine("root", Model.DefaultFileName))));
    }

    [Fact]
    public async Task IncorrectResumeRangeIsRejected()
    {
        using var directory = new TestDirectory(); var path = directory.File(Model.DefaultFileName);
        await File.WriteAllBytesAsync(path + ".partial", Payload[..4]);
        await AtomicFile.WriteJsonAsync(path + ".partial.json", new ModelDownloadArtifact(Model.Repository, "revision-a", "renamed-Q6_K.gguf", Payload.Length, Hash(Payload)), default);
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/")) return Task.FromResult(Metadata());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Payload.Length - 1, Payload.Length);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModelDownloadService(http).DownloadAsync(Model, Path.GetDirectoryName(path)!, new Sink(), default));
        Assert.Equal(Payload[..4], await File.ReadAllBytesAsync(path + ".partial"));
    }
}
