using System.Collections.Concurrent;
using Cuelify.Core.Media;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Infrastructure.Media;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Infrastructure.Transcription;

public sealed record TranscriptionOptions
{
    public ChunkPlannerOptions Chunks { get; init; } = new();
    public CueBuilderOptions Cues { get; init; } = new();
    public int Concurrency { get; init; } = 2;
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record TranscriptionProgress(string Stage, int? ChunkIndex = null, int? TotalChunks = null);
public sealed record TranscriptionResult(string JobId, TimeSpan Duration, IReadOnlyList<AudioChunk> Chunks,
    IReadOnlyList<WordToken> Words, IReadOnlyList<SubtitleCue> Cues, int AsrRequests, int CacheHits, string OutputPath);
public sealed class TranscriptionFailedException(IReadOnlyList<int> failedChunks) : Exception(
    $"ASR 分片失败：{string.Join(", ", failedChunks)}。成功分片已保存，可从缓存重试。")
{
    public IReadOnlyList<int> FailedChunks { get; } = failedChunks;
    public IReadOnlyDictionary<int, string> Categories { get; init; } = new Dictionary<int, string>();
}

// 仅编排原文识别；翻译参数不会进入音频/VAD/ASR 缓存键。
public sealed class TranscriptionPipeline(IAudioProcessor audioProcessor, IVoiceActivityDetector vad, IAsrClient asr,
    string vadSignature, ElevenLabsAsrOptions asrOptions, string? cacheRoot = null, string asrCacheNamespace = "elevenlabs-scribe")
{
    private readonly string _cacheRoot = cacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cuelify", "Jobs");

    public async Task<TranscriptionResult> RunAsync(string inputPath, string outputPath, TranscriptionOptions? options = null,
        bool overwrite = false, IProgress<TranscriptionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        options.Chunks.Validate();
        asrOptions.Validate();
        if (asrOptions.FileFormat != "other") throw new ArgumentException("当前流水线上传 PCM WAV，file_format 必须为 other。");
        if (options.Concurrency is < 1 or > 8 || options.MaximumAttempts is < 1 or > 5 ||
            options.InitialRetryDelay < TimeSpan.Zero || options.MaximumRetryDelay < options.InitialRetryDelay ||
            options.MaximumRetryDelay > TimeSpan.FromMinutes(5) || string.IsNullOrWhiteSpace(vadSignature) || string.IsNullOrWhiteSpace(asrCacheNamespace))
            throw new ArgumentException("并发、重试或 VAD 签名配置无效。");
        // 在付费请求之前校验输出与分句配置。
        _ = new CueBuilder().Build([], options.Cues);
        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".srt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仅支持 SRT 输出。");
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输出不能覆盖输入媒体。");
        if (!overwrite && File.Exists(outputPath)) throw new IOException("SRT 已存在，请明确选择覆盖或更换输出路径。");
        // 从指纹计算至完成期间只允许其他读取者，防止媒体在识别过程中被改写。
        await using var inputGuard = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var input = new FileInfo(inputPath);
        var fingerprint = new { Path = inputPath, input.Length, ModifiedUtc = input.LastWriteTimeUtc,
            Sha256 = await AtomicFile.HashFileAsync(inputPath, cancellationToken), FfmpegAudioProcessor.PreparationVersion };
        var jobId = AtomicFile.Hash(fingerprint);
        var directory = Path.Combine(_cacheRoot, jobId);
        Directory.CreateDirectory(directory);
        // 同一作业的多个窗口/进程不得同时发起相同付费请求。
        await using var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var statusPath = Path.Combine(directory, "status.json");
        await AtomicFile.WriteJsonAsync(Path.Combine(directory, "input.json"), fingerprint, cancellationToken);
        int requests = 0, hits = 0;
        var failures = new ConcurrentDictionary<int, string>();
        try
        {
            await Status("AudioPreparing");
            var pcmPath = Path.Combine(directory, "audio.pcm");
            var preparation = await AtomicFile.ReadJsonAsync<AudioCache>(Path.Combine(directory, "audio.json"), cancellationToken);
            PreparedAudio prepared;
            if (preparation is not null && File.Exists(pcmPath) && new FileInfo(pcmPath).Length == preparation.Bytes &&
                await AtomicFile.HashFileAsync(pcmPath, cancellationToken) == preparation.Sha256)
                prepared = new(pcmPath, preparation.Duration);
            else
            {
                var media = await audioProcessor.ProbeAsync(inputPath, cancellationToken);
                prepared = await audioProcessor.PrepareAsync(inputPath, media, pcmPath, cancellationToken);
                preparation = new(prepared.Duration, new FileInfo(pcmPath).Length, await AtomicFile.HashFileAsync(pcmPath, cancellationToken));
                await AtomicFile.WriteJsonAsync(Path.Combine(directory, "audio.json"), preparation, cancellationToken);
            }
            var vadKey = AtomicFile.Hash(new { preparation!.Sha256, vadSignature });
            var vadPath = Path.Combine(directory, $"vad-{vadKey}.json");
            var detected = await AtomicFile.ReadJsonAsync<VadCache>(vadPath, cancellationToken);
            if (detected is null)
            {
                await Status("VadAnalyzing");
                await using var pcm = File.OpenRead(pcmPath);
                detected = new(await vad.DetectAsync(pcm, cancellationToken));
                await AtomicFile.WriteJsonAsync(vadPath, detected, cancellationToken);
            }
            await Status("ChunkPlanning");
            var plannerOptions = options.Chunks with { MaximumUploadBytes = Math.Min(options.Chunks.MaximumUploadBytes, asrOptions.MaximumUploadBytes) };
            IReadOnlyList<AudioChunk> chunks = [];
            string chunkDirectory = "";
            // 实际文件过大时缩短硬上限并重导出，绝不上传/重试同一个过大请求。
            for (var round = 0; round < 4; round++)
            {
                chunks = new AudioChunkPlanner().Plan(prepared.Duration, detected.Regions, plannerOptions);
                chunkDirectory = Path.Combine(directory, "chunks-" + AtomicFile.Hash(chunks));
                Directory.CreateDirectory(chunkDirectory);
                AudioChunk? oversized = null;
                long oversizedBytes = 0;
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = ChunkPath(chunkDirectory, chunk.Index);
                    // 派生 WAV 每次重导出；不信任未完成或被外部修改的旧文件。
                    await audioProcessor.ExportChunkAsync(prepared, chunk, path, cancellationToken);
                    var bytes = new FileInfo(path).Length;
                    if (bytes == 0) throw new InvalidDataException($"分片 {chunk.Index} 文件为空。");
                    if (bytes > plannerOptions.MaximumUploadBytes) { oversized = chunk; oversizedBytes = bytes; break; }
                }
                if (oversized is null) break;
                if (round == 3) throw new InvalidDataException("缩短分片后仍超过实际上传大小上限，请检查音频编码配置。");
                var shorter = TimeSpan.FromTicks((long)(oversized.Duration.Ticks * .8 * plannerOptions.MaximumUploadBytes / oversizedBytes));
                plannerOptions = plannerOptions with { MaximumDuration = shorter };
            }
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "chunks.json"), chunks, cancellationToken);
            await Status("Transcribing");
            using var concurrency = new SemaphoreSlim(options.Concurrency);
            var transcripts = new ConcurrentDictionary<int, ChunkTranscript>();
            await Task.WhenAll(chunks.Select(async chunk =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    progress?.Report(new("Transcribing", chunk.Index, chunks.Count));
                    var cacheKey = AtomicFile.Hash(new { preparation.Sha256, chunk.Start, chunk.End, asrCacheNamespace,
                        asrOptions.ModelId, asrOptions.LanguageCode, asrOptions.FileFormat, Protocol = "scribe-words-v1" });
                    var path = Path.Combine(directory, $"asr-{cacheKey}.json");
                    var cached = await AtomicFile.ReadJsonAsync<AsrTranscript>(path, cancellationToken);
                    if (cached is not null)
                    {
                        _ = WordTimelineMerger.ToGlobal(new(chunk, cached));
                        transcripts[chunk.Index] = new(chunk, cached);
                        Interlocked.Increment(ref hits);
                        return;
                    }
                    for (var attempt = 1; attempt <= options.MaximumAttempts; attempt++)
                    {
                        try
                        {
                            Interlocked.Increment(ref requests);
                            var transcript = await asr.TranscribeAsync(ChunkPath(chunkDirectory, chunk.Index), cancellationToken);
                            _ = WordTimelineMerger.ToGlobal(new(chunk, transcript));
                            // 已收齐且通过校验的付费结果，即使此时用户取消，也先原子保存。
                            await AtomicFile.WriteJsonAsync(path, transcript, CancellationToken.None);
                            transcripts[chunk.Index] = new(chunk, transcript);
                            return;
                        }
                        catch (Exception exception) when (CanRetry(exception, cancellationToken) && attempt < options.MaximumAttempts)
                        {
                            var delay = TimeSpan.FromTicks((long)Math.Min(options.MaximumRetryDelay.Ticks,
                                options.InitialRetryDelay.Ticks * Math.Pow(2, attempt - 1)));
                            if (exception is AsrServiceException { RetryAfter: { } retryAfter } && retryAfter > delay) delay = retryAfter;
                            if (delay > options.MaximumRetryDelay) throw; // 不违背服务 Retry-After 提前重试。
                            progress?.Report(new("RetryWaiting", chunk.Index, chunks.Count));
                            await Task.Delay(delay, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { failures[chunk.Index] = FailureCategory(exception); }
                finally { concurrency.Release(); }
            }));
            if (!failures.IsEmpty) throw new TranscriptionFailedException(failures.Keys.Order().ToArray()) { Categories = new Dictionary<int, string>(failures) };
            cancellationToken.ThrowIfCancellationRequested();
            await Status("BuildingCues");
            var words = new WordTimelineMerger().Merge(transcripts.Values.ToArray());
            var cues = new CueBuilder().Build(words, options.Cues);
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "words.json"), words, cancellationToken);
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "cues.json"), cues, cancellationToken);
            await Status("Exporting");
            await AtomicFile.WriteTextAsync(outputPath, SrtSerializer.SerializeSource(cues), overwrite, cancellationToken);
            var result = new TranscriptionResult(jobId, prepared.Duration, chunks, words, cues, requests, hits, outputPath);
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
            await Status("Completed");
            return result;
        }
        catch (Exception exception)
        {
            await AtomicFile.WriteJsonAsync(statusPath, new { Stage = cancellationToken.IsCancellationRequested ? "Cancelled" : "Failed",
                Category = FailureCategory(exception), FailedChunks = failures.OrderBy(item => item.Key).ToArray(), AsrRequests = requests, CacheHits = hits }, CancellationToken.None);
            throw;
        }

        async Task Status(string stage)
        {
            await AtomicFile.WriteJsonAsync(statusPath, new { Stage = stage, AsrRequests = requests, CacheHits = hits }, cancellationToken);
            progress?.Report(new(stage));
        }
    }

    private static string ChunkPath(string directory, int index) => Path.Combine(directory, $"chunk-{index:000000}.wav");
    private static bool CanRetry(Exception exception, CancellationToken token) => !token.IsCancellationRequested &&
        (exception is AsrServiceException { IsTransient: true } or HttpRequestException or TimeoutException or OperationCanceledException);
    private static string FailureCategory(Exception exception) => exception switch
    {
        ServiceCredentialException credential => credential.Message,
        AsrServiceException service => $"HTTP {(int)service.StatusCode}",
        InvalidDataException => "数据或缓存校验失败", OperationCanceledException => "取消或超时",
        TimeoutException => "超时", HttpRequestException => "网络失败", IOException => "文件或外部命令失败",
        TranscriptionFailedException => "部分 ASR 分片失败", InvalidOperationException => "会话凭据或处理状态无效", _ => "配置或处理失败"
    };
    private sealed record AudioCache(TimeSpan Duration, long Bytes, string Sha256);
    private sealed record VadCache(IReadOnlyList<SpeechRegion> Regions);
}
