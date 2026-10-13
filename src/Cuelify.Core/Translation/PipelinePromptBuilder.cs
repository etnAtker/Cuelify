using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Globalization;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public sealed record PipelineWordBlock(int First, int Last, IReadOnlyList<int> Boundaries)
{
    public GenerationOutputContract? OutputContract { get; init; }
}

public static class PipelinePromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static IReadOnlyList<string> Variables { get; } = ["source_language", "target_language", "target_style", "context_before", "context_after", "source_text", "words_json", "boundaries_json"];

    public static IReadOnlyList<PipelineWordBlock> Plan(IReadOnlyList<WordToken> words, SubtitlePipelineProfile profile)
    {
        ValidateInput(words, profile);
        var result = new List<PipelineWordBlock>();
        var first = 0;
        var end = TimeSpan.Zero;
        for (var last = 0; last < words.Count; last++)
        {
            if (words[last].End > end) end = words[last].End;
            if (last != words.Count - 1 && (end - words[first].Start).TotalSeconds < profile.BlockSeconds) continue;
            result.Add(CreateBlock(words, first, last, profile)); first = last + 1; end = TimeSpan.Zero;
        }
        return result;
    }

    public static PipelineWordBlock CreateBlock(IReadOnlyList<WordToken> words, int first, int last, SubtitlePipelineProfile profile)
    {
        var block = new PipelineWordBlock(first, last, Enumerable.Range(first, last - first + 1).ToArray());
        return block with { OutputContract = Contract(words, block, profile) };
    }

    public static void ValidateInput(IReadOnlyList<WordToken> words, SubtitlePipelineProfile profile)
    {
        profile.ValidateSegmentation();
        if (words.Any(w => string.IsNullOrWhiteSpace(w.Text) || w.Start < TimeSpan.Zero || w.End < w.Start) ||
            words.Zip(words.Skip(1)).Any(pair => pair.First.Start > pair.Second.Start))
            throw new InvalidDataException("原始词文本或时间顺序无效，不能分句。");
        if (words.Any(w => (w.End - w.Start).TotalSeconds > profile.MaximumCueSeconds + .001))
            throw new InvalidDataException("识别词的原始时间跨度超过最长字幕时间，无法在保留词时间的情况下分句。");
    }

    public static TranslationRequest Build(IReadOnlyList<WordToken> words, PipelineWordBlock block,
        SubtitlePipelineProfile profile, TranslationSettings settings) => BuildTexts(words, block, profile, settings);

    public static IReadOnlyList<SourceWordRange> Parse(string response, IReadOnlyList<WordToken> words,
        PipelineWordBlock block, SubtitlePipelineProfile profile)
    {
        return Contract(words, block, profile).ParseRanges(response);
    }

    public static GenerationOutputContract Contract(IReadOnlyList<WordToken> words, PipelineWordBlock block, SubtitlePipelineProfile profile) => block.OutputContract ?? new()
    { Kind = GenerationOutputKind.Segmentation, FirstWord = block.First, LastWord = block.Last,
        SegmentationFormat = profile.SegmentationFormat,
        SourceWords = words.Skip(block.First).Take(block.Last - block.First + 1).Select(w => w.Text).ToArray(),
        AllowedEnds = Choices(words, block, profile.MaximumCueSeconds) };

    public static PipelineWordBlock NextBlock(IReadOnlyList<WordToken> words, int first, SubtitlePipelineProfile profile)
    {
        var end = words[first].End; var last = first;
        while (last < words.Count - 1 && (end - words[first].Start).TotalSeconds < profile.BlockSeconds)
        { last++; if (words[last].End > end) end = words[last].End; }
        return CreateBlock(words, first, last, profile);
    }

    private static TranslationRequest BuildTexts(IReadOnlyList<WordToken> words, PipelineWordBlock block, SubtitlePipelineProfile profile, TranslationSettings settings)
    {
        var output = Contract(words, block, profile);
        var table = new StringBuilder(); var origin = words[block.First].Start.TotalSeconds;
        for (var i = block.First; i <= block.Last; i++)
            table.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2:F2}\t{3:F2}\n", i - block.First, words[i].Text, words[i].Start.TotalSeconds - origin, words[i].End.TotalSeconds - origin);
        var values = new Dictionary<string, string>
        {
            ["source_language"] = settings.SourceLanguage, ["target_language"] = settings.TargetLanguage, ["target_style"] = settings.TargetStyle,
            ["source_text"] = output.SourceText(block.First, block.Last),
            ["context_before"] = Tail(string.Concat(words.Take(block.First).Select(w => w.Text)), profile.ContextCharacters),
            ["context_after"] = string.Concat(words.Skip(block.Last + 1).Select(w => w.Text)).Truncate(profile.ContextCharacters)
        };
        string Render(string text) => Regex.Replace(text, @"\{([a-z_]+)\}", m => m.Groups[1].Value switch
        {
            "words_json" => JsonSerializer.Serialize(words.Skip(block.First).Take(block.Last - block.First + 1).Select((w, i) =>
                new { id = i + block.First, text = w.Text, start = w.Start.TotalSeconds, end = w.End.TotalSeconds, speaker = w.SpeakerId }), JsonOptions),
            "boundaries_json" => JsonSerializer.Serialize(output.AllowedEnds.OrderBy(item => item.Key)
                .ToDictionary(item => item.Key.ToString(CultureInfo.InvariantCulture), item => new { first = item.Key, last = item.Value[^1] }), JsonOptions),
            var name => values[name]
        });
        const string system = "你是字幕分句编辑。只决定原文的字幕边界，不翻译，不改写。ASR token 可能只是单个汉字、假名或标点，不等于一句字幕。先阅读连贯原文理解完整表达。通常一条字幕承载完整的一句话、短语或独立回应，不要拆开单词，也不要为了凑满最长时间把不同完整句子机械拼接。合并同一句中的犹豫和未完成片段，保留独立回应。";
        var contract = $"请把本块原文分成自然的字幕，只返回 JSON {{\"texts\":[\"原文字幕1\",\"原文字幕2\"]}}。逐字复制本块原文，不翻译、不解释、不补标点、不改写。拼接 texts 必须完全等于本块原文，边界只能落在完整 token 之间。每个完整句子或独立回应单独一条，不要拆开单词，不要把不同完整句子合成一条。每条从首 token 开始到末 token 结束最长 {profile.MaximumCueSeconds} 秒，停顿计入跨度。" +
            "例：原文「起きたの？おはよう。」应返回 {\"texts\":[\"起きたの？\",\"おはよう。\"]}，不能逐字拆分。\n" +
            $"参考前文（不输出）：{values["context_before"]}\n参考后文（不输出）：{values["context_after"]}\n本块原文：{values["source_text"]}\n时间表每行是局部序号、原文 token、开始秒、结束秒：\n{table}";
        var messages = new List<PromptMessage> { new("system", system + (string.IsNullOrWhiteSpace(profile.Segmentation.SystemTemplate) ? "" : "\n" + Render(profile.Segmentation.SystemTemplate))) };
        messages.Add(new("user", Render(profile.Segmentation.UserTemplate) + "\n" + contract));
        return new([new("block", words[block.First].Start, words[block.Last].End, values["source_text"])], messages)
        { Parameters = profile.Segmentation.Parameters, OutputContract = output };
    }

    private static Dictionary<int, int[]> Choices(IReadOnlyList<WordToken> words, PipelineWordBlock block, double maximum)
    {
        var result = new Dictionary<int, int[]>();
        for (var start = block.Last; start >= block.First; start--)
        {
            var allowed = new List<int>(); var endTime = words[start].End;
            for (var end = start; end <= block.Last; end++)
            {
                if (words[end].End > endTime) endTime = words[end].End;
                if ((endTime - words[start].Start).TotalSeconds > maximum + .001) break;
                if (end == block.Last || result.ContainsKey(end + 1)) allowed.Add(end);
            }
            if (allowed.Count > 0) result[start] = allowed.ToArray();
        }
        if (!result.ContainsKey(block.First)) throw new InvalidDataException("识别词的原始时间跨度超过最长字幕时间，无法在保留词时间的情况下分句。");
        return result;
    }

    private static string Tail(string text, int count) => text.Length <= count ? text : text[^count..];
    private static string Truncate(this string text, int count) => text.Length <= count ? text : text[..count];
}
