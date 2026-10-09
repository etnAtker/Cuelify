using System.Text.Json;
using System.Text.RegularExpressions;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public static class PromptPresets
{
    public static PromptProfile Cloud { get; } = new("通用字幕批量翻译", """
        你是视频字幕翻译编辑。把输入字幕从{source_language}翻译为{target_language}。
        忠实传达语义、语气与人物关系；译文自然简洁，适合屏幕阅读。
        结合上下文处理代词与省略；保持专名一致；不要无故删改、弱化原意。
        每条字幕必须独立对应原 ID，不得合并、拆分、遗漏或新增。
        仅输出一个合法 JSON 对象，其键与输入 cueId 完全一致，值为译文字符串。
        不要输出解释、Markdown、时间码或额外字段。
        """, """
        以下内容仅用于理解上下文，不需要翻译或输出：
        {context_before}

        请翻译这些字幕，严格保留所有 ID：
        {cues_json}
        """, TranslationOutputFormat.CueIdJson);
    public static PromptProfile Concise { get; } = Cloud with
    {
        Name = "通用简洁字幕", SystemTemplate = Cloud.SystemTemplate + "\n采用简短口语表达，避免冗长译文。风格要求：{target_style}"
    };
    public static PromptProfile Local { get; } = new("Hy-MT2 单条纯译文", "", """
        请将下面的字幕翻译成{target_language}。保持原文意思、语气和专有名词，语言自然简短。只返回翻译后的文本，不要解释或附加编号。

        {source_text}
        """, TranslationOutputFormat.PlainText);
}

public sealed class PromptBuilder
{
    private static readonly Regex Variable = new(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.CultureInvariant);
    public static IReadOnlyList<string> Variables { get; } = ["source_language", "target_language", "target_style", "context_before", "cues_json", "source_text"];

    public static void Validate(PromptProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.UserTemplate) ||
            profile.SystemTemplate is null || !Enum.IsDefined(profile.OutputFormat)) throw new ArgumentException("提示词模板无效。");
        foreach (Match match in Variable.Matches(profile.SystemTemplate + "\n" + profile.UserTemplate))
            if (!Variables.Contains(match.Groups[1].Value)) throw new ArgumentException($"不支持提示词变量：{match.Groups[1].Value}");
        var required = profile.OutputFormat == TranslationOutputFormat.CueIdJson ? "{cues_json}" : "{source_text}";
        if (!profile.UserTemplate.Contains(required, StringComparison.Ordinal)) throw new ArgumentException($"用户模板必须包含 {required}。");
    }

    public TranslationRequest Build(PromptProfile profile, TranslationSettings settings, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<SubtitleCue> context)
    {
        Validate(profile);
        settings.Validate();
        if (cues.Count == 0 || (profile.OutputFormat == TranslationOutputFormat.PlainText && cues.Count != 1))
            throw new ArgumentException("请求字幕条数与输出协议不符。");
        var contextItems = context.TakeLast(settings.ContextCues).Select(cue => new { Source = cue.SourceText, Translation = cue.TranslatedText }).ToList();
        var contextJson = JsonSerializer.Serialize(contextItems);
        while (contextJson.Length > settings.MaximumContextCharacters && contextItems.Count > 0)
        {
            contextItems.RemoveAt(0);
            contextJson = JsonSerializer.Serialize(contextItems);
        }
        var values = new Dictionary<string, string>
        {
            ["source_language"] = settings.SourceLanguage, ["target_language"] = settings.TargetLanguage,
            ["target_style"] = settings.TargetStyle, ["context_before"] = contextItems.Count == 0 ? "（无）" : contextJson,
            ["cues_json"] = JsonSerializer.Serialize(cues.ToDictionary(cue => cue.Id, cue => cue.SourceText)),
            ["source_text"] = string.Join('\n', cues.Select(cue => cue.SourceText))
        };
        // 单次替换：原文中的花括号/变量名不会被二次解释。
        string Render(string template) => Variable.Replace(template, match => values[match.Groups[1].Value]);
        var messages = new List<PromptMessage>();
        if (!string.IsNullOrWhiteSpace(profile.SystemTemplate)) messages.Add(new("system", Render(profile.SystemTemplate)));
        messages.Add(new("user", Render(profile.UserTemplate)));
        return new(cues, messages);
    }
}
