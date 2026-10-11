using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public enum SegmentationMethod { Program, Model }

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

public sealed record SubtitlePipelineProfile(string Name, SegmentationMethod Method)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Description { get; init; } = "";
    public Dictionary<Cuelify.Core.Projects.TaskStage, string> DefaultModels { get; init; } = [];
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
    [JsonIgnore] public object SegmentationIdentity => new { Method, Segmentation = Method == SegmentationMethod.Model ? Segmentation : null,
        BlockSeconds = Method == SegmentationMethod.Model ? BlockSeconds : 0, ContextCharacters = Method == SegmentationMethod.Model ? ContextCharacters : 0,
        MaximumCueSeconds, MinimumCueSeconds };
    [JsonIgnore] public PromptProfile EffectiveTranslation => Translation with { Parameters = TranslationParameters,
        MaximumTextCharacters = LineCharacters * 2, ReadingCharactersPerSecond = ReadingCharactersPerSecond };
    [JsonIgnore] public object TranslationIdentity => new { Translation = EffectiveTranslation.ExecutionIdentity,
        BeforeCues, AfterCues, MaximumContextCharacters };
    public override string ToString() => Name;

    public void Validate() { ValidateSegmentation(); ValidateTranslation(); }
    private void ValidateMetadata()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Id) || Description is null || !Enum.IsDefined(Method))
            throw new ArgumentException("字幕管线名称或分句方式无效。");
    }
    public void ValidateSegmentation()
    {
        ValidateMetadata();
        if (!double.IsFinite(MaximumCueSeconds) || MaximumCueSeconds is < 1 or > 15 ||
            !double.IsFinite(MinimumCueSeconds) || MinimumCueSeconds < 0 || MinimumCueSeconds > MaximumCueSeconds)
            throw new ArgumentException("分句配置无效，请检查分块、上下文及字幕时长。");
        if (Method == SegmentationMethod.Program) return;
        if (Segmentation is null || BlockSeconds is < 5 or > 180 || ContextCharacters is < 0 or > 5000)
            throw new ArgumentException("模型分句的分块或上下文配置无效。");
        Segmentation.Parameters.Validate();
        if (Segmentation.SystemTemplate is null || string.IsNullOrWhiteSpace(Segmentation.UserTemplate))
            throw new ArgumentException("请填写分句阶段的用户提示词。");
        foreach (Match match in Regex.Matches(Segmentation.SystemTemplate + "\n" + Segmentation.UserTemplate, @"\{([a-z_]+)\}"))
            if (!PipelinePromptBuilder.Variables.Contains(match.Groups[1].Value)) throw new ArgumentException($"分句阶段不支持变量：{match.Groups[1].Value}");
    }
    public void ValidateTranslation()
    {
        ValidateMetadata();
        if (Translation is null || TranslationParameters is null || BeforeCues is < 0 or > 8 || AfterCues is < 0 or > 8 ||
            MaximumContextCharacters is < 0 or > 20000 || LineCharacters is < 5 or > 80 ||
            !double.IsFinite(ReadingCharactersPerSecond) || ReadingCharactersPerSecond is < 1 or > 40)
            throw new ArgumentException("翻译配置无效，请检查批次、上下文及显示长度。");
        PromptBuilder.Validate(EffectiveTranslation);
    }

}

public static class SubtitlePipelinePresets
{
    public static readonly SubtitlePipelineProfile Direct = new("程序分句后翻译", SegmentationMethod.Program)
    { Id = "pipeline-direct", Description = "程序分句，再独立翻译。", Translation = PromptPresets.SingleSimple };
    public static readonly SubtitlePipelineProfile TwoStage = new("模型分句后翻译", SegmentationMethod.Model)
    { Id = "pipeline-two-stage", Description = "模型选择自然分句，再独立翻译。", Translation = PromptPresets.BatchSubtitles with { BatchSize = 25 },
        TranslationParameters = new() { MaximumTokens = 2048 } };
    public static IReadOnlyList<SubtitlePipelineProfile> All { get; } = [Direct, TwoStage];
    public static bool IsBuiltIn(string id) => All.Any(profile => profile.Id == id);
}

public sealed record SourceWordRange(int First, int Last);
public sealed record SubtitlePipelineResult(IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<SourceWordRange> Ranges, int EngineCalls, int CacheHits)
{
    public void ValidateSource(int wordCount)
    {
        if (Cues.Count != Ranges.Count || Cues.Select(c => c.Id).Distinct().Count() != Cues.Count ||
            Cues.Any(c => string.IsNullOrWhiteSpace(c.SourceText) || c.Start < TimeSpan.Zero || c.End <= c.Start))
            throw new InvalidDataException("分句结果的字幕与来源范围不一致。");
        var next = 0;
        foreach (var range in Ranges)
        {
            if (range.First != next || range.Last < range.First || range.Last >= wordCount)
                throw new InvalidDataException("分句来源必须连续覆盖原始识别词。");
            next = range.Last + 1;
        }
        if (next != wordCount) throw new InvalidDataException("分句来源未完整覆盖原始识别词。");
    }
}
