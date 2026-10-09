using System.Globalization;
using System.Text.Json.Serialization;
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
    [ObservableProperty] private int batchSize = 12;
    [ObservableProperty] private int translationConcurrency = 1;
    [ObservableProperty] private int contextCues = 5;
    [ObservableProperty] private int followingContextCues = 2;
    [ObservableProperty] private int maximumAttempts = 3;
    [ObservableProperty] private int maximumBatchCharacters = 6000;
    [ObservableProperty] private int maximumContextCharacters = 3000;
    [ObservableProperty] private string modelPath = "";
    [ObservableProperty] private string embeddedModelId = "";
    [ObservableProperty] private string modelSha256 = "";
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
    [ObservableProperty] private PromptProfile compatibleProfile = PromptPresets.Cloud;
    [ObservableProperty] private PromptProfile deepSeekProfile = PromptPresets.Cloud;
    // 仅用于读取旧版的共用模板；保存时迁移到各模型的模板。
    [ObservableProperty]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private PromptProfile? localProfile;
    public Dictionary<string, PromptProfile> EmbeddedModelProfiles { get; set; } = new(StringComparer.Ordinal);

    public PromptProfile GetLocalProfile() => EmbeddedModelProfiles.GetValueOrDefault(LocalOptions().ModelId) ?? LocalProfile ?? LocalOptions().Model.DefaultProfile;
    public void MigrateLocalProfiles()
    {
        EmbeddedModelProfiles ??= new(StringComparer.Ordinal);
        var model = LocalOptions().Model;
        if (LocalProfile is { } previous && !EmbeddedModelProfiles.ContainsKey(model.Id))
            EmbeddedModelProfiles[model.Id] = previous == PromptPresets.LegacyLocal || previous == PromptPresets.Local
                ? model.DefaultProfile : previous;
        LocalProfile = null;
    }
    public PromptProfile GetProfile() => Provider switch { TranslationProvider.Local => GetLocalProfile(), TranslationProvider.DeepSeek => DeepSeekProfile, _ => CompatibleProfile };
    public void SetProfile(PromptProfile profile)
    {
        if (Provider == TranslationProvider.Local) EmbeddedModelProfiles[LocalOptions().ModelId] = profile;
        else if (Provider == TranslationProvider.DeepSeek) DeepSeekProfile = profile;
        else CompatibleProfile = profile;
    }
    public TranslationSettings TranslationSettings() => new()
    {
        SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage, TargetStyle = TargetStyle,
        BatchSize = Provider == TranslationProvider.Local ? 1 : BatchSize,
        Concurrency = Provider == TranslationProvider.Local ? 1 : TranslationConcurrency,
        ContextCues = ContextCues, FollowingContextCues = FollowingContextCues, MaximumAttempts = MaximumAttempts,
        MaximumBatchCharacters = MaximumBatchCharacters, MaximumContextCharacters = MaximumContextCharacters
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
        ModelId = string.IsNullOrWhiteSpace(EmbeddedModelId) ? EmbeddedModelCatalog.Default.Id : EmbeddedModelId,
        ExpectedSha256 = ModelSha256,
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
