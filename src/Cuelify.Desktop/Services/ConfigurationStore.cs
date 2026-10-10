using System.Text.Json;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.Services;

public sealed class ConfigurationStore(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify");
    public string DefaultModelPath(EmbeddedModel model) => Path.Combine(Path.GetFullPath(Root), model.DefaultFileName);
    public string DefaultLlamaServerPath => Path.Combine(Path.GetFullPath(Root), "llama.cpp", "llama-server.exe");
    public AppSettings CreateDefaults() => new() { LocalModelId = EmbeddedModelCatalog.Default.Id, ModelPath = DefaultModelPath(EmbeddedModelCatalog.Default), LlamaServerPath = DefaultLlamaServerPath };
    public string LastLoadNotice { get; private set; } = "";
    public async Task<AppSettings?> LoadAsync(CancellationToken token = default)
    {
        LastLoadNotice = "";
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, token);
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out var value) ? value.GetInt32() : 0;
        if (version < AppSettings.CurrentSchemaVersion)
        {
            var backup = Path.Combine(Root, $"settings.previous-{Guid.NewGuid():N}.json");
            File.Copy(path, backup);
            var defaults = CreateDefaults();
            await SaveAsync(defaults, [], token);
            LastLoadNotice = "旧版设置已备份并重建，请重新配置模型和服务。";
            return defaults;
        }
        if (version != AppSettings.CurrentSchemaVersion) throw new InvalidDataException("设置文件版本高于当前应用，请使用对应版本的应用。");
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("设置文件内容无效。");
        Validate(settings);
        return settings;
    }
    public Task SaveAsync(AppSettings settings, IEnumerable<string> secrets, CancellationToken token = default)
    {
        Validate(settings);
        settings.ModelFiles[settings.LocalOptions().ModelId] = new(settings.ModelPath, settings.ModelSha256);
        ValidateSecrets(settings, secrets);
        return AtomicFile.WriteJsonAsync(Path.Combine(Root, "settings.json"), settings, token);
    }
    public static void ValidateSecrets(AppSettings settings, IEnumerable<string> secrets)
    {
        var json = JsonSerializer.Serialize(settings);
        if (secrets.Where(key => !string.IsNullOrWhiteSpace(key)).Any(key => json.Contains(key, StringComparison.Ordinal) || json.Contains(JsonSerializer.Serialize(key).Trim('"'), StringComparison.Ordinal)))
            throw new ArgumentException("设置或提示词中包含 API 密钥，无法保存。请移除其中的密钥，并将密钥填写在专用输入框中。");
    }
    public static AppSettings Snapshot(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    public static void Validate(AppSettings settings, bool validateAllProfiles = true)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion || settings.PromptLibrary is null || settings.ModelPromptBindings is null || settings.ModelFiles is null)
            throw new ArgumentException("设置文件版本或提示词库无效。");
        var source = SpeechLanguages.Resolve(settings.SourceLanguage);
        settings.SourceCode = SpeechLanguages.AsrCode(source, settings.SourceCode);
        settings.SourceLanguage = source?.Name ?? "自动识别";
        if (settings.ZeroDurationToleranceMs is < 1 or > 7000) throw new ArgumentException("零时长字幕的合并间隔应为 1～7000 毫秒。");
        if (!Enum.IsDefined(settings.Provider) || settings.Theme is not ("系统" or "浅色" or "深色")) throw new ArgumentException("提供商或主题无效。");
        settings.TranslationSettings().Validate();
        if (validateAllProfiles)
        {
            foreach (var (id, profile) in settings.PromptLibrary)
            {
                if (profile is null || id != profile.Id || PromptPresets.IsBuiltIn(id)) throw new ArgumentException("自定义模板 ID 无效或与内置模板冲突。");
                ValidatePrompt(profile);
            }
            if (settings.AllPrompts.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException("提示词模板名称重复。");
            foreach (var (key, binding) in settings.ModelPromptBindings)
            {
                if (string.IsNullOrWhiteSpace(key) || binding is null || string.IsNullOrWhiteSpace(binding.ModelName)) throw new ArgumentException("模型提示词关联无效。");
                _ = settings.FindPrompt(binding.PromptId);
            }
        }
        ValidatePrompt(settings.GetProfile());
        // 高级参数可能包含凭据，即使选择本地也必须在写盘前验证。
        _ = settings.CloudOptions().AdditionalParameters();
        settings.LocalOptions().Validate();
        if (settings.Provider != TranslationProvider.Local)
            _ = CloudRequestBuilder.Build(settings.CloudOptions(), [new("user", "配置校验")], settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
    }

    private static void ValidatePrompt(PromptProfile profile)
    {
        try { PromptBuilder.Validate(profile); }
        catch (ArgumentException exception) { throw new ArgumentException($"提示词“{profile.Name}”：{exception.Message}", exception); }
    }
}
