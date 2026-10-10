using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class VulkanEvidenceTests
{
    // 以下均为合成日志，仅验证门槛判定，不能当作真实 GPU 证据。
    private const string Device = "ggml_vulkan: 0 = NVIDIA GeForce RTX 4070 Laptop GPU (NVIDIA)";
    private const string Buffer = "load_tensors: Vulkan0 model buffer size = 1001.50 MiB";
    private const string Offload = "load_tensors: offloaded 33/33 layers to GPU";

    [Fact]
    public void RequiresAllThreeGpuEvidenceTypes()
    {
        var evidence = VulkanEvidence.Parse(string.Join('\n', Device, Buffer, Offload));
        Assert.True(evidence.HasGpuOffload);
        Assert.Equal(33, evidence.OffloadedLayers);
        Assert.Equal(33, evidence.TotalLayers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ggml_vulkan: Found 1 Vulkan devices:")]
    [InlineData("load_tensors: offloaded 33/33 layers to GPU")]
    public void DeviceDiscoveryOrLayersAloneDoNotPass(string log) => Assert.False(VulkanEvidence.Parse(log).HasGpuOffload);

    [Theory]
    [InlineData("offloaded 0/33 layers to GPU")]
    [InlineData("offloaded 34/33 layers to GPU")]
    public void RejectsZeroOrImpossibleOffload(string offload) =>
        Assert.False(VulkanEvidence.Parse(string.Join('\n', Device, Buffer, offload)).HasGpuOffload);

    [Fact]
    public void CpuBufferDoesNotCountAsGpuAllocation() =>
        Assert.False(VulkanEvidence.Parse(string.Join('\n', Device, Offload, "CPU model buffer size = 1001.50 MiB")).HasGpuOffload);

    [Fact]
    public void UsesLatestOffloadRatherThanEarlierSuccess() =>
        Assert.False(VulkanEvidence.Parse(string.Join('\n', Device, Buffer, Offload, "offloaded 0/33 layers to GPU")).HasGpuOffload);

    [Fact]
    public void SupportsSelectedDeviceLogFromCurrentLlamaCppWithoutAcceptingDiscoveryAlone()
    {
        const string selected = "0.00.334.835 I llama_prepare_model_devices: using device Vulkan0 (NVIDIA GeForce RTX 4070 Laptop GPU) (0000:01:00.0) - 7180 MiB free";
        Assert.True(VulkanEvidence.Parse(string.Join('\n', selected, Buffer, Offload)).HasGpuOffload);
        Assert.False(VulkanEvidence.Parse(string.Join('\n', "common_param: - Vulkan0 : NVIDIA GPU", Buffer, Offload)).HasGpuOffload);
    }
}
