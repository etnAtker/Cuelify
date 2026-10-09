namespace Cuelify.Infrastructure.Speech;

public sealed record ElevenLabsAsrOptions
{
    public string ModelId { get; init; } = "scribe_v2";
    public string? LanguageCode { get; init; }
    public string FileFormat { get; init; } = "other";
    public long MaximumUploadBytes { get; init; } = 2_900_000_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId) || MaximumUploadBytes is <= 0 or > 2_900_000_000 ||
            Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(1) || FileFormat is not ("other" or "pcm_s16le_16") ||
            (LanguageCode is not null && !System.Text.RegularExpressions.Regex.IsMatch(LanguageCode, "^[a-z]{2,3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new ArgumentException("ASR 配置无效：检查模型、语言代码、上传上限、格式和超时。");
    }
}
