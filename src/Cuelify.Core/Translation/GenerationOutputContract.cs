using System.Text.Json;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public enum GenerationOutputKind { Segmentation, Translation }

// 描述业务输出，与提供商及 decoder 的约束语法无关。
public sealed record GenerationOutputContract
{
    public GenerationOutputKind Kind { get; init; }
    public int FirstWord { get; init; }
    public int LastWord { get; init; }
    public Dictionary<int, int[]> AllowedEnds { get; init; } = [];
    public TranslationOutputFormat Format { get; init; }
    public string[] CueIds { get; init; } = [];
    public int? MaximumTextCharacters { get; init; }

    public IReadOnlyList<SourceWordRange> ParseRanges(string response)
    {
        if (Kind != GenerationOutputKind.Segmentation) throw new InvalidOperationException("输出契约不是分句。");
        try
        {
            using var json = JsonDocument.Parse(response);
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 ||
                !json.RootElement.TryGetProperty("ends", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("分句应只返回 ends 数组。");
            var next = FirstWord;
            var ranges = new List<SourceWordRange>();
            foreach (var row in rows.EnumerateArray())
            {
                var end = row.GetInt32();
                if (!AllowedEnds.TryGetValue(next, out var allowed) || !allowed.Contains(end))
                    throw new InvalidDataException($"分句起点 {next} 不能选择末词 {end}；允许的末词为 [{string.Join(',', allowed ?? [])}]。请完整输出。");
                ranges.Add(new(next, end)); next = end + 1;
            }
            if (next != LastWord + 1) throw new InvalidDataException($"分句未覆盖全部词，最终末词应为 {LastWord}。请完整输出。");
            return ranges;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("分句输出结构无效，请返回完整的 ends JSON。"); }
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
