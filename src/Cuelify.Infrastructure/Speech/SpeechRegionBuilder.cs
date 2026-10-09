using Cuelify.Core.Speech;

namespace Cuelify.Infrastructure.Speech;

public sealed class SpeechRegionBuilder
{
    private readonly SileroVadOptions _options;
    private readonly List<(long Start, long End)> _regions = [];
    private long? _start;
    private long? _silenceStart;
    private long _nextSample;
    private bool _completed;
    public const int SampleRate = 16000;
    public const int WindowSamples = 512;

    public SpeechRegionBuilder(SileroVadOptions options)
    {
        options.Validate();
        _options = options;
    }

    public void Add(float probability, int validSamples = WindowSamples)
    {
        if (_completed) throw new InvalidOperationException("语音区间计算已经结束。");
        if (!float.IsFinite(probability) || probability is < 0 or > 1 || validSamples is <= 0 or > WindowSamples)
            throw new ArgumentException("概率或有效样本数无效。");
        var frameStart = _nextSample;
        _nextSample += validSamples;
        if (probability >= _options.Threshold)
        {
            _start ??= frameStart;
            _silenceStart = null;
        }
        else if (_start is not null && probability < Math.Max(0.01f, _options.Threshold - 0.15f))
        {
            _silenceStart ??= frameStart;
            if (frameStart - _silenceStart.Value >= ToSamples(_options.MinimumSilence))
            {
                AddRegion(_start.Value, _silenceStart.Value);
                _start = null;
                _silenceStart = null;
            }
        }
    }

    public IReadOnlyList<SpeechRegion> Complete()
    {
        if (_completed) throw new InvalidOperationException("语音区间计算已经结束。");
        _completed = true;
        if (_start is not null) AddRegion(_start.Value, _nextSample);
        var padded = new List<SpeechRegion>();
        var padding = ToSamples(_options.SpeechPadding);
        foreach (var (start, end) in _regions)
        {
            var region = new SpeechRegion(ToTime(Math.Max(0, start - padding)), ToTime(Math.Min(_nextSample, end + padding)));
            if (padded.Count > 0 && padded[^1].End >= region.Start)
                padded[^1] = padded[^1] with { End = region.End };
            else
                padded.Add(region);
        }
        return padded;
    }

    private void AddRegion(long start, long end)
    {
        if (end - start >= ToSamples(_options.MinimumSpeech)) _regions.Add((start, end));
    }

    private static long ToSamples(TimeSpan duration) => checked((long)(duration.TotalSeconds * SampleRate));
    private static TimeSpan ToTime(long sample) => TimeSpan.FromSeconds(sample / (double)SampleRate);
}
