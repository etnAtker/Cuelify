using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cuelify.Core.Projects;

public enum ProcessingState { NotStarted, Running, PartialFailure, Failed, Cancelled, Interrupted, Completed }
public enum ProcessingMode { Complete, RetryFailed, Restart }
public enum TaskStage { Chunking, Asr, Subtitles, Translation }
public enum StageOperation { Generate, Retranslate }

public sealed class StageRecord
{
    public bool Archived { get; set; }
    public string ImportedFromVersionId { get; set; } = "";
    public TaskStage? UpstreamStage { get; set; }
    public string UpstreamVersionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public StageOperation Operation { get; set; }
    public string BaseResultVersionId { get; set; } = "";
    public string Version { get; set; } = "";
    public bool ReuseSharedCache { get; set; } = true;
    public ProcessingState State { get; set; }
    public bool NeedsUpdate { get; set; }
    public bool ResultUnavailable { get; set; }
    public string InputIdentity { get; set; } = "";
    public string InputResultPath { get; set; } = "";
    public string OutputIdentity { get; set; } = "";
    public JsonElement? Settings { get; set; }
    public string ResultPath { get; set; } = "";
    public string PreviousResultPath { get; set; } = "";
    public JsonElement? PreviousSettings { get; set; }
    public List<string> FailedUnits { get; set; } = [];
    public Dictionary<string, ProcessingState> UnitStates { get; set; } = [];
    public bool WorkPlanKnown { get; set; }
    [JsonIgnore] public bool HasPendingWork => !WorkPlanKnown || UnitStates.Values.Any(s => s is ProcessingState.NotStarted or ProcessingState.Running or ProcessingState.Cancelled or ProcessingState.Interrupted);
    public Dictionary<string, string> UnitErrors { get; set; } = [];
    public string Error { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    [JsonIgnore] public bool IsReady => State == ProcessingState.Completed && !NeedsUpdate && !ResultUnavailable && ResultPath.Length > 0;
}

public sealed class MediaTaskDocument
{
    public int SchemaVersion { get; set; } = 3;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InputPath { get; set; } = "";
    public JsonElement? Options { get; set; }
    public TimeSpan? Duration { get; set; }
    public string? SourceSha256 { get; set; }
    public Dictionary<TaskStage, string> ModelOverrides { get; set; } = [];
    public StageRecord Chunking { get; set; } = new();
    public StageRecord Asr { get; set; } = new();
    public StageRecord Subtitles { get; set; } = new();
    public StageRecord Translation { get; set; } = new();
    public StageRecord Stage(TaskStage stage) => stage switch
    { TaskStage.Chunking => Chunking, TaskStage.Asr => Asr, TaskStage.Subtitles => Subtitles, _ => Translation };
    [JsonIgnore] public IEnumerable<StageRecord> Stages => [Chunking, Asr, Subtitles, Translation];
    public void SetStage(TaskStage kind, StageRecord record)
    {
        switch (kind)
        {
            case TaskStage.Chunking: Chunking = record; break;
            case TaskStage.Asr: Asr = record; break;
            case TaskStage.Subtitles: Subtitles = record; break;
            case TaskStage.Translation: Translation = record; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新项目";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public JsonElement? DefaultSettings { get; set; }
    public List<string> MediaIds { get; set; } = [];
    public List<string> RemovedMediaIds { get; set; } = [];
    public BatchRunDocument? Batch { get; set; }
    public override string ToString() => Name;
}

public sealed class BatchRunDocument
{
    public bool FailedOnly { get; set; }
    public ProcessingState State { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BatchMediaPlan> Items { get; set; } = [];
}

public sealed class BatchMediaPlan
{
    public string SkipReason { get; set; } = "";
    public string MediaId { get; set; } = "";
    public JsonElement? Settings { get; set; }
    public ProcessingState State { get; set; }
    public List<TaskStage> Stages { get; set; } = [];
}

public sealed record WorkspaceState(string? ProjectId, string? MediaId, double MediaPaneWidth = 190);
