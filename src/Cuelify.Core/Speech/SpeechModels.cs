namespace Cuelify.Core.Speech;

public sealed record SpeechRegion(TimeSpan Start, TimeSpan End);
public sealed record WordToken(string Text, TimeSpan Start, TimeSpan End, string? SpeakerId);
public sealed record AsrTranscript(string? LanguageCode, IReadOnlyList<WordToken> Words);

public interface IVoiceActivityDetector
{
    // 输入必须是单声道、16 kHz、有符号 16 位 little-endian PCM；保留原始时间轴。
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(Stream pcm16Khz, CancellationToken cancellationToken);
}

public interface IAsrClient
{
    // 返回分片局部时间；后续分片编排负责转换成全局时间。
    Task<AsrTranscript> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken);
}
