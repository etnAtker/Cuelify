using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class WorkspaceRedesignTests
{
    private static Task Flush() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    private static async Task<string[]> Media(Fixture fixture, params string[] names)
    {
        var paths = names.Select(n => Path.Combine(fixture.Root, n)).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "media fixture");
        return paths;
    }
    private static async Task Process(MainWindowViewModel model, string operation)
    {
        if (operation == "Complete")
        {
            foreach (var step in model.Workspace.Steps.ToArray())
                if (!model.SelectedMedia!.Document.Stage(step.Kind).IsReady) await Process(model, $"{step.Kind}:Complete");
            return;
        }
        await WorkspaceTestActions.ExecuteAsync(model, operation); await Flush();
    }
    [AvaloniaFact]
    public async Task SingleProcessingIgnoresBatchChecksAndKeepsEntryOnCompletedMedia()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        Assert.True(model.IsProjects);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4", "two.mp4"));
        model.MediaTasks[1].IsChecked = true;
        await Process(model, "Complete");
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.True(model.MediaTasks[0].Document.Translation.IsReady);
        Assert.Equal(ProcessingState.NotStarted, model.MediaTasks[1].Document.Asr.State);
        Assert.Contains(model.Workspace.StepOperations, o => o.Operation == "Translation:Restart" && o.Available);
        Assert.Equal("one.mp4", model.SelectedMedia!.Name);
    }
    [AvaloniaFact]
    public async Task WordResultsNeverInventSubtitlesAndTwoStepSegmentationIsIndependent()
    {
        using var fixture = new Fixture(); fixture.Jobs.SourceWords = [new("Hello", TimeSpan.Zero, TimeSpan.FromMilliseconds(370), "s1"), new(" world", TimeSpan.FromMilliseconds(410), TimeSpan.FromMilliseconds(960), "s1")];
        await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4"));
        await Process(model, "Chunking:Restart"); await Process(model, "Asr:Restart");
        model.Workspace.ResultTab = 1; await model.WaitForWorkspaceAsync(); await Flush();
        Assert.Equal(2, model.Workspace.Words.Count); Assert.Equal("00:00:00.370", model.Workspace.Words[0].End);
        Assert.Empty(model.Rows); Assert.Empty(model.Workspace.Cues);
        model.Workspace.ResultTab = 2; await model.WaitForWorkspaceAsync(); await Flush(); Assert.Empty(model.Workspace.Cues); Assert.False(model.Workspace.CanExport);
        model.Workspace.SettingsCommand.Execute(null); model.Workspace.PipelineDraft = SubtitlePipelinePresets.TwoStage;
        await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        await Process(model, "Subtitles:Restart");
        Assert.Equal(1, fixture.Jobs.Generations); Assert.Null(fixture.Jobs.LastSettings);
        Assert.Equal(ProcessingState.NotStarted, model.SelectedMedia!.Document.Translation.State);
        model.Workspace.SegmentationView = true; await model.WaitForWorkspaceAsync(); await Flush();
        Assert.Equal(2, model.Workspace.Cues.Count); Assert.False(model.Workspace.CanExport);
    }
    [AvaloniaFact]
    public async Task EachMediaHasOwnModelAndPipelineAndDefaultsOnlyAffectNewMedia()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4", "two.mp4"));
        var second = model.MediaTasks[1].Document.Options!.Value.GetRawText();
        model.Workspace.SettingsCommand.Execute(null);
        model.Workspace.PipelineDraft = SubtitlePipelinePresets.TwoStage;
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, model.Workspace.PipelineDraft.Id);
        model.Workspace.TargetDraft = "英语"; await model.Workspace.SubmitDialogCommand.ExecuteAsync(null);
        Assert.Contains("英语", model.CurrentMediaSettings);
        Assert.Equal(second, model.MediaTasks[1].Document.Options!.Value.GetRawText());
        model.Settings.TargetLanguage = "日语"; await model.SaveSettingsCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "three.mp4"));
        Assert.Equal("日语", model.SelectedMedia!.Document.Options!.Value.Deserialize<AppSettings>()!.TargetLanguage);
        Assert.Equal(second, model.MediaTasks[1].Document.Options!.Value.GetRawText());
    }
    [AvaloniaFact]
    public async Task UpstreamRestartRetainsHistoryAndSettingOldAsrCurrentRestoresExportableBranch()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4")); await Process(model, "Complete");
        var oldAsr = model.SelectedMedia!.Document.Asr.Version; var oldResult = model.SelectedMedia.Document.Translation.Version;
        fixture.Jobs.SourceWords = [new("new", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)];
        await Process(model, "Asr:Restart");
        Assert.Equal("", model.SelectedMedia.Document.Translation.Version);
        Assert.Equal(2, model.Workspace.Versions.Count);
        model.Workspace.Version = model.Workspace.Versions.Single(v => v.Record.Version == oldAsr); await model.WaitForWorkspaceAsync();
        Assert.NotEqual(oldAsr, model.SelectedMedia.Document.Asr.Version);
        await model.Workspace.SetCurrentCommand.ExecuteAsync(null);
        Assert.Equal(oldResult, model.SelectedMedia.Document.Translation.Version);
        model.Workspace.SelectedStep = model.Workspace.Steps.Last(); await model.WaitForWorkspaceAsync();
        Assert.True(model.Workspace.CanExport);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "old.srt"); await model.Workspace.ExportCommand.ExecuteAsync(null);
        Assert.Contains("中文译文", await File.ReadAllTextAsync(fixture.Dialogs.SavePath));
    }
    [AvaloniaFact]
    public async Task CancelledBatchPersistsScopeAndResumesWithoutStartingCompletedMedia()
    {
        using var fixture = new Fixture(); string projectId;
        await using (var model = fixture.Model())
        {
            await model.InitializeCommand.ExecuteAsync(null); await model.AddMediaPathsAsync(await Media(fixture, "one.mp4", "two.mp4", "three.mp4"));
            await Process(model, "Complete");
            model.MediaTasks[1].IsChecked = true; model.MediaTasks[2].IsChecked = true;
            fixture.Jobs.BlockTranslation = true;
            model.Workspace.BatchCommand.Execute(null); Assert.Equal(2, model.Workspace.BatchRows.Count);
            var running = model.Workspace.SubmitDialogCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
            model.ShowProjectsCommand.Execute(null); Assert.True(model.IsProcessing);
            model.CancelCommand.Execute(null); await running; await Flush();
            projectId = model.SelectedProject!.Id;
            Assert.Equal(ProcessingState.Cancelled, model.SelectedProject.Batch!.State);
            Assert.Equal(2, model.SelectedProject.Batch.Items.Count);
            Assert.Equal(ProcessingState.NotStarted, model.MediaTasks[2].Document.Asr.State);
        }
        fixture.Jobs.BlockTranslation = false;
        await using var restored = fixture.Model(); await restored.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(projectId, restored.SelectedProject!.Id); Assert.True(restored.Workspace.HasInterruptedBatch);
        restored.Workspace.ResumeBatchCommand.Execute(null); Assert.Equal(2, restored.Workspace.BatchRows.Count);
        await restored.Workspace.SubmitDialogCommand.ExecuteAsync(null); await Flush();
        Assert.Equal(3, fixture.Jobs.Transcriptions); Assert.True(restored.SelectedProject.Batch!.Items.All(i => i.State == ProcessingState.Completed));
    }
    [AvaloniaFact]
    public async Task MediaRemovalIsPersistentAndUndoRetainsResultsAndOriginalFile()
    {
        using var fixture = new Fixture(); var paths = await Media(fixture, "one.mp4");
        await using (var model = fixture.Model())
        {
            await model.InitializeCommand.ExecuteAsync(null); await model.AddMediaPathsAsync(paths); await Process(model, "Complete");
            await model.Workspace.RemoveCommand.ExecuteAsync(null); Assert.Empty(model.MediaTasks); Assert.True(File.Exists(paths[0]));
        }
        await using var restored = fixture.Model(); await restored.InitializeCommand.ExecuteAsync(null);
        Assert.True(restored.Workspace.HasRemovedMedia); await restored.Workspace.UndoRemoveCommand.ExecuteAsync(null);
        Assert.True(restored.SelectedMedia!.Document.Translation.IsReady); Assert.Equal(1, fixture.Jobs.Transcriptions);
    }
    [AvaloniaFact]
    public async Task ChangingJointTranslationModelDoesNotRegenerateFrozenCues()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4")); await Process(model, "Complete");
        var timeline = model.Rows.Select(c => (c.Id, c.Cue.Start, c.Cue.End)).ToArray();
        model.Workspace.StepModel = model.Workspace.Models.First(m => m.Settings.Provider == TranslationProvider.Local);
        await model.Workspace.SaveStepModelCommand.ExecuteAsync(null); await Process(model, "Translation:Retranslate");
        Assert.Equal(1, fixture.Jobs.Generations); Assert.Equal(1, fixture.Jobs.Transcriptions);
        Assert.Equal(timeline, model.Rows.Select(c => (c.Id, c.Cue.Start, c.Cue.End)).ToArray());
        Assert.Equal(TranslationProvider.Local, fixture.Jobs.LastSettings!.Provider);
    }
    [AvaloniaFact]
    public async Task SwitchingProjectsDuringBatchKeepsOriginalTargetsAndAllowsCompleteResultExport()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "view.mp4")); await Process(model, "Complete"); var viewedProject = model.SelectedProject!;
        model.ProjectName = "处理项目"; await model.CreateProjectCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "run.mp4", "later.mp4")); var runningProject = model.SelectedProject!;
        fixture.Jobs.BlockTranslation = true; model.Workspace.BatchCommand.Execute(null);
        var running = model.Workspace.SubmitDialogCommand.ExecuteAsync(null); await fixture.Jobs.TranslationStarted.Task;
        model.SelectedProject = viewedProject; await model.Workspace.OpenProjectCommand.ExecuteAsync(new ProjectListItem(viewedProject, "")); await model.WaitForWorkspaceAsync(); await Flush();
        Assert.Equal("view.mp4", model.SelectedMedia!.Name); Assert.True(model.Workspace.CanExport);
        fixture.Dialogs.SavePath = Path.Combine(fixture.Root, "during.srt"); await model.Workspace.ExportCommand.ExecuteAsync(null); Assert.True(File.Exists(fixture.Dialogs.SavePath));
        model.SelectedProject = runningProject; await model.Workspace.OpenProjectCommand.ExecuteAsync(new ProjectListItem(runningProject, "")); await model.WaitForWorkspaceAsync(); await Flush();
        Assert.Equal(ProcessingState.Running, model.MediaTasks[0].Document.Translation.State);
        model.CancelCommand.Execute(null); await running;
        Assert.Equal(ProcessingState.NotStarted, model.MediaTasks[1].Document.Asr.State);
    }
    [AvaloniaFact]
    public async Task LockedCredentialsCanBeUnlockedAndContinueTheConfirmedIntent()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        model.LockCredentialsCommand.Execute(null);
        await model.AddMediaPathsAsync(await Media(fixture, "one.mp4")); Assert.False(model.IsUnlocked); Assert.Equal(0, fixture.Jobs.Transcriptions);
        await Process(model, "Complete"); Assert.True(model.IsUnlocked); Assert.Equal(1, fixture.Jobs.Transcriptions);
        Assert.True(model.SelectedMedia!.Document.Translation.IsReady);
    }
    [AvaloniaTheory]
    [InlineData(900, 608, "深色")]
    [InlineData(1240, 828, "浅色")]
    public async Task WorkspaceShowsThreeNormalCueRowsAndPersistentSingleMediaControls(int width, int height, string theme)
    {
        using var fixture = new Fixture(); fixture.Jobs.SourceCues = Enumerable.Range(1, 8).Select(i => new SubtitleCue($"c{i}", TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds(i * 5 + 4), "This is a normal subtitle with several words.", null)).ToArray();
        await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        await model.AddMediaPathsAsync(await Media(fixture, "一个很长的中文媒体名称.mp4", "two.mp4")); await Process(model, "Complete");
        model.Settings.Theme = theme; model.ShowWorkspaceCommand.Execute(null);
        var window = new MainWindow { DataContext = model, Width = width, Height = height, MinHeight = 0 };
        window.Show(); await Flush(); window.UpdateLayout();
        try
        {
            var workspace = window.FindControl<WorkspaceView>("WorkspacePage")!;
            var results = workspace.FindControl<WorkspaceResultsView>("Results")!;
            var list = results.FindControl<ListBox>("CueList")!;
            Assert.True(workspace.FindControl<Button>("MediaSettingsButton")!.IsEffectivelyVisible);
            Assert.True(list.IsEffectivelyVisible); Assert.True(list.ItemCount >= 3);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var artifacts = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (artifacts is not null) { Directory.CreateDirectory(artifacts); frame!.Save(Path.Combine(artifacts, $"workspace-new-{width}-{theme}.png")); }
            Assert.True(list.ContainerFromIndex(2)!.TranslatePoint(default, list)!.Value.Y + list.ContainerFromIndex(2)!.Bounds.Height <= list.Bounds.Height, $"第三行未完整显示：列表高 {list.Bounds.Height}，第三行 {list.ContainerFromIndex(2)!.TranslatePoint(default, list)!.Value.Y} + {list.ContainerFromIndex(2)!.Bounds.Height}");

        }
        finally { window.Close(); }
    }
}
