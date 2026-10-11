using System.Text;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public static class PipelineDisplay
{
    public static (IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<SourceWordRange> Ranges) Build(IReadOnlyList<WordToken> words,
        IReadOnlyList<(SourceWordRange Range, string? Translation)> segments, SubtitlePipelineProfile profile, TimeSpan duration)
    {
        var cues = new List<SubtitleCue>(); var ranges = new List<SourceWordRange>();
        int next = 0;
        foreach (var (range, translation) in segments)
        {
            if (range.First != next || range.Last < range.First || range.Last >= words.Count) throw new InvalidDataException("字幕词范围未完整覆盖原文。");
            var slice = words.Skip(range.First).Take(range.Last - range.First + 1).ToArray();
            var start = slice.Min(w => w.Start); var end = slice.Max(w => w.End);
            var cue = new SubtitleCue("", start, end, string.Concat(slice.Select(w => w.Text)), translation);
            // 重叠语音及同一时刻的零时长标点共屏，保留连续源文引用。
            if (cues.Count > 0 && (start < cues[^1].End || start == cues[^1].Start))
            {
                var prior = cues[^1];
                cue = prior with { End = end > prior.End ? end : prior.End, SourceText = prior.SourceText + cue.SourceText,
                    TranslatedText = prior.TranslatedText is null || translation is null ? null : prior.TranslatedText + translation };
                ranges[^1] = new(ranges[^1].First, range.Last); cues[^1] = cue;
            }
            else { cues.Add(cue); ranges.Add(range); }
            next = range.Last + 1;
        }
        if (next != words.Count) throw new InvalidDataException("字幕未覆盖全部词。");
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var available = i + 1 < cues.Count ? cues[i + 1].Start : duration;
            var requested = cue.Start + TimeSpan.FromSeconds(Math.Max(profile.MinimumCueSeconds,
                cue.TranslatedText is null ? 0 : CharacterCount(cue.TranslatedText) / profile.ReadingCharactersPerSecond));
            var cap = cue.Start + TimeSpan.FromSeconds(profile.MaximumCueSeconds);
            var end = requested > cue.End ? new[] { requested, available, cap }.Min() : cue.End;
            if (end <= cue.Start) end = new[] { cue.Start + TimeSpan.FromMilliseconds(1), available }.Min();
            if (end <= cue.Start || end - cue.Start > TimeSpan.FromSeconds(profile.MaximumCueSeconds) + TimeSpan.FromMilliseconds(1))
                throw new InvalidDataException("字幕源文时间超出管线限制，请调整分句或最长显示时间。");
            var translated = cue.TranslatedText;
            // 共屏合并后过长的译文需要按合并源文重译，不能截断文字冒充成功。
            if (translated is not null && CharacterCount(translated) > profile.LineCharacters * 2) translated = null;
            cues[i] = cue with { Id = $"cue-{i + 1:000000}", End = end,
                TranslatedText = translated is null ? null : Wrap(translated, profile.LineCharacters) };
        }
        return (cues, ranges);
    }

    public static int CharacterCount(string text) => text.EnumerateRunes().Count(r => r.Value is not ('\r' or '\n'));

    public static string Wrap(string text, int lineCharacters)
    {
        var runes = text.Replace("\r", "").Replace("\n", "").EnumerateRunes().ToArray();
        if (runes.Length <= lineCharacters) return string.Concat(runes);
        if (runes.Length > lineCharacters * 2) throw new InvalidDataException("译文超出两行显示长度，请重翻或调整管线每行字数。");
        var split = (runes.Length + 1) / 2;
        var minimum = Math.Max(runes.Length - lineCharacters, runes.Length / 3);
        var maximum = Math.Min(lineCharacters, runes.Length * 2 / 3);
        var candidates = Enumerable.Range(minimum, maximum - minimum + 1).Where(i => i > 0 && i < runes.Length &&
            "，。！？；、,.!?; ".Contains(runes[i - 1].ToString(), StringComparison.Ordinal)).OrderBy(i => Math.Abs(i - split)).ToArray();
        if (candidates.Length > 0) split = candidates[0];
        return string.Concat(runes.Take(split)).TrimEnd() + "\n" + string.Concat(runes.Skip(split)).TrimStart();
    }
}
