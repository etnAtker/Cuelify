using System.Globalization;
using System.Text.Json;
using Cuelify.Core.Media;

namespace Cuelify.Infrastructure.Media;

public sealed class FfmpegAudioProcessor(ICommandRunner runner, string ffmpegPath, string ffprobePath) : IAudioProcessor
{
    public const string PreparationVersion = "pcm16k-mono-timeline-v1";
    public async Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new(ffprobePath,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", inputPath], TimeSpan.FromMinutes(1)), null, cancellationToken);
        EnsureSuccess(result, "媒体探测");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        var audio = root.GetProperty("streams").EnumerateArray().FirstOrDefault(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
        if (audio.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("输入文件没有可用音轨。");
        var seconds = Duration(root.GetProperty("format"));
        if (seconds <= 0) seconds = Duration(audio);
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 36000)
            throw new InvalidDataException("媒体时长无效或超过 10 小时上限。");
        return new(TimeSpan.FromSeconds(seconds), audio.GetProperty("index").GetInt32());
    }

    public async Task<PreparedAudio> PrepareAsync(string inputPath, MediaInfo media, string pcmPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pcmPath))!);
        var temporary = pcmPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
            {
                var duration = Seconds(media.Duration);
                // first_pts=0 补齐延迟音轨的开头，apad/atrim 保留媒体尾部时间轴。
                var result = await runner.RunAsync(new(ffmpegPath,
                    ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", $"0:{media.AudioStreamIndex}", "-vn",
                     "-af", $"aresample=16000:async=1:first_pts=0,apad=whole_dur={duration},atrim=duration={duration}",
                     "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"], TimeSpan.FromMinutes(30)), output, cancellationToken);
                EnsureSuccess(result, "音频准备");
                await output.FlushAsync(cancellationToken);
            }
            var length = new FileInfo(temporary).Length;
            if (length == 0 || length % 2 != 0) throw new InvalidDataException("FFmpeg 返回空或不完整 PCM 数据。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pcmPath, overwrite: true);
            return new(pcmPath, TimeSpan.FromTicks(length / 2 * ChunkPlannerOptions.TicksPerSample));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task ExportChunkAsync(PreparedAudio audio, AudioChunk chunk, string outputPath, CancellationToken cancellationToken)
    {
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var result = await runner.RunAsync(new(ffmpegPath,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", "16000", "-ac", "1", "-i", audio.PcmPath,
                 "-ss", Seconds(chunk.Start), "-t", Seconds(chunk.Duration), "-c:a", "pcm_s16le", "-f", "wav", temporary],
                TimeSpan.FromMinutes(5)), null, cancellationToken);
            EnsureSuccess(result, $"导出分片 {chunk.Index}");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static double Duration(JsonElement element) => element.TryGetProperty("duration", out var duration) &&
        double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.########", CultureInfo.InvariantCulture);
    private static void EnsureSuccess(CommandResult result, string stage)
    {
        if (result.ExitCode != 0) throw new IOException($"FFmpeg {stage}失败，退出码 {result.ExitCode}：{result.StandardError}");
    }
}
