using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public sealed record PipelineWordBlock(int First, int Last, IReadOnlyList<int> Boundaries);

public static class PipelinePromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static IReadOnlyList<string> Variables { get; } = ["source_language", "target_language", "target_style", "context_before", "context_after", "source_text", "words_json", "boundaries_json"];

    public static IReadOnlyList<PipelineWordBlock> Plan(IReadOnlyList<WordToken> words, SubtitlePipelineProfile profile)
    {
        profile.Validate();
        if (words.Any(w => string.IsNullOrWhiteSpace(w.Text) || w.Start < TimeSpan.Zero || w.End < w.Start) ||
            words.Zip(words.Skip(1)).Any(pair => pair.First.Start > pair.Second.Start))
            throw new InvalidDataException("原始词文本或时间顺序无效，不能分句。");
        var boundaries = new SortedSet<int>();
        int clauseStart = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (i + 1 == words.Count || EndsPhrase(words[i].Text) || SpeakerChange(words[i], words[i + 1]) ||
                words[i + 1].Start - words[i].End > TimeSpan.FromSeconds(2))
            {
                boundaries.Add(i);
                if ((words.Skip(clauseStart).Take(i - clauseStart + 1).Max(w => w.End) - words[clauseStart].Start).TotalSeconds > profile.MaximumCueSeconds)
                    for (var k = clauseStart; k < i; k++)
                        if (words[k + 1].Start - words[k].End >= TimeSpan.FromMilliseconds(250) &&
                            !new[] { "の", "が", "は", "を", "に", "と", "で", "も", "ね", "よ", "た", "る", "す" }.Contains(words[k + 1].Text.Trim())) boundaries.Add(k);
                clauseStart = i + 1;
            }
        }
        var result = new List<PipelineWordBlock>();
        var first = 0;
        foreach (var last in boundaries)
        {
            if (last != words.Count - 1 && (words[last].End - words[first].Start).TotalSeconds < profile.BlockSeconds) continue;
            var block = new PipelineWordBlock(first, last, boundaries.Where(b => b >= first && b <= last).ToArray());
            _ = Choices(words, block, profile.MaximumCueSeconds); // 没有合法路径时明确失败，不机械强切词。
            result.Add(block); first = last + 1;
        }
        return result;
    }

    public static TranslationRequest Build(IReadOnlyList<WordToken> words, PipelineWordBlock block,
        SubtitlePipelineProfile profile, TranslationSettings settings)
    {
        var choices = Choices(words, block, profile.MaximumCueSeconds);
        var values = new Dictionary<string, string>
        {
            ["source_language"] = settings.SourceLanguage, ["target_language"] = settings.TargetLanguage, ["target_style"] = settings.TargetStyle,
            ["source_text"] = string.Concat(words.Skip(block.First).Take(block.Last - block.First + 1).Select(w => w.Text)),
            ["context_before"] = Tail(string.Concat(words.Take(block.First).Select(w => w.Text)), profile.ContextCharacters),
            ["context_after"] = string.Concat(words.Skip(block.Last + 1).Select(w => w.Text)).Truncate(profile.ContextCharacters),
            ["words_json"] = JsonSerializer.Serialize(words.Skip(block.First).Take(block.Last - block.First + 1).Select((w, i) =>
                new { id = i + block.First, text = w.Text, start = w.Start.TotalSeconds, end = w.End.TotalSeconds, speaker = w.SpeakerId }), JsonOptions),
            ["boundaries_json"] = JsonSerializer.Serialize(choices.OrderBy(item => item.Key).ToDictionary(item => item.Key.ToString(), item => item.Value), JsonOptions)
        };
        string Render(string text) => Regex.Replace(text, @"\{([a-z_]+)\}", m => values[m.Groups[1].Value]);
        var joint = profile.Mode == SubtitlePipelineMode.JointSegmentationTranslation;
        var contract = joint ? "仅返回 JSON {\"segments\":[{\"end\":末词ID,\"text\":译文},...]}。译文语言：" + settings.TargetLanguage :
            "仅返回 JSON {\"ends\":[末词ID,...]}。这一阶段不翻译。";
        contract += $"\n从词 {block.First} 开始逐段选择末词 ID，下一段从上段末词+1开始，最后必须为 {block.Last}。不得漏词、重复或重排。" +
            $"每条最长 {profile.MaximumCueSeconds} 秒。必须逐段查表，只能选择当前起点对应的末词 ID，不可使用其他起点的选项。起点到可选末词的映射：{values["boundaries_json"]}。" +
            $"译文目标每行 {profile.LineCharacters} 字、最多两行、每秒约 {profile.ReadingCharactersPerSecond} 字，不编造或无故删减事实。" +
            $"\n参考前文（不输出）：{values["context_before"]}\n参考后文（不输出）：{values["context_after"]}\n连贯原文（以词表 ID 对齐）：{values["source_text"]}\n待处理词：{values["words_json"]}";
        var messages = new List<PromptMessage>();
        if (!string.IsNullOrWhiteSpace(profile.Segmentation.SystemTemplate)) messages.Add(new("system", Render(profile.Segmentation.SystemTemplate)));
        messages.Add(new("user", Render(profile.Segmentation.UserTemplate) + "\n" + contract));
        return new([new("block", words[block.First].Start, words[block.Last].End, string.Concat(words.Skip(block.First).Take(block.Last - block.First + 1).Select(w => w.Text)))], messages)
        { Parameters = profile.Segmentation.Parameters, Grammar = Grammar(choices, block, joint) };
    }

    public static IReadOnlyList<(SourceWordRange Range, string? Translation)> Parse(string response, IReadOnlyList<WordToken> words,
        PipelineWordBlock block, SubtitlePipelineProfile profile)
    {
        try
        {
            using var json = JsonDocument.Parse(response);
            var joint = profile.Mode == SubtitlePipelineMode.JointSegmentationTranslation;
            var rows = json.RootElement.GetProperty(joint ? "segments" : "ends");
            if (json.RootElement.EnumerateObject().Count() != 1 || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            var choices = Choices(words, block, profile.MaximumCueSeconds);
            var next = block.First;
            var result = new List<(SourceWordRange, string?)>();
            foreach (var row in rows.EnumerateArray())
            {
                var end = joint ? row.GetProperty("end").GetInt32() : row.GetInt32();
                var text = joint ? row.GetProperty("text").GetString() : null;
                if (!choices.TryGetValue(next, out var allowed) || !allowed.Contains(end))
                    throw new InvalidDataException($"分句起点 {next} 不能选择末词 {end}；该起点允许的末词为 [{string.Join(',', allowed ?? [])}]。请从首词重新逐段查表，完整输出。");
                if (joint && (!AlignmentValidator.IsValid(text ?? "") || row.EnumerateObject().Count() != 2 ||
                    text!.EnumerateRunes().All(r => !Rune.IsLetterOrDigit(r)) || PipelineDisplay.CharacterCount(text!) > profile.LineCharacters * 2))
                    throw new InvalidDataException($"词 {next}～{end} 的译文必须非空、有实际文字、最多 {profile.LineCharacters * 2} 字符，且条目只包含 end 与 text。请完整输出全部段落。");
                result.Add((new(next, end), text)); next = end + 1;
            }
            if (next != block.Last + 1) throw new InvalidDataException($"分句未覆盖全部词，下一起点为 {next}，最终末词应为 {block.Last}。请完整输出。");
            return result;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("分句结果未完整覆盖原文或边界不合法，请重试或调整管线分句设置。"); }
    }

    private static Dictionary<int, int[]> Choices(IReadOnlyList<WordToken> words, PipelineWordBlock block, double maximum)
    {
        var result = new Dictionary<int, int[]>();
        foreach (var start in block.Boundaries.Select(b => b + 1).Prepend(block.First).Where(s => s <= block.Last).Reverse())
        {
            var allowed = block.Boundaries.Where(end => end >= start &&
                string.Concat(words.Skip(start).Take(end - start + 1).Select(w => w.Text)).EnumerateRunes().Any(Rune.IsLetterOrDigit) &&
                (end == block.Last || words[end + 1].Text.EnumerateRunes().Any(Rune.IsLetterOrDigit)) &&
                (words.Skip(start).Take(end - start + 1).Max(w => w.End) - words[start].Start).TotalSeconds <= maximum + .001 &&
                (end == block.Last || result.ContainsKey(end + 1)) &&
                (end == block.Last || !words[end].Text.TrimEnd().EndsWith('、') && !words[end].Text.TrimEnd().EndsWith(',') ||
                    (words[end].End - words[start].Start).TotalSeconds >= 1.2 || words[end + 1].Start - words[end].End >= TimeSpan.FromSeconds(2)) &&
                !Enumerable.Range(start, end - start).Any(i => SpeakerChange(words[i], words[i + 1]) ||
                    words[i + 1].Start - words[i].End > TimeSpan.FromSeconds(2) && block.Boundaries.Contains(i))).ToArray();
            if (allowed.Length > 0) result[start] = allowed;
        }
        if (!result.ContainsKey(block.First)) throw new InvalidDataException("原文缺少符合时长要求的自然切点，请增加管线最长字幕时间或使用直接翻译。");
        return result;
    }

    private static string Grammar(Dictionary<int, int[]> choices, PipelineWordBlock block, bool joint)
    {
        string Literal(string s) => JsonSerializer.Serialize(s);
        var b = new StringBuilder($"root ::= {Literal(joint ? "{\"segments\":[" : "{\"ends\":[")} s{block.First} {Literal("]}")}\n");
        foreach (var (start, ends) in choices)
            b.Append($"s{start} ::= ").AppendJoin(" | ", ends.Select(end =>
                (joint ? Literal("{\"end\":" + end + ",\"text\":") + " string " + Literal("}") : Literal(end.ToString())) +
                (end == block.Last ? "" : " " + Literal(",") + " s" + (end + 1)))).AppendLine();
        if (joint) b.AppendLine("string ::= \"\\\"\" ([^\"\\\\\\x00-\\x1F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4}))* \"\\\"\"");
        return b.ToString();
    }
    private static bool EndsPhrase(string text) => ".!?。！？、，,;；:：".Contains(text.TrimEnd()[^1]);
    private static bool SpeakerChange(WordToken a, WordToken b) => a.SpeakerId is not null && b.SpeakerId is not null && a.SpeakerId != b.SpeakerId;
    private static string Tail(string text, int count) => text.Length <= count ? text : text[^count..];
    private static string Truncate(this string text, int count) => text.Length <= count ? text : text[..count];
}
