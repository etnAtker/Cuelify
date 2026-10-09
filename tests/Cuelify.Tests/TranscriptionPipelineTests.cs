using System.Net;
using Cuelify.Core.Media;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Xunit;

namespace Cuelify.Tests;

public sealed class TranscriptionPipelineTests
{
    private static readonly TranscriptionOptions Options = new()
    {
        Chunks = new() { TargetDuration = TimeSpan.FromSeconds(1), MaximumDuration = TimeSpan.FromSeconds(1),
            ForcedOverlap = TimeSpan.Zero, SearchRadius = TimeSpan.Zero, AlwaysChunk = true },
        Concurrency = 1, MaximumAttempts = 3, InitialRetryDelay = TimeSpan.Zero, MaximumRetryDelay = TimeSpan.FromSeconds(1)
    };

    internal sealed class FakeVad : IVoiceActivityDetector
    {
        public int Calls;
        public Task<IReadOnlyList<SpeechRegion>> DetectAsync(Stream pcm16Khz, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SpeechRegion>>([]);
        }
    }

    internal sealed class FakeAsr : IAsrClient
    {
        public int Calls;
        public Func<int, int, CancellationToken, Task<AsrTranscript>>? Behavior;
        public Task<AsrTranscript> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            var index = int.Parse(Path.GetFileNameWithoutExtension(audioFilePath).Split('-')[1]);
            return Behavior?.Invoke(index, call, cancellationToken) ?? Task.FromResult(Transcript(index));
        }
    }

    private static AsrTranscript Transcript(int index) => new("en", [new($"word{index}", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.4), null)]);
    private static TranscriptionPipeline Pipeline(TestDirectory directory, FakeVad vad, FakeAsr asr, string model = "scribe_v2") =>
        new(NativeMediaTests.Processor(), vad, asr, "fake-vad", new() { ModelId = model }, directory.File("cache"));

    [Fact]
    public async Task ZeroDurationToleranceRerendersCuesWithoutRepeatingPaidRecognition()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr { Behavior = (_, _, _) => Task.FromResult(new AsrTranscript("zho",
            [new("你", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.1), null),
             new("好", TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.9), null)])) };
        var pipeline = Pipeline(directory, new(), asr);
        var first = await pipeline.RunAsync(input, directory.File("first.srt"), Options);
        Assert.Single(first.Cues); Assert.Equal("你好", first.Cues[0].SourceText);
        var second = await pipeline.RunAsync(input, directory.File("second.srt"), Options with
            { Cues = new() { ZeroDurationTolerance = TimeSpan.FromMilliseconds(200) } });
        Assert.Equal(2, second.Cues.Count); Assert.Equal(TimeSpan.FromSeconds(.3), second.Cues[0].End);
        Assert.Equal(0, second.AsrRequests); Assert.Equal(first.Chunks.Count, second.CacheHits);
        Assert.Equal(first.Words, second.Words);
    }

    [Theory]
    [InlineData("wav")] [InlineData("mp3")] [InlineData("mp4")]
    public async Task NativeMediaToSrtWithFakeAsrSupportsCacheAndCueRerender(string extension)
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, extension, 3);
        var vad = new FakeVad();
        var asr = new FakeAsr();
        var pipeline = Pipeline(directory, vad, asr);
        var first = await pipeline.RunAsync(input, directory.File("first.srt"), Options);
        Assert.True(first.AsrRequests >= 3);
        Assert.Contains("00:00:01,100", await File.ReadAllTextAsync(first.OutputPath));
        var second = await pipeline.RunAsync(input, directory.File("second.srt"), Options with { Cues = new CueBuilderOptions { PauseThreshold = TimeSpan.FromSeconds(2) } });
        Assert.Equal(0, second.AsrRequests);
        Assert.Equal(first.Chunks.Count, second.CacheHits);
        Assert.Equal(1, vad.Calls);
        var changedModel = await Pipeline(directory, vad, asr, "other-model").RunAsync(input, directory.File("changed.srt"), Options);
        Assert.Equal(first.Chunks.Count, changedModel.AsrRequests);
        Assert.Equal(1, vad.Calls);
    }

    [Fact]
    public async Task FailedChunkResumesWithoutPayingAgainForSuccessfulChunks()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 3);
        var asr = new FakeAsr { Behavior = (index, _, _) => index == 1
            ? Task.FromException<AsrTranscript>(new AsrServiceException(HttpStatusCode.Unauthorized, null)) : Task.FromResult(Transcript(index)) };
        var pipeline = Pipeline(directory, new(), asr);
        var error = await Assert.ThrowsAsync<TranscriptionFailedException>(() => pipeline.RunAsync(input, directory.File("out.srt"), Options));
        Assert.Equal([1], error.FailedChunks);
        Assert.Equal(3, asr.Calls); // 401 不重试。
        Assert.False(File.Exists(directory.File("out.srt")));
        asr.Behavior = null;
        var recovered = await pipeline.RunAsync(input, directory.File("out.srt"), Options);
        Assert.Equal(1, recovered.AsrRequests);
        Assert.Equal(2, recovered.CacheHits);
    }

    [Fact]
    public async Task CancelAfterSuccessfulResponseStillPreservesPaidResult()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 3);
        using var cancellation = new CancellationTokenSource();
        var asr = new FakeAsr { Behavior = (index, _, _) => { cancellation.Cancel(); return Task.FromResult(Transcript(index)); } };
        var pipeline = Pipeline(directory, new(), asr);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(input, directory.File("out.srt"), Options, cancellationToken: cancellation.Token));
        Assert.Equal(1, asr.Calls);
        Assert.False(File.Exists(directory.File("out.srt")));
        asr.Behavior = null;
        var recovered = await pipeline.RunAsync(input, directory.File("out.srt"), Options);
        Assert.Equal(1, recovered.CacheHits);
        Assert.Equal(2, recovered.AsrRequests);
    }

    [Fact]
    public async Task TransientFailureRetriesWithinLimit()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr { Behavior = (index, call, _) => call == 1
            ? Task.FromException<AsrTranscript>(new AsrServiceException(HttpStatusCode.TooManyRequests, TimeSpan.Zero)) : Task.FromResult(Transcript(index)) };
        var result = await Pipeline(directory, new(), asr).RunAsync(input, directory.File("out.srt"), Options);
        Assert.Equal(2, result.AsrRequests);
    }

    [Fact]
    public async Task LongRetryAfterDoesNotRetryEarly()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr { Behavior = (_, _, _) => Task.FromException<AsrTranscript>(new AsrServiceException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10))) };
        await Assert.ThrowsAsync<TranscriptionFailedException>(() => Pipeline(directory, new(), asr).RunAsync(input, directory.File("out.srt"), Options));
        Assert.Equal(1, asr.Calls);
    }

    [Fact]
    public async Task PersistentTransientFailureStopsAtAttemptLimit()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr { Behavior = (_, _, _) => Task.FromException<AsrTranscript>(new AsrServiceException(HttpStatusCode.ServiceUnavailable, null)) };
        await Assert.ThrowsAsync<TranscriptionFailedException>(() => Pipeline(directory, new(), asr).RunAsync(input, directory.File("out.srt"), Options));
        Assert.Equal(3, asr.Calls);
    }

    [Fact]
    public async Task FakeHttpCacheCannotSatisfyRealServiceNamespace()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr();
        var fake = new TranscriptionPipeline(NativeMediaTests.Processor(), new FakeVad(), asr, "vad", new(), directory.File("cache"), "fixture-http");
        var real = new TranscriptionPipeline(NativeMediaTests.Processor(), new FakeVad(), asr, "vad", new(), directory.File("cache"), "elevenlabs-scribe");
        await fake.RunAsync(input, directory.File("fake.srt"), Options);
        var result = await real.RunAsync(input, directory.File("real.srt"), Options);
        Assert.Equal(1, result.AsrRequests);
        Assert.Equal(0, result.CacheHits);
    }

    private sealed class LargeHeaderProcessor : IAudioProcessor
    {
        private readonly IAudioProcessor _inner = NativeMediaTests.Processor();
        public int Exports;
        public Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken token) => _inner.ProbeAsync(inputPath, token);
        public Task<PreparedAudio> PrepareAsync(string inputPath, MediaInfo media, string pcmPath, CancellationToken token) => _inner.PrepareAsync(inputPath, media, pcmPath, token);
        public async Task ExportChunkAsync(PreparedAudio audio, AudioChunk chunk, string outputPath, CancellationToken token)
        {
            Exports++;
            await _inner.ExportChunkAsync(audio, chunk, outputPath, token);
            await using var stream = new FileStream(outputPath, FileMode.Append);
            await stream.WriteAsync(new byte[7000], token); // 模拟估算之外的封装开销。
        }
    }

    [Fact]
    public async Task ActualOversizedFilesAreReplannedBeforeAsr()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var processor = new LargeHeaderProcessor();
        var asr = new FakeAsr { Behavior = (index, _, _) => Task.FromResult(new AsrTranscript("en",
            [new($"w{index}", TimeSpan.FromSeconds(.001), TimeSpan.FromSeconds(.002), null)])) };
        var pipeline = new TranscriptionPipeline(processor, new FakeVad(), asr, "vad", new(), directory.File("cache"));
        var result = await pipeline.RunAsync(input, directory.File("out.srt"), Options with { Chunks = Options.Chunks with { MaximumUploadBytes = 12000 } });
        Assert.True(processor.Exports > result.Chunks.Count);
        Assert.Equal(result.Chunks.Count, asr.Calls);
        Assert.All(result.Chunks, chunk => Assert.True(chunk.Duration.TotalSeconds * 32000 + 78 + 7000 <= 12000));
    }

    [Fact]
    public async Task InvalidReturnedTimestampDoesNotEnterSuccessfulCache()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr { Behavior = (_, _, _) => Task.FromResult(new AsrTranscript("en", [new("bad", TimeSpan.FromSeconds(-1), TimeSpan.Zero, null)])) };
        await Assert.ThrowsAsync<TranscriptionFailedException>(() => Pipeline(directory, new(), asr).RunAsync(input, directory.File("out.srt"), Options));
        Assert.Equal(1, asr.Calls);
        Assert.Empty(Directory.GetFiles(directory.File("cache"), "asr-*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BadCacheFailsWithoutCallingAsrAgain()
    {
        using var directory = new TestDirectory();
        var input = await NativeMediaTests.Fixture(directory, duration: 1);
        var asr = new FakeAsr();
        var pipeline = Pipeline(directory, new(), asr);
        await pipeline.RunAsync(input, directory.File("out.srt"), Options);
        var cache = Directory.GetFiles(directory.File("cache"), "asr-*.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(cache, "{broken");
        await Assert.ThrowsAsync<TranscriptionFailedException>(() => pipeline.RunAsync(input, directory.File("again.srt"), Options));
        Assert.Equal(1, asr.Calls);
    }

    [Fact]
    public async Task ExistingOutputFailsBeforeAnyPaidWork()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(directory.File("out.srt"), "existing");
        var asr = new FakeAsr();
        await Assert.ThrowsAsync<IOException>(() => Pipeline(directory, new(), asr).RunAsync("missing.wav", directory.File("out.srt"), Options));
        Assert.Equal(0, asr.Calls);
    }

    [Fact]
    public async Task CancelledAtomicWritePreservesOldFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("cache.json");
        await AtomicFile.WriteJsonAsync(path, new { Version = 1 }, CancellationToken.None);
        var original = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteJsonAsync(path, new { Version = 2 }, cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
