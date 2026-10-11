using System.Text.Json;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

// 云端仍由统一校验器验收；本地 decoder 直接限定已知字幕 ID 与显示长度。
public static class PipelineTranslationGrammar
{
    public static string Build(IReadOnlyList<SubtitleCue> cues, int maximumCharacters)
    {
        string Literal(string value) => JsonSerializer.Serialize(value);
        var entries = cues.Select(cue => Literal(JsonSerializer.Serialize(cue.Id)) + " ws " + Literal(":") + " ws string");
        return "root ::= " + Literal("{") + " ws " + string.Join(" ws " + Literal(",") + " ws ", entries) + " ws " + Literal("}") + " ws\n" +
            "ws ::= [ \\t\\n\\r]*\n" +
            "string ::= \"\\\"\" character{1," + maximumCharacters + "} \"\\\"\"\n" +
            "character ::= [^\"\\\\\\x00-\\x1F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4})\n";
    }
}
