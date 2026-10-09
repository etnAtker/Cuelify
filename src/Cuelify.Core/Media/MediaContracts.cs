namespace Cuelify.Core.Media;

public sealed record CommandRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    // output 非空时按二进制流复制 stdout，不进行文本解码，也不拥有 output。
    Task<CommandResult> RunAsync(CommandRequest command, Stream? output, CancellationToken cancellationToken);
}

public sealed record MediaInfo(TimeSpan Duration, int AudioStreamIndex);
public sealed record PreparedAudio(string PcmPath, TimeSpan Duration);

public interface IAudioProcessor
{
    Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken);
    Task<PreparedAudio> PrepareAsync(string inputPath, MediaInfo media, string pcmPath, CancellationToken cancellationToken);
    Task ExportChunkAsync(PreparedAudio audio, AudioChunk chunk, string outputPath, CancellationToken cancellationToken);
}

public sealed record AudioChunk(int Index, TimeSpan Start, TimeSpan End, bool ForcedEnd)
{
    public TimeSpan Duration => End - Start;
}

public sealed record ChunkPlannerOptions
{
    public TimeSpan TargetDuration { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan SearchRadius { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ForcedOverlap { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MinimumSilence { get; init; } = TimeSpan.FromMilliseconds(400);
    public long MaximumUploadBytes { get; init; } = 2_900_000_000;
    public bool AlwaysChunk { get; init; }
    // PCM WAV 的保守头部预算，导出后仍检查实际文件大小。
    public const int WaveHeaderBudget = 4096;
    public const int BytesPerSecond = 32000;
    public const int TicksPerSample = 625;

    public void Validate()
    {
        if (TargetDuration <= TimeSpan.Zero || MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromHours(10) ||
            SearchRadius < TimeSpan.Zero || ForcedOverlap < TimeSpan.Zero || MinimumSilence <= TimeSpan.Zero ||
            MaximumUploadBytes <= WaveHeaderBudget + 2 || MaximumUploadBytes > 2_900_000_000)
            throw new ArgumentException("分片时长、静音、重叠或上传大小配置无效。");
        var hardSamples = Math.Min(MaximumDuration.Ticks / TicksPerSample, (MaximumUploadBytes - WaveHeaderBudget) / 2);
        if (hardSamples <= 1 || ForcedOverlap.Ticks / TicksPerSample >= hardSamples)
            throw new ArgumentException("强制重叠必须小于实际分片硬上限，确保分片始终推进。");
    }
}
