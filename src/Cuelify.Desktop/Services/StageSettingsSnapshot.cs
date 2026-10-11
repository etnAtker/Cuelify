using System.Text.Json;
using System.Text.Json.Nodes;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;

namespace Cuelify.Desktop.Services;

// AppSettings 是编辑模型；阶段只保存其实际执行所需的字段。
public static class StageSettingsSnapshot
{
    public static JsonElement Capture(TaskStage kind, AppSettings settings)
    {
        var all = JsonSerializer.SerializeToNode(settings)!.AsObject();
        var names = kind switch
        {
            TaskStage.Chunking => new[] { "FfmpegPath", "FfprobePath", "VadPath", "VadThreshold", "MinimumSpeechMs", "MinimumSilenceMs",
                "ChunkTargetSeconds", "ChunkMaximumSeconds", "ChunkSearchSeconds", "OverlapMs", "MaximumUploadBytes", "AlwaysChunk" },
            TaskStage.Asr => ["FfmpegPath", "FfprobePath", "AsrModel", "SourceCode", "AsrConcurrency", "MaximumAttempts", "MaximumUploadBytes"],
            _ => ["SourceLanguage", "TargetLanguage", "TargetStyle", "MaximumAttempts", "ZeroDurationToleranceMs"]
        };
        var node = new JsonObject { ["SchemaVersion"] = AppSettings.CurrentSchemaVersion };
        foreach (var name in names) node[name] = all[name]?.DeepClone();
        if (kind is TaskStage.Subtitles or TaskStage.Translation)
        {
            var pipeline = settings.GetPipeline();
            var definition = JsonSerializer.SerializeToNode(pipeline)!.AsObject();
            var fields = kind == TaskStage.Subtitles
                ? new[] { "Name", "Id", "Method", "Segmentation", "BlockSeconds", "ContextCharacters", "MaximumCueSeconds", "MinimumCueSeconds" }
                : ["Name", "Id", "Method", "Translation", "TranslationParameters", "BeforeCues", "AfterCues", "MaximumContextCharacters", "LineCharacters", "ReadingCharactersPerSecond"];
            var own = new JsonObject(); foreach (var name in fields) own[name] = definition[name]?.DeepClone();
            // 快照使用独立 ID，内置管线后续变更不会覆盖已执行的版本。
            var snapshotId = "snapshot:" + pipeline.Id; own["Id"] = snapshotId;
            node["TaskPipelineId"] = snapshotId; node["DefaultPipelineId"] = snapshotId;
            node["PipelineLibrary"] = new JsonObject { [snapshotId] = own };
            if (kind == TaskStage.Translation || pipeline.Method == SegmentationMethod.Model)
            {
                var parameters = ModelProfile.ExecutionParameters(settings);
                foreach (var (name, value) in parameters) node[name] = JsonNode.Parse(value.GetRawText());
                var model = new ModelProfile(settings.ModelKey(), settings.ModelName(), parameters);
                node["Models"] = new JsonObject { [model.Id] = JsonSerializer.SerializeToNode(model) };
                node["ExecutionModels"] = new JsonObject { [kind.ToString()] = model.Id };
            }
        }
        return JsonSerializer.SerializeToElement(node);
    }
}
