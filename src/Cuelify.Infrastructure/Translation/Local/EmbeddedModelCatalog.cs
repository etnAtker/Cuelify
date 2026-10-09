using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record EmbeddedModel(string Id, string Name, string Repository, string Quantization, string Architecture, int FileType, bool Legacy = false, bool UseContextPrompt = false)
{
    public string DefaultFileName => Id + ".gguf";
    public PromptProfile DefaultProfile => UseContextPrompt ? PromptPresets.Local : PromptPresets.LocalSimple;
    public override string ToString() => Name;
}

public static class EmbeddedModelCatalog
{
    public static IReadOnlyList<EmbeddedModel> Presets { get; } =
    [
        new("hy-mt2-1.8b-q6k", "Hy-MT2-1.8B · Q6_K", "tencent/Hy-MT2-1.8B-GGUF", "Q6_K", "hunyuan-dense", 18),
        new("hy-mt2-7b-q4km", "Hy-MT2-7B · Q4_K_M", "tencent/Hy-MT2-7B-GGUF", "Q4_K_M", "hunyuan-dense", 15, UseContextPrompt: true)
    ];
    public static EmbeddedModel Legacy { get; } = new("hy-mt2-1.8b-q4km", "Hy-MT2-1.8B · Q4_K_M（旧配置）", "tencent/Hy-MT2-1.8B-GGUF", "Q4_K_M", "hunyuan-dense", 15, true);
    public static EmbeddedModel Default => Presets[0];
    public static EmbeddedModel Get(string id) => Presets.Append(Legacy).FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException("请选择支持的内嵌模型。");
}

public sealed record EmbeddedModelFile(string Path, string Sha256);
