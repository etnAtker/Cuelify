using Cuelify.Core.Media;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class LlamaBinaryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RejectsCliOrRuntimeWithoutVulkan(bool server, bool vulkan)
    {
        using var directory = new TestDirectory(); var path = directory.File("llama-server.exe");
        await File.WriteAllTextAsync(path, "test-runtime");
        var error = await Assert.ThrowsAsync<LocalTranslationException>(() => LlamaServerBinary.InspectAsync(path, default, new Runner(server, vulkan)));
        Assert.Equal(server ? "VulkanOffload" : "ServerIncompatible", error.Category);
    }

    [Fact]
    public async Task ChecksCapabilitiesAndFingerprintsCompanionLibraries()
    {
        using var directory = new TestDirectory(); var path = directory.File("llama-server.exe");
        await File.WriteAllTextAsync(path, "test-runtime"); await File.WriteAllTextAsync(directory.File("ggml-vulkan.dll"), "first-library");
        var runner = new Runner(true, true);
        var first = await LlamaServerBinary.InspectAsync(path, default, runner);
        Assert.Equal("Vulkan0", first.Device); Assert.Equal(3, runner.Calls);
        await File.WriteAllTextAsync(directory.File("ggml-vulkan.dll"), "updated-library");
        Assert.NotEqual(first.Identity, await LlamaServerBinary.FingerprintAsync(path, default));
    }

    private sealed class Runner(bool server, bool vulkan) : ICommandRunner
    {
        public int Calls;
        public Task<CommandResult> RunAsync(CommandRequest command, Stream? output, CancellationToken token)
        {
            Calls++;
            var text = command.Arguments[0] switch
            {
                "--help" => server ? "--parallel --host --port --device --no-webui --no-kv-unified --fit --cors-origins" : "--model --device",
                "--version" => "version: synthetic-test",
                "--list-devices" => vulkan ? "Vulkan0: Synthetic GPU" : "CPU: Synthetic CPU",
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new CommandResult(0, text, ""));
        }
    }
}
