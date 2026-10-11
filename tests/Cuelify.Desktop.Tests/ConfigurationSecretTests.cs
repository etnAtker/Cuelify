using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class ConfigurationSecretTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("2048")]
    [InlineData("Provider")]
    [InlineData("Model")]
    [InlineData("local")]
    public async Task DedicatedCredentialValuesDoNotMatchSettingNamesNumbersOrPaths(string key)
    {
        using var fixture = new Fixture();
        var settings = fixture.Store.CreateDefaults();
        settings.AdvancedJson = "{\"Provider\":1,\"budget\":99}";
        await fixture.Store.SaveAsync(settings, [key]);
        Assert.NotNull(await fixture.Store.LoadAsync());
    }

    [Fact]
    public void GeneratedIdentifiersAndHashesAreNotScannedAsEditableText()
    {
        const string key = "a0123456789abcdef0123456789abcdef";
        var settings = new AppSettings { ModelSha256 = key, DefaultPipelineId = key };
        settings.ExecutionModels[TaskStage.Translation] = key;
        settings.Models[key] = new(key, "模型", []);
        ConfigurationStore.ValidateSecrets(settings, [key]);
    }

    [Theory]
    [InlineData("session-secret-123456789")]
    [InlineData("quoted-\"secret-\\-123456789")]
    public void PastedPromptSecretReportsItsFieldWithoutIncludingSecret(string key)
    {
        var settings = new AppSettings();
        settings.SavePipeline(SubtitlePipelinePresets.TwoStage with
        {
            Id = "custom", Name = "测试管线", Segmentation = new("前文 " + key + " 后文", "翻译")
        });
        var error = Assert.Throws<ArgumentException>(() => ConfigurationStore.ValidateSecrets(settings, ["other-secret", key]));
        Assert.Contains("字幕管线 1 / 分句系统提示词", error.Message);
        Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public void AdvancedJsonComparesDecodedValuesAndNotPropertyNamesOrNumbers()
    {
        const string key = "quoted-\"secret-\\-123456789";
        var settings = new AppSettings { AdvancedJson = JsonSerializer.Serialize(new { Provider = 99 }) };
        ConfigurationStore.ValidateSecrets(settings, [key, "99", "Provider"]);
        settings.AdvancedJson = JsonSerializer.Serialize(new { extra = new[] { key } });
        var error = Assert.Throws<ArgumentException>(() => ConfigurationStore.ValidateSecrets(settings, [key]));
        Assert.Contains("自定义请求参数", error.Message); Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public void LongKeyUsedAsCustomParameterNameIsStillRejected()
    {
        const string key = "session-secret-123456789";
        var settings = new AppSettings { AdvancedJson = JsonSerializer.Serialize(new Dictionary<string, object> { [key] = true }) };
        var error = Assert.Throws<ArgumentException>(() => ConfigurationStore.ValidateSecrets(settings, [key]));
        Assert.Contains("自定义请求参数 / 字段名", error.Message); Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public void ShortKeyPastedAsWholePromptValueIsStillRejected()
    {
        var settings = new AppSettings();
        settings.SavePipeline(SubtitlePipelinePresets.TwoStage with { Id = "custom", Name = "测试管线", Segmentation = new("dummy", "翻译") });
        Assert.Throws<ArgumentException>(() => ConfigurationStore.ValidateSecrets(settings, ["dummy"]));
    }

    [Fact]
    public void SavedModelParametersStillProtectSecretsForOtherProviders()
    {
        const string key = "other-provider-secret-123456789";
        var settings = new AppSettings();
        var profile = ModelProfile.Capture(settings);
        profile.Parameters[nameof(AppSettings.AdvancedJson)] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new { extra = key }));
        settings.Models[profile.Id] = profile;
        var error = Assert.Throws<ArgumentException>(() => ConfigurationStore.ValidateSecrets(settings, [key]));
        Assert.Contains("模型配置 1 / 自定义请求参数", error.Message); Assert.DoesNotContain(key, error.Message);
    }
}
