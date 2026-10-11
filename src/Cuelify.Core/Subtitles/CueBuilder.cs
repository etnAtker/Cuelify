using System.Text;
using Cuelify.Core.Speech;
using Cuelify.Core.Translation;

namespace Cuelify.Core.Subtitles;

public sealed record SubtitleCue(string Id, TimeSpan Start, TimeSpan End, string SourceText, string? TranslatedText = null);
public sealed record CueBuilderOptions
{
    public TimeSpan PauseThreshold { get; init; } = TimeSpan.FromMilliseconds(600);
    public TimeSpan TargetDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(7);
    public int MaximumDisplayWidth { get; init; } = 84;
    public TimeSpan ZeroDurationTolerance { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed class CueBuilder
{
    public IReadOnlyList<SubtitleCue> Build(IReadOnlyList<WordToken> words, CueBuilderOptions? options = null) => BuildWithRanges(words.OrderBy(w => w.Start).ToArray(), options).Cues;

    public SubtitlePipelineResult BuildWithRanges(IReadOnlyList<WordToken> words, CueBuilderOptions? options = null)
    {
        options ??= new CueBuilderOptions();
        if (words.Zip(words.Skip(1)).Any(pair => pair.First.Start > pair.Second.Start))
            throw new InvalidDataException("识别词顺序无效，不能建立连续词范围。");
        if (options.PauseThreshold < TimeSpan.Zero || options.TargetDuration <= TimeSpan.Zero ||
            options.MaximumDuration < options.TargetDuration || options.MaximumDisplayWidth < 2 ||
            options.ZeroDurationTolerance < TimeSpan.FromMilliseconds(1) || options.ZeroDurationTolerance > options.MaximumDuration)
            throw new ArgumentException("字幕分句配置无效。");
        var groups = new List<List<IndexedWord>>();
        var current = new List<IndexedWord>();
        foreach (var item in MergeZeroDurationWords(words, options.ZeroDurationTolerance))
        {
            var word = item.Word;
            if (word.Start < TimeSpan.Zero || word.End <= word.Start || string.IsNullOrWhiteSpace(word.Text))
                throw new InvalidDataException("词文本或时间戳无效，不能构建字幕。");
            if (item.Independent)
            {
                if (current.Count > 0) { groups.Add(current); current = []; }
                groups.Add([item]);
                continue;
            }
            if (current.Count > 0)
            {
                var end = current.Max(token => token.Word.End);
                var speakerChanged = current[^1].Word.SpeakerId is not null && word.SpeakerId is not null && current[^1].Word.SpeakerId != word.SpeakerId;
                if (speakerChanged || word.Start - end >= options.PauseThreshold ||
                    word.End - current[0].Word.Start > options.MaximumDuration || DisplayWidth(Join(current.Select(i => i.Word).Append(word))) > options.MaximumDisplayWidth)
                {
                    groups.Add(current);
                    current = [];
                }
            }
            current.Add(item);
            var span = current.Max(token => token.Word.End) - current[0].Word.Start;
            if (span >= options.TargetDuration || (span >= TimeSpan.FromSeconds(1) && EndsSentence(word.Text)))
            {
                groups.Add(current);
                current = [];
            }
        }
        if (current.Count > 0) groups.Add(current);
        var normalized = new List<(SourceWordRange Range, TimeSpan Start, TimeSpan End)>();
        foreach (var group in groups.OrderBy(g => g.Min(i => i.First)))
        {
            var range = new SourceWordRange(group.Min(i => i.First), group.Max(i => i.Last));
            var start = group.Min(i => i.Word.Start); var end = group.Max(i => i.Word.End);
            // 零时长词归入邻词后，合并交叠来源，保持原始词连续覆盖。
            if (normalized.Count > 0 && range.First <= normalized[^1].Range.Last)
            {
                var prior = normalized[^1];
                normalized[^1] = (new(prior.Range.First, Math.Max(prior.Range.Last, range.Last)),
                    start < prior.Start ? start : prior.Start, end > prior.End ? end : prior.End);
            }
            else normalized.Add((range, start, end));
        }
        var ordered = words.OrderBy(w => w.Start).ToArray();
        return new(normalized.Select((item, index) => new SubtitleCue($"cue-{index + 1:000000}", item.Start, item.End,
            Join(ordered.Skip(item.Range.First).Take(item.Range.Last - item.Range.First + 1)))).ToArray(), normalized.Select(i => i.Range).ToArray(), 0, 0);
    }

    private sealed record IndexedWord(WordToken Word, bool Independent, int First, int Last);
    private static IReadOnlyList<IndexedWord> MergeZeroDurationWords(IReadOnlyList<WordToken> words, TimeSpan tolerance)
    {
        var ordered = words.OrderBy(word => word.Start).ToArray();
        if (ordered.Any(word => word.Start < TimeSpan.Zero || word.End < word.Start || string.IsNullOrWhiteSpace(word.Text)))
            throw new InvalidDataException("词文本或时间戳无效，不能构建字幕。");
        var groups = new Dictionary<int, List<int>>();
        var independent = new HashSet<int>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var word = ordered[index];
            var anchor = index;
            if (word.Start == word.End)
            {
                // 容差内选更近的同说话人邻词；等距优先后词。孤立词使用明确配置的显示时长。
                var previous = index - 1;
                while (previous >= 0 && ordered[previous].Start == ordered[previous].End && SameSpeaker(ordered[previous], word)) previous--;
                if (previous >= 0 && !CanMerge(ordered[previous], word)) previous = -1;
                var next = index + 1;
                while (next < ordered.Length && ordered[next].Start == ordered[next].End && SameSpeaker(ordered[next], word)) next++;
                if (next < ordered.Length && !CanMerge(ordered[next], word)) next = ordered.Length;
                var previousDistance = previous < 0 ? long.MaxValue : Math.Max(0, (word.Start - ordered[previous].End).Ticks);
                var nextDistance = next == ordered.Length ? long.MaxValue : Math.Abs((ordered[next].Start - word.Start).Ticks);
                if (Math.Min(previousDistance, nextDistance) <= tolerance.Ticks)
                    anchor = previousDistance < nextDistance ? previous : next;
                else
                {
                    // 同一时刻的连续孤立文字组成一句，重复文字仍按原顺序保留。
                    if (index > 0 && ordered[index - 1].Start == word.Start && ordered[index - 1].End == word.End &&
                        ordered[index - 1].SpeakerId == word.SpeakerId && independent.Contains(index - 1))
                    { anchor = index - 1; while (anchor > 0 && !groups.ContainsKey(anchor)) anchor--; }
                    independent.Add(index);
                }
            }
            if (!groups.TryGetValue(anchor, out var group)) groups[anchor] = group = [];
            group.Add(index);
        }
        return groups.OrderBy(item => item.Key).Select(item => new IndexedWord(ordered[item.Key] with
        { Text = Join(item.Value.Select(i => ordered[i])), Start = item.Value.Min(i => ordered[i].Start),
            End = independent.Contains(item.Key) ? ordered[item.Key].Start + tolerance : item.Value.Max(i => ordered[i].End) },
            independent.Contains(item.Key), item.Value.Min(), item.Value.Max())).OrderBy(item => item.Word.Start).ToArray();

        static bool CanMerge(WordToken candidate, WordToken word) => candidate.End > candidate.Start &&
            SameSpeaker(candidate, word);
        static bool SameSpeaker(WordToken candidate, WordToken word) => candidate.SpeakerId is null || word.SpeakerId is null || candidate.SpeakerId == word.SpeakerId;
    }

    private static string Join(IEnumerable<WordToken> words)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var text = word.Text.Trim();
            if (builder.Length > 0 && !IsCjk(builder[^1]) && !IsCjk(text[0]) &&
                !".,!?;:，。！？；：、)]}”’".Contains(text[0]) && !"([{“‘".Contains(builder[^1])) builder.Append(' ');
            builder.Append(text);
        }
        return builder.ToString();
    }

    private static bool EndsSentence(string text) => ".!?。！？".Contains(text.TrimEnd()[^1]);
    private static int DisplayWidth(string text) => text.EnumerateRunes().Sum(rune => rune.Value >= 0x2e80 ? 2 : 1);
    private static bool IsCjk(char value) => value is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}
