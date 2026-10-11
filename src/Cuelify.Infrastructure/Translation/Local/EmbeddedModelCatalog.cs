using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record EmbeddedModel(string Id, string Name, string Repository, string Quantization, string DefaultPipelineId = "pipeline-direct")
{
    public string DefaultFileName => Id + ".gguf";
    public InferenceParameters DefaultParameters => Id == "index-translate-9b-q4km"
        ? new() { Temperature = 0, TopP = 1, TopK = 40, RepeatPenalty = 1 }
        : new() { Temperature = .7, TopP = .6, TopK = 20, RepeatPenalty = 1.05 };
    public override string ToString() => Name;
}

public static class EmbeddedModelCatalog
{
    public static IReadOnlyList<EmbeddedModel> Presets { get; } =
    [
        new("hy-mt2-1.8b-q6k", "Hy-MT2-1.8B · Q6_K", "tencent/Hy-MT2-1.8B-GGUF", "Q6_K"),
        new("hy-mt2-7b-q4km", "Hy-MT2-7B · Q4_K_M", "tencent/Hy-MT2-7B-GGUF", "Q4_K_M", DefaultPipelineId: "pipeline-two-stage"),
        new("index-translate-9b-q4km", "Index-Translate-9B · Q4_K_M", "IndexTeam/Index-Translate-9B-GGUF", "Q4_K_M", DefaultPipelineId: "pipeline-two-stage")
    ];
    public static EmbeddedModel Default => Presets[0];
    public static EmbeddedModel Get(string id) => Presets.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentException("请选择支持的本地模型。");
}

public sealed record EmbeddedModelFile(string Path, string Sha256);
