using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class HyMt2ModelOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsCpuOnlyLayers(int layers) =>
        Assert.Throws<ArgumentException>(() => new HyMt2ModelOptions { GpuLayers = layers }.Validate());

    [Theory]
    [InlineData(128, 64)]
    [InlineData(2048, 0)]
    [InlineData(2048, 2048)]
    public void RejectsInvalidContextOrOutputBudget(uint contextSize, int maximumTokens) =>
        Assert.Throws<ArgumentException>(() => new HyMt2ModelOptions { ContextSize = contextSize, MaximumTokens = maximumTokens }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void RejectsInvalidTimeout(int seconds) =>
        Assert.Throws<ArgumentException>(() => new HyMt2ModelOptions { InferenceTimeout = TimeSpan.FromSeconds(seconds) }.Validate());

    [Fact]
    public void PreservesUnicodeAndSpacesInPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "字幕 模型", HyMt2ModelOptions.FileName);
        var options = new HyMt2ModelOptions { ModelPath = path };
        options.Validate();
        Assert.Equal(path, options.ModelPath);
    }

    [Fact]
    public async Task RejectsMissingModel()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), HyMt2ModelOptions.FileName);
        await Assert.ThrowsAsync<FileNotFoundException>(() => HyMt2ModelOptions.VerifyIdentityAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOtherModelsAndCorruptedWeights()
    {
        using var directory = new TestDirectory();
        var other = directory.File("other.gguf");
        var officialName = directory.File(HyMt2ModelOptions.FileName);
        await File.WriteAllTextAsync(other, "损坏的模型文件");
        await File.WriteAllTextAsync(officialName, "损坏的模型文件");
        await Assert.ThrowsAsync<InvalidDataException>(() => HyMt2ModelOptions.VerifyIdentityAsync(other, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => HyMt2ModelOptions.VerifyIdentityAsync(officialName, CancellationToken.None));
    }
}
