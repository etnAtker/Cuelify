using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record EmbeddedModelOptions
{
    public string ModelId { get; init; } = EmbeddedModelCatalog.Default.Id;
    public string ModelPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify", EmbeddedModelCatalog.Default.DefaultFileName);
    public string ExpectedSha256 { get; init; } = "";
    public EmbeddedModel Model => EmbeddedModelCatalog.Get(ModelId);
    public int GpuLayers { get; init; } = 99;
    public uint ContextSize { get; init; } = 4096;
    public int MaximumTokens { get; init; } = 256;
    public TimeSpan InferenceTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public void Validate()
    {
        _ = Model;
        if (GpuLayers <= 0 || ContextSize is < 512 or > 32768 || MaximumTokens <= 0 || MaximumTokens > ContextSize / 2 ||
            InferenceTimeout <= TimeSpan.Zero || InferenceTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("本地模型参数无效，请检查 GPU 卸载层数、上下文长度、最大输出长度和超时时间。");
    }
    public static async Task<string> VerifyIdentityAsync(string modelPath, CancellationToken cancellationToken, string expectedSha256 = "")
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到指定 GGUF 模型。", modelPath);
        await using (var file = File.OpenRead(modelPath))
        {
            var magic = new byte[4];
            if (await file.ReadAsync(magic, cancellationToken) != 4 || !magic.AsSpan().SequenceEqual("GGUF"u8))
                throw new InvalidDataException("模型不是有效的 GGUF 文件，请重新下载或选择模型。");
        }
        var hash = await AtomicFile.HashFileAsync(modelPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedSha256) && hash != expectedSha256)
            throw new InvalidDataException("模型文件已改变或不完整，请重新下载或选择模型。");
        return hash;
    }
}
