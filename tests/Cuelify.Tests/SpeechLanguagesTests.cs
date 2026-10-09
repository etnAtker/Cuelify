using Cuelify.Infrastructure.Speech;
using Xunit;

namespace Cuelify.Tests;

public sealed class SpeechLanguagesTests
{
    [Theory]
    [InlineData("日语", "jpn")]
    [InlineData("日文", "jpn")]
    [InlineData("ja", "jpn")]
    [InlineData("JPN", "jpn")]
    [InlineData("ja-JP", "jpn")]
    [InlineData("zh-CN", "zho")]
    [InlineData("cmn", "zho")]
    [InlineData("粤语", "yue")]
    public void NamesAndLegacyCodesResolveToOfficialLanguageCodes(string input, string code) =>
        Assert.Equal(code, SpeechLanguages.Resolve(input)!.Code);

    [Fact]
    public void AutomaticLanguageOmitsAsrHintAndUnknownLanguageFailsBeforeUpload()
    {
        Assert.Equal("", SpeechLanguages.AsrCode(SpeechLanguages.Resolve("自动识别")));
        Assert.Throws<ArgumentException>(() => SpeechLanguages.Resolve("不存在的语言"));
        Assert.Equal("ja", SpeechLanguages.AsrCode(SpeechLanguages.Resolve("日语"), "ja"));
        Assert.Equal("cmn", SpeechLanguages.AsrCode(SpeechLanguages.Resolve("中文"), "cmn"));
        Assert.Equal("jpn", SpeechLanguages.AsrCode(SpeechLanguages.Resolve("日语"), "en"));
        Assert.Equal(SpeechLanguages.Supported.Count, SpeechLanguages.Supported.Select(language => language.Code).Distinct().Count());
    }
}
