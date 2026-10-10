using System.Text.Json;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public static class AlignmentValidator
{
    public static AlignmentResult Parse(string output, IReadOnlyList<SubtitleCue> cues, TranslationOutputFormat format)
    {
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (format == TranslationOutputFormat.PlainText)
        {
            if (cues.Count != 1) throw new ArgumentException("纯文本输出仅支持单条字幕。");
            if (IsValid(output) && !output.TrimStart().StartsWith("```", StringComparison.Ordinal))
                accepted[cues[0].Id] = output.Trim();
        }
        else
        {
            var json = output.Trim();
            if (json.StartsWith("```json\n", StringComparison.Ordinal) || json.StartsWith("```json\r\n", StringComparison.Ordinal) || json.StartsWith("```\n", StringComparison.Ordinal))
            {
                var newline = json.IndexOf('\n');
                if (json.EndsWith("\n```", StringComparison.Ordinal)) json = json[(newline + 1)..^4].Trim();
            }
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var expected = cues.ToDictionary(cue => cue.Id, StringComparer.Ordinal);
                    var entries = document.RootElement.EnumerateObject().ToArray();
                    var unique = entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() == entries.Length;
                    // 多余/重复 ID 无法可靠判断错位，整个响应拒收；缺失 ID 只补翻缺失项。
                    if (unique && entries.All(entry => expected.ContainsKey(entry.Name)))
                        foreach (var entry in entries)
                            if (entry.Value.ValueKind == JsonValueKind.String && IsValid(entry.Value.GetString()))
                                accepted[entry.Name] = entry.Value.GetString()!.Trim();
                }
            }
            catch (JsonException) { /* 不从任意解释文字中截取花括号冒充结构化响应。 */ }
        }
        return new(accepted, cues.Where(cue => !accepted.ContainsKey(cue.Id)).Select(cue => cue.Id).ToArray());
    }

    public static bool IsValid(string? text) => !string.IsNullOrWhiteSpace(text) &&
        !text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
