using System.Text;
using Cuelify.Core.Speech;

namespace Cuelify.Core.Subtitles;

public sealed record SubtitleCue(string Id, TimeSpan Start, TimeSpan End, string SourceText, string? TranslatedText = null);
public sealed record CueBuilderOptions
{
    public TimeSpan PauseThreshold { get; init; } = TimeSpan.FromMilliseconds(600);
    public TimeSpan TargetDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(7);
    public int MaximumDisplayWidth { get; init; } = 84;
}

public sealed class CueBuilder
{
    public IReadOnlyList<SubtitleCue> Build(IReadOnlyList<WordToken> words, CueBuilderOptions? options = null)
    {
        options ??= new CueBuilderOptions();
        if (options.PauseThreshold < TimeSpan.Zero || options.TargetDuration <= TimeSpan.Zero ||
            options.MaximumDuration < options.TargetDuration || options.MaximumDisplayWidth < 2)
            throw new ArgumentException("字幕分句配置无效。");
        var groups = new List<List<WordToken>>();
        var current = new List<WordToken>();
        foreach (var word in words.OrderBy(word => word.Start))
        {
            if (word.Start < TimeSpan.Zero || word.End <= word.Start || string.IsNullOrWhiteSpace(word.Text))
                throw new InvalidDataException("词文本或时间戳无效，不能构建字幕。");
            if (current.Count > 0)
            {
                var end = current.Max(token => token.End);
                var speakerChanged = current[^1].SpeakerId is not null && word.SpeakerId is not null && current[^1].SpeakerId != word.SpeakerId;
                if (speakerChanged || word.Start - end >= options.PauseThreshold ||
                    word.End - current[0].Start > options.MaximumDuration || DisplayWidth(Join(current.Append(word))) > options.MaximumDisplayWidth)
                {
                    groups.Add(current);
                    current = [];
                }
            }
            current.Add(word);
            var span = current.Max(token => token.End) - current[0].Start;
            if (span >= options.TargetDuration || (span >= TimeSpan.FromSeconds(1) && EndsSentence(word.Text)))
            {
                groups.Add(current);
                current = [];
            }
        }
        if (current.Count > 0) groups.Add(current);
        return groups.Select((group, index) => new SubtitleCue($"cue-{index + 1:000000}", group.Min(word => word.Start), group.Max(word => word.End), Join(group))).ToArray();
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
