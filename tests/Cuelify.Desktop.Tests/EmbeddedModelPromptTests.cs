using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.Views;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class EmbeddedModelPromptTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DefaultsAndLegacyBuiltInProfilesFollowSelectedModel(int index)
    {
        using var fixture = new Fixture(); var selected = EmbeddedModelCatalog.Presets[index];
        var settings = new AppSettings { EmbeddedModelId = selected.Id };
        Assert.Equal(selected.DefaultProfile, settings.GetLocalProfile());
        Assert.Equal(PromptPresets.LocalSimple, EmbeddedModelCatalog.Legacy.DefaultProfile);
        foreach (var previous in new[] { PromptPresets.Local, PromptPresets.LegacyLocal })
        {
            settings.LocalProfile = previous;
            await AtomicFile.WriteJsonAsync(Path.Combine(fixture.Root, "settings.json"), settings, default);
            var loaded = (await fixture.Store.LoadAsync())!;
            Assert.Equal(selected.DefaultProfile, loaded.GetLocalProfile());
            await fixture.Store.SaveAsync(loaded, []);
            Assert.DoesNotContain("\"LocalProfile\"", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "settings.json")));
            Assert.Equal(selected.DefaultProfile, (await fixture.Store.LoadAsync())!.GetLocalProfile());
        }
    }

    [Fact]
    public async Task LegacyCustomProfileMigratesOnlyToSelectedModelAndSnapshotsKeepIndependentProfiles()
    {
        using var fixture = new Fixture();
        var first = EmbeddedModelCatalog.Default; var second = EmbeddedModelCatalog.Presets[1];
        var custom = PromptPresets.Local with { Name = "原有自定义", UserTemplate = "我的规则 {source_text}" };
        await AtomicFile.WriteJsonAsync(Path.Combine(fixture.Root, "settings.json"), new AppSettings { EmbeddedModelId = first.Id, LocalProfile = custom }, default);
        var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Equal(custom, loaded.GetLocalProfile());
        loaded.EmbeddedModelId = second.Id; Assert.Equal(second.DefaultProfile, loaded.GetLocalProfile());
        loaded.Provider = TranslationProvider.Local;
        loaded.SetProfile(PromptPresets.LocalSimple with { Name = "7B 自定义" });
        var snapshot = ConfigurationStore.Snapshot(loaded);
        loaded.SetProfile(PromptPresets.Local with { Name = "后来修改" });
        Assert.Equal("7B 自定义", snapshot.GetProfile().Name);
        Assert.Equal(custom, snapshot.EmbeddedModelProfiles[first.Id]);
        var invalid = ConfigurationStore.Snapshot(loaded);
        invalid.EmbeddedModelProfiles[first.Id] = PromptPresets.Cloud;
        Assert.Throws<ArgumentException>(() => ConfigurationStore.Validate(invalid));
    }

    [AvaloniaFact]
    public async Task EditsPresetsAndResetStayWithEachModelAcrossProviderSwitchAndRestart()
    {
        using var fixture = new Fixture();
        var first = EmbeddedModelCatalog.Default; var second = EmbeddedModelCatalog.Presets[1];
        await using (var model = fixture.Model())
        {
            await model.InitializeCommand.ExecuteAsync(null); model.ProviderIndex = (int)TranslationProvider.Local;
            Assert.Equal(PromptPresets.LocalSimple.UserTemplate, model.UserTemplate);
            model.ProfileName = "1.8B 自定义"; model.UserTemplate = "简单编辑 {source_text}";
            model.SelectedEmbeddedModel = second;
            Assert.Equal(PromptPresets.Local.UserTemplate, model.UserTemplate);
            model.ProfileName = "7B 自定义"; model.SystemTemplate = "7B 的系统规则"; model.UserTemplate = "7B 编辑 {context_before} {source_text}";
            model.SelectedEmbeddedModel = first;
            Assert.Equal("1.8B 自定义", model.ProfileName); Assert.Empty(model.SystemTemplate);
            model.ProviderIndex = (int)TranslationProvider.DeepSeek; model.ProfileName = "云端独立";
            model.ProviderIndex = (int)TranslationProvider.Local;
            Assert.Equal("简单编辑 {source_text}", model.UserTemplate);
            model.SelectedEmbeddedModel = second;
            Assert.Equal("7B 的系统规则", model.SystemTemplate);
            Assert.Equal("7B 自定义", model.ProfileName);
            model.ResetPromptCommand.Execute(null);
            Assert.Equal(second.DefaultProfile.UserTemplate, model.UserTemplate); Assert.Empty(model.SystemTemplate);
            model.PresetSelection = PromptPresets.LocalSimple.Name;
            Assert.Equal(PromptPresets.LocalSimple.UserTemplate, model.UserTemplate);
            model.UserTemplate = "再次编辑 {source_text}";
            model.PresetSelection = PromptPresets.LocalSimple.Name;
            Assert.Equal(PromptPresets.LocalSimple.UserTemplate, model.UserTemplate);
            model.SelectedEmbeddedModel = first;
            Assert.Equal("1.8B 自定义", model.ProfileName);
            await model.SaveSettingsCommand.ExecuteAsync(null); Assert.Empty(model.Error);
        }
        await using var reloaded = fixture.Model(); await reloaded.InitializeCommand.ExecuteAsync(null);
        Assert.Equal("1.8B 自定义", reloaded.ProfileName); Assert.Equal("简单编辑 {source_text}", reloaded.UserTemplate);
        reloaded.SelectedEmbeddedModel = second; Assert.Equal(PromptPresets.LocalSimple.UserTemplate, reloaded.UserTemplate);
        reloaded.SelectedEmbeddedModel = first; reloaded.ResetPromptCommand.Execute(null);
        Assert.Equal(first.DefaultProfile.UserTemplate, reloaded.UserTemplate);
        reloaded.ProviderIndex = (int)TranslationProvider.DeepSeek; Assert.Equal("云端独立", reloaded.ProfileName);
    }

    [AvaloniaFact]
    public async Task SwitchingAndSavingModelPromptsDoesNotAlterCompletedJobRetryOrPreview()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        await model.InitializeCommand.ExecuteAsync(null); model.ProviderIndex = (int)TranslationProvider.Local;
        var file = Path.Combine(fixture.Root, "test.gguf"); await File.WriteAllTextAsync(file, "GGUF测试"); model.Settings.ModelPath = file;
        model.ProfileName = "任务原模板"; model.UserTemplate = "原模板 {source_text}";
        await model.SaveSettingsCommand.ExecuteAsync(null); model.TaskProviderIndex = (int)TranslationProvider.Local; model.InputPath = "fixture.mp4";
        await model.RunCommand.ExecuteAsync(null); Assert.True(model.CanExport);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Presets[1]; model.UserTemplate = "7B 新模板 {source_text}";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        model.SelectedEmbeddedModel = EmbeddedModelCatalog.Default; model.UserTemplate = "1.8B 后来修改 {source_text}";
        await model.SaveSettingsCommand.ExecuteAsync(null);
        await model.PreviewRequestCommand.ExecuteAsync(null); model.SelectedCue = model.Rows[0]; await model.RetryCueCommand.ExecuteAsync(null);
        Assert.Equal("原模板 {source_text}", fixture.Jobs.PreviewSettings!.GetProfile().UserTemplate);
        Assert.Equal("原模板 {source_text}", fixture.Jobs.LastSettings!.GetProfile().UserTemplate);
        Assert.True(model.CanExport);
    }

    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task PromptPageShowsModelAndIndependentDefaultsInBothThemes(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme; model.ProviderIndex = (int)TranslationProvider.Local;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 2;
        var view = window.FindControl<SettingsView>("SettingsPage")!;
        var selector = view.FindControl<ComboBox>("PromptModelSelector")!;
        var presets = view.FindControl<ComboBox>("PromptPresetSelector")!;
        foreach (var selected in EmbeddedModelCatalog.Presets)
        {
            selector.SelectedItem = selected;
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Assert.True(selector.IsEffectivelyVisible); Assert.True(selector.Bounds.Height > 0);
            Assert.Equal(selected, model.SelectedEmbeddedModel); Assert.Equal(selected.DefaultProfile.UserTemplate, model.UserTemplate);
            Assert.Equal(2, presets.ItemCount);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"model-prompts-{selected.Id}-{width}-{theme}.png")); }
        }
        window.Close();
    }
}
