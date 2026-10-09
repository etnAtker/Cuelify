using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LLama.Native;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record NativeModule(string Path, string Sha256);
public static class VulkanRuntime
{
    private static readonly object Sync = new();
    private static readonly object InitializationSync = new();
    private static readonly StringBuilder Log = new();
    private static bool _initialized;
    public static void Initialize()
    {
        lock (InitializationSync)
        {
            if (_initialized) return;
            NativeLibraryConfig.All.WithCuda(false).WithVulkan(true).WithAutoFallback(false).WithLogCallback((_, message) =>
            {
                lock (Sync) Log.Append(message);
            });
            NativeApi.llama_empty_call();
            if (!VulkanEvidence.HasVulkanNativeModules(ReadNativeModules().Select(module => module.Path)) || !NativeApi.llama_supports_gpu_offload())
                throw new InvalidOperationException("实际加载的原生后端不符合 Vulkan GPU 要求，拒绝 CPU-only 回退。");
            _initialized = true;
        }
    }
    public static string Snapshot() { lock (Sync) return Log.ToString(); }
    public static List<NativeModule> ReadNativeModules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>()
            .Where(module => module.ModuleName.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Equals("llama.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module =>
            {
                using var file = File.OpenRead(module.FileName);
                return new NativeModule(module.FileName, Convert.ToHexStringLower(SHA256.HashData(file)));
            }).ToList();
    }
}
