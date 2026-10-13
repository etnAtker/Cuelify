using System.Text.Json;

namespace Cuelify.Desktop.Services;

// 模型只保存服务、推理参数与资产引用，管线和媒体参数不属于模型。
public sealed record ModelProfile(string Id, string Name, Dictionary<string, JsonElement> Parameters)
{
    private static readonly string[] LocalFields = [nameof(AppSettings.Provider), nameof(AppSettings.ModelPath), nameof(AppSettings.LocalModelId),
        nameof(AppSettings.ModelSha256), nameof(AppSettings.LlamaServerPath), nameof(AppSettings.LlamaServerVersion), nameof(AppSettings.LocalConcurrency),
        nameof(AppSettings.GpuLayers), nameof(AppSettings.ContextSize), nameof(AppSettings.LocalMaximumTokens), nameof(AppSettings.TimeoutSeconds)];
    private static readonly string[] CloudFields = [nameof(AppSettings.Provider), nameof(AppSettings.BaseUrl), nameof(AppSettings.CloudModel),
        nameof(AppSettings.Temperature), nameof(AppSettings.TopP), nameof(AppSettings.MaximumTokens), nameof(AppSettings.FrequencyPenalty),
        nameof(AppSettings.PresencePenalty), nameof(AppSettings.ReasoningCapability), nameof(AppSettings.ReasoningEffort), nameof(AppSettings.AdvancedJson),
        nameof(AppSettings.ThinkingEnabled), nameof(AppSettings.ThinkingEffort), nameof(AppSettings.TimeoutSeconds), nameof(AppSettings.TranslationConcurrency)];
    private static readonly string[] Fields = CloudFields.Concat(LocalFields).Distinct().ToArray();
    public static ModelProfile Capture(AppSettings settings) => new(settings.ModelKey(), settings.ModelName(), ReadParameters(settings, Fields));
    public static Dictionary<string, JsonElement> ExecutionParameters(AppSettings settings) =>
        ReadParameters(settings, settings.Provider == TranslationProvider.Local ? LocalFields : CloudFields);
    private static Dictionary<string, JsonElement> ReadParameters(AppSettings settings, IEnumerable<string> names) => names.ToDictionary(name => name,
        name => JsonSerializer.SerializeToElement(typeof(AppSettings).GetProperty(name)!.GetValue(settings)));
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
