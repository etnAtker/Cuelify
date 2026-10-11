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
    [ObservableProperty] private int maximumAttempts = 3;
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
    public const int CurrentSchemaVersion = 3;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Dictionary<string, SubtitlePipelineProfile> PipelineLibrary { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ModelPipelineBinding> ModelPipelineBindings { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<SubtitlePipelineProfile> AllPipelines => SubtitlePipelinePresets.All.Concat(PipelineLibrary.Values);
    public string? TaskPipelineId { get; set; }

    public string ModelKey(TranslationProvider? provider = null)
    {
        var selected = provider ?? Provider;
        return selected == TranslationProvider.Local ? "local:" + LocalOptions().ModelId :
            "cloud:" + Infrastructure.Storage.AtomicFile.Hash(new { Provider = selected,
                Endpoint = new CloudTranslationOptions { BaseUrl = BaseUrl }.Endpoint().AbsoluteUri, Model = CloudModel.Trim() });
    }
    public string ModelName(TranslationProvider? provider = null) => (provider ?? Provider) == TranslationProvider.Local
        ? LocalOptions().Model.Name : $"{((provider ?? Provider) == TranslationProvider.DeepSeek ? "DeepSeek" : "OpenAI 兼容服务")} · {CloudModel.Trim()} · {BaseUrl.Trim()}";
    public SubtitlePipelineProfile FindPipeline(string id) => AllPipelines.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("关联的字幕管线不存在，请重新选择。");
    public SubtitlePipelineProfile GetPipeline(TranslationProvider? provider = null)
    {
        if (TaskPipelineId is { } task) return FindPipeline(task);
        var selected = provider ?? Provider;
        var fallback = selected == TranslationProvider.Local ? LocalOptions().Model.DefaultPipelineId : SubtitlePipelinePresets.Joint.Id;
        return FindPipeline(ModelPipelineBindings.TryGetValue(ModelKey(selected), out var binding) ? binding.PipelineId : fallback);
    }
    public PromptProfile GetProfile(TranslationProvider? provider = null)
    {
        var pipeline = GetPipeline(provider);
        return pipeline.Translation with { Parameters = pipeline.TranslationParameters, MaximumTextCharacters = pipeline.LineCharacters * 2,
            ReadingCharactersPerSecond = pipeline.ReadingCharactersPerSecond };
    }
    public void AssociatePipeline(string id)
    {
        _ = FindPipeline(id);
        ModelPipelineBindings[ModelKey()] = new(ModelName(), id);
        OnPropertyChanged(nameof(ModelPipelineBindings));
    }
    public void SavePipeline(SubtitlePipelineProfile profile)
    {
        profile.Validate();
        if (SubtitlePipelinePresets.IsBuiltIn(profile.Id)) throw new ArgumentException("内置管线不可修改，请先创建副本。");
        if (AllPipelines.Any(other => other.Id != profile.Id && other.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("管线名称已存在，请使用其他名称。");
        PipelineLibrary[profile.Id] = profile;
        OnPropertyChanged(nameof(PipelineLibrary));
    }
    public void DeletePipeline(string id)
    {
        if (!PipelineLibrary.ContainsKey(id)) throw new ArgumentException("只能删除自定义管线。");
        foreach (var item in ModelPipelineBindings.Where(item => item.Value.PipelineId == id).ToArray()) ModelPipelineBindings.Remove(item.Key);
        PipelineLibrary.Remove(id);
        OnPropertyChanged(nameof(PipelineLibrary)); OnPropertyChanged(nameof(ModelPipelineBindings));
    }
    public TranslationSettings TranslationSettings() => new()
    {
        SourceLanguage = SourceLanguage, TargetLanguage = TargetLanguage, TargetStyle = TargetStyle,
        BatchSize = GetProfile().BatchTranslation ? GetProfile().BatchSize : 1,
        Concurrency = Provider == TranslationProvider.Local ? LocalConcurrency : TranslationConcurrency,
        ContextCues = GetPipeline().BeforeCues, FollowingContextCues = GetPipeline().AfterCues, MaximumAttempts = MaximumAttempts,
        MaximumBatchCharacters = GetProfile().BatchTranslation ? GetProfile().MaximumBatchCharacters : 50000, MaximumContextCharacters = GetPipeline().MaximumContextCharacters
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

public sealed record ModelPipelineBinding(string ModelName, string PipelineId);
