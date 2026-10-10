using System.Text.Json;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class TranslationPromptTests
{
    internal static SubtitleCue Cue(string id = "cue-1", string text = "Hello.", double start = 0) =>
        new(id, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 1), text);

    [Fact]
    public void JsonAndSourceVariablesAreReplacedOnceWithoutReinterpretingSource()
    {
        var cue = Cue(text: "\"Hello\"\n{target_language} {unknown}");
        var request = new PromptBuilder().Build(PromptPresets.BatchSubtitles, new(), [cue], []);
        var user = request.Messages[^1].Content;
        var json = user[(user.LastIndexOf('\n') + 1)..];
        Assert.Equal(cue.SourceText, JsonSerializer.Deserialize<Dictionary<string, string>>(json)![cue.Id]);
        Assert.Contains("翻译为中文", request.Messages[0].Content);
    }

    [Fact]
    public void LocalDefaultHasNoSystemMessageAndContextIsReadOnly()
    {
        var local = new PromptBuilder().Build(PromptPresets.SingleContext, new(), [Cue()], [Cue("old", "Previous") with { TranslatedText = "上文" }]);
        Assert.Single(local.Messages);
        Assert.Equal("user", local.Messages[0].Role);
        Assert.Contains("Previous", local.Messages[0].Content);
        Assert.Contains("上文", local.Messages[0].Content);
        var cloud = new PromptBuilder().Build(PromptPresets.BatchSubtitles, new(), [Cue()], [Cue("old", "Previous") with { TranslatedText = "上文" }]);
        Assert.Contains("Previous", cloud.Messages[^1].Content);
        var output = AlignmentValidator.Parse("{\"old\":\"多余\",\"cue-1\":\"你好\"}", [Cue()], TranslationOutputFormat.CueIdJson);
        Assert.Empty(output.Translations);
    }

    [Fact]
    public void SimpleLocalPresetRendersOnlyCurrentSubtitleWithoutContext()
    {
        var request = new PromptBuilder().Build(PromptPresets.SingleSimple, new(), [Cue(text: "Current")],
            [Cue("before", "Before") with { TranslatedText = "上文译文" }], [Cue("after", "After")]);
        Assert.Single(request.Messages); Assert.Equal("user", request.Messages[0].Role);
        Assert.Contains("Current", request.Messages[0].Content); Assert.Contains("翻译成中文", request.Messages[0].Content);
        Assert.DoesNotContain("Before", request.Messages[0].Content); Assert.DoesNotContain("After", request.Messages[0].Content);
        Assert.DoesNotContain("上文译文", request.Messages[0].Content);
    }

    [Theory]
    [InlineData("{unknown} {cues_json}")]
    [InlineData("Only context {context_before}")]
    public void UnknownOrMissingSourceVariableIsRejected(string user) => Assert.Throws<ArgumentException>(() =>
        PromptBuilder.Validate(PromptPresets.BatchSubtitles with { UserTemplate = user }));

    [Fact]
    public async Task EditedProfileCanBeSavedAndRestored()
    {
        using var directory = new TestDirectory();
        var profile = PromptPresets.BatchSubtitles with { Name = "我的预设", UserTemplate = "风格：{target_style}\n{context_before}\n{cues_json}" };
        await PromptProfileStore.SaveAsync(directory.File("prompt.json"), profile, CancellationToken.None);
        Assert.Equal(profile, await PromptProfileStore.LoadAsync(directory.File("prompt.json"), CancellationToken.None));
    }

    [Fact]
    public void ResponseOrderDoesNotChangeCueOrder()
    {
        var output = AlignmentValidator.Parse("{\"b\":\"再见\",\"a\":\"你好\"}", [Cue("a"), Cue("b", "Goodbye.")], TranslationOutputFormat.CueIdJson);
        Assert.Empty(output.FailedIds);
        Assert.Equal("你好", output.Translations["a"]);
    }

    [Theory]
    [InlineData("{\"cue-1\":\"你好\",\"cue-1\":\"重复\"}")]
    [InlineData("{\"cue-1\":\"你好\",\"unknown\":\"多余\"}")]
    [InlineData("{broken")]
    [InlineData("解释：{\"cue-1\":\"你好\"}")]
    [InlineData("{\"cue-1\":42}")]
    [InlineData("{\"cue-1\":\" \"}")]
    [InlineData("{\"cue-1\":\"Hello.\"}")]
    public void InvalidOutputCannotMasqueradeAsSuccessfulTranslation(string output)
    {
        var aligned = AlignmentValidator.Parse(output, [Cue()], TranslationOutputFormat.CueIdJson);
        Assert.Empty(aligned.Translations);
        Assert.Equal(["cue-1"], aligned.FailedIds);
    }

    [Fact]
    public void MissingIdOnlyRetriesMissingCueAndExactFenceIsAccepted()
    {
        var result = AlignmentValidator.Parse("```json\n{\"a\":\"你好\"}\n```", [Cue("a"), Cue("b")], TranslationOutputFormat.CueIdJson);
        Assert.Equal("你好", result.Translations["a"]);
        Assert.Equal(["b"], result.FailedIds);
    }

    [Fact]
    public void SameLanguageAndNumbersCanRemainUnchanged()
    {
        Assert.True(AlignmentValidator.IsValid("Hello.", Cue(), true));
        Assert.True(AlignmentValidator.IsValid("123", Cue(text: "123"), false));
    }

    [Fact]
    public void SrtRequiresEveryTranslationAndPreservesTimes()
    {
        Assert.Throws<InvalidDataException>(() => SrtSerializer.SerializeTranslated([Cue()]));
        var cue = Cue(start: 3601.25) with { TranslatedText = "你好。" };
        var srt = SrtSerializer.SerializeTranslated([cue]);
        Assert.Contains("01:00:01,250 --> 01:00:02,250", srt);
        Assert.Contains("你好。", srt);
        Assert.DoesNotContain("Hello", srt);
    }

    [Fact]
    public void LocalOptionsRejectCpuOnlyAndUnknownPresetButDoNotInspectModelContent()
    {
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { GpuLayers = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { MaximumTokens = 4096 }.Validate());
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { ModelId = "unknown" }.Validate());
        Assert.Throws<FileNotFoundException>(() => ModelFileVersion.Read("other.gguf"));
    }
}
