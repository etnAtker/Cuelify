using System.Globalization;
using System.Text;

namespace Cuelify.Core.Subtitles;

public static class SrtSerializer
{
    public static string SerializeSource(IReadOnlyList<SubtitleCue> cues)
        => Serialize(cues, false);

    public static string SerializeTranslated(IReadOnlyList<SubtitleCue> cues)
        => Serialize(cues, true);

    private static string Serialize(IReadOnlyList<SubtitleCue> cues, bool translated)
    {
        var builder = new StringBuilder();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lastStart = TimeSpan.Zero;
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var start = cue.Start.Ticks / TimeSpan.TicksPerMillisecond;
            var end = cue.End.Ticks / TimeSpan.TicksPerMillisecond;
            var text = CleanText((translated ? cue.TranslatedText : cue.SourceText) ?? "");
            if (cue.Start < TimeSpan.Zero || end <= start || cue.Start < lastStart || string.IsNullOrWhiteSpace(cue.Id) || !ids.Add(cue.Id) || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("字幕为空、ID 重复、顺序或毫秒时间戳无效，拒绝导出。");
            lastStart = cue.Start;
            builder.Append(index + 1).Append("\r\n").Append(Format(start)).Append(" --> ").Append(Format(end)).Append("\r\n").Append(text).Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    private static string Format(long milliseconds) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00},{milliseconds % 1000:000}");

    private static string CleanText(string text) => string.Join("\r\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
        .Split('\n').Select(line => new string(line.Where(character => !char.IsControl(character) || character == '\t').ToArray()).Trim()).Where(line => line.Length > 0));
}
