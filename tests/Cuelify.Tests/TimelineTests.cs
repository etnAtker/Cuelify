using Cuelify.Core.Media;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Xunit;

namespace Cuelify.Tests;

public sealed class TimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static WordToken W(string text, double start, double end, string? speaker = null) => new(text, S(start), S(end), speaker);

    [Theory]
    [InlineData(10)] [InlineData(300)] [InlineData(310)] [InlineData(3600)]
    public void ContinuousOrUndetectedSpeechHasNoHolesOrOversizedChunks(int seconds)
    {
        var chunks = new AudioChunkPlanner().Plan(S(seconds), [], new());
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.Equal(S(seconds), chunks[^1].End);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Duration.TotalSeconds, .0000625, 300));
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].Start > chunks[i - 1].Start);
            Assert.Equal(S(.25), chunks[i - 1].End - chunks[i].Start);
        }
    }

    [Fact]
    public void LongestNearbySilenceIsChosenWithoutRemovingIt()
    {
        var chunks = new AudioChunkPlanner().Plan(S(400), [new(S(0), S(175)), new(S(185), S(400))], new());
        Assert.Equal(S(180), chunks[0].End);
        Assert.Equal(chunks[0].End, chunks[1].Start);
        Assert.False(chunks[0].ForcedEnd);
    }

    [Fact]
    public void TenSecondTailAndByteLimitArePreserved()
    {
        var options = new ChunkPlannerOptions { MaximumDuration = S(180), ForcedOverlap = TimeSpan.Zero,
            MaximumUploadBytes = 4096 + 180 * 32000 };
        var chunks = new AudioChunkPlanner().Plan(S(190), [new(S(0), S(190))], options);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(S(10), chunks[1].Duration);
        Assert.All(chunks, chunk => Assert.True(chunk.Duration.TotalSeconds * 32000 + 4096 <= options.MaximumUploadBytes));
    }

    [Fact]
    public void InvalidOverlapAndVadIntervalsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new AudioChunkPlanner().Plan(S(2), [], new() { MaximumDuration = S(.2) }));
        Assert.Throws<InvalidDataException>(() => new AudioChunkPlanner().Plan(S(2), [new(S(0), S(3))], new()));
    }

    [Fact]
    public void LocalTimesBecomeGlobalAndOnlyOverlapDuplicatesAreRemoved()
    {
        var first = new AudioChunk(0, S(0), S(180.25), true);
        var second = new AudioChunk(1, S(180), S(190), false);
        var merged = new WordTimelineMerger().Merge([
            new(first, new("en", [W("yes", 180.05, 180.2)])),
            new(second, new("en", [W("yes", .05, .2), W("yes", 1.25, 1.5)]))]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(S(181.25), merged[1].Start);
    }

    [Theory]
    [InlineData(-1, 1)] [InlineData(1, 1)] [InlineData(1.5, 1)] [InlineData(0, 2.2)]
    public void InvalidLocalTimesFail(double start, double end) => Assert.Throws<InvalidDataException>(() =>
        WordTimelineMerger.ToGlobal(new(new(0, S(0), S(2), false), new("en", [W("word", start, end)]))));

    [Fact]
    public void SmallServiceEndRoundingIsClampedToChunkEnd()
    {
        var words = WordTimelineMerger.ToGlobal(new(new(0, S(10), S(12), false), new("en", [W("word", 1.9, 2.02)])));
        Assert.Equal(S(12), words[0].End);
    }

    [Fact]
    public void IdenticalServiceTokensAreDeduplicatedButDifferentSpeakersAreKept()
    {
        var words = WordTimelineMerger.ToGlobal(new(new(0, S(0), S(2), false),
            new("en", [W("yes", 0, .5, "a"), W("yes", 0, .5, "a"), W("yes", 0, .5, "b")])));
        Assert.Equal(2, words.Count);
    }

    [Fact]
    public void ForcedOverlapPreservesDifferentSimultaneousSpeakers()
    {
        var words = new WordTimelineMerger().Merge([
            new(new(0, S(0), S(2), true), new("en", [W("yes", 1.8, 2, "a")])),
            new(new(1, S(1.75), S(3), false), new("en", [W("yes", .05, .25, "b")]))]);
        Assert.Equal(2, words.Count);
    }

    [Fact]
    public void SpeakerChangePreservesOverlappingDialogueAndUnicode()
    {
        var cues = new CueBuilder().Build([W("你好", 0, .5, "a"), W("世界。", .5, 1.1, "a"), W("はい。", 1, 1.5, "b")]);
        Assert.Equal(2, cues.Count);
        Assert.Equal("你好世界。", cues[0].SourceText);
        Assert.Equal(S(1), cues[1].Start);
        Assert.Contains("はい。", SrtSerializer.SerializeSource(cues));
    }

    [Fact]
    public void EnglishPunctuationAndNumbersStayTogetherUntilPause()
    {
        var cues = new CueBuilder().Build([W("Cost", 0, .2), W("12.5", .2, .5), W("USD.", .5, .9), W("Again", 2, 2.5)]);
        Assert.Equal("Cost 12.5 USD.", cues[0].SourceText);
        Assert.Equal(2, cues.Count);
        Assert.Equal("cue-000002", cues[1].Id);
    }

    [Fact]
    public void SrtFormatsHourAndCleansBlockBreakingText()
    {
        var text = SrtSerializer.SerializeSource([new("id", S(3601.25), S(3602.999), "中文\0\n\n第二行")]);
        Assert.Equal("1\r\n01:00:01,250 --> 01:00:02,999\r\n中文\r\n第二行\r\n\r\n", text);
    }

    [Theory]
    [InlineData("", 0, 1)] [InlineData("text", -1, 1)] [InlineData("text", 1, 1)] [InlineData("text", 1, 1.0001)]
    public void InvalidSrtCueFails(string text, double start, double end) => Assert.Throws<InvalidDataException>(() =>
        SrtSerializer.SerializeSource([new("id", S(start), S(end), text)]));
}
