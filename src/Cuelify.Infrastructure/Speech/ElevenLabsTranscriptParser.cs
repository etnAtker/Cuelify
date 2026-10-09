using System.Text.Json;
using Cuelify.Core.Speech;

namespace Cuelify.Infrastructure.Speech;

public static class ElevenLabsTranscriptParser
{
    public static AsrTranscript Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("ASR 响应缺少词级时间戳列表。");
            var tokens = new List<WordToken>();
            foreach (var word in words.EnumerateArray())
            {
                if (word.ValueKind != JsonValueKind.Object || !word.TryGetProperty("type", out var type) || type.GetString() != "word") continue;
                if (!word.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                    !word.TryGetProperty("start", out var start) || !start.TryGetDouble(out var startSeconds) ||
                    !word.TryGetProperty("end", out var end) || !end.TryGetDouble(out var endSeconds) ||
                    !double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || startSeconds < 0 || endSeconds < startSeconds || endSeconds > 36000 ||
                    string.IsNullOrWhiteSpace(text.GetString()))
                    throw new InvalidDataException("ASR 返回了无效的词文本或时间戳。");
                var speaker = word.TryGetProperty("speaker_id", out var speakerId) && speakerId.ValueKind == JsonValueKind.String ? speakerId.GetString() : null;
                tokens.Add(new WordToken(text.GetString()!, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), speaker));
            }
            var language = root.TryGetProperty("language_code", out var languageCode) && languageCode.ValueKind == JsonValueKind.String ? languageCode.GetString() : null;
            // 同时说话允许交叠；稳定按开始时间排序，不删相似台词。
            return new AsrTranscript(language, tokens.OrderBy(token => token.Start).ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // 不携带响应正文或内部异常，避免服务错误回显敏感内容。
            throw new InvalidDataException("ASR 响应格式无效。");
        }
    }
}
