using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Interactivity;
using System.Text.Json;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class EmbeddedModelPromptTests
{
    private static SubtitlePipelineProfile Custom(string name = "我的管线") => SubtitlePipelinePresets.Direct with { Id = Guid.NewGuid().ToString("N"), Name = name };

    [Fact]
    public void LibraryHasTwoBuiltInsAndModelsDoNotSelectPipelines()
    {
        var settings = new AppSettings();
        Assert.Equal(2, settings.AllPipelines.Count()); Assert.Empty(settings.PipelineLibrary);
        Assert.Equal(SubtitlePipelinePresets.TwoStage, settings.GetPipeline());
        settings.Provider = TranslationProvider.Local;
        Assert.Equal(SubtitlePipelinePresets.TwoStage, settings.GetPipeline());
        settings.LocalModelId = EmbeddedModelCatalog.Presets[1].Id;
        Assert.Equal(SubtitlePipelinePresets.TwoStage, settings.GetPipeline());
        Assert.Throws<ArgumentException>(() => settings.SavePipeline(SubtitlePipelinePresets.TwoStage with { Name = "覆盖内置" }));
    }

    [Theory]
    [InlineData(TranslationProvider.Compatible)]
    [InlineData(TranslationProvider.DeepSeek)]
    [InlineData(TranslationProvider.Local)]
    public async Task CustomTemplateCanBeSharedRenamedAndUpdatedWithoutCopyingIntoModels(TranslationProvider provider)
    {
        using var fixture = new Fixture(); var settings = fixture.Store.CreateDefaults();
        var custom = Custom(); settings.SavePipeline(custom);
        settings.Provider = provider; settings.SetDefaultPipeline(custom.Id);
        settings.Provider = TranslationProvider.Local; settings.LocalModelId = EmbeddedModelCatalog.Presets[1].Id; settings.SetDefaultPipeline(custom.Id);
        var snapshot = ConfigurationStore.Snapshot(settings);
        settings.SavePipeline(custom with { Name = "改名", Translation = custom.Translation with { UserTemplate = "修改规则 {source_text}" } });
        Assert.Equal("修改规则 {source_text}", settings.GetPipeline().Translation.UserTemplate);
        settings.Provider = provider; Assert.Equal("改名", settings.GetPipeline().Name);
        Assert.Equal(custom.Id, snapshot.GetPipeline().Id); Assert.Equal(custom.Translation, snapshot.GetPipeline().Translation);
        await fixture.Store.SaveAsync(settings, []);
        var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Single(loaded.PipelineLibrary); Assert.Equal(custom.Id, loaded.GetPipeline().Id);
        Assert.Equal("改名", loaded.GetPipeline().Name);
    }

    [Fact]
    public void ModelIdentityDistinguishesEndpointAndModelWithoutChangingPipeline()
    {
        var settings = new AppSettings { Provider = TranslationProvider.Compatible, BaseUrl = "https://provider.example/v1", CloudModel = "model-a" };
        var custom = Custom(); settings.SavePipeline(custom); settings.SetDefaultPipeline(custom.Id);
        var identity = settings.ModelKey();
        settings.BaseUrl = "https://provider.example/v1/chat/completions"; Assert.Equal(identity, settings.ModelKey());
        settings.CloudModel = "model-b"; Assert.NotEqual(identity, settings.ModelKey()); Assert.Equal(custom.Id, settings.GetPipeline().Id);
        settings.BaseUrl = "https://other.example/v1"; Assert.NotEqual(identity, settings.ModelKey()); Assert.Equal(custom.Id, settings.GetPipeline().Id);
        settings.Provider = TranslationProvider.DeepSeek; Assert.Equal(custom.Id, settings.GetPipeline().Id);
    }
    [Fact]
    public void DeleteRemovesReferencesAndRestoresEachModelDefault()
    {
        var settings = new AppSettings(); var custom = Custom(); settings.SavePipeline(custom);
        settings.SetDefaultPipeline(custom.Id); settings.Provider = TranslationProvider.Local; settings.SetDefaultPipeline(custom.Id);
        settings.LocalModelId = EmbeddedModelCatalog.Presets[1].Id; settings.SetDefaultPipeline(custom.Id);
        var other = Custom("其他模板"); settings.SavePipeline(other);
        settings.Provider = TranslationProvider.Compatible; settings.SetDefaultPipeline(other.Id);
        settings.DeletePipeline(custom.Id);
        Assert.Single(settings.PipelineLibrary); Assert.Equal(other.Id, settings.DefaultPipelineId);
        Assert.Equal(other, settings.GetPipeline());
        settings.Provider = TranslationProvider.DeepSeek; Assert.Equal(other.Id, settings.GetPipeline().Id);
        settings.Provider = TranslationProvider.Local; Assert.Equal(other.Id, settings.GetPipeline().Id);
        settings.DeletePipeline(other.Id); Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, settings.DefaultPipelineId);
        Assert.Throws<ArgumentException>(() => settings.DeletePipeline(SubtitlePipelinePresets.TwoStage.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureSchemaOrCorruptJsonIsNotReplacedOrBackedUp(bool future)
    {
        using var fixture = new Fixture(); var file = Path.Combine(fixture.Root, "settings.json");
        var content = future ? "{\"SchemaVersion\":99}" : "{";
        await File.WriteAllTextAsync(file, content);
        if (future) await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.LoadAsync());
        else await Assert.ThrowsAnyAsync<JsonException>(() => fixture.Store.LoadAsync());
        Assert.Equal(content, await File.ReadAllTextAsync(file)); Assert.Empty(Directory.GetFiles(fixture.Root, "settings.previous-*.json"));
    }

    [Fact]
    public async Task DanglingReferenceIsRejectedBeforeReplacingSavedConfiguration()
    {
        using var fixture = new Fixture(); var settings = fixture.Store.CreateDefaults(); await fixture.Store.SaveAsync(settings, []);
        var file = Path.Combine(fixture.Root, "settings.json"); var previous = await File.ReadAllTextAsync(file);
        settings.PipelineModelDefaults["missing-template"] = [];
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(settings, []));
        Assert.Equal(previous, await File.ReadAllTextAsync(file));
    }

    [AvaloniaFact]
    public async Task CopyCanBeSavedWithoutEditingAndNewTemplateCanBeCreatedAndRenamed()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.InitializeCommand.ExecuteAsync(null);
        model.CopyPromptCommand.Execute(null); Assert.True(model.CanEditPrompt); Assert.True(model.HasPromptEdits);
        await model.SavePromptCommand.ExecuteAsync(null); Assert.Empty(model.Error); Assert.Single(model.Settings.PipelineLibrary);
        var copyId = model.SelectedPipeline!.Id; Assert.NotEqual(SubtitlePipelinePresets.TwoStage.Id, copyId);
        model.NewPromptCommand.Execute(null); Assert.NotEmpty(model.PromptValidationError);
        model.ProfileName = "空白新建"; model.UserTemplate = "译成中文 {source_text}";
        await model.SavePromptCommand.ExecuteAsync(null); Assert.Empty(model.Error); Assert.Equal(2, model.Settings.PipelineLibrary.Count);
        model.ProfileName = "重命名"; await model.SavePromptCommand.ExecuteAsync(null);
        Assert.Equal("重命名", (await fixture.Store.LoadAsync())!.FindPipeline(model.SelectedPipeline!.Id).Name);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Translation.UserTemplate, model.Settings.FindPipeline(SubtitlePipelinePresets.TwoStage.Id).Translation.UserTemplate);
    }

    [AvaloniaFact]
    public async Task DirtySelectionSupportsCancelDiscardAndSaveWithoutChangingAssociation()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.CopyPromptCommand.Execute(null); var copyId = model.SelectedPipeline!.Id; model.ProfileName = "待保存";
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; await model.PromptSwitchTask;
        Assert.Equal(copyId, model.SelectedPipeline.Id); Assert.True(model.HasPromptEdits);
        fixture.Dialogs.SwitchChoice = PromptSwitchChoice.Save;
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; await model.PromptSwitchTask;
        Assert.Empty(model.Error); Assert.Equal(SubtitlePipelinePresets.Direct.Id, model.SelectedPipeline.Id); Assert.Equal("待保存", model.Settings.FindPipeline(copyId).Name);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, model.DefaultPipeline!.Id);
        model.CopyPromptCommand.Execute(null); var discarded = model.SelectedPipeline!.Id;
        fixture.Dialogs.SwitchChoice = PromptSwitchChoice.Discard;
        model.SelectedPipeline = SubtitlePipelinePresets.TwoStage; await model.PromptSwitchTask;
        Assert.False(model.HasPromptEdits); Assert.False(model.Settings.PipelineLibrary.ContainsKey(discarded));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsavedNewAndCopiedTemplatesCanBeDeletedWithoutWritingConfiguration(bool copy)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        if (copy) model.CopyPromptCommand.Execute(null); else model.NewPromptCommand.Execute(null);
        var id = model.SelectedPipeline!.Id; Assert.True(model.CanDeletePrompt);
        model.Settings.TimeoutSeconds = 45;
        fixture.Dialogs.Confirm = false; await model.DeletePromptCommand.ExecuteAsync(null);
        Assert.Equal(id, model.SelectedPipeline.Id); Assert.True(model.HasPromptEdits);
        fixture.Dialogs.Confirm = true; await model.DeletePromptCommand.ExecuteAsync(null);
        Assert.Equal(SubtitlePipelinePresets.TwoStage, model.SelectedPipeline); Assert.False(model.HasPromptEdits);
        Assert.DoesNotContain(model.VisiblePipelineItems, profile => profile.Id == id);
        Assert.Empty(model.Settings.PipelineLibrary); Assert.False(File.Exists(Path.Combine(fixture.Root, "settings.json")));
        Assert.Equal(45, model.Settings.TimeoutSeconds); Assert.True(model.HasUnsavedSettings); Assert.False(model.CanDeletePrompt);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveDuringSwitchKeepsDraftSelectionAndLibraryUnchanged(bool invalidTemplate)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.CopyPromptCommand.Execute(null); var draft = model.SelectedPipeline!.Id;
        if (invalidTemplate) model.UserTemplate = "缺少变量";
        else Directory.CreateDirectory(Path.Combine(fixture.Root, "settings.json"));
        fixture.Dialogs.SwitchChoice = PromptSwitchChoice.Save;
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; await model.PromptSwitchTask;
        Assert.Equal(draft, model.SelectedPipeline.Id); Assert.True(model.HasPromptEdits); Assert.NotEmpty(model.Error);
        Assert.Empty(model.Settings.PipelineLibrary); Assert.Contains(model.VisiblePipelineItems, profile => profile.Id == draft);
        if (!invalidTemplate) Directory.Delete(Path.Combine(fixture.Root, "settings.json"));
        model.UserTemplate = "翻译 {source_text}"; model.BatchTranslation = false;
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; await model.PromptSwitchTask;
        Assert.Empty(model.Error); Assert.Equal(SubtitlePipelinePresets.Direct, model.SelectedPipeline);
        Assert.Equal("翻译 {source_text}", (await fixture.Store.LoadAsync())!.FindPipeline(draft).Translation.UserTemplate);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewAndCopyActionsRequireConfirmationWhenCurrentDraftIsDirty(bool copy)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.CopyPromptCommand.Execute(null); var draft = model.SelectedPipeline!.Id;
        if (copy) model.CopyPromptCommand.Execute(null); else model.NewPromptCommand.Execute(null);
        await model.PromptSwitchTask;
        Assert.Equal(draft, model.SelectedPipeline.Id); Assert.Equal(1, fixture.Dialogs.SwitchConfirmations);
        fixture.Dialogs.SwitchChoice = PromptSwitchChoice.Discard;
        if (copy) model.CopyPromptCommand.Execute(null); else model.NewPromptCommand.Execute(null);
        await model.PromptSwitchTask;
        Assert.NotEqual(draft, model.SelectedPipeline.Id); Assert.True(model.HasPromptEdits);
        Assert.Empty(model.Settings.PipelineLibrary); Assert.DoesNotContain(model.VisiblePipelineItems, profile => profile.Id == draft);
    }

    [AvaloniaFact]
    public async Task PreviewTabRendersCurrentDraftWithoutInferenceAndCopyIsDisabledOnError()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; model.CopyPromptCommand.Execute(null);
        model.UserTemplate = "当前草稿 {source_text}"; model.PromptSectionIndex = 2;
        Assert.Contains("当前草稿 Hello.", model.PromptUserPreview); Assert.DoesNotContain("{source_text}", model.PromptUserPreview);
        Assert.True(model.CanCopyPromptPreview); Assert.True(model.HasPromptEdits); Assert.Empty(model.Settings.PipelineLibrary);
        await model.CopyPromptPreviewCommand.ExecuteAsync(null); Assert.Equal(model.PromptSamplePreview, fixture.Dialogs.CopiedText);
        Assert.Equal("已复制预览", model.PromptPreviewStatus); Assert.Null(fixture.Jobs.EngineSettings); Assert.Null(fixture.Jobs.PreviewSettings);
        model.PromptSectionIndex = 0; model.UserTemplate = "无效草稿"; model.PromptSectionIndex = 2;
        Assert.NotEmpty(model.PromptPreviewError); Assert.Empty(model.PromptSamplePreview); Assert.Empty(model.PromptUserPreview);
        Assert.False(model.CanCopyPromptPreview); Assert.Empty(model.Error);
    }

    [AvaloniaTheory]
    [InlineData(PromptSwitchChoice.Cancel, "CancelSwitchButton")]
    [InlineData(PromptSwitchChoice.Discard, "DiscardSwitchButton")]
    [InlineData(PromptSwitchChoice.Save, "SaveSwitchButton")]
    public async Task RealModalDialogReturnsChoiceAndBelongsToOwner(PromptSwitchChoice choice, string buttonName)
    {
        var owner = new Window { Width = 700, Height = 500 }; owner.Show();
        var dialog = new PromptSwitchDialog("测试模板");
        try
        {
            var result = dialog.ShowDialog<PromptSwitchChoice>(owner);
            Assert.True(dialog.IsVisible); Assert.False(result.IsCompleted); Assert.Equal(owner, dialog.Owner); Assert.Contains("测试模板", dialog.Message);
            dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(choice, await result); Assert.True(owner.IsEnabled);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task EditorRemainsIndependentFromProviderAndModelSwitchAndPreviewDoesNotCallInference()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.SelectedPipeline = SubtitlePipelinePresets.TwoStage; var id = model.SelectedPipeline!.Id; var text = model.UserTemplate;
        model.ProviderIndex = (int)TranslationProvider.Local; model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1];
        model.ProviderIndex = (int)TranslationProvider.Compatible;
        Assert.Equal(id, model.SelectedPipeline.Id); Assert.Equal(text, model.UserTemplate);
        model.PreviewPromptSampleCommand.Execute(null); Assert.Contains("Hello.", model.PromptSamplePreview); Assert.Contains("How are you?", model.PromptSamplePreview);
        Assert.Null(fixture.Jobs.EngineSettings); Assert.Null(fixture.Jobs.PreviewSettings); Assert.False(model.HasPromptEdits);
    }

    [AvaloniaFact]
    public async Task BatchToggleRequiresMatchingTemplateAndControlsBothBackends()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.SelectedPipeline = SubtitlePipelinePresets.Direct; model.CopyPromptCommand.Execute(null);
        model.BatchTranslation = true; Assert.Contains("source_text", model.PromptValidationError);
        await model.SavePromptCommand.ExecuteAsync(null); Assert.NotEmpty(model.Error); Assert.Empty(model.Settings.PipelineLibrary);
        model.UserTemplate = "返回 ID 对应译文 JSON {cues_json}"; model.PromptBatchSize = 3;
        await model.SavePromptCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        model.DefaultPipeline = model.SelectedPipeline;
        Assert.Equal(3, model.Settings.TranslationSettings().BatchSize); Assert.Equal(4, model.Settings.TranslationSettings().Concurrency);
        model.ProviderIndex = (int)TranslationProvider.Local; model.DefaultPipeline = model.SelectedPipeline;
        Assert.Equal(3, model.Settings.TranslationSettings().BatchSize); Assert.Equal(2, model.Settings.TranslationSettings().Concurrency);
    }

    [AvaloniaFact]
    public async Task DeletingReferencedCustomTemplatePersistsDefaultRestorationAndRemovalTogether()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.CopyPromptCommand.Execute(null); await model.SavePromptCommand.ExecuteAsync(null);
        var id = model.SelectedPipeline!.Id; model.DefaultPipeline = model.SelectedPipeline;
        model.ProviderIndex = (int)TranslationProvider.Local; model.DefaultPipeline = model.SelectedPipeline;
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1]; model.DefaultPipeline = model.SelectedPipeline;
        fixture.Dialogs.Confirm = false; await model.DeletePromptCommand.ExecuteAsync(null);
        Assert.Contains("新媒体的默认管线", fixture.Dialogs.ConfirmationMessage); Assert.Single(model.Settings.PipelineLibrary);
        fixture.Dialogs.Confirm = true; await model.DeletePromptCommand.ExecuteAsync(null);
        Assert.Empty(model.Error); var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Empty(loaded.PipelineLibrary); Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, loaded.DefaultPipelineId);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, model.DefaultPipeline!.Id);
        Assert.DoesNotContain(model.PipelineItems, profile => profile.Id == id);
    }

    [AvaloniaFact]
    public async Task TestUsesAssociatedTemplateRatherThanEditorAndIgnoresInvalidUnusedDraft()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.ProviderIndex = (int)TranslationProvider.Local; model.SelectedPipeline = SubtitlePipelinePresets.TwoStage;
        model.CopyPromptCommand.Execute(null); model.UserTemplate = "无效的未保存草稿";
        await model.TestEngineCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Id, fixture.Jobs.EngineSettings!.GetPipeline().Id);
        var bad = Custom("无效的其他模板") with { Translation = PromptPresets.SingleSimple with { UserTemplate = "缺少变量" } }; model.Settings.PipelineLibrary[bad.Id] = bad;
        await model.TestEngineCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        Assert.Contains("无效的其他模板", Assert.Throws<ArgumentException>(() => ConfigurationStore.Validate(model.Settings)).Message);
    }

    [AvaloniaFact]
    public async Task EditingSharedTemplateDoesNotAlterCompletedTaskRetryOrPreview()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        model.ProviderIndex = (int)TranslationProvider.Local; model.SelectedPipeline = SubtitlePipelinePresets.Direct; model.CopyPromptCommand.Execute(null);
        model.UserTemplate = "原模板 {source_text}"; await model.SavePromptCommand.ExecuteAsync(null); model.DefaultPipeline = model.SelectedPipeline;
        var file = Path.Combine(fixture.Root, "test.gguf"); await File.WriteAllTextAsync(file, "GGUF测试"); model.Settings.ModelPath = file;
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = (int)TranslationProvider.Local; model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null); Assert.True(model.CanExport);
        model.UserTemplate = "后来修改 {source_text}"; await model.SavePromptCommand.ExecuteAsync(null);
        await model.PreviewRequestCommand.ExecuteAsync(null); model.CueSelection.Clear(); model.CueSelection.Select(0); await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal("原模板 {source_text}", fixture.Jobs.PreviewSettings!.GetPipeline().Translation.UserTemplate);
        Assert.Equal("原模板 {source_text}", fixture.Jobs.LastSettings!.GetPipeline().Translation.UserTemplate); Assert.True(model.CanExport);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task LibraryIsVisibleBuiltInsAreReadOnlyAndSelectorsDoNotRecurse(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        try
        {
            await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme; model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var view = window.FindControl<SettingsView>("SettingsPage")!;
            var library = view.FindControl<ListBox>("PipelineLibraryList")!;
            Assert.Equal(2, library.ItemCount); Assert.Equal(model.SelectedPipeline, library.SelectedItem);
            Assert.True(view.FindControl<TextBox>("PromptNameEditor")!.IsReadOnly);
            Assert.False(view.FindControl<CheckBox>("BatchTranslationToggle")!.IsEnabled);
            CapturePromptUi(window, $"prompt-body-{width}-{theme}.png");
            model.PipelineEditingStage = 1;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(view.FindControl<TextBox>("PromptUserEditor")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<TextBox>("PromptUserEditor")!.IsReadOnly);
            window.UpdateLayout();
            var systemEditor = view.FindControl<TextBox>("PromptSystemEditor")!;
            var userEditor = view.FindControl<TextBox>("PromptUserEditor")!;
            Assert.True(systemEditor.TranslatePoint(new Point(0, systemEditor.Bounds.Height), window)!.Value.Y <
                userEditor.TranslatePoint(default, window)!.Value.Y);
            CapturePromptUi(window, $"pipeline-translation-{width}-{theme}.png");
            model.PipelineEditingStage = 0;
            model.PromptSectionIndex = 1;
            CapturePromptUi(window, $"prompt-options-{width}-{theme}.png");
            model.PromptSectionIndex = 2;
            Assert.Contains("Hello.", model.PromptUserPreview);
            Assert.True(view.FindControl<TextBox>("PromptUserPreviewBox")!.IsReadOnly);
            CapturePromptUi(window, $"prompt-preview-{width}-{theme}.png");
            model.PromptSectionIndex = 0;
            model.PromptSearch = "模型分句"; Assert.Single(model.VisiblePipelineItems); model.PromptSearch = "";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(model.SelectedPipeline, library.SelectedItem);
            library.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, SubtitlePipelinePresets.Direct);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(SubtitlePipelinePresets.Direct, model.SelectedPipeline);
            model.CopyPromptCommand.Execute(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(model.SelectedPipeline, library.SelectedItem); Assert.False(view.FindControl<TextBox>("PromptNameEditor")!.IsReadOnly);
            var more = view.FindControl<Button>("MorePromptButton")!; more.Flyout!.ShowAt(more);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var delete = view.FindControl<MenuItem>("DeletePromptMenuItem")!;
            Assert.True(delete.IsEnabled); Assert.Same(model.DeletePromptCommand, delete.Command); more.Flyout.Hide();
            var editor = view.FindControl<TextBox>("PromptUserEditor")!;
            Assert.True(editor.Bounds.Height >= 80);
            var promptScroll = view.FindControl<ScrollViewer>("TranslationPromptScroll")!;
            var editorBottom = promptScroll.TranslatePoint(new Point(0, promptScroll.Bounds.Height), window)!.Value;
            var saveTop = view.FindControl<Button>("SettingsSaveButton")!.TranslatePoint(default, window)!.Value;
            Assert.True(editorBottom.Y <= saveTop.Y);
            view.FindControl<TextBox>("PromptNameEditor")!.SetCurrentValue(TextBox.TextProperty, "界面自定义模板");
            view.FindControl<TextBox>("PromptUserEditor")!.SetCurrentValue(TextBox.TextProperty, "规则 {source_text}");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            fixture.Dialogs.SwitchCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var draft = model.SelectedPipeline;
            library.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, SubtitlePipelinePresets.TwoStage);
            Assert.Equal(draft, library.SelectedItem); Assert.Equal(draft, model.SelectedPipeline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(1, fixture.Dialogs.SwitchConfirmations);
            model.NewPromptCommand.Execute(null); Assert.Equal(draft, model.SelectedPipeline);
            fixture.Dialogs.SwitchCompletion.SetResult(PromptSwitchChoice.Cancel);
            await model.PromptSwitchTask;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(model.SelectedPipeline, library.SelectedItem);
            await model.SavePromptCommand.ExecuteAsync(null); Assert.Empty(model.Error);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(model.SelectedPipeline, library.SelectedItem); Assert.Equal("规则 {source_text}", model.Settings.FindPipeline(model.SelectedPipeline!.Id).Translation.UserTemplate);
            model.SettingsSectionIndex = 1; model.ProviderIndex = (int)TranslationProvider.Local;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var selector = view.FindControl<ComboBox>("EmbeddedModelSelector")!; var changes = 0;
            model.Settings.PropertyChanging += (_, args) => { if (args.PropertyName == nameof(AppSettings.LocalModelId) && ++changes > 8) throw new InvalidOperationException("递归切换"); };
            foreach (var selected in new[] { EmbeddedModelCatalog.Presets[1], EmbeddedModelCatalog.Default, EmbeddedModelCatalog.Presets[1], EmbeddedModelCatalog.Default })
            {
                selector.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, selected);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Equal(selected, model.SelectedEmbeddedModel); Assert.Equal(selected, selector.SelectedItem);
            }
            Assert.Equal(4, changes); Assert.Equal(3, selector.ItemCount);
            model.SettingsSectionIndex = 2; window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"prompt-library-{width}-{theme}.png")); }
        }
        finally { window.Close(); }
    }

    private static void CapturePromptUi(Window window, string fileName)
    {
        window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, fileName)); }
    }
}
