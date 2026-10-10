using Cuelify.Core.Translation;

namespace Cuelify.Desktop.ViewModels;

public sealed record PromptVariableItem(string Name, string Description)
{
    public string Placeholder => "{" + Name + "}";
    public string CopyLabel => "复制 " + Placeholder;
    public static IReadOnlyList<PromptVariableItem> All { get; } = PromptBuilder.Variables.Select(name => new PromptVariableItem(name, name switch
    {
        "source_language" => "本次任务的源语言，例如日语；选择自动识别时，内容为“自动识别”。",
        "target_language" => "本次任务的目标语言，例如中文。",
        "target_style" => "翻译高级设置中填写的译文风格；未填写时为空。",
        "context_before" => "参考前文，包含原文与已有译文，用于理解语境，无需再次翻译。没有可用前文时为“（无）”。",
        "context_after" => "参考后文，仅包含原文，用于理解当前字幕的省略和指代，无需翻译或输出。没有后文时为“（无）”。",
        "cues_json" => "本批待翻译字幕的 JSON，包含字幕标识与原文。批量翻译的用户模板需要保留此变量。",
        "source_text" => "当前一条待翻译的原文。单条翻译的用户模板需要保留此变量。",
        _ => throw new InvalidOperationException("提示词变量缺少说明。")
    })).ToArray();
}
