using System.Text.Json;

namespace Cuelify.Desktop.Services;

// 模型只保存服务、推理参数与资产引用，管线和媒体参数不属于模型。
public sealed record ModelProfile(string Id, string Name, Dictionary<string, JsonElement> Parameters)
{
    private static readonly string[] Fields = [nameof(AppSettings.Provider), nameof(AppSettings.BaseUrl), nameof(AppSettings.CloudModel),
        nameof(AppSettings.Temperature), nameof(AppSettings.TopP), nameof(AppSettings.MaximumTokens), nameof(AppSettings.FrequencyPenalty),
        nameof(AppSettings.PresencePenalty), nameof(AppSettings.ReasoningCapability), nameof(AppSettings.ReasoningEffort), nameof(AppSettings.AdvancedJson),
        nameof(AppSettings.ThinkingEnabled), nameof(AppSettings.ThinkingEffort), nameof(AppSettings.TimeoutSeconds), nameof(AppSettings.TranslationConcurrency),
        nameof(AppSettings.ModelPath), nameof(AppSettings.LocalModelId), nameof(AppSettings.ModelSha256), nameof(AppSettings.LlamaServerPath),
        nameof(AppSettings.LlamaServerVersion), nameof(AppSettings.LocalConcurrency), nameof(AppSettings.GpuLayers), nameof(AppSettings.ContextSize), nameof(AppSettings.LocalMaximumTokens)];
    public static ModelProfile Capture(AppSettings settings) => new(settings.ModelKey(), settings.ModelName(), Fields.ToDictionary(name => name,
        name => JsonSerializer.SerializeToElement(typeof(AppSettings).GetProperty(name)!.GetValue(settings))));
    public static Dictionary<string, JsonElement> ExecutionParameters(AppSettings settings)
    {
        var names = settings.Provider == TranslationProvider.Local
            ? new[] { "Provider", "ModelPath", "LocalModelId", "ModelSha256", "LlamaServerPath", "LlamaServerVersion", "LocalConcurrency", "GpuLayers", "ContextSize", "LocalMaximumTokens" }
            : ["Provider", "BaseUrl", "CloudModel", "Temperature", "TopP", "MaximumTokens", "FrequencyPenalty", "PresencePenalty", "ReasoningCapability", "ReasoningEffort", "AdvancedJson", "ThinkingEnabled", "ThinkingEffort", "TimeoutSeconds", "TranslationConcurrency"];
        return names.ToDictionary(name => name, name => JsonSerializer.SerializeToElement(typeof(AppSettings).GetProperty(name)!.GetValue(settings)));
    }
    public void Apply(AppSettings settings)
    {
        foreach (var name in Fields)
            if (Parameters.TryGetValue(name, out var value))
            {
                var property = typeof(AppSettings).GetProperty(name)!;
                property.SetValue(settings, value.Deserialize(property.PropertyType));
            }
    }
}
