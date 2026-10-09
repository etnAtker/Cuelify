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
        "cues_json" => "本批待翻译字幕的 JSON，包含字幕标识与原文。云端翻译的用户模板需要保留此变量。",
        "source_text" => "待翻译的原文。本地翻译时为当前单条字幕，本地用户模板需要保留此变量；云端批次中为多条原文按行拼接。",
        _ => throw new InvalidOperationException("提示词变量缺少说明。")
    })).ToArray();
}
