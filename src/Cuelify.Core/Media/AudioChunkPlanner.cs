using Cuelify.Core.Speech;

namespace Cuelify.Core.Media;

public sealed class AudioChunkPlanner
{
    public IReadOnlyList<AudioChunk> Plan(TimeSpan duration, IReadOnlyList<SpeechRegion> speech, ChunkPlannerOptions options)
    {
        options.Validate();
        if (duration <= TimeSpan.Zero) throw new ArgumentException("音频时长必须大于零。");
        var samples = duration.Ticks / ChunkPlannerOptions.TicksPerSample;
        if (samples == 0) throw new ArgumentException("音频不足一个 PCM 样本。");
        var hard = Math.Min(options.MaximumDuration.Ticks / ChunkPlannerOptions.TicksPerSample,
            (options.MaximumUploadBytes - ChunkPlannerOptions.WaveHeaderBudget) / 2);
        var target = Math.Min(hard, Math.Max(1, options.TargetDuration.Ticks / ChunkPlannerOptions.TicksPerSample));
        var radius = options.SearchRadius.Ticks / ChunkPlannerOptions.TicksPerSample;
        var overlap = options.ForcedOverlap.Ticks / ChunkPlannerOptions.TicksPerSample;
        var gaps = FindGaps(duration, speech);
        var result = new List<AudioChunk>();
        long start = 0;
        while (start < samples)
        {
            var remaining = samples - start;
            if (remaining <= hard && (!options.AlwaysChunk || remaining <= target))
            {
                result.Add(new(result.Count, ToTime(start), ToTime(samples), false));
                break;
            }
            var desired = start + target;
            var lower = Math.Max(start + overlap + 1, desired - radius);
            var upper = Math.Min(start + hard, Math.Min(samples, desired + radius));
            var candidates = gaps.Select(gap => new
            {
                Length = gap.End - gap.Start,
                Left = Math.Max(lower, gap.Start),
                Right = Math.Min(upper, gap.End)
            }).Where(gap => gap.Right > gap.Left && gap.Length >= options.MinimumSilence.Ticks / ChunkPlannerOptions.TicksPerSample)
              .Select(gap => new { gap.Length, Cut = gap.Left + (gap.Right - gap.Left) / 2 })
              .OrderByDescending(gap => gap.Length).ThenBy(gap => Math.Abs(gap.Cut - desired)).ToArray();
            var forced = candidates.Length == 0;
            var end = forced ? Math.Min(start + hard, samples) : candidates[0].Cut;
            result.Add(new(result.Count, ToTime(start), ToTime(end), forced && end < samples));
            if (end == samples) break;
            start = forced ? end - overlap : end;
        }
        return result;
    }

    private static List<(long Start, long End)> FindGaps(TimeSpan duration, IReadOnlyList<SpeechRegion> speech)
    {
        var gaps = new List<(long, long)>();
        // 空检测并不能证明整段静音，按连续语音保守切分，仍上传完整时间轴。
        if (speech.Count == 0) return gaps;
        long previousEnd = 0;
        foreach (var region in speech.OrderBy(region => region.Start))
        {
            if (region.Start < TimeSpan.Zero || region.End <= region.Start || region.End > duration)
                throw new InvalidDataException("VAD 语音区间不合法或超出音频时长。");
            var start = region.Start.Ticks / ChunkPlannerOptions.TicksPerSample;
            var end = region.End.Ticks / ChunkPlannerOptions.TicksPerSample;
            if (start > previousEnd) gaps.Add((previousEnd, start));
            previousEnd = Math.Max(previousEnd, end);
        }
        var total = duration.Ticks / ChunkPlannerOptions.TicksPerSample;
        if (previousEnd < total) gaps.Add((previousEnd, total));
        return gaps;
    }

    private static TimeSpan ToTime(long samples) => TimeSpan.FromTicks(checked(samples * ChunkPlannerOptions.TicksPerSample));
}
