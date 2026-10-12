using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuelify.Core.Media;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Transcription;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class StageProgressTests
{
    private static Task Flush() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    [Fact]
    public void OldSnapshotsKeepEndsWhileNewExecutionCapturesTexts()
    {
        using var fixture = new Fixture(); var settings = fixture.Store.CreateDefaults();
        var snapshot = JsonSerializer.SerializeToNode(StageSettingsSnapshot.Capture(TaskStage.Subtitles, settings))!.AsObject();
        var id = snapshot["TaskPipelineId"]!.GetValue<string>();
        snapshot["PipelineLibrary"]![id]!.AsObject().Remove("SegmentationFormat");
        var old = ConfigurationStore.ReadSnapshot(JsonSerializer.SerializeToElement(snapshot))!;
        Assert.Equal(SegmentationOutputFormat.WordEnds, old.GetPipeline().SegmentationFormat);
        var current = old.ForStep(TaskStage.Subtitles);
        Assert.Equal(SegmentationOutputFormat.SourceTexts, current.GetPipeline().SegmentationFormat);
        Assert.Equal(SegmentationOutputFormat.WordEnds, old.GetPipeline().SegmentationFormat);
        var stage = new StageRecord { Settings = StageSettingsSnapshot.Capture(TaskStage.Subtitles, current) };
        Assert.False(ProjectTaskService.ConfigurationChanged(stage, TaskStage.Subtitles, old));
        Assert.Equal(SegmentationOutputFormat.SourceTexts, ConfigurationStore.ReadSnapshot(stage.Settings)!.GetPipeline().SegmentationFormat);
    }

    [AvaloniaTheory]
    [InlineData(1240, 828, "浅色", false)]
    [InlineData(900, 608, "深色", false)]
    [InlineData(1240, 828, "浅色", true)]
    [InlineData(900, 608, "深色", true)]
    public async Task AsrAndSegmentationShowActualProgressAndKeepItWithActiveMedia(int width, int height, string theme, bool segmentation)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        fixture.Jobs.PreparedFiles = Enumerable.Range(0, 4).Select(i => new PreparedChunk(new AudioChunk(i, TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i+1), false), "fixture.wav", 100, "fixture")).ToArray();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        Task? operation = null;
        try
        {
            await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme;
            var paths = new[] { Path.Combine(fixture.Root, "正在处理的媒体.mp4"), Path.Combine(fixture.Root, "另一个媒体.mp4") };
            foreach (var path in paths) await File.WriteAllTextAsync(path, "test");
            await model.AddMediaPathsAsync(paths); await WorkspaceTestActions.ExecuteAsync(model, "Chunking:Restart");
            model.ShowWorkspaceCommand.Execute(null);
            if (segmentation)
            {
                await WorkspaceTestActions.ExecuteAsync(model, "Asr:Restart");
                fixture.Jobs.BlockGeneration = true;
                fixture.Jobs.GenerationProgress = progress => progress.Report(new("Segmenting", []) { WorkProgress = new(300, 1000, Running: 1, PendingWords: 12) });
            }
            else
            {
                fixture.Jobs.Block = true;
                fixture.Jobs.RecognitionProgress = progress =>
                {
                    progress.Report(new("Transcribing", 0, 4, ProcessingState.Completed));
                    progress.Report(new("Transcribing", 1, 4, ProcessingState.Running));
                    progress.Report(new("Transcribing", 2, 4, ProcessingState.Failed));
                };
            }
            var stage = segmentation ? TaskStage.Subtitles : TaskStage.Asr;
            operation = WorkspaceTestActions.ExecuteAsync(model, stage + ":Restart");
            await (segmentation ? fixture.Jobs.GenerationStarted.Task : fixture.Jobs.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Flush(); window.UpdateLayout();
            var bar = window.FindControl<ProgressBar>("ActiveProgressBar")!;
            Assert.False(bar.IsIndeterminate); Assert.Equal(segmentation ? 30 : 25, bar.Value);
            Assert.True(model.Workspace.HasStepProgress);
            Assert.Equal("正在处理", model.Workspace.CurrentVersionText);
            Assert.True(window.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "StepProgress").IsEffectivelyVisible);
            Assert.Contains(segmentation ? "300/1000 词" : "1/4 片", model.Workspace.ActiveProgressText);
            Assert.Contains(segmentation ? "12 词待确认" : "1 项失败", model.Workspace.ActiveProgressText);
            var count = window.FindControl<TextBlock>("ActiveProgressCount")!;
            var origin = count.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 0 && origin.X + count.Bounds.Width <= width);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"progress-{stage}-{width}-{theme}.png"));
            }
            model.Workspace.Version = new(new StageRecord { Version = Guid.NewGuid().ToString("N") }, Current: true);
            await model.WaitForWorkspaceAsync(); await Flush();
            Assert.False(model.Workspace.HasStepProgress);
            Assert.Equal(segmentation ? 30 : 25, model.Workspace.ActiveProgressPercent);
            model.SelectedMedia = model.MediaTasks[1]; await model.WaitForWorkspaceAsync(); await Flush();
            Assert.False(model.Workspace.HasStepProgress); Assert.Equal(segmentation ? 30 : 25, model.Workspace.ActiveProgressPercent);
            model.CancelCommand.Execute(null); await operation;
            Assert.False(model.IsBusy); Assert.False(model.Workspace.HasStepProgress);
        }
        finally
        {
            model.CancelCommand.Execute(null); if (operation is not null) await operation;
            window.Close();
        }
    }
}
