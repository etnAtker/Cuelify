namespace Cuelify.Core.Projects;

public static class TaskStagePlan
{
    public static IReadOnlyList<TaskStage> Steps() => [TaskStage.Chunking, TaskStage.Asr, TaskStage.Subtitles, TaskStage.Translation];
    public static TaskStage? Upstream(TaskStage kind) => kind switch
    {
        TaskStage.Chunking => null,
        TaskStage.Asr => TaskStage.Chunking,
        TaskStage.Subtitles => TaskStage.Asr,
        TaskStage.Translation => TaskStage.Subtitles,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
