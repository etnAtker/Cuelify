using System.Collections.Concurrent;
using Cuelify.Core.Media;
using Cuelify.Core.Projects;
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

public sealed record TranscriptionProgress(string Stage, int? ChunkIndex = null, int? TotalChunks = null, ProcessingState? State = null)
{
    public StageWorkProgress? WorkProgress { get; init; }
}
public sealed record TranscriptionResult(string JobId, TimeSpan Duration, IReadOnlyList<AudioChunk> Chunks,
    IReadOnlyList<WordToken> Words, IReadOnlyList<SubtitleCue> Cues, int AsrRequests, int CacheHits, string OutputPath);
public sealed class TranscriptionFailedException(IReadOnlyList<int> failedChunks) : Exception(
    $"ASR 分片失败：{string.Join(", ", failedChunks)}。成功分片已保存，可从缓存重试。")
{
    public IReadOnlyList<int> FailedChunks { get; } = failedChunks;
    public IReadOnlyDictionary<int, string> Categories { get; init; } = new Dictionary<int, string>();
}

// 仅编排原文识别；翻译参数不会进入音频/VAD/ASR 缓存键。
public sealed class TranscriptionPipeline(IAudioProcessor audioProcessor, IVoiceActivityDetector? vad, IAsrClient asr,
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
        var prepared = await new ChunkingPipeline(audioProcessor, vad ?? throw new InvalidOperationException("请先提供 VAD 检测器或使用已有分片。"), vadSignature, _cacheRoot).RunAsync(inputPath,
            options.Chunks with { MaximumUploadBytes = Math.Min(options.Chunks.MaximumUploadBytes, asrOptions.MaximumUploadBytes) },
            progress: progress, token: cancellationToken);
        var result = await TranscribePreparedAsync(prepared, options, progress: progress, cancellationToken: cancellationToken);
        var cues = new CueBuilder().Build(result.Words, options.Cues);
        await AtomicFile.WriteTextAsync(outputPath, SrtSerializer.SerializeSource(cues), overwrite, cancellationToken);
        result = result with { Cues = cues };
        return result with { OutputPath = outputPath };
    }

    public async Task<TranscriptionResult> TranscribePreparedAsync(ChunkingResult prepared, TranscriptionOptions? options = null,
        string? workDirectory = null, IReadOnlySet<int>? retryChunks = null, IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default, bool reuseShared = true)
    {
        options ??= new(); asrOptions.Validate();
        if (options.Concurrency is < 1 or > 8 || options.MaximumAttempts is < 1 or > 5) throw new ArgumentException("识别并发或重试次数无效。");
        _ = new CueBuilder().Build([], options.Cues);
        var jobId = prepared.JobId;
        var directory = workDirectory ?? Path.Combine(_cacheRoot, jobId); Directory.CreateDirectory(directory);
        await using var jobLock = new FileStream(Path.Combine(directory, "asr.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var statusPath = Path.Combine(directory, "status.json");
        var chunks = prepared.Files.Select(f => f.Chunk).ToArray();
        int requests = 0, hits = 0;
        var failures = new ConcurrentDictionary<int, string>();
        var transcripts = new ConcurrentDictionary<int, ChunkTranscript>();
        try
        {
            await Status("Transcribing");
            using var concurrency = new SemaphoreSlim(options.Concurrency);
            await Task.WhenAll(chunks.Select(async chunk =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    progress?.Report(new("Transcribing", chunk.Index, chunks.Length));
                    var cacheKey = AtomicFile.Hash(new { Sha256 = prepared.AudioSha256, chunk.Start, chunk.End, asrCacheNamespace,
                        asrOptions.ModelId, asrOptions.LanguageCode, asrOptions.FileFormat, Protocol = "scribe-words-v1" });
                    var path = Path.Combine(directory, $"asr-{cacheKey}.json");
                    var cached = await AtomicFile.ReadJsonAsync<AsrTranscript>(path, cancellationToken);
                    if (cached is null && reuseShared && workDirectory is not null)
                        cached = await AtomicFile.ReadJsonAsync<AsrTranscript>(Path.Combine(_cacheRoot, jobId, $"asr-{cacheKey}.json"), cancellationToken);
                    if (cached is not null)
                    {
                        _ = WordTimelineMerger.ToGlobal(new(chunk, cached));
                        await AtomicFile.WriteJsonAsync(path, cached, CancellationToken.None);
                        transcripts[chunk.Index] = new(chunk, cached);
                        Interlocked.Increment(ref hits);
                        progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.Completed));
                        return;
                    }
                    if (retryChunks is not null && !retryChunks.Contains(chunk.Index))
                    { failures[chunk.Index] = "本次未选择该未完成分片"; progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.NotStarted)); return; }
                    var file = prepared.Files.Single(f => f.Chunk.Index == chunk.Index);
                    if (!await ChunkingPipeline.ValidAsync(file, cancellationToken))
                        throw new InvalidDataException($"分片 {chunk.Index + 1} 文件缺失或校验失败，请重新分片。");
                    progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.Running));
                    for (var attempt = 1; attempt <= options.MaximumAttempts; attempt++)
                    {
                        try
                        {
                            Interlocked.Increment(ref requests);
                            var transcript = await asr.TranscribeAsync(file.Path, cancellationToken,
                                new InlineProgress<string>(stage => progress?.Report(new(stage, chunk.Index, chunks.Length))));
                            _ = WordTimelineMerger.ToGlobal(new(chunk, transcript));
                            // 已收齐且通过校验的付费结果，即使此时用户取消，也先原子保存。
                            await AtomicFile.WriteJsonAsync(path, transcript, CancellationToken.None);
                            transcripts[chunk.Index] = new(chunk, transcript);
                            progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.Completed));
                            return;
                        }
                        catch (Exception exception) when (CanRetry(exception, cancellationToken) && attempt < options.MaximumAttempts)
                        {
                            var delay = TimeSpan.FromTicks((long)Math.Min(options.MaximumRetryDelay.Ticks,
                                options.InitialRetryDelay.Ticks * Math.Pow(2, attempt - 1)));
                            if (exception is AsrServiceException { RetryAfter: { } retryAfter } && retryAfter > delay) delay = retryAfter;
                            if (delay > options.MaximumRetryDelay) throw; // 不违背服务 Retry-After 提前重试。
                            progress?.Report(new("RetryWaiting", chunk.Index, chunks.Length));
                            await Task.Delay(delay, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.Cancelled)); throw; }
                catch (Exception exception)
                { failures[chunk.Index] = FailureCategory(exception); progress?.Report(new("Transcribing", chunk.Index, chunks.Length, ProcessingState.Failed)); }
                finally { concurrency.Release(); }
            }));
            if (!failures.IsEmpty) throw new TranscriptionFailedException(failures.Keys.Order().ToArray()) { Categories = new Dictionary<int, string>(failures) };
            cancellationToken.ThrowIfCancellationRequested();
            var words = new WordTimelineMerger().Merge(transcripts.Values.ToArray());
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "words.json"), words, cancellationToken);
            var result = new TranscriptionResult(jobId, prepared.Duration, chunks, words, [], requests, hits, "");
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), result, CancellationToken.None);
            await Status("Completed");
            return result;
        }
        catch (Exception exception)
        {
            if (!transcripts.IsEmpty)
            {
                var partialWords = new WordTimelineMerger().Merge(transcripts.Values.ToArray());
                await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"), new TranscriptionResult(jobId, prepared.Duration,
                    chunks, partialWords, [], requests, hits, ""), CancellationToken.None);
            }
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
}
