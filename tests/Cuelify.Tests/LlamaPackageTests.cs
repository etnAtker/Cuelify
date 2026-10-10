using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class LlamaPackageTests
{
    private static byte[] Package(string? malicious = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "bin/llama-server.exe", "bin/ggml-vulkan.dll", "bin/ggml-base.dll" }.Concat(malicious is null ? [] : new[] { malicious }))
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("test-package-" + name); }
        }
        return stream.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static LlamaPackageArtifact Artifact(byte[] bytes, string version = "b-test") =>
        new(version, "llama-b-test-bin-win-vulkan-x64.zip", "https://github.com/ggml-org/llama.cpp/releases/download/b-test/package.zip", bytes.Length, Hash(bytes));
    private static HttpResponseMessage Releases(LlamaPackageArtifact artifact) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new[]
        {
            new { tag_name = "stable-without-package", draft = false, prerelease = false, assets = Array.Empty<object>() },
            new { tag_name = artifact.Version, draft = false, prerelease = true, assets = new object[]
            { new { name = artifact.FileName, browser_download_url = artifact.Url, size = artifact.Size, digest = "sha256:" + artifact.Sha256 } } }
        }))
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Sink(Action<ModelDownloadProgress>? action = null) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => action?.Invoke(value); }
    private static HttpResponseMessage Content(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task InstallsRollingVulkanPackageAndKeepsPriorVersionAndLicense()
    {
        using var directory = new TestDirectory();
        var bytes = Package(); var artifact = Artifact(bytes);
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        { "api.github.com" => Releases(artifact), "raw.githubusercontent.com" => Content(Encoding.UTF8.GetBytes("MIT test license")), _ => Content(bytes) })));
        var service = new LlamaPackageService(http);
        var first = await service.DownloadAsync(directory.File("配置 文件"), new Sink(), default);
        Assert.StartsWith(Path.GetFullPath(directory.File("配置 文件/llama.cpp")), first.ServerPath);
        Assert.True(File.Exists(first.ServerPath));
        var firstRoot = Directory.GetParent(Path.GetDirectoryName(first.ServerPath)!)!.FullName;
        Assert.Contains("MIT", await File.ReadAllTextAsync(Path.Combine(firstRoot, "llama.cpp-LICENSE.txt")));
        artifact = Artifact(bytes, "next-version");
        var second = await service.DownloadAsync(directory.File("配置 文件"), new Sink(), default);
        Assert.NotEqual(first.ServerPath, second.ServerPath); Assert.True(File.Exists(first.ServerPath));
    }

    [Theory]
    [InlineData("../escaped.exe")]
    [InlineData("bin/../../escaped.exe")]
    [InlineData("bin/llama-server.exe:stream")]
    public async Task RejectsUnsafeZipPathsAndLeavesExistingInstall(string malicious)
    {
        using var directory = new TestDirectory(); var bytes = Package(malicious); var artifact = Artifact(bytes);
        var existing = directory.File("llama.cpp/previous/llama-server.exe"); Directory.CreateDirectory(Path.GetDirectoryName(existing)!); await File.WriteAllTextAsync(existing, "old");
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Releases(artifact) : Content(bytes))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LlamaPackageService(http).DownloadAsync(Path.GetDirectoryName(directory.File("unused"))!, new Sink(), default));
        Assert.Equal("old", await File.ReadAllTextAsync(existing)); Assert.False(File.Exists(directory.File("escaped.exe")));
    }

    [Fact]
    public async Task CancellationKeepsVerifiedPartialForResume()
    {
        using var directory = new TestDirectory(); var bytes = Package(); var artifact = Artifact(bytes);
        var root = Path.GetDirectoryName(directory.File("unused"))!;
        var partial = directory.File("llama.cpp/package.zip.partial"); Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, bytes[..10]);
        await AtomicFile.WriteJsonAsync(partial + ".json", artifact, default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = true;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Releases(artifact);
            if (request.RequestUri.Host == "raw.githubusercontent.com") return Content(Encoding.UTF8.GetBytes("MIT"));
            Assert.Equal(10, request.Headers.Range!.Ranges.Single().From); started.TrySetResult();
            if (block) await Task.Delay(Timeout.Infinite, token);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[10..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, bytes.Length - 1, bytes.Length);
            return response;
        }));
        var service = new LlamaPackageService(http); using var cancellation = new CancellationTokenSource();
        var download = service.DownloadAsync(root, new Sink(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(bytes[..10], await File.ReadAllBytesAsync(partial));
        block = false; var installed = await service.DownloadAsync(root, new Sink(), default);
        Assert.True(File.Exists(installed.ServerPath)); Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task RejectsHashMismatchBeforeExtracting()
    {
        using var directory = new TestDirectory(); var bytes = Package(); var artifact = Artifact(bytes) with { Sha256 = new string('0', 64) };
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Releases(artifact) : Content(bytes))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LlamaPackageService(http).DownloadAsync(Path.GetDirectoryName(directory.File("unused"))!, new Sink(), default));
        Assert.Empty(Directory.GetFiles(directory.File("llama.cpp"), "*.exe", SearchOption.AllDirectories));
    }
}
