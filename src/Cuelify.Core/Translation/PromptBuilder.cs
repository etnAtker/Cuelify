using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public static class PromptPresets
{
    public const string BatchId = "builtin:batch-subtitles";
    public const string SimpleId = "builtin:single-simple";
    public const string ContextId = "builtin:single-context";
    public static PromptProfile BatchSubtitles { get; } = new("通用字幕批量翻译", """
        你是视频字幕翻译编辑。把输入字幕从{source_language}翻译为{target_language}。
        忠实传达语义、语气与人物关系；译文自然简洁，适合屏幕阅读。
        结合上下文处理代词与省略；保持专名一致；不要无故删改、弱化原意。
        每条字幕必须独立对应原 ID，不得合并、拆分、遗漏或新增。
        仅输出一个合法 JSON 对象，其键与输入 cueId 完全一致，值为译文字符串。
        不要输出解释、Markdown、时间码或额外字段。
        """, """
        以下内容仅用于理解上下文，不需要翻译或输出：
        {context_before}

        以下后文仅用于理解上下文，不需要翻译或输出：
        {context_after}

        请翻译这些字幕，严格保留所有 ID：
        {cues_json}
        """, true) { Id = BatchId, Description = "一次翻译多条字幕，结合前后文保持表达一致。" };
    public static PromptProfile SingleSimple { get; } = new("简单单条翻译", "", """
        请将下面的字幕翻译成{target_language}。保持原文意思、语气和专有名词，语言自然简短。只返回翻译后的文本，不要解释或附加编号。

        {source_text}
        """, false) { Id = SimpleId, Description = "逐条翻译，只返回译文。" };
    public static PromptProfile SingleContext { get; } = new("上下文单条翻译", "", """
        你是视频字幕翻译编辑。将当前字幕从{source_language}翻译为{target_language}。
        忠实传达原意、语气与人物关系；译文自然简洁，适合屏幕阅读。
        结合参考上下文理解代词、省略和专名，保持称呼与术语一致；信息不足时不要编造。
        风格要求：{target_style}

        参考前文（原文与已有译文，仅供理解，不需要翻译或输出）：
        {context_before}

        参考后文（仅原文，仅供理解，不需要翻译或输出）：
        {context_after}

        当前待翻译字幕：
        {source_text}

        只输出当前字幕的译文，不合并前后文，不输出解释、编号、Markdown或时间码。
        """, false) { Id = ContextId, Description = "逐条翻译，结合前后文理解省略、指代与专名。" };
    public static IReadOnlyList<PromptProfile> All { get; } = [BatchSubtitles, SingleSimple, SingleContext];
    public static PromptProfile Get(string id) => All.FirstOrDefault(profile => profile.Id == id) ?? throw new ArgumentException("内置提示词不存在。");
    public static bool IsBuiltIn(string id) => All.Any(profile => profile.Id == id);
}

public sealed class PromptBuilder
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex Variable = new(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.CultureInvariant);
    public static IReadOnlyList<string> Variables { get; } = ["source_language", "target_language", "target_style", "context_before", "context_after", "cues_json", "source_text"];

    public static void Validate(PromptProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.UserTemplate) ||
            profile.SystemTemplate is null || string.IsNullOrWhiteSpace(profile.Id) || profile.Description is null) throw new ArgumentException("提示词模板无效。");
        if (profile.BatchTranslation && (profile.BatchSize is < 1 or > 100 || profile.MaximumBatchCharacters is < 100 or > 50000))
            throw new ArgumentException("每批条数应为 1～100，字符上限应为 100～50000。");
        foreach (Match match in Variable.Matches(profile.SystemTemplate + "\n" + profile.UserTemplate))
            if (!Variables.Contains(match.Groups[1].Value)) throw new ArgumentException($"不支持提示词变量：{match.Groups[1].Value}");
        var incompatible = profile.BatchTranslation ? "{source_text}" : "{cues_json}";
        if ((profile.SystemTemplate + "\n" + profile.UserTemplate).Contains(incompatible, StringComparison.Ordinal))
            throw new ArgumentException($"当前翻译方式不支持 {incompatible}，请调整批量翻译开关或模板内容。");
        var required = profile.OutputFormat == TranslationOutputFormat.CueIdJson ? "{cues_json}" : "{source_text}";
        if (!profile.UserTemplate.Contains(required, StringComparison.Ordinal)) throw new ArgumentException($"用户模板必须包含 {required}。");
    }

    public TranslationRequest Build(PromptProfile profile, TranslationSettings settings, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<SubtitleCue> context, IReadOnlyList<SubtitleCue>? following = null)
    {
        Validate(profile);
        settings.Validate();
        if (cues.Count == 0 || (profile.OutputFormat == TranslationOutputFormat.PlainText && cues.Count != 1))
            throw new ArgumentException("请求字幕条数与输出协议不符。");
        var contextItems = context.TakeLast(settings.ContextCues).Select(cue => new { Source = cue.SourceText, Translation = cue.TranslatedText }).ToList();
        var followingItems = (following ?? []).Take(settings.FollowingContextCues).Select(cue => cue with { TranslatedText = null }).ToList();
        var contextJson = JsonSerializer.Serialize(contextItems, PromptJsonOptions);
        var followingJson = JsonSerializer.Serialize(followingItems.Select(cue => new { Source = cue.SourceText }), PromptJsonOptions);
        while (contextJson.Length + followingJson.Length > settings.MaximumContextCharacters && (contextItems.Count > 0 || followingItems.Count > 0))
        {
            if (contextItems.Count > followingItems.Count) contextItems.RemoveAt(0);
            else followingItems.RemoveAt(followingItems.Count - 1);
            contextJson = JsonSerializer.Serialize(contextItems, PromptJsonOptions);
            followingJson = JsonSerializer.Serialize(followingItems.Select(cue => new { Source = cue.SourceText }), PromptJsonOptions);
        }
        var values = new Dictionary<string, string>
        {
            ["source_language"] = settings.SourceLanguage, ["target_language"] = settings.TargetLanguage,
            ["target_style"] = settings.TargetStyle, ["context_before"] = contextItems.Count == 0 ? "（无）" : contextJson,
            ["context_after"] = followingItems.Count == 0 ? "（无）" : followingJson,
            ["cues_json"] = JsonSerializer.Serialize(cues.ToDictionary(cue => cue.Id, cue => cue.SourceText), PromptJsonOptions),
            ["source_text"] = string.Join('\n', cues.Select(cue => cue.SourceText))
        };
        // 单次替换：原文中的花括号/变量名不会被二次解释。
        string Render(string template) => Variable.Replace(template, match => values[match.Groups[1].Value]);
        var messages = new List<PromptMessage>();
        if (!string.IsNullOrWhiteSpace(profile.SystemTemplate)) messages.Add(new("system", Render(profile.SystemTemplate)));
        messages.Add(new("user", Render(profile.UserTemplate)));
        return new(cues, messages)
        {
            PromptContext = new(profile, settings, context.TakeLast(contextItems.Count).ToArray(), followingItems)
        };
    }
}
