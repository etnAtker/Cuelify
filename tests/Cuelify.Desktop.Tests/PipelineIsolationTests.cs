using System.Text.Json;
using System.Text.Json.Nodes;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class PipelineIsolationTests
{
    [Fact]
    public void ExecutingStepDoesNotValidateUnusedProviderOrOtherStepParameters()
    {
        var settings = new ConfigurationStore().CreateDefaults();
        settings.GpuLayers = 0; settings.ContextSize = -1; settings.LocalMaximumTokens = -1;
        settings.TranslationConcurrency = 1;
        var profile = SubtitlePipelinePresets.TwoStage with { Id = "isolated", Name = "独立校验",
            Translation = PromptPresets.BatchSubtitles with { BatchSize = 0, UserTemplate = "无效" } };
        settings.PipelineLibrary[profile.Id] = profile; settings.TaskPipelineId = profile.Id;
        ConfigurationStore.Validate(settings, false, TaskStage.Subtitles);
        Assert.Throws<ArgumentException>(() => ConfigurationStore.Validate(settings, false, TaskStage.Translation));
        settings.Provider = TranslationProvider.Local; settings.GpuLayers = 99; settings.ContextSize = 512; settings.LocalMaximumTokens = 300;
        settings.Temperature = "unused"; settings.AdvancedJson = "unused"; settings.CloudModel = "";
        ConfigurationStore.Validate(settings, false, TaskStage.Subtitles);
        (SubtitlePipelinePresets.Direct with { Segmentation = null!, BlockSeconds = -1, ContextCharacters = -1 }).ValidateSegmentation();
    }

    [Fact]
    public void StageIdentityAndSnapshotExcludeOtherStepsAndOtherProviders()
    {
        using var fixture = new Fixture();
        var settings = fixture.Store.CreateDefaults();
        var pipeline = SubtitlePipelinePresets.TwoStage with { Id = "custom", Name = "独立步骤" };
        settings.SavePipeline(pipeline); settings.TaskPipelineId = pipeline.Id;
        var segmentation = ProjectTaskService.ConfigurationIdentity(TaskStage.Subtitles, settings);
        var translation = ProjectTaskService.ConfigurationIdentity(TaskStage.Translation, settings);
        settings.SavePipeline(pipeline with { Translation = pipeline.Translation with { UserTemplate = "新翻译规则 {cues_json}" } });
        Assert.Equal(segmentation, ProjectTaskService.ConfigurationIdentity(TaskStage.Subtitles, settings));
        Assert.NotEqual(translation, ProjectTaskService.ConfigurationIdentity(TaskStage.Translation, settings));
        settings.SavePipeline(pipeline with { Segmentation = pipeline.Segmentation with { UserTemplate = "新的分句规则" } });
        Assert.Equal(translation, ProjectTaskService.ConfigurationIdentity(TaskStage.Translation, settings));
        settings.ModelPath = "无关的本地模型.gguf";
        Assert.Equal(translation, ProjectTaskService.ConfigurationIdentity(TaskStage.Translation, settings));
        var snapshot = StageSettingsSnapshot.Capture(TaskStage.Subtitles, settings).GetRawText();
        Assert.DoesNotContain("TranslationParameters", snapshot); Assert.DoesNotContain("无关的本地模型", snapshot);
        Assert.DoesNotContain("PipelineModelDefaults", snapshot);
        var resultSnapshot = StageSettingsSnapshot.Capture(TaskStage.Translation, settings);
        Assert.DoesNotContain("Segmentation\"", resultSnapshot.GetRawText());
        Assert.Equal(pipeline.Translation.UserTemplate, resultSnapshot.Deserialize<AppSettings>()!.GetProfile().UserTemplate);
        var unusedInvalid = pipeline with { Translation = pipeline.Translation with { UserTemplate = "无效翻译模板" } };
        unusedInvalid.ValidateSegmentation(); Assert.Throws<ArgumentException>(unusedInvalid.ValidateTranslation);
        (pipeline with { Segmentation = new("", "") }).ValidateTranslation();
    }

    [Fact]
    public async Task SchemaFourMigrationArchivesJointPromptAndPreservesModelsAndStepDefaults()
    {
        using var fixture = new Fixture(); var settings = fixture.Store.CreateDefaults();
        var custom = SubtitlePipelinePresets.TwoStage with { Id = "old-joint", Name = "旧自定义" };
        settings.SavePipeline(custom);
        var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
        node["SchemaVersion"] = 4; node["DefaultPipelineId"] = "old-joint";
        node["PipelineLibrary"]![custom.Id]!["Mode"] = 1; node["PipelineLibrary"]![custom.Id]!.AsObject().Remove("Method");
        node["PipelineLibrary"]![custom.Id]!["Segmentation"]!["UserTemplate"] = "旧联合：同时分句并翻译";
        var path = Path.Combine(fixture.Root, "settings.json"); await File.WriteAllTextAsync(path, node.ToJsonString());
        var loaded = (await fixture.Store.LoadAsync())!;
        Assert.Equal(5, loaded.SchemaVersion); Assert.Equal(custom.Id, loaded.DefaultPipelineId);
        Assert.Equal(SegmentationMethod.Model, loaded.GetPipeline().Method);
        Assert.Equal(SubtitlePipelinePresets.TwoStage.Segmentation, loaded.GetPipeline().Segmentation);
        Assert.Contains("旧联合", loaded.ArchivedPipelines[custom.Id].GetProperty("Segmentation").GetProperty("UserTemplate").GetString());
        Assert.Equal(settings.Models.Keys.Order(), loaded.Models.Keys.Order());
        Assert.NotEmpty(Directory.GetFiles(fixture.Root, "settings.schema4-*.backup"));
        Assert.Equal(loaded.ArchivedPipelines.Keys, (await fixture.Store.LoadAsync())!.ArchivedPipelines.Keys);
    }

    [Fact]
    public void MissingStepModelDoesNotSilentlySelectAnotherModel()
    {
        var settings = new AppSettings();
        Assert.Throws<ArgumentException>(() => settings.ForStep(TaskStage.Translation));
        settings.PipelineModelDefaults[settings.GetPipeline().Id] = new() { [TaskStage.Subtitles] = "missing" };
        Assert.Throws<ArgumentException>(() => settings.ForStep(TaskStage.Subtitles));
        settings.TaskPipelineId = SubtitlePipelinePresets.Direct.Id;
        Assert.Equal(SegmentationMethod.Program, settings.ForStep(TaskStage.Subtitles).GetPipeline().Method);
    }
}
