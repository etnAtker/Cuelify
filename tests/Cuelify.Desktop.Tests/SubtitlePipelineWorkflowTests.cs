using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class SubtitlePipelineWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task TaskPipelineSelectorUsesIndependentSelectionAndFitsOptionsPage(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            await model.InitializeCommand.ExecuteAsync(null);
            model.Settings.Theme = theme; model.InputPath = "fixture.mp4"; model.Step = WorkflowStep.Options;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            var selector = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<ComboBox>("TaskPipelineSelector")!;
            Assert.True(selector.IsEffectivelyVisible); Assert.Equal(3, selector.ItemCount);
            selector.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, SubtitlePipelinePresets.TwoStage);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(SubtitlePipelinePresets.TwoStage, model.TaskPipeline); Assert.Equal(SubtitlePipelinePresets.Joint, model.Settings.GetPipeline());
            Assert.True(selector.Bounds.Width > 200);
            var notice = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<TextBlock>("UploadNotice")!;
            Assert.True(notice.IsEffectivelyVisible);
            var run = window.FindControl<WorkspaceView>("WorkspacePage")!.FindControl<Button>("RunButton")!;
            Assert.True(notice.TranslatePoint(new Point(0, notice.Bounds.Height), window)!.Value.Y <= run.TranslatePoint(default, window)!.Value.Y);
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"task-pipeline-{width}-{theme}.png")); }
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(TranslationProvider.Compatible)]
    [InlineData(TranslationProvider.DeepSeek)]
    [InlineData(TranslationProvider.Local)]
    public void AnyModelCanChooseEveryPipelineWithoutChangingItsDefault(TranslationProvider provider)
    {
        var settings = new AppSettings { Provider = provider };
        var original = settings.GetPipeline().Id;
        foreach (var pipeline in SubtitlePipelinePresets.All)
        {
            settings.TaskPipelineId = pipeline.Id;
            Assert.Equal(pipeline, settings.GetPipeline());
            var snapshot = ConfigurationStore.Snapshot(settings);
            settings.TaskPipelineId = null;
            Assert.Equal(pipeline, snapshot.GetPipeline()); Assert.Equal(original, settings.GetPipeline().Id);
        }
        settings.LocalModelId = "index-translate-9b-q4km"; settings.Provider = TranslationProvider.Local;
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, settings.GetPipeline().Id);
    }

    [Fact]
    public void InvalidEffectiveCloudStageIsRejectedBeforeProcessing()
    {
        var custom = SubtitlePipelinePresets.Joint with { Id = "invalid-thinking-stage", Name = "阶段参数测试",
            Segmentation = SubtitlePipelinePresets.Joint.Segmentation with { Parameters = new() { TopP = .8 } } };
        var settings = new AppSettings { Provider = TranslationProvider.DeepSeek, ThinkingEnabled = true, TaskPipelineId = custom.Id };
        settings.SavePipeline(custom);
        Assert.Contains("top_p", Assert.Throws<ArgumentException>(() => ConfigurationStore.Validate(settings)).Message);
    }

    [AvaloniaFact]
    public async Task TaskOverrideUsesSavedPipelineWithoutChangingModelAssociation()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.TaskPipeline = SubtitlePipelinePresets.TwoStage;
        model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, fixture.Jobs.GenerationSettings!.GetPipeline().Id);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, fixture.Jobs.LastSettings!.GetPipeline().Id);
        Assert.Equal(SubtitlePipelinePresets.Joint.Id, model.Settings.GetPipeline().Id);
    }

    [AvaloniaFact]
    public async Task JointResultSkipsSecondTranslationAndRetryKeepsOriginalTaskPipeline()
    {
        using var fixture = new Fixture(); fixture.Jobs.JointComplete = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Null(fixture.Jobs.LastSettings);
        Assert.All(model.Rows, r => Assert.Equal("联合译文", r.Translation));
        model.AssociatedPipeline = SubtitlePipelinePresets.Direct;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        await model.PreviewRequestCommand.ExecuteAsync(null);
        model.CueSelection.Select(1); await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal(SubtitlePipelinePresets.Joint.Id, fixture.Jobs.PreviewSettings!.GetPipeline().Id);
        Assert.Equal(SubtitlePipelinePresets.Joint.Id, fixture.Jobs.LastSettings!.GetPipeline().Id);
        Assert.Equal("联合译文", model.Rows[0].Translation); Assert.Equal("中文译文", model.Rows[1].Translation);
        Assert.Equal(FakeJobs.Cues[1].Start, model.Rows[1].Cue.Start);
    }

    [AvaloniaFact]
    public async Task ContinueAfterJointRetranslationFailureDoesNotRegenerateOrRestoreOldTranslation()
    {
        using var fixture = new Fixture(); fixture.Jobs.JointComplete = true;
        await using var model = fixture.Model(); model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null);
        model.CueSelection.Select(1); fixture.Jobs.Partial = true;
        await model.RetryCueCommand.ExecuteAsync(null);
        Assert.True(model.CanContinue); Assert.Null(model.Rows[1].Cue.TranslatedText);
        fixture.Jobs.Partial = false;
        await model.RunCommand.ExecuteAsync(null);
        Assert.True(model.CanExport); Assert.Equal("中文译文", model.Rows[1].Translation);
        Assert.Equal(1, fixture.Jobs.Transcriptions); Assert.Equal(1, fixture.Jobs.Generations);
        Assert.Equal(FakeJobs.Cues[1].Start, model.Rows[1].Cue.Start);
    }

    [AvaloniaFact]
    public async Task StageParametersAndSubtitleRulesAreSavedInIndependentCustomPipeline()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.SelectedPipeline = SubtitlePipelinePresets.TwoStage;
        model.CopyPromptCommand.Execute(null); var id = model.SelectedPipeline!.Id;
        model.PipelineModeIndex = 1; model.SegmentationTemperature = "0.2"; model.TranslationTemperature = "0";
        model.PipelineLineCharacters = 20; model.PipelineBlockSeconds = 30;
        await model.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Empty(model.Error);
        var saved = (await fixture.Store.LoadAsync())!.FindPipeline(id);
        Assert.Equal(SubtitlePipelineMode.JointSegmentationTranslation, saved.Mode);
        Assert.Equal(.2, saved.Segmentation.Parameters.Temperature); Assert.Equal(0, saved.TranslationParameters.Temperature);
        Assert.Equal(20, saved.LineCharacters); Assert.Equal(30, saved.BlockSeconds);
        Assert.Equal(SubtitlePipelinePresets.Joint.Id, model.Settings.GetPipeline().Id);
    }
}
