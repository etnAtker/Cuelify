using System.Text.RegularExpressions;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed record VulkanEvidence(string[] DeviceLines, int OffloadedLayers, int TotalLayers, string[] BufferLines)
{
    public bool HasGpuOffload => DeviceLines.Length > 0 && OffloadedLayers > 0 && TotalLayers >= OffloadedLayers && BufferLines.Length > 0;

    public static VulkanEvidence Parse(string log)
    {
        var lines = log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var devices = lines.Where(line => Regex.IsMatch(line,
            @"(?:ggml_vulkan:\s*\d+\s*=\s*.+|llama_prepare_model_devices:\s*using device Vulkan\d+\s*\(.+\))",
            RegexOptions.CultureInvariant)).Distinct().ToArray();
        var offloads = Regex.Matches(log, @"offloaded\s+(\d+)/(\d+)\s+layers to GPU", RegexOptions.CultureInvariant);
        var offload = offloads.LastOrDefault();
        var buffers = lines.Where(line => Regex.IsMatch(line, @"Vulkan\d+\s+model buffer size\s*=\s*[1-9]\d*(?:\.\d+)?\s+MiB", RegexOptions.CultureInvariant)).Distinct().ToArray();
        return new VulkanEvidence(devices,
            offload is null ? 0 : int.Parse(offload.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            offload is null ? 0 : int.Parse(offload.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), buffers);
    }

}
