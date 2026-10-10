using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record LlamaPackageArtifact(string Version, string FileName, string Url, long Size, string Sha256);
public sealed record LlamaPackageInstallation(string ServerPath, string Version, string PackageSha256);
public interface ILlamaPackageService
{
    Task<LlamaPackageInstallation> DownloadAsync(string root, IProgress<ModelDownloadProgress> progress, CancellationToken token);
    Task<LlamaServerBinary> InspectAsync(string path, CancellationToken token);
}

public sealed class LlamaPackageService(HttpClient http) : ILlamaPackageService
{
    public Task<LlamaServerBinary> InspectAsync(string path, CancellationToken token) => LlamaServerBinary.InspectAsync(path, token);

    public async Task<LlamaPackageArtifact> ResolveAsync(CancellationToken token)
    {
        // 滚动构建可能标记 prerelease；latest 接口只返回正式版，不能据此找到当前运行包。
        for (var page = 1; page <= 3; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30&page={page}");
            request.Headers.UserAgent.ParseAdd("Cuelify");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean()) continue;
                var version = release.GetProperty("tag_name").GetString() ?? "";
                if (!Regex.IsMatch(version, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$")) continue;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!Regex.IsMatch(name, @"^llama-[a-zA-Z0-9._-]+-bin-win-vulkan-x64\.zip$")) continue;
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    var size = asset.GetProperty("size").GetInt64();
                    var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
                    if (!url.StartsWith("https://github.com/ggml-org/llama.cpp/releases/download/", StringComparison.Ordinal) ||
                        size is <= 0 or > 536_870_912 || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                        throw new InvalidDataException("官方 llama.cpp 运行包信息或 SHA-256 校验值无效，请稍后重试。");
                    return new(version, name, url, size, digest[7..].ToLowerInvariant());
                }
            }
            if (json.RootElement.GetArrayLength() < 30) break;
        }
        throw new InvalidDataException("未找到官方 Windows x64 Vulkan 运行包，请稍后重试或手动选择 llama-server.exe。");
    }

    public async Task<LlamaPackageInstallation> DownloadAsync(string root, IProgress<ModelDownloadProgress> progress, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetFullPath(root), "llama.cpp");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("llama.cpp 下载目录不能是文件夹链接，请使用普通文件夹。");
        await using var downloadLock = new FileStream(Path.Combine(directory, "package.download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress.Report(new("查询 llama.cpp 运行包"));
        var artifact = await ResolveAsync(token);
        var partial = Path.Combine(directory, "package.zip.partial");
        await ResumableDownload.DownloadAsync(http, partial, artifact, artifact.Url, artifact.Size, progress, token);
        progress.Report(new("校验 llama.cpp 运行包"));
        if (await AtomicFile.HashFileAsync(partial, token) != artifact.Sha256)
        {
            File.Delete(partial); File.Delete(partial + ".json");
            throw new InvalidDataException("llama.cpp 运行包校验失败，请重新下载。");
        }
        var target = Path.Combine(directory, artifact.Version + "-" + artifact.Sha256[..12]);
        var staging = Path.Combine(directory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report(new("解压 llama.cpp 运行包"));
            await ExtractAsync(partial, staging, token);
            // 官方包中配套 DLL 和目录布局全部保留。
            var servers = Directory.GetFiles(staging, "llama-server.exe", SearchOption.AllDirectories);
            if (servers.Length != 1 || !File.Exists(Path.Combine(Path.GetDirectoryName(servers[0])!, "ggml-vulkan.dll")))
                throw new InvalidDataException("运行包缺少 llama-server.exe 或 Vulkan 配套库，请重新下载。");
            var relative = Path.GetRelativePath(staging, servers[0]);
            using var licenseRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://raw.githubusercontent.com/ggml-org/llama.cpp/{Uri.EscapeDataString(artifact.Version)}/LICENSE");
            using var license = await http.SendAsync(licenseRequest, token);
            license.EnsureSuccessStatusCode();
            await AtomicFile.WriteTextAsync(Path.Combine(staging, "llama.cpp-LICENSE.txt"), await license.Content.ReadAsStringAsync(token), false, token);
            var installation = new LlamaPackageInstallation(Path.Combine(target, relative), artifact.Version, artifact.Sha256);
            await AtomicFile.WriteJsonAsync(Path.Combine(staging, "installation.json"), installation, token);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                // 同一资产已安装时核对全部文件，不能原地覆盖仍被旧任务引用的运行包。
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    var installed = Path.Combine(target, Path.GetRelativePath(staging, file));
                    if (!File.Exists(installed) || await AtomicFile.HashFileAsync(installed, token) != await AtomicFile.HashFileAsync(file, token))
                        throw new InvalidDataException("已安装的同版本运行包发生变化，请选择其他完整运行包或移走损坏版本后重试。");
                }
            }
            else Directory.Move(staging, target);
            File.Delete(partial); File.Delete(partial + ".json");
            return installation;
        }
        finally
        {
            // staging 由本次调用创建，路径在已核对的专用目录下，且拒绝解压链接。
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task ExtractAsync(string archivePath, string staging, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10_000) throw new InvalidDataException("运行包文件数量异常。");
        var prefix = staging + Path.DirectorySeparatorChar;
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("运行包包含不安全的文件路径或链接。");
            size = checked(size + entry.Length);
            if (size > 2_147_483_648) throw new InvalidDataException("运行包解压大小异常。");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await input.CopyToAsync(output, token);
            if (output.Length != entry.Length) throw new InvalidDataException("运行包解压文件不完整。");
        }
    }
}
