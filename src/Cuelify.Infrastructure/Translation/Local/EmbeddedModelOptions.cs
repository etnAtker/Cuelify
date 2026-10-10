namespace Cuelify.Infrastructure.Translation.Local;

public sealed record EmbeddedModelOptions
{
    public string ModelId { get; init; } = EmbeddedModelCatalog.Default.Id;
    public string ModelPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify", EmbeddedModelCatalog.Default.DefaultFileName);
    public ModelFileVersion? FileVersion { get; init; }
    public string ServerPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify", "llama.cpp", "llama-server.exe");
    public int Concurrency { get; init; } = 2;
    public EmbeddedModel Model => EmbeddedModelCatalog.Get(ModelId);
    public int GpuLayers { get; init; } = 99;
    public uint ContextSize { get; init; } = 4096;
    public int MaximumTokens { get; init; } = 256;
    public TimeSpan InferenceTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public void Validate()
    {
        _ = Model;
        if (GpuLayers <= 0 || Concurrency is < 1 or > 8 || ContextSize is < 512 or > 32768 || MaximumTokens <= 0 || MaximumTokens > ContextSize / 2 ||
            InferenceTimeout <= TimeSpan.Zero || InferenceTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentException("本地模型参数无效，请检查并发数、GPU 卸载层数、上下文长度、最大输出长度和超时时间。");
    }
}
