using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record HyMt2ModelOptions
{
    public const string FileName = "Hy-MT2-1.8B-Q4_K_M.gguf";
    public const string Sha256 = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699";
    public string ModelPath { get; init; } = Path.Combine("assets", "models", FileName);
    public int GpuLayers { get; init; } = 99;
    public uint ContextSize { get; init; } = 2048;
    public int MaximumTokens { get; init; } = 256;
    public TimeSpan InferenceTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public void Validate()
    {
        if (GpuLayers <= 0 || ContextSize is < 512 or > 32768 || MaximumTokens <= 0 || MaximumTokens > ContextSize / 2 ||
            InferenceTimeout <= TimeSpan.Zero || InferenceTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("本地模型参数无效，请检查 GPU 卸载层数、上下文长度、最大输出长度和超时时间。");
    }
    public static async Task VerifyIdentityAsync(string modelPath, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFileName(modelPath), FileName, StringComparison.Ordinal)) throw new InvalidDataException($"仅支持腾讯官方 {FileName}。");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到指定 GGUF 模型。", modelPath);
        if (await AtomicFile.HashFileAsync(modelPath, cancellationToken) != Sha256) throw new InvalidDataException("模型文件不完整或版本不匹配，请重新下载官方 Hy-MT2-1.8B-Q4_K_M.gguf 文件。");
    }
}
