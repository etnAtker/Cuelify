using Cuelify.Core.Projects;
using Cuelify.Core.Translation;

namespace Cuelify.Desktop.Services;

internal static class StageExecutionPolicy
{
    public const string LegacySegmentationMessage = "此分句版本无法继续，请重新分句；已有结果保留。";
    public static bool IsRetired(TaskStage kind, StageRecord record) => kind == TaskStage.Subtitles &&
        record.Version.Length > 0 && PipelineSettingsMigration.IsLegacySegmentation(record.Settings);
    public static bool NeedsModel(TaskStage kind, AppSettings settings) => kind == TaskStage.Translation ||
        kind == TaskStage.Subtitles && settings.GetPipeline().Method == SegmentationMethod.Model;
    public static bool NeedsCloud(TaskStage kind, AppSettings settings) => NeedsModel(kind, settings) && settings.Provider != TranslationProvider.Local;
    public static AppSettings ResolveSettings(MediaTaskDocument task, TaskStage kind, ProcessingMode mode, AppSettings requested)
    {
        var record = task.Stage(kind);
        if (mode != ProcessingMode.Restart && record.State != ProcessingState.Completed && IsRetired(kind, record))
            throw new ArgumentException(LegacySegmentationMessage);
        return mode == ProcessingMode.Restart || record.Version.Length == 0
            ? requested.ForStep(kind, task)
            : ConfigurationStore.ReadSnapshot(record.Settings) is { } saved ? ConfigurationStore.Snapshot(saved) : requested.ForStep(kind, task);
    }
    public static List<TaskStage> NeededStages(MediaTaskDocument task) => TaskStagePlan.Steps().SkipWhile(k => task.Stage(k).IsReady).ToList();
    public static List<TaskStage> FailedStages(MediaTaskDocument task) => TaskStagePlan.Steps()
        .Where(k => task.Stage(k).FailedUnits.Count > 0 && !task.Stage(k).NeedsUpdate && !IsRetired(k, task.Stage(k))).ToList();
}
