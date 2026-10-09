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
        var request = new PromptBuilder().Build(PromptPresets.Cloud, new(), [cue], []);
        var user = request.Messages[^1].Content;
        var json = user[(user.LastIndexOf('\n') + 1)..];
        Assert.Equal(cue.SourceText, JsonSerializer.Deserialize<Dictionary<string, string>>(json)![cue.Id]);
        Assert.Contains("翻译为中文", request.Messages[0].Content);
    }

    [Fact]
    public void LocalDefaultHasNoSystemMessageAndContextIsReadOnly()
    {
        var local = new PromptBuilder().Build(PromptPresets.Local, new(), [Cue()], [Cue("old", "Previous") with { TranslatedText = "上文" }]);
        Assert.Single(local.Messages);
        Assert.Equal("user", local.Messages[0].Role);
        var cloud = new PromptBuilder().Build(PromptPresets.Cloud, new(), [Cue()], [Cue("old", "Previous") with { TranslatedText = "上文" }]);
        Assert.Contains("Previous", cloud.Messages[^1].Content);
        var output = AlignmentValidator.Parse("{\"old\":\"多余\",\"cue-1\":\"你好\"}", [Cue()], TranslationOutputFormat.CueIdJson);
        Assert.Empty(output.Translations);
    }

    [Theory]
    [InlineData("{unknown} {cues_json}")]
    [InlineData("Only context {context_before}")]
    public void UnknownOrMissingSourceVariableIsRejected(string user) => Assert.Throws<ArgumentException>(() =>
        PromptBuilder.Validate(PromptPresets.Cloud with { UserTemplate = user }));

    [Fact]
    public async Task EditedProfileCanBeSavedAndRestored()
    {
        using var directory = new TestDirectory();
        var profile = PromptPresets.Concise with { Name = "我的预设", UserTemplate = "风格：{target_style}\n{context_before}\n{cues_json}" };
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
    public async Task LocalOptionsCannotEnableCpuOnlyOrArbitraryModel()
    {
        Assert.Throws<ArgumentException>(() => new HyMt2TranslationEngine(new() { GpuLayers = 0 }));
        Assert.Throws<ArgumentException>(() => new HyMt2TranslationEngine(new() { MaximumTokens = 4096 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => HyMt2ModelOptions.VerifyIdentityAsync("other.gguf", CancellationToken.None));
    }
}
