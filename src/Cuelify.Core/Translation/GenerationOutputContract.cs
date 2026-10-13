using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public enum GenerationOutputKind { Segmentation, Translation }
// 0 为已停用的旧格式；保留 texts 的序列化编号和缓存身份。
public enum SegmentationOutputFormat { SourceTexts = 1 }

// 描述业务输出，与提供商及 decoder 的约束语法无关。
public sealed record GenerationOutputContract
{
    public GenerationOutputKind Kind { get; init; }
    public int FirstWord { get; init; }
    public int LastWord { get; init; }
    public Dictionary<int, int[]> AllowedEnds { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SegmentationOutputFormat SegmentationFormat { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SourceWords { get; init; }
    public TranslationOutputFormat Format { get; init; }
    public string[] CueIds { get; init; } = [];
    public int? MaximumTextCharacters { get; init; }

    public IReadOnlyList<SourceWordRange> ParseRanges(string response)
    {
        if (Kind != GenerationOutputKind.Segmentation) throw new InvalidOperationException("输出契约不是分句。");
        try
        {
            using var json = JsonDocument.Parse(response);
            const string property = "texts";
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 ||
                !json.RootElement.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"分句应只返回 {property} 数组。");
            var next = FirstWord;
            var ranges = new List<SourceWordRange>();
            foreach (var row in rows.EnumerateArray())
            {
                var end = FindTextEnd(row.GetString(), next);
                if (!AllowedEnds.TryGetValue(next, out var allowed) || !allowed.Contains(end))
                    throw new InvalidDataException($"原文片段「{row.GetString()}」超出最长字幕时间，请拆开这条原文；时间跨度包括停顿。");
                ranges.Add(new(next, end)); next = end + 1;
            }
            if (next != LastWord + 1) throw new InvalidDataException($"分句未覆盖全部词，最终末词应为 {LastWord}。请完整输出。");
            return ranges;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("分句输出结构无效，请返回完整 JSON。"); }
    }

    public string SourceText(int first, int last) => SourceWords is { } words && first >= FirstWord && last <= LastWord && last >= first
        ? string.Concat(words.Skip(first - FirstWord).Take(last - first + 1))
        : throw new InvalidDataException("分句契约缺少原始词引用。");

    private int FindTextEnd(string? text, int first)
    {
        if (string.IsNullOrEmpty(text) || SourceWords is not { } words || first > LastWord)
            throw new InvalidDataException("字幕原文为空或超出原始词范围，请逐字复制本块原文。");
        var combined = new StringBuilder();
        for (var last = first; last <= LastWord; last++)
        {
            combined.Append(words[last - FirstWord]);
            if (combined.Length == text.Length && combined.ToString().Equals(text, StringComparison.Ordinal)) return last;
            if (combined.Length >= text.Length) break;
        }
        throw new InvalidDataException("当前起点的字幕原文与连续原始词不一致，不能增删文字、标点或在 token 内部断开。");
    }

    public bool IsValidText(string? text) => text is not null && AlignmentValidator.IsValid(text) &&
        (MaximumTextCharacters is not { } maximum || PipelineDisplay.CharacterCount(text) <= maximum);

    public AlignmentResult Align(string response)
    {
        if (Kind != GenerationOutputKind.Translation) throw new InvalidOperationException("输出契约不是翻译。");
        var aligned = AlignmentValidator.Parse(response, CueIds, Format);
        var valid = aligned.Translations.Where(pair => MaximumTextCharacters is not { } maximum || PipelineDisplay.CharacterCount(pair.Value) <= maximum)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return new(valid, CueIds.Where(id => !valid.ContainsKey(id)).ToArray());
    }
}
