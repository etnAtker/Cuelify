using System.Diagnostics;
using Cuelify.Core.Media;
using Cuelify.Infrastructure.Media;
using Xunit;

namespace Cuelify.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cuelify.Tests", Guid.NewGuid().ToString("N"), "中文 空格");
    public TestDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cuelify.Tests")) + System.IO.Path.DirectorySeparatorChar;
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理路径越界。");
        Directory.Delete(resolved, recursive: true);
    }
}

public sealed class NativeMediaTests
{
    internal static readonly ProcessCommandRunner Runner = new();
    internal static FfmpegAudioProcessor Processor() => new(Runner, "ffmpeg.exe", "ffprobe.exe");
    internal static async Task<string> Fixture(TestDirectory directory, string extension = "wav", double duration = 2)
    {
        var path = directory.File("媒体 输入." + extension);
        var durationText = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        if (extension == "mp4") arguments.AddRange(["-f", "lavfi", "-i", $"color=c=black:s=16x16:r=10:d={durationText}"]);
        arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", durationText]);
        if (extension == "mp4") arguments.AddRange(["-c:v", "mpeg4", "-c:a", "aac"]);
        arguments.Add(path);
        var result = await Runner.RunAsync(new("ffmpeg.exe", arguments, TimeSpan.FromSeconds(15)), null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    [Theory]
    [InlineData("wav")] [InlineData("mp3")] [InlineData("mp4")]
    public async Task NativeFfmpegProbesPreparesAndExportsWithUnicodePath(string extension)
    {
        using var directory = new TestDirectory();
        var input = await Fixture(directory, extension);
        var processor = Processor();
        var media = await processor.ProbeAsync(input, CancellationToken.None);
        var audio = await processor.PrepareAsync(input, media, directory.File("audio.pcm"), CancellationToken.None);
        Assert.InRange(audio.Duration.TotalSeconds, 1.99, 2.2);
        var bytes = await File.ReadAllBytesAsync(audio.PcmPath);
        Assert.Contains(bytes, value => value >= 128); // signed PCM 不能经过 UTF-8 解码。
        Assert.Equal(audio.Duration.Ticks / 625 * 2, bytes.Length);
        var chunk = new AudioChunk(0, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.5), false);
        await processor.ExportChunkAsync(audio, chunk, directory.File("chunk.wav"), CancellationToken.None);
        var chunkMedia = await processor.ProbeAsync(directory.File("chunk.wav"), CancellationToken.None);
        Assert.Equal(1, chunkMedia.Duration.TotalSeconds, 3);
    }

    [Fact]
    public async Task DelayedAudioTrackKeepsVideoTimeline()
    {
        using var directory = new TestDirectory();
        var path = directory.File("延迟音轨.mp4");
        var result = await Runner.RunAsync(new("ffmpeg.exe",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=16x16:r=10:d=3", "-itsoffset", "1", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000:duration=1", "-c:v", "mpeg4", "-c:a", "aac", path], TimeSpan.FromSeconds(15)), null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var processor = Processor();
        var audio = await processor.PrepareAsync(path, await processor.ProbeAsync(path, CancellationToken.None), directory.File("audio.pcm"), CancellationToken.None);
        Assert.Equal(3, audio.Duration.TotalSeconds, 3);
        var pcm = await File.ReadAllBytesAsync(audio.PcmPath);
        Assert.All(pcm.Take(28000), value => Assert.Equal(0, value));
        Assert.Contains(pcm.Skip(35000).Take(20000), value => value != 0);
    }

    [Fact]
    public async Task CommandCancellationTerminatesChildProcessTree()
    {
        using var directory = new TestDirectory();
        var pidPath = directory.File("child.pid");
        var script = directory.File("child.ps1");
        await File.WriteAllTextAsync(script, "param([string]$PidFile)\n$p = Start-Process -WindowStyle Hidden -FilePath powershell.exe -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 120' -PassThru\n[System.IO.File]::WriteAllText($PidFile + '.tmp', [string]$p.Id)\nMove-Item -LiteralPath ($PidFile + '.tmp') -Destination $PidFile\nStart-Sleep -Seconds 120\n");
        using var cancellation = new CancellationTokenSource();
        var run = Runner.RunAsync(new("powershell.exe", ["-NoProfile", "-NonInteractive", "-File", script, pidPath], TimeSpan.FromSeconds(20)), null, cancellation.Token);
        try
        {
            for (var index = 0; index < 250 && !File.Exists(pidPath); index++) await Task.Delay(20);
            Assert.True(File.Exists(pidPath));
            var childId = int.Parse((await File.ReadAllTextAsync(pidPath)).Trim());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            try
            {
                using var child = Process.GetProcessById(childId);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
            }
            catch (ArgumentException) { /* 已退出并移出进程表。 */ }
        }
        finally
        {
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CommandTimeoutIsDistinctFromUserCancellation() => await Assert.ThrowsAsync<TimeoutException>(() =>
        Runner.RunAsync(new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromMilliseconds(200)), null, CancellationToken.None));

    private sealed class FailedSink : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException(new IOException("目标写入失败"));
    }

    [Fact]
    public async Task BinaryConsumerFailureDoesNotWaitForCommandTimeout()
    {
        using var sink = new FailedSink();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => Runner.RunAsync(new("ffmpeg.exe",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-f", "s16le", "pipe:1"], TimeSpan.FromSeconds(30)), sink, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
