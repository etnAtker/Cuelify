using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuelify.Core.Projects;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class UnifiedProgressUiTests
{
    private static Task Flush() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    [AvaloniaFact]
    public async Task FeedbackDelaysShortOperationsRejectsStaleScopesAndFreezesCancellation()
    {
        var model = new OperationProgressViewModel();
        using var first = model.Begin("读取版本"); Assert.False(model.IsVisible);
        await Task.Delay(400); await Flush(); Assert.True(model.IsVisible);
        using var second = model.Begin("新的任务", immediate: true);
        first.Update("旧通知", new(1, 1)); first.Dispose();
        Assert.Equal("新的任务", model.Message); Assert.True(model.IsActive);
        second.Update("提取音频", new(1_000_000, 4_000_000) { Unit = "字节" });
        Assert.Equal(25, model.Percent); Assert.Contains("1.0/4.0 MB", model.CountText);
        second.Update("加载模型", reset: true); Assert.False(model.HasTotal); Assert.True(model.IsIndeterminate);
        model.ReduceMotion = true; Assert.False(model.IsIndeterminate); Assert.Equal(0, model.BarValue);
        model.Cancel(); second.Update("晚到的完成通知", new(4, 4));
        Assert.Equal("正在取消…", model.Message); Assert.False(model.CanCancel);
        second.Dispose(); Assert.False(model.IsActive); Assert.False(model.IsVisible);
    }

    [AvaloniaFact]
    public async Task ImportKeepsGlobalProgressAcrossPagesAndCancellationKeepsAddedMedia()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model(); await model.InitializeCommand.ExecuteAsync(null);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new[] { Path.Combine(fixture.Root, "第一个.mp4"), Path.Combine(fixture.Root, "第二个.mp4") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, "fixture");
        fixture.Jobs.ProbeBehavior = async (path, token) =>
        { if (path == paths[1]) { secondStarted.SetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var operation = model.AddMediaPathsAsync(paths);
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Flush();
            Assert.True(model.IsImporting); Assert.True(model.Activity.IsVisible);
            Assert.Contains("1/2 个文件", model.Activity.CountText); Assert.Contains("第二个", model.Activity.Message);
            model.ShowLogsCommand.Execute(null); Assert.True(model.Activity.IsVisible);
            model.CancelCommand.Execute(null); Assert.Equal("正在取消…", model.Activity.Message);
            await operation;
            Assert.Single(model.MediaTasks); Assert.Single(model.SelectedProject!.MediaIds);
            var saved = (await new ProjectStore(fixture.Root).ListAsync()).Single();
            Assert.Single(saved!.MediaIds); Assert.False(model.Activity.IsActive); Assert.False(model.IsImporting);
        }
        finally { model.CancelCommand.Execute(null); await operation; }
    }

    [AvaloniaTheory]
    [InlineData(900, 608, "深色", false)]
    [InlineData(1240, 828, "浅色", false)]
    [InlineData(900, 608, "深色", true)]
    [InlineData(1240, 828, "浅色", true)]
    public async Task ChunkingAndTranslationShareLayoutAndUnknownPhaseClearsOldCount(int width, int height, string theme, bool translation)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        Task? operation = null;
        try
        {
            await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme; model.Settings.ReduceMotion = false;
            var path = Path.Combine(fixture.Root, "进度显示.mp4"); await File.WriteAllTextAsync(path, "fixture");
            await model.AddMediaPathsAsync([path]); model.ShowWorkspaceCommand.Execute(null);
            IProgress<TranscriptionProgress>? chunkProgress = null;
            if (translation)
            {
                foreach (var stage in new[] { "Chunking", "Asr", "Subtitles" }) await WorkspaceTestActions.ExecuteAsync(model, stage + ":Restart");
                fixture.Jobs.BlockTranslation = true;
                fixture.Jobs.TranslationProgress = progress => progress.Report(new("Translating", []) { WorkProgress = new(1, 2, Running: 1) { Unit = "条", ShowWaiting = true } });
            }
            else
            {
                fixture.Jobs.BlockChunking = true;
                fixture.Jobs.ChunkingProgress = progress => { chunkProgress = progress; progress.Report(new("SourceChecking") { WorkProgress = new(1_000_000, 4_000_000) { Unit = "字节" } }); };
            }
            operation = WorkspaceTestActions.ExecuteAsync(model, (translation ? "Translation" : "Chunking") + ":Restart");
            await (translation ? fixture.Jobs.TranslationStarted.Task : fixture.Jobs.ChunkingStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Flush(); window.UpdateLayout();
            Assert.True(model.Workspace.HasStepProgress);
            var bar = window.FindControl<ProgressBar>("ActiveProgressBar")!;
            Assert.Equal(translation ? 50 : 25, bar.Value); Assert.False(bar.IsIndeterminate);
            Assert.Contains(translation ? "1/2 条" : "1.0/4.0 MB", model.Activity.CountText);
            foreach (var control in window.GetVisualDescendants().OfType<ProgressBar>().Where(c => c.IsEffectivelyVisible))
            { Assert.Equal(4, control.Bounds.Height); Assert.False(control.IsIndeterminate); Assert.Equal(bar.Value, control.Value); }
            var count = window.FindControl<TextBlock>("ActiveProgressCount")!;
            var origin = count.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 0 && origin.X + count.Bounds.Width <= width);
            await Task.Delay(250); await Flush(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output))
            { Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, $"unified-{(translation ? "translation" : "chunking")}-{width}-{theme}.png")); }
            if (!translation)
            {
                chunkProgress!.Report(new("VadAnalyzing")); await Flush();
                Assert.True(model.Workspace.HasStepProgress, $"步骤={model.Workspace.ResultStage}");
                Assert.True(model.Activity.IsIndeterminate, $"阶段={model.Activity.Message}，计数={model.Activity.CountText}"); Assert.Empty(model.Activity.CountText);
            }
            model.SelectedMedia = null; await model.WaitForWorkspaceAsync(); await Flush();
            Assert.False(model.Workspace.HasStepProgress); Assert.True(model.Activity.IsVisible);
            model.CancelCommand.Execute(null); Assert.Equal("正在取消…", model.Activity.Message);
            await operation; Assert.False(model.Activity.IsActive);
        }
        finally { model.CancelCommand.Execute(null); if (operation is not null) await operation; window.Close(); }
    }
}
