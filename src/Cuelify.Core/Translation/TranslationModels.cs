using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public enum TranslationOutputFormat { CueIdJson, PlainText }
public sealed record PromptProfile(string Name, string SystemTemplate, string UserTemplate, TranslationOutputFormat OutputFormat);
public sealed record TranslationSettings
{
    public string SourceLanguage { get; init; } = "自动识别";
    public string TargetLanguage { get; init; } = "中文";
    public string TargetStyle { get; init; } = "";
    public int BatchSize { get; init; } = 12;
    public int Concurrency { get; init; } = 1;
    public int MaximumBatchCharacters { get; init; } = 6000;
    public int ContextCues { get; init; } = 5;
    public int FollowingContextCues { get; init; } = 2;
    public int MaximumContextCharacters { get; init; } = 3000;
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceLanguage) || string.IsNullOrWhiteSpace(TargetLanguage) ||
            BatchSize is < 1 or > 100 || Concurrency is < 1 or > 2 || MaximumBatchCharacters is < 100 or > 50000 ||
            ContextCues is < 0 or > 8 || FollowingContextCues is < 0 or > 8 || MaximumContextCharacters is < 0 or > 20000 || MaximumAttempts is < 1 or > 5 ||
            RetryDelay < TimeSpan.Zero || MaximumRetryDelay < RetryDelay || MaximumRetryDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentException("翻译语言、批次、上下文或重试配置无效。");
    }
}

public sealed record PromptMessage(string Role, string Content);
public sealed record TranslationPromptContext(PromptProfile Profile, TranslationSettings Settings, IReadOnlyList<SubtitleCue> Before, IReadOnlyList<SubtitleCue> After);
public sealed record TranslationRequest(IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<PromptMessage> Messages)
{
    // 裁剪参考内容后从模板重建，不能按字符切断渲染后的指令或当前字幕。
    public TranslationPromptContext? PromptContext { get; init; }
}
public sealed record TranslationResponse(string Content);
public interface ITranslationEngine
{
    TranslationOutputFormat OutputFormat { get; }
    // 仅包含非敏感配置的稳定签名；不得包含 Key。
    string CacheIdentity { get; }
    Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public sealed record AlignmentResult(IReadOnlyDictionary<string, string> Translations, IReadOnlyList<string> FailedIds);
public sealed record TranslationResult(IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<string> FailedIds, int EngineCalls, int CacheHits)
{
    public IReadOnlyDictionary<string, string> FailureReasons { get; init; } = new Dictionary<string, string>();
    public bool IsComplete => FailedIds.Count == 0;
}
