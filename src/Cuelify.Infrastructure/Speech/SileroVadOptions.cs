namespace Cuelify.Infrastructure.Speech;

public sealed record SileroVadOptions
{
    public float Threshold { get; init; } = 0.5f;
    public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MinimumSilence { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan SpeechPadding { get; init; } = TimeSpan.FromMilliseconds(30);

    public void Validate()
    {
        if (!float.IsFinite(Threshold) || Threshold is <= 0 or > 1 || MinimumSpeech <= TimeSpan.Zero ||
            MinimumSilence < TimeSpan.Zero || SpeechPadding < TimeSpan.Zero)
            throw new ArgumentException("VAD 阈值必须在 (0,1] 内，最短语音必须大于零，静音和补边时长不能为负。");
    }
}
