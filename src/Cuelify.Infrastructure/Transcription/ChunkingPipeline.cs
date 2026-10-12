using Cuelify.Core.Media;
using Cuelify.Core.Projects;
using Cuelify.Core.Speech;
using Cuelify.Infrastructure.Media;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Transcription;

public sealed record PreparedChunk(AudioChunk Chunk, string Path, long Bytes, string Sha256);
public sealed record ChunkingResult(string JobId, TimeSpan Duration, string AudioSha256, string PcmPath, IReadOnlyList<PreparedChunk> Files)
{
    public string? SourceSha256 { get; init; }
    public string Identity => AtomicFile.Hash(new { AudioSha256, Duration, Chunks = Files.Select(f => new { f.Chunk, f.Bytes, f.Sha256 }) });
}

public sealed class ChunkingPipeline(IAudioProcessor audio, IVoiceActivityDetector vad, string vadSignature, string cacheRoot)
{
    public async Task<ChunkingResult> RunAsync(string inputPath, ChunkPlannerOptions options, string? workDirectory = null,
        IProgress<TranscriptionProgress>? progress = null, CancellationToken token = default, bool restart = false)
    {
        options.Validate(); inputPath = Path.GetFullPath(inputPath);
        IProgress<StageWorkProgress> Observe(string stage) => new InlineProgress<StageWorkProgress>(value =>
            progress?.Report(new(stage) { WorkProgress = value }));
        progress?.Report(new("SourceChecking"));
        await using var inputGuard = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var input = new FileInfo(inputPath);
        var fingerprint = new { Path = inputPath, input.Length, ModifiedUtc = input.LastWriteTimeUtc,
            Sha256 = await AtomicFile.HashFileAsync(inputPath, token, Observe("SourceChecking")), FfmpegAudioProcessor.PreparationVersion };
        var jobId = AtomicFile.Hash(fingerprint);
        var shared = Path.Combine(cacheRoot, jobId); Directory.CreateDirectory(shared);
        await using var jobLock = new FileStream(Path.Combine(shared, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var directory = workDirectory ?? shared; Directory.CreateDirectory(directory);
        await AtomicFile.WriteJsonAsync(Path.Combine(shared, "input.json"), fingerprint, token);
        progress?.Report(new("AudioPreparing"));
        var pcmPath = Path.Combine(shared, "audio.pcm");
        var preparation = await AtomicFile.ReadJsonAsync<AudioCache>(Path.Combine(shared, "audio.json"), token);
        PreparedAudio prepared;
        if (preparation is not null && File.Exists(pcmPath) && new FileInfo(pcmPath).Length == preparation.Bytes &&
            await AtomicFile.HashFileAsync(pcmPath, token, Observe("AudioChecking")) == preparation.Sha256)
            prepared = new(pcmPath, preparation.Duration);
        else
        {
            var media = await audio.ProbeAsync(inputPath, token);
            prepared = await audio.PrepareAsync(inputPath, media, pcmPath, token, Observe("AudioPreparing"));
            preparation = new(prepared.Duration, new FileInfo(pcmPath).Length, await AtomicFile.HashFileAsync(pcmPath, token, Observe("AudioChecking")));
            await AtomicFile.WriteJsonAsync(Path.Combine(shared, "audio.json"), preparation, token);
        }
        var vadKey = AtomicFile.Hash(new { preparation.Sha256, vadSignature });
        var vadPath = Path.Combine(directory, $"vad-{vadKey}.json");
        var detected = await AtomicFile.ReadJsonAsync<VadCache>(vadPath, token);
        if (detected is null && !restart && directory != shared)
            detected = await AtomicFile.ReadJsonAsync<VadCache>(Path.Combine(shared, $"vad-{vadKey}.json"), token);
        if (detected is null)
        {
            progress?.Report(new("VadAnalyzing"));
            await using var pcm = File.OpenRead(pcmPath);
            detected = new(await vad.DetectAsync(pcm, token, Observe("VadAnalyzing")));
            await AtomicFile.WriteJsonAsync(vadPath, detected, CancellationToken.None);
        }
        progress?.Report(new("ChunkPlanning"));
        var planner = options;
        IReadOnlyList<AudioChunk> chunks = [];
        List<PreparedChunk> files = [];
        for (var round = 0; round < 4; round++)
        {
            chunks = new AudioChunkPlanner().Plan(prepared.Duration, detected.Regions, planner);
            var chunkDirectory = Path.Combine(directory, "chunks-" + AtomicFile.Hash(chunks)); Directory.CreateDirectory(chunkDirectory);
            files = [];
            progress?.Report(new("ChunkPlanning") { WorkProgress = new(0, chunks.Count) { Unit = "片" } });
            AudioChunk? oversized = null; long oversizedBytes = 0;
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(chunkDirectory, $"chunk-{chunk.Index:000000}.wav");
                var metadataPath = path + ".json";
                var cached = await AtomicFile.ReadJsonAsync<PreparedChunk>(metadataPath, token);
                if (cached is not null && cached.Chunk == chunk && await ValidAsync(cached with { Path = path }, token))
                { files.Add(cached with { Path = path }); progress?.Report(new("ChunkPlanning") { WorkProgress = new(files.Count, chunks.Count) { Unit = "片" } }); continue; }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await audio.ExportChunkAsync(prepared, chunk, temporary, token);
                    var bytes = new FileInfo(temporary).Length;
                    if (bytes == 0) throw new InvalidDataException($"分片 {chunk.Index + 1} 文件为空。");
                    if (bytes > planner.MaximumUploadBytes) { oversized = chunk; oversizedBytes = bytes; break; }
                    var hash = await AtomicFile.HashFileAsync(temporary, token);
                    File.Move(temporary, path, true);
                    var exported = new PreparedChunk(chunk, path, bytes, hash);
                    await AtomicFile.WriteJsonAsync(metadataPath, exported, CancellationToken.None);
                    files.Add(exported);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                progress?.Report(new("ChunkPlanning", chunk.Index + 1, chunks.Count) { WorkProgress = new(files.Count, chunks.Count) { Unit = "片" } });
            }
            if (oversized is null) break;
            if (round == 3) throw new InvalidDataException("缩短分片后仍超过上传大小上限，请检查音频编码配置。");
            var shorter = TimeSpan.FromTicks((long)(oversized.Duration.Ticks * .8 * planner.MaximumUploadBytes / oversizedBytes));
            planner = planner with { MaximumDuration = shorter };
        }
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "chunks.json"), chunks, token);
        var result = new ChunkingResult(jobId, prepared.Duration, preparation.Sha256, pcmPath, files) { SourceSha256 = fingerprint.Sha256 };
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "chunking.json"), result, CancellationToken.None);
        return result;
    }
    public static async Task<bool> ValidAsync(PreparedChunk file, CancellationToken token) => File.Exists(file.Path) &&
        new FileInfo(file.Path).Length == file.Bytes && await AtomicFile.HashFileAsync(file.Path, token) == file.Sha256;
    private sealed record AudioCache(TimeSpan Duration, long Bytes, string Sha256);
    private sealed record VadCache(IReadOnlyList<SpeechRegion> Regions);
}
