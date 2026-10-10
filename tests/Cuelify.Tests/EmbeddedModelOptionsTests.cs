using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class EmbeddedModelOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsCpuOnlyLayers(int layers) =>
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { GpuLayers = layers }.Validate());

    [Theory]
    [InlineData(128, 64)]
    [InlineData(2048, 0)]
    [InlineData(2048, 2048)]
    public void RejectsInvalidContextOrOutputBudget(uint contextSize, int maximumTokens) =>
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { ContextSize = contextSize, MaximumTokens = maximumTokens }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void RejectsInvalidTimeout(int seconds) =>
        Assert.Throws<ArgumentException>(() => new EmbeddedModelOptions { InferenceTimeout = TimeSpan.FromSeconds(seconds) }.Validate());

    [Fact]
    public void PreservesUnicodeAndSpacesInPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "字幕 模型", EmbeddedModelCatalog.Default.DefaultFileName);
        var options = new EmbeddedModelOptions { ModelPath = path };
        options.Validate();
        Assert.Equal(path, options.ModelPath);
    }

    [Fact]
    public void RejectsMissingModelFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), EmbeddedModelCatalog.Default.DefaultFileName);
        Assert.Throws<FileNotFoundException>(() => ModelFileVersion.Read(path));
    }

    [Fact]
    public async Task FileVersionAcceptsAnyContentAndDoesNotReadLockedWeights()
    {
        using var directory = new TestDirectory();
        var other = directory.File("other.gguf");
        var officialName = directory.File(EmbeddedModelCatalog.Default.DefaultFileName);
        await File.WriteAllTextAsync(other, "损坏的模型文件");
        await File.WriteAllTextAsync(officialName, "损坏的模型文件");
        using var locked = new FileStream(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(locked.Length, ModelFileVersion.Read(other).Length);
        Assert.Equal(ModelFileVersion.Read(other).Length, ModelFileVersion.Read(officialName).Length);
    }
}
