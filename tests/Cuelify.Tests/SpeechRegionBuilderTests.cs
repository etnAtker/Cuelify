using Cuelify.Infrastructure.Speech;
using Xunit;

namespace Cuelify.Tests;

public sealed class SpeechRegionBuilderTests
{
    private static SpeechRegionBuilder Create() => new(new SileroVadOptions { SpeechPadding = TimeSpan.Zero });

    [Fact]
    public void SilenceAndShortNoiseProduceNoSpeech()
    {
        var builder = Create();
        for (var i = 0; i < 50; i++) builder.Add(0);
        for (var i = 0; i < 3; i++) builder.Add(0.9f);
        for (var i = 0; i < 20; i++) builder.Add(0);
        Assert.Empty(builder.Complete());
    }

    [Fact]
    public void PreservesSilenceAndPartialTailOnAbsoluteTimeline()
    {
        var builder = Create();
        for (var i = 0; i < 10; i++) builder.Add(0);
        for (var i = 0; i < 10; i++) builder.Add(0.9f);
        for (var i = 0; i < 20; i++) builder.Add(0);
        for (var i = 0; i < 10; i++) builder.Add(0.9f);
        builder.Add(0.9f, 160);
        var regions = builder.Complete();
        Assert.Equal(2, regions.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.32), regions[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.64), regions[0].End);
        Assert.Equal(TimeSpan.FromSeconds(1.28), regions[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(1.61), regions[1].End);
    }

    [Fact]
    public void HysteresisAndShortPausesDoNotSplitSpeech()
    {
        var builder = Create();
        for (var i = 0; i < 10; i++) builder.Add(0.9f);
        for (var i = 0; i < 10; i++) builder.Add(0.1f);
        for (var i = 0; i < 10; i++) builder.Add(0.4f);
        for (var i = 0; i < 10; i++) builder.Add(0.9f);
        Assert.Single(builder.Complete());
    }

    [Fact]
    public void PaddingClampsToActualDuration()
    {
        var builder = new SpeechRegionBuilder(new SileroVadOptions());
        for (var i = 0; i < 10; i++) builder.Add(0.9f);
        builder.Add(0.9f, 16);
        var region = Assert.Single(builder.Complete());
        Assert.Equal(TimeSpan.Zero, region.Start);
        Assert.Equal(TimeSpan.FromSeconds(0.321), region.End);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void RejectsInvalidProbabilities(float probability) => Assert.Throws<ArgumentException>(() => Create().Add(probability));

    [Fact]
    public void EmptyInputAndSingleUseAreExplicit()
    {
        var builder = Create();
        Assert.Empty(builder.Complete());
        Assert.Throws<InvalidOperationException>(() => builder.Add(0));
        Assert.Throws<InvalidOperationException>(() => builder.Complete());
    }
}
