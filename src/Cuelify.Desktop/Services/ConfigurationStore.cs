using System.Text.Json;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation;

namespace Cuelify.Desktop.Services;

public sealed class ConfigurationStore(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify");
    public Task<AppSettings?> LoadAsync(CancellationToken token = default) => AtomicFile.ReadJsonAsync<AppSettings>(Path.Combine(Root, "settings.json"), token);
    public Task SaveAsync(AppSettings settings, IEnumerable<string> secrets, CancellationToken token = default)
    {
        Validate(settings);
        var json = JsonSerializer.Serialize(settings);
        if (secrets.Where(key => !string.IsNullOrWhiteSpace(key)).Any(key => json.Contains(key, StringComparison.Ordinal) || json.Contains(JsonSerializer.Serialize(key).Trim('"'), StringComparison.Ordinal)))
            throw new ArgumentException("设置或提示词中包含 API 密钥，无法保存。请移除其中的密钥，并将密钥填写在专用输入框中。");
        return AtomicFile.WriteJsonAsync(Path.Combine(Root, "settings.json"), settings, token);
    }
    public static AppSettings Snapshot(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    public static void Validate(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Provider) || settings.Theme is not ("系统" or "浅色" or "深色")) throw new ArgumentException("提供商或主题无效。");
        settings.TranslationSettings().Validate();
        PromptBuilder.Validate(settings.CompatibleProfile);
        PromptBuilder.Validate(settings.DeepSeekProfile);
        PromptBuilder.Validate(settings.LocalProfile);
        if (settings.CompatibleProfile.OutputFormat != TranslationOutputFormat.CueIdJson || settings.DeepSeekProfile.OutputFormat != TranslationOutputFormat.CueIdJson || settings.LocalProfile.OutputFormat != TranslationOutputFormat.PlainText)
            throw new ArgumentException("提示词输出协议不匹配。");
        // 高级参数可能包含凭据，即使选择本地也必须在写盘前验证。
        _ = settings.CloudOptions().AdditionalParameters();
        if (settings.Provider == TranslationProvider.Local) settings.LocalOptions().Validate();
        if (settings.Provider != TranslationProvider.Local)
            _ = CloudRequestBuilder.Build(settings.CloudOptions(), [new("user", "配置校验")], settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
    }
}
