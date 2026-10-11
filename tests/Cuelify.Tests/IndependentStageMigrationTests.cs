using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Xunit;

namespace Cuelify.Tests;

public sealed class IndependentStageMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JointMigrationImportsOnlyVerifiedRangesAndRetainsOriginalResult(bool reliableRanges)
        => await VerifyMigration(reliableRanges, ProcessingState.Completed, "Hello.");

    [Theory]
    [InlineData(ProcessingState.Failed, "")]
    [InlineData(ProcessingState.Interrupted, "")]
    [InlineData(ProcessingState.Completed, "\u0001")]
    [InlineData(ProcessingState.Completed, "This historical translation exceeds the permitted forty four characters.")]
    public async Task ImportedFailuresUseCueWorkPlanInsteadOfJointBlockIds(ProcessingState state, string text)
        => await VerifyMigration(true, state, text);

    private static async Task VerifyMigration(bool reliableRanges, ProcessingState oldState, string translatedText)
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        var task = new MediaTaskDocument { SchemaVersion = 2, InputPath = "media.mp4",
            Options = JsonSerializer.SerializeToElement(new { SchemaVersion = 4, TaskPipelineId = "pipeline-joint" }) };
        task.Chunking = await Save(TaskStage.Chunking, null, new { Identity = "chunks" });
        WordToken[] words = [new("你好。", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)];
        task.Asr = await Save(TaskStage.Asr, task.Chunking,
            new TranscriptionResult("old", TimeSpan.FromSeconds(2), [], words, [], 0, 0, ""));
        SubtitleCue[] cues = [new("old-id", TimeSpan.Zero, TimeSpan.FromSeconds(1), "你好。", translatedText)];
        task.Translation = await Save(TaskStage.Translation, task.Asr, new TranslationResult(cues, [], 0, 0)
        { Ranges = reliableRanges ? [new(0, 0)] : [] });
        task.Translation.State = oldState;
        task.Translation.FailedUnits = ["old-joint-block"];
        await AtomicFile.WriteJsonAsync(Path.Combine(store.StageDirectory(project.Id, task.Id, TaskStage.Translation, task.Translation.Version), "stage.json"), task.Translation, default);
        var originalId = task.Translation.Version; var originalPath = store.ResolveResult(project.Id, task.Id, task.Translation.ResultPath);
        var originalBytes = await File.ReadAllBytesAsync(originalPath);
        await store.SaveTaskAsync(project.Id, task);
        using var projectLock = store.AcquireProject(project.Id);
        var migrated = await store.LoadTaskAsync(project.Id, task.Id);
        Assert.Equal(3, migrated.SchemaVersion); Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
        Assert.True(File.Exists(Path.Combine(store.MediaDirectory(project.Id, task.Id), "task.json.schema2.backup")));
        Assert.Contains(await store.VersionsAsync(project.Id, migrated, TaskStage.Translation), v => v.Version == originalId && v.Archived);
        if (reliableRanges)
        {
            Assert.Equal(originalId, migrated.Subtitles.ImportedFromVersionId);
            Assert.Equal(TaskStage.Subtitles, migrated.Translation.UpstreamStage);
            Assert.Equal(migrated.Subtitles.Version, migrated.Translation.UpstreamVersionId);
            var segmented = (await store.ReadResultAsync<SubtitlePipelineResult>(project.Id, task.Id, migrated.Subtitles.ResultPath))!;
            Assert.Null(segmented.Cues.Single().TranslatedText); Assert.Equal(cues.Single().Start, segmented.Cues.Single().Start);
            var translated = (await store.ReadResultAsync<TranslationResult>(project.Id, task.Id, migrated.Translation.ResultPath))!;
            var valid = new GenerationOutputContract { MaximumTextCharacters = 44 }.IsValidText(translatedText);
            Assert.Equal(cues.Single().Start, translated.Cues.Single().Start);
            Assert.Equal(cues.Single().End, translated.Cues.Single().End);
            Assert.Equal(cues.Single().Id, translated.Cues.Single().Id);
            Assert.DoesNotContain("old-joint-block", migrated.Translation.UnitStates.Keys);
            if (valid)
            {
                Assert.Equal(cues, translated.Cues); _ = SrtSerializer.SerializeTranslated(translated.Cues);
                Assert.Empty(migrated.Translation.FailedUnits);
                Assert.Equal(ProcessingState.Completed, migrated.Translation.State);
            }
            else
            {
                Assert.Null(translated.Cues.Single().TranslatedText); Assert.Equal(["old-id"], translated.FailedIds);
                Assert.Equal(oldState == ProcessingState.Interrupted ? ProcessingState.NotStarted : ProcessingState.Failed, migrated.Translation.UnitStates["old-id"]);
                Assert.Equal(oldState == ProcessingState.Interrupted, migrated.Translation.HasPendingWork);
                Assert.Equal(oldState == ProcessingState.Interrupted ? [] : new[] { "old-id" }, migrated.Translation.FailedUnits);
            }
        }
        else { Assert.Empty(migrated.Subtitles.Version); Assert.Empty(migrated.Translation.Version); }
        var count = (await store.VersionsAsync(project.Id, migrated, TaskStage.Translation)).Count;
        var second = await store.LoadTaskAsync(project.Id, task.Id);
        Assert.Equal(count, (await store.VersionsAsync(project.Id, second, TaskStage.Translation)).Count);

        async Task<StageRecord> Save<T>(TaskStage kind, StageRecord? parent, T data)
        {
            var record = new StageRecord { Version = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
                UpstreamStage = parent is null ? null : kind == TaskStage.Asr ? TaskStage.Chunking : TaskStage.Asr,
                UpstreamVersionId = parent?.Version ?? "", State = ProcessingState.Completed,
                Settings = kind == TaskStage.Translation ? task.Options : null };
            var path = store.StageDirectory(project.Id, task.Id, kind, record.Version);
            record.ResultPath = Path.GetRelativePath(store.MediaDirectory(project.Id, task.Id), Path.Combine(path, "result.json"));
            await AtomicFile.WriteJsonAsync(Path.Combine(path, "result.json"), data, default);
            await AtomicFile.WriteJsonAsync(Path.Combine(path, "stage.json"), record, default);
            return record;
        }
    }
}
