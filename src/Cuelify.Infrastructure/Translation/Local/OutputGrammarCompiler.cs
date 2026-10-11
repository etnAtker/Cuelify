using System.Text;
using System.Text.Json;
using Cuelify.Core.Translation;

namespace Cuelify.Infrastructure.Translation.Local;

// 只有本地适配器了解 GBNF；云端与本地共享同一业务输出契约。
public static class OutputGrammarCompiler
{
    public static string Compile(GenerationOutputContract contract)
    {
        static string Literal(string text) => JsonSerializer.Serialize(text);
        if (contract.Kind == GenerationOutputKind.Segmentation)
        {
            var grammar = new StringBuilder($"root ::= {Literal("{\"ends\":[")} s{contract.FirstWord} {Literal("]}")}\n");
            foreach (var (start, ends) in contract.AllowedEnds)
                grammar.Append($"s{start} ::= ").AppendJoin(" | ", ends.Select(end => Literal(end.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                    (end == contract.LastWord ? "" : " " + Literal(",") + " s" + (end + 1)))).AppendLine();
            return grammar.ToString();
        }
        var limit = contract.MaximumTextCharacters is { } maximum ? $"{{1,{maximum}}}" : "+";
        if (contract.Format == TranslationOutputFormat.PlainText)
            return "root ::= character" + limit + "\ncharacter ::= [^\\x00-\\x1F] | [\\n\\r\\t]\n";
        var entries = contract.CueIds.Select(id => Literal(JsonSerializer.Serialize(id)) + " ws " + Literal(":") + " ws string");
        return "root ::= " + Literal("{") + " ws " + string.Join(" ws " + Literal(",") + " ws ", entries) + " ws " + Literal("}") + " ws\n" +
            "ws ::= [ \\t\\n\\r]*\n" + "string ::= \"\\\"\" character" + limit + " \"\\\"\"\n" +
            "character ::= [^\"\\\\\\x00-\\x1F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4})\n";
    }
}
