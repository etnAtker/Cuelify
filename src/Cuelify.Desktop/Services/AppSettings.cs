using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.Services;

public enum TranslationProvider { Compatible, DeepSeek, Local }

// 此对象仅包含可持久化的非敏感设置；Key 单独通过主密码加密保存。
public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private string ffmpegPath = "ffmpeg.exe";
    [ObservableProperty] private string ffprobePath = "ffprobe.exe";
    [ObservableProperty] private string vadPath = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
    [ObservableProperty] private string asrModel = "scribe_v2";
    [ObservableProperty] private string sourceCode = "";
    [ObservableProperty] private string sourceLanguage = "自动识别";
    [ObservableProperty] private string targetLanguage = "中文";
    [ObservableProperty] private string targetStyle = "";
    [ObservableProperty] private TranslationProvider provider = TranslationProvider.DeepSeek;
    [ObservableProperty] private string baseUrl = "https://api.deepseek.com";
    [ObservableProperty] private string cloudModel = "deepseek-flash";
    [ObservableProperty] private string temperature = "";
    [ObservableProperty] private string topP = "";
    [ObservableProperty] private string maximumTokens = "2048";
    [ObservableProperty] private string frequencyPenalty = "";
    [ObservableProperty] private string presencePenalty = "";
    [ObservableProperty] private ReasoningCapability reasoningCapability;
    [ObservableProperty] private string reasoningEffort = "";
    [ObservableProperty] private string advancedJson = "";
    [ObservableProperty] private bool thinkingEnabled;
    [ObservableProperty] private string thinkingEffort = "low";
    [ObservableProperty] private int timeoutSeconds = 120;
    [ObservableProperty] private int translationConcurrency = 4;
    [ObservableProperty] private int contextCues = 5;
    [ObservableProperty] private int followingContextCues = 2;
    [ObservableProperty] private int maximumAttempts = 3;
    [ObservableProperty] private int maximumContextCharacters = 3000;
    [ObservableProperty] private string modelPath = "";
    [ObservableProperty] private string localModelId = "";
    [ObservableProperty] private string modelSha256 = "";
    [ObservableProperty] private string llamaServerPath = "";
    [ObservableProperty] private string llamaServerVersion = "";
    [ObservableProperty] private int localConcurrency = 2;
    public Dictionary<string, EmbeddedModelFile> ModelFiles { get; set; } = new(StringComparer.Ordinal);
    [ObservableProperty] private int gpuLayers = 99;
    [ObservableProperty] private int contextSize = 4096;
    [ObservableProperty] private int localMaximumTokens = 256;
    [ObservableProperty] private int chunkTargetSeconds = 180;
    [ObservableProperty] private int chunkMaximumSeconds = 300;
    [ObservableProperty] private int chunkSearchSeconds = 30;
    [ObservableProperty] private bool alwaysChunk;
    [ObservableProperty] private int asrConcurrency = 2;
    [ObservableProperty] private double vadThreshold = .5;
    [ObservableProperty] private int minimumSpeechMs = 250;
    [ObservableProperty] private int minimumSilenceMs = 400;
    [ObservableProperty] private int overlapMs = 250;
    [ObservableProperty] private int zeroDurationToleranceMs = 500;
    [ObservableProperty] private long maximumUploadBytes = 2_900_000_000;
    [ObservableProperty] private string theme = "系统";
    [ObservableProperty] private bool reduceMotion = true;
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Dictionary<string, PromptProfile> PromptLibrary { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ModelPromptBinding> ModelPromptBindings { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PromptProfile> AllPrompts => PromptPresets.All.Concat(PromptLibrary.Values);

    public string ModelKey(TranslationProvider? provider = null)
    {
        var selected = provider ?? Provider;
        return selected == TranslationProvider.Local ? "local:" + LocalOptions().ModelId :
            "cloud:" + Infrastructure.Storage.AtomicFile.Hash(new { Provider = selected,
                Endpoint = new CloudTranslationOptions { BaseUrl = BaseUrl }.Endpoint().AbsoluteUri, Model = CloudModel.Trim() });
    }
    public string ModelName(TranslationProvider? provider = null) => (provider ?? Provider) == TranslationProvider.Local
        ? LocalOptions().Model.Name : $"{((provider ?? Provider) == TranslationProvider.DeepSeek ? "DeepSeek" : "OpenAI 兼容服务")} · {CloudModel.Trim()} · {BaseUrl.Trim()}";
    public PromptProfile FindPrompt(string id) => PromptPresets.All.FirstOrDefault(profile => profile.Id == id)
        ?? PromptLibrary.GetValueOrDefault(id) ?? throw new ArgumentException("关联的提示词不存在，请重新选择提示词。");
    public PromptProfile GetProfile(TranslationProvider? provider = null)
    {
        var selected = provider ?? Provider;
        var fallback = selected == TranslationProvider.Local ? FindPrompt(LocalOptions().Model.DefaultPromptId) : PromptPresets.BatchSubtitles;
        return ModelPromptBindings.TryGetValue(ModelKey(selected), out var binding) ? FindPrompt(binding.PromptId) : fallback;
    }
    public void AssociatePrompt(string id)
    {
        _ = FindPrompt(id);
        ModelPromptBindings[ModelKey()] = new(ModelName(), id);
        OnPropertyChanged(nameof(ModelPromptBindings));
    }
    public void SavePrompt(PromptProfile profile)
    {
        PromptBuilder.Validate(profile);
        if (PromptPresets.IsBuiltIn(profile.Id)) throw new ArgumentException("内置模板不可修改，请先创建副本。");
        if (AllPrompts.Any(other => other.Id != profile.Id && other.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("模板名称已存在，请使用其他名称。");
        PromptLibrary[profile.Id] = profile;
        OnPropertyChanged(nameof(PromptLibrary));
    }
    public void DeletePrompt(string id)
    {
        if (!PromptLibrary.ContainsKey(id)) throw new ArgumentException("只能删除自定义模板。");
        var references = ModelPromptBindings.Where(item => item.Value.PromptId == id).ToArray();
        // 移除显式关联，各模型按自己的默认模板回退。
        foreach (var item in references) ModelPromptBindings.Remove(item.Key);
        PromptLibrary.Remove(id);
        OnPropertyChanged(nameof(PromptLibrary)); OnPropertyChanged(nameof(ModelPromptBindings));
    }
    public TranslationSettings TranslationSettings() => new()
    {
        SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage, TargetStyle = TargetStyle,
        BatchSize = GetProfile().BatchTranslation ? GetProfile().BatchSize : 1,
        Concurrency = Provider == TranslationProvider.Local ? LocalConcurrency : TranslationConcurrency,
        ContextCues = ContextCues, FollowingContextCues = FollowingContextCues, MaximumAttempts = MaximumAttempts,
        MaximumBatchCharacters = GetProfile().BatchTranslation ? GetProfile().MaximumBatchCharacters : 50000, MaximumContextCharacters = MaximumContextCharacters
    };
    public CloudTranslationOptions CloudOptions() => new()
    {
        BaseUrl = BaseUrl, Model = CloudModel, Temperature = Number(Temperature, "温度"), TopP = Number(TopP, "采样范围"),
        MaximumTokens = OptionalInteger(MaximumTokens, "最大输出长度"),
        FrequencyPenalty = Number(FrequencyPenalty, "重复惩罚"), PresencePenalty = Number(PresencePenalty, "新词倾向"),
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds), ReasoningCapability = ReasoningCapability,
        ReasoningEffort = string.IsNullOrWhiteSpace(ReasoningEffort) ? null : ReasoningEffort,
        AdditionalParametersJson = AdvancedJson
    };
    public DeepSeekThinkingOptions ThinkingOptions() => new(ThinkingEnabled, ThinkingEnabled ? ThinkingEffort : null);
    public EmbeddedModelOptions LocalOptions() => new()
    {
        ModelId = string.IsNullOrWhiteSpace(LocalModelId) ? EmbeddedModelCatalog.Default.Id : LocalModelId,
        ServerPath = LlamaServerPath, Concurrency = LocalConcurrency,
        ModelPath = ModelPath, GpuLayers = GpuLayers, ContextSize = checked((uint)ContextSize),
        MaximumTokens = LocalMaximumTokens, InferenceTimeout = TimeSpan.FromSeconds(TimeoutSeconds)
    };
    private static double? Number(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!double.TryParse(value, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new ArgumentException($"{label}数值格式无效，请输入数字，小数使用英文句点。留空使用服务默认值。");
        return number;
    }
    private static int? OptionalInteger(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException($"{label}数值格式无效，请输入整数，或留空使用服务默认值。");
        return number;
    }
}

public sealed record ModelPromptBinding(string ModelName, string PromptId);
