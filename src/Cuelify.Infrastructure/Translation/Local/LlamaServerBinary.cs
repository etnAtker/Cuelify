using System.Text.RegularExpressions;
using Cuelify.Core.Media;
using Cuelify.Infrastructure.Media;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record LlamaServerBinary(string Path, string Version, string Identity, string Device)
{
    public static string FileVersionIdentity(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new LocalTranslationException("ServerMissing", "找不到 llama-server，请下载 llama.cpp 或选择完整运行包中的 llama-server.exe。");
        path = System.IO.Path.GetFullPath(path);
        var files = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!, "*.dll").Append(path)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        return AtomicFile.Hash(files.Select(ModelFileVersion.Read).ToArray());
    }

    public static async Task<string> FingerprintAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new LocalTranslationException("ServerMissing", "找不到 llama-server，请下载 llama.cpp 或选择完整运行包中的 llama-server.exe。");
        path = System.IO.Path.GetFullPath(path);
        var files = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!, "*.dll")
            .Append(path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var hashes = new List<object>();
        foreach (var file in files)
            hashes.Add(new { Name = System.IO.Path.GetFileName(file), Sha256 = await AtomicFile.HashFileAsync(file, token) });
        return AtomicFile.Hash(hashes);
    }

    public static async Task<LlamaServerBinary> InspectAsync(string path, CancellationToken token, ICommandRunner? runner = null)
    {
        var identity = await FingerprintAsync(path, token);
        runner ??= new ProcessCommandRunner();
        path = System.IO.Path.GetFullPath(path);
        try
        {
            var help = await runner.RunAsync(new(path, ["--help"], TimeSpan.FromSeconds(30)), null, token);
            var text = help.StandardOutput + help.StandardError;
            if (help.ExitCode != 0 || new[] { "--parallel", "--host", "--port", "--device", "--no-webui", "--no-kv-unified", "--fit", "--cors-origins" }
                .Any(option => !text.Contains(option, StringComparison.Ordinal)))
                throw new LocalTranslationException("ServerIncompatible", "所选程序不是兼容的 llama-server，请下载当前 llama.cpp Vulkan 运行包。");
            var version = await runner.RunAsync(new(path, ["--version"], TimeSpan.FromSeconds(30)), null, token);
            var devices = await runner.RunAsync(new(path, ["--list-devices"], TimeSpan.FromSeconds(30)), null, token);
            var device = Regex.Match(devices.StandardOutput + devices.StandardError, @"(?m)^\s*(Vulkan\d+):", RegexOptions.CultureInvariant);
            if (version.ExitCode != 0 || devices.ExitCode != 0 || !device.Success)
                throw new LocalTranslationException("VulkanOffload", "运行包没有可用的 Vulkan 显卡，请检查完整运行包和显卡驱动。");
            var versionText = (version.StandardOutput + version.StandardError).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("version:", StringComparison.OrdinalIgnoreCase)) ?? "llama.cpp";
            if (await FingerprintAsync(path, token) != identity)
                throw new LocalTranslationException("ServerChanged", "运行程序在检查时发生变化，请重新测试。");
            return new(path, versionText.Trim(), identity, device.Groups[1].Value);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        { throw new LocalTranslationException("NativeLibrary", "无法启动 llama-server，请选择完整的 Windows x64 Vulkan 运行包，并检查文件权限。"); }
    }
}
