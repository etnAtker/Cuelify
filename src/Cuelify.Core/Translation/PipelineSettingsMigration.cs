using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuelify.Core.Translation;

// 只负责旧配置的显式转换；旧联合提示词保留为档案，不进入新分句请求。
public static class PipelineSettingsMigration
{
    public const string LegacyJointId = "pipeline-joint";
    // 只识别历史元数据，不转换已执行版本的快照或结果。
    public static bool IsLegacySegmentation(JsonElement? settings)
    {
        if (settings is not { } json) return false;
        var id = SelectedId(json);
        if (id is not null && json.TryGetProperty("PipelineLibrary", out var library) && library.TryGetProperty(id, out var pipeline))
        {
            var model = pipeline.TryGetProperty("Method", out var method) ? method.GetInt32() == 1 :
                pipeline.TryGetProperty("Mode", out var mode) && mode.GetInt32() != 0;
            return model && (!pipeline.TryGetProperty("SegmentationFormat", out var format) || format.GetInt32() == 0);
        }
        return id is null || id == SubtitlePipelinePresets.TwoStage.Id || id == LegacyJointId;
    }
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
        // 编辑和显示模型只使用现行格式；执行准入仍检查未经转换的阶段快照。
        if (node["PipelineLibrary"] is JsonObject profiles)
            foreach (var value in profiles.Select(p => p.Value).OfType<JsonObject>())
                if (value["Method"]?.GetValue<int>() == 1 && (value["SegmentationFormat"] is null || value["SegmentationFormat"]!.GetValue<int>() == 0))
                    value["SegmentationFormat"] = (int)SegmentationOutputFormat.SourceTexts;
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
