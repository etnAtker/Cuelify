using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public enum SubtitlePipelineMode { DirectTranslation, JointSegmentationTranslation, SegmentThenTranslate }

public sealed record InferenceParameters
{
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public double? RepeatPenalty { get; init; }
    public int? MaximumTokens { get; init; }
    public void Validate()
    {
        if (Temperature is { } t && (!double.IsFinite(t) || t is < 0 or > 2) ||
            TopP is { } p && (!double.IsFinite(p) || p is <= 0 or > 1) || TopK is <= 0 ||
            RepeatPenalty is { } r && (!double.IsFinite(r) || r is <= 0 or > 2) || MaximumTokens is <= 0)
            throw new ArgumentException("阶段生成参数无效，请检查温度、采样范围和输出长度。");
    }
}

public sealed record PipelineStage(string SystemTemplate, string UserTemplate)
{
    public InferenceParameters Parameters { get; init; } = new();
}

public sealed record SubtitlePipelineProfile(string Name, SubtitlePipelineMode Mode)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Description { get; init; } = "";
    public PipelineStage Segmentation { get; init; } = new("", "请按完整语义选择字幕边界，合并犹豫和未完碎片，保留独立回应。");
    public PromptProfile Translation { get; init; } = PromptPresets.BatchSubtitles;
    public InferenceParameters TranslationParameters { get; init; } = new();
    public int BlockSeconds { get; init; } = 24;
    public int ContextCharacters { get; init; } = 200;
    public int BeforeCues { get; init; } = 5;
    public int AfterCues { get; init; } = 2;
    public int MaximumContextCharacters { get; init; } = 3000;
    public double MaximumCueSeconds { get; init; } = 7;
    public double MinimumCueSeconds { get; init; } = .7;
    public int LineCharacters { get; init; } = 22;
    public double ReadingCharactersPerSecond { get; init; } = 12;
    [JsonIgnore] public object ExecutionIdentity => new { Mode, Segmentation, Translation = Translation.ExecutionIdentity,
        TranslationParameters, BlockSeconds, ContextCharacters, BeforeCues, AfterCues, MaximumContextCharacters,
        MaximumCueSeconds, MinimumCueSeconds, LineCharacters, ReadingCharactersPerSecond };
    public override string ToString() => Name;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Id) || Description is null || !Enum.IsDefined(Mode) ||
            Segmentation is null || Translation is null || TranslationParameters is null ||
            BlockSeconds is < 5 or > 180 || ContextCharacters is < 0 or > 5000 || BeforeCues is < 0 or > 8 || AfterCues is < 0 or > 8 ||
            MaximumContextCharacters is < 0 or > 20000 || !double.IsFinite(MaximumCueSeconds) || MaximumCueSeconds is < 1 or > 15 ||
            !double.IsFinite(MinimumCueSeconds) || MinimumCueSeconds < 0 || MinimumCueSeconds > MaximumCueSeconds ||
            LineCharacters is < 5 or > 80 || !double.IsFinite(ReadingCharactersPerSecond) || ReadingCharactersPerSecond is < 1 or > 40)
            throw new ArgumentException("字幕管线配置无效，请检查分块、上下文及字幕显示设置。");
        PromptBuilder.Validate(Translation);
        TranslationParameters.Validate();
        Segmentation.Parameters.Validate();
        if (Segmentation.SystemTemplate is null || string.IsNullOrWhiteSpace(Segmentation.UserTemplate))
            throw new ArgumentException("请填写分句阶段的用户提示词。");
        foreach (Match match in Regex.Matches(Segmentation.SystemTemplate + "\n" + Segmentation.UserTemplate, @"\{([a-z_]+)\}"))
            if (!PipelinePromptBuilder.Variables.Contains(match.Groups[1].Value)) throw new ArgumentException($"分句阶段不支持变量：{match.Groups[1].Value}");
    }
}

public static class SubtitlePipelinePresets
{
    public static readonly SubtitlePipelineProfile Direct = new("直接翻译", SubtitlePipelineMode.DirectTranslation)
    { Id = "pipeline-direct", Description = "保持识别字幕的边界，逐条翻译。", Translation = PromptPresets.SingleSimple };
    public static readonly SubtitlePipelineProfile Joint = new("联合分句与翻译", SubtitlePipelineMode.JointSegmentationTranslation)
    { Id = "pipeline-joint", Description = "同时选择自然分句并生成译文。", BlockSeconds = 60,
        Segmentation = new("", "将 {source_language} 对话整理为 {target_language} 字幕。结合译文长度选择完整语义的切点，填充词可在译文中精简，保留事实、否定和独立回应。结合连贯原文理解词表，不把专名猜成职业或普通词，专名可保留音译；信息不足不编造。风格：{target_style}。") { Parameters = new() { MaximumTokens = 2048 } },
        TranslationParameters = new() };
    public static readonly SubtitlePipelineProfile TwoStage = new("先分句后翻译", SubtitlePipelineMode.SegmentThenTranslate)
    { Id = "pipeline-two-stage", Description = "先选择自然分句，再批量翻译。", Translation = PromptPresets.BatchSubtitles with { BatchSize = 25 },
        TranslationParameters = new() { MaximumTokens = 2048 } };
    public static IReadOnlyList<SubtitlePipelineProfile> All { get; } = [Direct, Joint, TwoStage];
    public static bool IsBuiltIn(string id) => All.Any(profile => profile.Id == id);
}

public sealed record SourceWordRange(int First, int Last);
public sealed record SubtitlePipelineResult(IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<SourceWordRange> Ranges, int EngineCalls, int CacheHits);
