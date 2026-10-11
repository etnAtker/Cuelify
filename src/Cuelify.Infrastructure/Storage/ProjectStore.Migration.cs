using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Transcription;

namespace Cuelify.Infrastructure.Storage;

public sealed partial class ProjectStore
{
    private async Task UpgradeIndependentStagesAsync(string projectId, MediaTaskDocument task, CancellationToken token)
    {
        var taskPath = Path.Combine(MediaDirectory(projectId, task.Id), "task.json");
        if (!File.Exists(taskPath + ".schema2.backup")) File.Copy(taskPath, taskPath + ".schema2.backup");
        var originalCurrentResult = task.Translation.Version;
        var versions = await VersionsAsync(projectId, task, TaskStage.Translation, token);
        var sourceVersions = await VersionsAsync(projectId, task, TaskStage.Asr, token);
        var importedCurrent = false;
        foreach (var old in versions.Where(v => PipelineSettingsMigration.IsJoint(v.Settings) && v.UpstreamStage == TaskStage.Asr))
        {
            var asrStage = sourceVersions.FirstOrDefault(v => v.Version == old.UpstreamVersionId);
            if (asrStage is null || old.ResultPath.Length == 0 || asrStage.ResultPath.Length == 0) continue;
            try
            {
                var result = await ReadResultAsync<TranslationResult>(projectId, task.Id, old.ResultPath, token);
                var source = await ReadResultAsync<TranscriptionResult>(projectId, task.Id, asrStage.ResultPath, token);
                if (result is null || source is null || result.Cues.Count == 0) continue;
                var pure = result.Cues.Select(c => c with { TranslatedText = null }).ToArray();
                var segmentation = new SubtitlePipelineResult(pure, result.Ranges, 0, 0);
                segmentation.ValidateSource(source.Words.Count);
                foreach (var (cue, range) in pure.Zip(result.Ranges))
                {
                    var words = source.Words.Skip(range.First).Take(range.Last - range.First + 1).ToArray();
                    if (cue.SourceText != string.Concat(words.Select(w => w.Text)) || cue.Start != words.Min(w => w.Start) || cue.End < words.Max(w => w.End))
                        throw new InvalidDataException("旧字幕的来源或时间无法核实。");
                }
                // 重启迁移时复用已导入的版本，不重复生成；原 stage/result 文件始终保留。
                var imported = (await VersionsAsync(projectId, task, TaskStage.Subtitles, token))
                    .FirstOrDefault(v => v.ImportedFromVersionId == old.Version);
                if (imported is null)
                {
                    imported = new StageRecord { Version = Guid.NewGuid().ToString("N"), CreatedAt = old.CreatedAt,
                        ImportedFromVersionId = old.Version, UpstreamStage = TaskStage.Asr, UpstreamVersionId = asrStage.Version,
                        InputResultPath = asrStage.ResultPath, InputIdentity = asrStage.OutputIdentity, State = ProcessingState.Completed,
                        Settings = old.Settings is { } saved ? PipelineSettingsMigration.Upgrade(saved) : null,
                        OutputIdentity = AtomicFile.Hash(new { segmentation.Cues, segmentation.Ranges }), WorkPlanKnown = true };
                    await SaveImported(TaskStage.Subtitles, imported, segmentation);
                }
                var importedResult = (await VersionsAsync(projectId, task, TaskStage.Translation, token))
                    .FirstOrDefault(v => v.ImportedFromVersionId == old.Version && !v.Archived);
                if (importedResult is null)
                {
                    importedResult = JsonSerializer.Deserialize<StageRecord>(JsonSerializer.Serialize(old))!;
                    importedResult.Version = Guid.NewGuid().ToString("N"); importedResult.Archived = false;
                    importedResult.ImportedFromVersionId = old.Version;
                    importedResult.UpstreamStage = TaskStage.Subtitles; importedResult.UpstreamVersionId = imported.Version;
                    importedResult.InputResultPath = imported.ResultPath; importedResult.InputIdentity = imported.OutputIdentity;
                    importedResult.Settings = imported.Settings;
                    importedResult.BaseResultVersionId = ""; importedResult.PreviousResultPath = ""; importedResult.PreviousSettings = null;
                    // 旧联合块 ID 不能作为新翻译步骤的失败单元；按已核实的固定 cue 建立工作计划。
                    var maximumCharacters = 44;
                    if (old.Settings is { } snapshot && snapshot.TryGetProperty("TaskPipelineId", out var pipelineId) &&
                        snapshot.TryGetProperty("PipelineLibrary", out var library) && library.TryGetProperty(pipelineId.GetString()!, out var profile) &&
                        profile.TryGetProperty("LineCharacters", out var lineCharacters) && lineCharacters.TryGetInt32(out var limit) && limit is >= 5 and <= 80)
                        maximumCharacters = limit * 2;
                    var contract = new GenerationOutputContract { MaximumTextCharacters = maximumCharacters };
                    var incomplete = result.Cues.Where(c => !contract.IsValidText(c.TranslatedText)).Select(c => c.Id).ToHashSet();
                    importedResult.WorkPlanKnown = true;
                    importedResult.UnitStates = result.Cues.ToDictionary(c => c.Id, c => !incomplete.Contains(c.Id) ? ProcessingState.Completed :
                        old.State is ProcessingState.Cancelled or ProcessingState.Interrupted or ProcessingState.Running ? ProcessingState.NotStarted : ProcessingState.Failed);
                    importedResult.FailedUnits = importedResult.UnitStates.Where(u => u.Value == ProcessingState.Failed).Select(u => u.Key).ToList();
                    importedResult.UnitErrors = importedResult.FailedUnits.ToDictionary(id => id, _ => "旧译文未完成，请重试。");
                    if (incomplete.Count == 0) { importedResult.State = ProcessingState.Completed; importedResult.Error = ""; }
                    else if (importedResult.State == ProcessingState.Completed) importedResult.State = ProcessingState.PartialFailure;
                    var normalized = result with
                    {
                        Cues = result.Cues.Select(c => incomplete.Contains(c.Id) ? c with { TranslatedText = null } : c).ToArray(),
                        FailedIds = incomplete.ToArray(),
                        FailureReasons = incomplete.ToDictionary(id => id, _ => "invalid_response")
                    };
                    importedResult.OutputIdentity = AtomicFile.Hash(new { normalized.Cues, normalized.Ranges });
                    await SaveImported(TaskStage.Translation, importedResult, normalized);
                }
                if (old.Version == originalCurrentResult)
                { task.Subtitles = imported; task.Translation = importedResult; importedCurrent = true; }
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            { Notices.Add("旧字幕来源无法核实，请重新分句。原结果文件已保留。"); }
        }
        if (task.Options is { } options) task.Options = PipelineSettingsMigration.Upgrade(options);
        // 无完整来源的联合任务保留为档案，重新处理从已有识别词开始。
        if (!importedCurrent && PipelineSettingsMigration.IsJoint(task.Translation.Settings) && task.Translation.UpstreamStage == TaskStage.Asr)
        { task.Subtitles = new(); task.Translation = new(); }
        if (PipelineSettingsMigration.IsJoint(task.Subtitles.Settings)) task.Subtitles = new();
        task.SchemaVersion = 3;
        await SelectDownstreamAsync(projectId, task, null, token);
        await SaveTaskAsync(projectId, task, token);

        async Task SaveImported<T>(TaskStage kind, StageRecord record, T data)
        {
            var directory = StageDirectory(projectId, task.Id, kind, record.Version);
            record.ResultPath = Path.GetRelativePath(MediaDirectory(projectId, task.Id), Path.Combine(directory, "result.json"));
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), data, token);
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "stage.json"), record, token);
        }
    }
}
