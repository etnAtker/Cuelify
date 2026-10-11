using System.Net;
using Cuelify.Core.Media;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Transcription;
using Xunit;

namespace Cuelify.Tests;

public sealed class IndependentTranscriptionTests
{
    private sealed class CountingAudio : IAudioProcessor
    {
        private readonly IAudioProcessor _inner = NativeMediaTests.Processor();
        public int Exports;
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken token) => _inner.ProbeAsync(path, token);
        public Task<PreparedAudio> PrepareAsync(string path, MediaInfo media, string output, CancellationToken token) => _inner.PrepareAsync(path, media, output, token);
        public Task ExportChunkAsync(PreparedAudio audio, AudioChunk chunk, string path, CancellationToken token)
        { Exports++; return _inner.ExportChunkAsync(audio, chunk, path, token); }
    }
    private static ChunkPlannerOptions Planner => new() { TargetDuration = TimeSpan.FromSeconds(1), MaximumDuration = TimeSpan.FromSeconds(1),
        ForcedOverlap = TimeSpan.Zero, SearchRadius = TimeSpan.Zero, AlwaysChunk = true };
    [Fact]
    public async Task FailedRecognitionUsesPreparedChunksWithoutRepeatingVadOrExportAndWorksWithoutSourceMedia()
    {
        using var directory = new TestDirectory(); var input = await NativeMediaTests.Fixture(directory, duration: 3);
        var audio = new CountingAudio(); var vad = new TranscriptionPipelineTests.FakeVad(); var asr = new TranscriptionPipelineTests.FakeAsr();
        var preparation = new ChunkingPipeline(audio, vad, "vad", directory.File("cache"));
        var chunks = await preparation.RunAsync(input, Planner, directory.File("chunks"));
        var pipeline = new TranscriptionPipeline(audio, vad, asr, "vad", new ElevenLabsAsrOptions(), directory.File("cache"));
        asr.Behavior = (index, _, _) => index == 1 ? Task.FromException<Cuelify.Core.Speech.AsrTranscript>(new AsrServiceException(HttpStatusCode.Unauthorized, null)) :
            Task.FromResult(new Cuelify.Core.Speech.AsrTranscript("en", [new("text", TimeSpan.Zero, TimeSpan.FromSeconds(.5), null)]));
        await Assert.ThrowsAsync<TranscriptionFailedException>(() => pipeline.TranscribePreparedAsync(chunks, new() { Concurrency = 1 }, directory.File("asr")));
        var partial = await Cuelify.Infrastructure.Storage.AtomicFile.ReadJsonAsync<TranscriptionResult>(directory.File("asr/result.json"), CancellationToken.None);
        Assert.NotNull(partial); Assert.Equal(2, partial.Words.Count); Assert.Empty(partial.Cues);
        asr.Behavior = null; File.Delete(input);
        var result = await pipeline.TranscribePreparedAsync(chunks, new() { Concurrency = 1 }, directory.File("asr"), new HashSet<int> { 1 });
        Assert.Equal(1, result.AsrRequests); Assert.Equal(2, result.CacheHits);
        Assert.Equal(1, vad.Calls); Assert.Equal(3, audio.Exports);
    }
    [Fact]
    public async Task ExplicitRestartUsesNewAsrResultsWhileKeepingPreviousPaidResults()
    {
        using var directory = new TestDirectory(); var input = await NativeMediaTests.Fixture(directory, duration: 2);
        var audio = new CountingAudio(); var vad = new TranscriptionPipelineTests.FakeVad(); var asr = new TranscriptionPipelineTests.FakeAsr();
        var chunks = await new ChunkingPipeline(audio, vad, "vad", directory.File("cache")).RunAsync(input, Planner);
        var pipeline = new TranscriptionPipeline(audio, vad, asr, "vad", new(), directory.File("cache"));
        var first = await pipeline.TranscribePreparedAsync(chunks);
        var restarted = await pipeline.TranscribePreparedAsync(chunks, workDirectory: directory.File("restart"), reuseShared: false);
        Assert.Equal(2, first.AsrRequests); Assert.Equal(2, restarted.AsrRequests); Assert.Equal(0, restarted.CacheHits);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory.File("cache"), chunks.JobId), "asr-*.json").Length);
        Assert.Equal(2, audio.Exports);
    }
    [Fact]
    public async Task PreparedWavFilesAreVerifiedAndReusedAndNewChunkingRevisionRerunsVad()
    {
        using var directory = new TestDirectory(); var input = await NativeMediaTests.Fixture(directory, duration: 2);
        var audio = new CountingAudio(); var vad = new TranscriptionPipelineTests.FakeVad();
        var pipeline = new ChunkingPipeline(audio, vad, "vad", directory.File("cache"));
        var first = await pipeline.RunAsync(input, Planner, directory.File("one"));
        var resumed = await pipeline.RunAsync(input, Planner, directory.File("one"));
        Assert.Equal(first.Identity, resumed.Identity); Assert.Equal(2, audio.Exports); Assert.Equal(1, vad.Calls);
        await File.WriteAllTextAsync(first.Files[0].Path, "damaged");
        await pipeline.RunAsync(input, Planner, directory.File("one")); Assert.Equal(3, audio.Exports);
        await pipeline.RunAsync(input, Planner, directory.File("two"), restart: true);
        Assert.Equal(2, vad.Calls); Assert.Equal(5, audio.Exports);
    }
}
