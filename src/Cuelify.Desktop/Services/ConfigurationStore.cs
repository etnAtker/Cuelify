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
    public AppSettings CreateDefaults()
    {
        var settings = new AppSettings { LocalModelId = EmbeddedModelCatalog.Default.Id, ModelPath = DefaultModelPath(EmbeddedModelCatalog.Default), LlamaServerPath = DefaultLlamaServerPath };
        PopulateModels(settings); return settings;
    }
    public string LastLoadNotice { get; private set; } = "";
    public async Task<AppSettings?> LoadAsync(CancellationToken token = default)
    {
        LastLoadNotice = "";
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, token);
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out var value) ? value.GetInt32() : 0;
        if (version is 3 or 4)
        {
            File.Copy(path, Path.Combine(Root, $"settings.schema{version}-{Guid.NewGuid():N}.backup"));
            var node = System.Text.Json.Nodes.JsonNode.Parse(PipelineSettingsMigration.Upgrade(document.RootElement).GetRawText())!;
            node[nameof(AppSettings.SchemaVersion)] = AppSettings.CurrentSchemaVersion;
            var upgraded = node.Deserialize<AppSettings>() ?? throw new InvalidDataException("设置内容无效。");
            var key = upgraded.ModelKey();
            if (version == 3 && node["ModelPipelineBindings"]?[key]?["PipelineId"]?.GetValue<string>() is { } legacyPipeline)
                upgraded.DefaultPipelineId = legacyPipeline;
            if (upgraded.DefaultPipelineId == PipelineSettingsMigration.LegacyJointId) upgraded.DefaultPipelineId = SubtitlePipelinePresets.TwoStage.Id;
            PopulateModels(upgraded);
            await SaveAsync(upgraded, [], token);
            LastLoadNotice = "设置已升级，原联合管线已改为分句后翻译。";
            return upgraded;
        }
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
        foreach (var (id, pipeline) in settings.PipelineLibrary.ToArray())
            if (pipeline.Method == SegmentationMethod.Model) settings.PipelineLibrary[id] = pipeline with { SegmentationFormat = SegmentationOutputFormat.SourceTexts };
        PopulateModels(settings);
        Validate(settings);
        return settings;
    }
    public Task SaveAsync(AppSettings settings, IEnumerable<string> secrets, CancellationToken token = default)
    {
        PopulateModels(settings);
        Validate(settings);
        settings.ModelFiles[settings.LocalOptions().ModelId] = new(settings.ModelPath, settings.ModelSha256);
        ValidateSecrets(settings, secrets);
        return AtomicFile.WriteJsonAsync(Path.Combine(Root, "settings.json"), settings, token);
    }
    public void PopulateModels(AppSettings settings)
    {
        var selected = ModelProfile.Capture(settings); settings.Models[selected.Id] = selected;
        var deepSeek = ModelProfile.Capture(new AppSettings());
        settings.Models.TryAdd(deepSeek.Id, deepSeek);
        foreach (var model in EmbeddedModelCatalog.Presets)
        {
            var id = "local:" + model.Id;
            if (settings.Models.ContainsKey(id)) continue;
            var local = Snapshot(settings); local.Provider = TranslationProvider.Local; local.LocalModelId = model.Id;
            local.ModelPath = settings.ModelFiles.GetValueOrDefault(model.Id)?.Path ?? DefaultModelPath(model);
            settings.Models[id] = ModelProfile.Capture(local);
        }
        foreach (var pipeline in settings.AllPipelines)
        {
            if (!settings.PipelineModelDefaults.TryGetValue(pipeline.Id, out var defaults)) settings.PipelineModelDefaults[pipeline.Id] = defaults = [];
            defaults.TryAdd(Cuelify.Core.Projects.TaskStage.Subtitles, selected.Id);
            defaults.TryAdd(Cuelify.Core.Projects.TaskStage.Translation, selected.Id);
        }
    }
    public static void ValidateSecrets(AppSettings settings, IEnumerable<string> secrets)
    {
        var keys = secrets.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return;
        // 只检查用户可编辑的文本；字段名、数值、枚举和自动生成的版本/模型身份不是凭据输入。
        var fields = new Dictionary<string, string>
        {
            [nameof(AppSettings.FfmpegPath)] = "FFmpeg 路径", [nameof(AppSettings.FfprobePath)] = "ffprobe 路径",
            [nameof(AppSettings.VadPath)] = "VAD 路径", [nameof(AppSettings.AsrModel)] = "识别模型",
            [nameof(AppSettings.SourceLanguage)] = "源语言", [nameof(AppSettings.TargetLanguage)] = "目标语言",
            [nameof(AppSettings.TargetStyle)] = "翻译风格", [nameof(AppSettings.BaseUrl)] = "服务地址",
            [nameof(AppSettings.CloudModel)] = "翻译模型名称", [nameof(AppSettings.ModelPath)] = "本地模型路径",
            [nameof(AppSettings.LlamaServerPath)] = "运行程序路径", [nameof(AppSettings.AdvancedJson)] = "自定义请求参数"
        };
        foreach (var (name, label) in fields)
            CheckField(name, (string?)typeof(AppSettings).GetProperty(name)!.GetValue(settings), label);
        var index = 0;
        foreach (var file in settings.ModelFiles.Values) Check(file.Path, $"模型文件 {++index} / 路径");
        index = 0;
        foreach (var model in settings.Models.Values)
        {
            var label = $"模型配置 {++index}";
            Check(model.Name, label + " / 名称");
            foreach (var (name, value) in model.Parameters)
                if (fields.TryGetValue(name, out var field) && value.ValueKind == JsonValueKind.String)
                    CheckField(name, value.GetString(), label + " / " + field);
        }
        index = 0;
        foreach (var pipeline in settings.PipelineLibrary.Values)
        {
            var label = $"字幕管线 {++index}";
            Check(pipeline.Name, label + " / 名称"); Check(pipeline.Description, label + " / 说明");
            Check(pipeline.Segmentation.SystemTemplate, label + " / 分句系统提示词");
            Check(pipeline.Segmentation.UserTemplate, label + " / 分句用户提示词");
            Check(pipeline.Translation.Name, label + " / 翻译名称"); Check(pipeline.Translation.Description, label + " / 翻译说明");
            Check(pipeline.Translation.SystemTemplate, label + " / 翻译系统提示词");
            Check(pipeline.Translation.UserTemplate, label + " / 翻译用户提示词");
        }

        void CheckField(string name, string? value, string label)
        {
            if (name != nameof(AppSettings.AdvancedJson) || string.IsNullOrWhiteSpace(value)) { Check(value, label); return; }
            try
            {
                using var json = JsonDocument.Parse(value);
                CheckJson(json.RootElement, label);
            }
            catch (JsonException) { throw new ArgumentException($"{label}必须是合法 JSON 对象。"); }
        }
        void CheckJson(JsonElement value, string label)
        {
            // 解码后比较字符串，避免把 JSON 转义或参数字段名误当作密钥。
            if (value.ValueKind == JsonValueKind.String) Check(value.GetString(), label);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    // 自定义参数名称也是用户输入，不允许把长密钥用作参数名。
                    if (property.Name.Length >= 16) Check(property.Name, label + " / 字段名");
                    CheckJson(property.Value, label);
                }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) CheckJson(item, label);
        }
        void Check(string? value, string label)
        {
            // 短占位值可能与普通文本相同，仅比较完整字段；真实长密钥还检查误贴到文本中的情况。
            if (value is not null && keys.Any(key => value.Trim().Equals(key, StringComparison.Ordinal) ||
                key.Length >= 16 && value.Contains(key, StringComparison.Ordinal)))
                throw new ArgumentException($"{label}包含 API 密钥，无法保存。请移除该字段中的密钥；专用密钥输入框可正常使用。");
        }
    }
    public static AppSettings Snapshot(AppSettings settings)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(settings))!;
        node[nameof(AppSettings.SchemaVersion)] = AppSettings.CurrentSchemaVersion;
        return node.Deserialize<AppSettings>()!;
    }
    public static AppSettings? ReadSnapshot(JsonElement? json) => json is { } value
        ? PipelineSettingsMigration.Upgrade(value).Deserialize<AppSettings>() : null;
    public static void Validate(AppSettings settings, bool validateAllProfiles = true, Cuelify.Core.Projects.TaskStage? step = null)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion || settings.PipelineLibrary is null || settings.Models is null || settings.PipelineModelDefaults is null || settings.ModelFiles is null)
            throw new ArgumentException("设置文件版本或字幕管线库无效。");
        var source = SpeechLanguages.Resolve(settings.SourceLanguage);
        settings.SourceCode = SpeechLanguages.AsrCode(source, settings.SourceCode);
        settings.SourceLanguage = source?.Name ?? "自动识别";
        if (step is null or Cuelify.Core.Projects.TaskStage.Asr || step == Cuelify.Core.Projects.TaskStage.Subtitles && settings.GetPipeline().Method == SegmentationMethod.Program)
            if (settings.ZeroDurationToleranceMs is < 1 or > 7000) throw new ArgumentException("零时长字幕的合并间隔应为 1～7000 毫秒。");
        if (!Enum.IsDefined(settings.Provider) || step is null && settings.Theme is not ("系统" or "浅色" or "深色")) throw new ArgumentException("提供商或主题无效。");
        if (step == Cuelify.Core.Projects.TaskStage.Subtitles) settings.SegmentationSettings().Validate();
        else if (step is null or Cuelify.Core.Projects.TaskStage.Translation) settings.TranslationSettings().Validate();
        if (validateAllProfiles)
        {
            foreach (var (id, profile) in settings.PipelineLibrary)
            {
                if (profile is null || id != profile.Id || SubtitlePipelinePresets.IsBuiltIn(id)) throw new ArgumentException("自定义管线 ID 无效或与内置管线冲突。");
                ValidatePipeline(profile);
            }
            if (settings.AllPipelines.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException("字幕管线名称重复。");
            foreach (var (id, defaults) in settings.PipelineModelDefaults)
            {
                _ = settings.FindPipeline(id);
                if (defaults is null || defaults.Any(item => item.Key is not (Cuelify.Core.Projects.TaskStage.Subtitles or Cuelify.Core.Projects.TaskStage.Translation) || !settings.Models.ContainsKey(item.Value)))
                    throw new ArgumentException("管线步骤的默认模型无效，请重新选择。");
            }
        }
        ValidatePipeline(settings.GetPipeline(), step);
        // 写盘时检查高级 JSON 的安全边界，不解析未使用的云端数值参数。
        if (step is null) _ = new CloudTranslationOptions { AdditionalParametersJson = settings.AdvancedJson,
            ReasoningCapability = settings.ReasoningCapability }.AdditionalParameters();
        if (settings.Provider == TranslationProvider.Local) settings.LocalOptions().Validate();
        if (settings.Provider != TranslationProvider.Local)
        {
            var profile = settings.GetPipeline();
            var stages = step == Cuelify.Core.Projects.TaskStage.Subtitles ? new[] { profile.Segmentation.Parameters } :
                profile.Method == SegmentationMethod.Program || step == Cuelify.Core.Projects.TaskStage.Translation
                    ? new[] { profile.TranslationParameters } : new[] { profile.Segmentation.Parameters, profile.TranslationParameters };
            foreach (var parameters in stages)
                _ = CloudRequestBuilder.Build(settings.CloudOptions(), new TranslationRequest([], [new("user", "配置校验")]) { Parameters = parameters },
                    settings.Provider == TranslationProvider.DeepSeek ? settings.ThinkingOptions() : null);
        }
    }

    private static void ValidatePipeline(SubtitlePipelineProfile profile, Cuelify.Core.Projects.TaskStage? step = null)
    {
        try { if (step == Cuelify.Core.Projects.TaskStage.Subtitles) profile.ValidateSegmentation();
            else if (step == Cuelify.Core.Projects.TaskStage.Translation) profile.ValidateTranslation(); else profile.Validate(); }
        catch (ArgumentException exception) { throw new ArgumentException($"管线“{profile.Name}”：{exception.Message}", exception); }
    }
}
