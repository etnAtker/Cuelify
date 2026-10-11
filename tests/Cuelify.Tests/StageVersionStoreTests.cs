using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Tests;

public sealed class StageVersionStoreTests
{
    private static async Task<StageRecord> Record(ProjectStore store, string projectId, MediaTaskDocument task, TaskStage kind,
        StageRecord? parent, TaskStage? parentKind, ProcessingState state = ProcessingState.Completed, string pipeline = "pipeline-two-stage")
    {
        var id = Guid.NewGuid().ToString("N"); var directory = store.StageDirectory(projectId, task.Id, kind, id);
        var record = new StageRecord { Version = id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, State = state,
            UpstreamStage = parentKind, UpstreamVersionId = parent?.Version ?? "", InputResultPath = parent?.ResultPath ?? "",
            ResultPath = Path.GetRelativePath(store.MediaDirectory(projectId, task.Id), Path.Combine(directory, "result.json")),
            Settings = JsonSerializer.SerializeToElement(new { TaskPipelineId = pipeline }) };
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "stage.json"), record, CancellationToken.None);
        return record;
    }
    [Fact]
    public async Task CascadingSelectsLatestFailedDescendantAndFiltersIdenticalContentByVersionId()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        var task = new MediaTaskDocument { InputPath = "media.mp4" };
        task.Chunking = await Record(store, project.Id, task, TaskStage.Chunking, null, null);
        task.Asr = await Record(store, project.Id, task, TaskStage.Asr, task.Chunking, TaskStage.Chunking);
        var firstAsr = task.Asr;
        var success = await Record(store, project.Id, task, TaskStage.Subtitles, firstAsr, TaskStage.Asr);
        var failed = await Record(store, project.Id, task, TaskStage.Subtitles, firstAsr, TaskStage.Asr, ProcessingState.Failed);
        failed.CreatedAt = success.CreatedAt.AddSeconds(1);
        await AtomicFile.WriteJsonAsync(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Subtitles, failed.Version), "stage.json"), failed, CancellationToken.None);
        task.Asr = await Record(store, project.Id, task, TaskStage.Asr, task.Chunking, TaskStage.Chunking);
        task.Asr.OutputIdentity = firstAsr.OutputIdentity = "same-content";
        await store.SelectDownstreamAsync(project.Id, task, TaskStage.Asr);
        Assert.Empty(task.Subtitles.Version);
        await store.SetCurrentAsync(project.Id, task, TaskStage.Asr, firstAsr);
        Assert.Equal(failed.Version, task.Subtitles.Version); Assert.Equal(ProcessingState.Failed, task.Subtitles.State);
        Assert.Empty(task.Translation.Version);
    }
    [Fact]
    public async Task SchemaOneMigrationRetainsBothPipelineBranchesAndBacksUpOriginalDescriptors()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        var task = new MediaTaskDocument { SchemaVersion = 1, InputPath = "media.mp4", Options = JsonSerializer.SerializeToElement(new { TaskPipelineId = "pipeline-joint" }) };
        task.Chunking = await Record(store, project.Id, task, TaskStage.Chunking, null, null);
        task.Asr = await Record(store, project.Id, task, TaskStage.Asr, task.Chunking, TaskStage.Chunking);
        var segmentation = await Record(store, project.Id, task, TaskStage.Subtitles, task.Asr, TaskStage.Asr);
        task.Translation = await Record(store, project.Id, task, TaskStage.Translation, segmentation, TaskStage.Subtitles);
        var cues = new[] { new SubtitleCue("cue-1", TimeSpan.Zero, TimeSpan.FromSeconds(1), "原文", "译文") };
        await AtomicFile.WriteJsonAsync(store.ResolveResult(project.Id, task.Id, segmentation.ResultPath),
            new SubtitlePipelineResult(cues, [new SourceWordRange(0, 1)], 0, 0), CancellationToken.None);
        await AtomicFile.WriteJsonAsync(store.ResolveResult(project.Id, task.Id, task.Translation.ResultPath), new TranslationResult(cues, [], 0, 0), CancellationToken.None);
        var oldResult = task.Translation.Version;
        task.Subtitles = await Record(store, project.Id, task, TaskStage.Subtitles, task.Asr, TaskStage.Asr, ProcessingState.Failed, "pipeline-joint");
        var failedJoint = task.Subtitles.Version;
        foreach (var kind in Enum.GetValues<TaskStage>())
        {
            var record = task.Stage(kind); record.UpstreamStage = null; record.UpstreamVersionId = ""; record.CreatedAt = default;
            await AtomicFile.WriteJsonAsync(Path.Combine(store.StageDirectory(project.Id, task.Id, kind, record.Version), "stage.json"), record, CancellationToken.None);
        }
        await store.SaveTaskAsync(project.Id, task);
        using var projectLock = store.AcquireProject(project.Id);
        var migrated = await store.LoadTaskAsync(project.Id, task.Id);
        Assert.Equal(3, migrated.SchemaVersion); Assert.Equal(oldResult, migrated.Translation.Version);
        Assert.Equal(TaskStage.Subtitles, migrated.Translation.UpstreamStage);
        Assert.Contains(await store.VersionsAsync(project.Id, migrated, TaskStage.Translation), v => v.Version == failedJoint && v.Archived);
        Assert.True(File.Exists(Path.Combine(store.MediaDirectory(project.Id, task.Id), "task.json.schema1.backup")));
        var originalJoint = Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Subtitles, failedJoint), "stage.json");
        Assert.True(File.Exists(originalJoint + ".schema1.backup"));
        await store.SelectDownstreamAsync(project.Id, migrated, TaskStage.Asr);
        Assert.Equal(segmentation.Version, migrated.Subtitles.Version); Assert.Equal(oldResult, migrated.Translation.Version);
        Assert.Equal(new[] { new SourceWordRange(0, 1) }, (await store.ReadResultAsync<TranslationResult>(project.Id, task.Id, migrated.Translation.ResultPath))!.Ranges);
    }
    [Fact]
    public void ProgramSegmentationHasContinuousWordRangesAndDoesNotMutateRawWords()
    {
        Cuelify.Core.Speech.WordToken[] words = [new("你", TimeSpan.Zero, TimeSpan.Zero, "a"),
            new("是", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), "b"),
            new("好", TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800), "a")];
        var original = words.ToArray(); var result = new CueBuilder().BuildWithRanges(words);
        result.ValidateSource(words.Length); Assert.Equal(original, words);
        Assert.Equal(new[] { new SourceWordRange(0, 0), new(1, 1), new(2, 2) }, result.Ranges);
        var invalid = result with { Ranges = [new(0, 0), new(2, 2), new(2, 2)] };
        Assert.Throws<InvalidDataException>(() => invalid.ValidateSource(words.Length));
    }
}
