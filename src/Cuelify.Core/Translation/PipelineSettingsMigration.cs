using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuelify.Core.Translation;

// 只负责旧配置的显式转换；旧联合提示词保留为档案，不进入新分句请求。
public static class PipelineSettingsMigration
{
    public const string LegacyJointId = "pipeline-joint";
    public static bool IsJoint(JsonElement? settings)
    {
        if (settings is not { } json) return false;
        var id = SelectedId(json);
        return id == LegacyJointId || id is not null && json.TryGetProperty("PipelineLibrary", out var library) &&
            library.TryGetProperty(id, out var pipeline) && pipeline.TryGetProperty("Mode", out var mode) && mode.GetInt32() == 1;
    }
    private static string? SelectedId(JsonElement json) =>
        json.TryGetProperty("TaskPipelineId", out var task) && task.ValueKind == JsonValueKind.String ? task.GetString() :
        json.TryGetProperty("DefaultPipelineId", out var selected) && selected.ValueKind == JsonValueKind.String ? selected.GetString() : null;

    public static JsonElement Upgrade(JsonElement settings)
    {
        var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
        if (node["PipelineLibrary"] is JsonObject library)
            foreach (var (id, value) in library.ToArray())
            {
                if (value is not JsonObject profile || profile["Mode"] is not { } mode) continue;
                var legacyMode = mode.GetValue<int>();
                if (legacyMode == 1)
                {
                    if (node["ArchivedPipelines"] is not JsonObject) node["ArchivedPipelines"] = new JsonObject();
                    node["ArchivedPipelines"]![id] = profile.DeepClone();
                    profile["Segmentation"] = JsonSerializer.SerializeToNode(SubtitlePipelinePresets.TwoStage.Segmentation);
                }
                profile["Method"] = legacyMode == 0 ? 0 : 1;
                profile.Remove("Mode");
            }
        foreach (var key in new[] { "DefaultPipelineId", "TaskPipelineId" })
            if (node[key]?.GetValue<string>() == LegacyJointId) node[key] = SubtitlePipelinePresets.TwoStage.Id;
        if (node["PipelineModelDefaults"] is JsonObject defaults && defaults[LegacyJointId] is { } oldDefaults)
        {
            defaults[SubtitlePipelinePresets.TwoStage.Id] ??= oldDefaults.DeepClone();
            defaults.Remove(LegacyJointId);
        }
        node["SchemaVersion"] = 5;
        return JsonSerializer.SerializeToElement(node);
    }
}
