namespace Cuelify.Infrastructure.Speech;

public sealed record SpeechLanguage(string Name, string Code, string? TwoLetterCode = null);

public static class SpeechLanguages
{
    // Scribe v2 官方语言表；接口接受 ISO 639-1 / 639-3，地区标识仅用于兼容用户输入。
    // https://elevenlabs.io/docs/overview/capabilities/speech-to-text#supported-languages
    public static IReadOnlyList<SpeechLanguage> Supported { get; } = [
        new("中文", "zho", "zh"), new("日语", "jpn", "ja"), new("英语", "eng", "en"),
        new("韩语", "kor", "ko"), new("粤语", "yue"), new("法语", "fra", "fr"),
        new("德语", "deu", "de"), new("西班牙语", "spa", "es"), new("葡萄牙语", "por", "pt"),
        new("俄语", "rus", "ru"), new("意大利语", "ita", "it"), new("阿拉伯语", "ara", "ar"),
        new("南非荷兰语", "afr", "af"), new("阿姆哈拉语", "amh", "am"), new("亚美尼亚语", "hye", "hy"),
        new("阿萨姆语", "asm", "as"), new("阿斯图里亚斯语", "ast"), new("阿塞拜疆语", "aze", "az"),
        new("白俄罗斯语", "bel", "be"), new("孟加拉语", "ben", "bn"), new("波斯尼亚语", "bos", "bs"),
        new("保加利亚语", "bul", "bg"), new("缅甸语", "mya", "my"), new("加泰罗尼亚语", "cat", "ca"),
        new("宿务语", "ceb"), new("齐切瓦语", "nya", "ny"), new("克罗地亚语", "hrv", "hr"),
        new("捷克语", "ces", "cs"), new("丹麦语", "dan", "da"), new("荷兰语", "nld", "nl"),
        new("爱沙尼亚语", "est", "et"), new("菲律宾语", "fil"), new("芬兰语", "fin", "fi"),
        new("富拉语", "ful", "ff"), new("加利西亚语", "glg", "gl"), new("卢干达语", "lug", "lg"),
        new("格鲁吉亚语", "kat", "ka"), new("希腊语", "ell", "el"), new("古吉拉特语", "guj", "gu"),
        new("豪萨语", "hau", "ha"), new("希伯来语", "heb", "he"), new("印地语", "hin", "hi"),
        new("匈牙利语", "hun", "hu"), new("冰岛语", "isl", "is"), new("伊博语", "ibo", "ig"),
        new("印度尼西亚语", "ind", "id"), new("爱尔兰语", "gle", "ga"), new("爪哇语", "jav", "jv"),
        new("佛得角克里奥尔语", "kea"), new("卡纳达语", "kan", "kn"), new("哈萨克语", "kaz", "kk"),
        new("高棉语", "khm", "km"), new("库尔德语", "kur", "ku"), new("吉尔吉斯语", "kir", "ky"),
        new("老挝语", "lao", "lo"), new("拉脱维亚语", "lav", "lv"), new("林加拉语", "lin", "ln"),
        new("立陶宛语", "lit", "lt"), new("卢奥语", "luo"), new("卢森堡语", "ltz", "lb"),
        new("马其顿语", "mkd", "mk"), new("马来语", "msa", "ms"), new("马拉雅拉姆语", "mal", "ml"),
        new("马耳他语", "mlt", "mt"), new("毛利语", "mri", "mi"), new("马拉地语", "mar", "mr"),
        new("蒙古语", "mon", "mn"), new("尼泊尔语", "nep", "ne"), new("北索托语", "nso"),
        new("挪威语", "nor", "no"), new("奥克语", "oci", "oc"), new("奥里亚语", "ori", "or"),
        new("普什图语", "pus", "ps"), new("波斯语", "fas", "fa"), new("波兰语", "pol", "pl"),
        new("旁遮普语", "pan", "pa"), new("罗马尼亚语", "ron", "ro"), new("塞尔维亚语", "srp", "sr"),
        new("绍纳语", "sna", "sn"), new("信德语", "snd", "sd"), new("僧伽罗语", "sin", "si"),
        new("斯洛伐克语", "slk", "sk"), new("斯洛文尼亚语", "slv", "sl"), new("索马里语", "som", "so"),
        new("斯瓦希里语", "swa", "sw"), new("瑞典语", "swe", "sv"), new("泰米尔语", "tam", "ta"),
        new("塔吉克语", "tgk", "tg"), new("泰卢固语", "tel", "te"), new("泰语", "tha", "th"),
        new("土耳其语", "tur", "tr"), new("乌克兰语", "ukr", "uk"), new("翁本杜语", "umb"),
        new("乌尔都语", "urd", "ur"), new("乌兹别克语", "uzb", "uz"), new("越南语", "vie", "vi"),
        new("威尔士语", "cym", "cy"), new("沃洛夫语", "wol", "wo"), new("科萨语", "xho", "xh"),
        new("祖鲁语", "zul", "zu")
    ];
    public static string[] Names { get; } = ["自动识别", .. Supported.Select(language => language.Name)];

    public static SpeechLanguage? Resolve(string value)
    {
        value = value.Trim();
        if (value is "" or "自动识别") return null;
        value = value switch { "日文" => "日语", "英文" => "英语", "普通话" or "cmn" => "中文", "韩文" => "韩语", _ => value };
        // ja-JP / zh-CN 等旧输入转为语言代码；不把地区标识发送给 ASR。
        var code = value.Split('-')[0];
        return Supported.FirstOrDefault(language => language.Name == value ||
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language.TwoLetterCode, code, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("源语言无效，请选择支持的语言，或输入对应的 ISO 语言代码。");
    }

    public static string AsrCode(SpeechLanguage? language, string? preferredCode = null)
    {
        if (language is null) return "";
        // 保留已有合法两位/三位代码，维持已付费分片的缓存身份。
        if (preferredCode == language.Code || preferredCode == language.TwoLetterCode ||
            (preferredCode == "cmn" && language.Code == "zho")) return preferredCode!;
        return language.Code;
    }
}
