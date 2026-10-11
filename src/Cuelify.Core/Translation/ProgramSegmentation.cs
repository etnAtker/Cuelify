using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;

namespace Cuelify.Core.Translation;

public static class ProgramSegmentation
{
    public static SubtitlePipelineResult Build(IReadOnlyList<WordToken> words, TimeSpan duration,
        SubtitlePipelineProfile profile, TimeSpan? zeroDurationTolerance = null)
    {
        profile.ValidateSegmentation();
        var result = new CueBuilder().BuildWithRanges(words, new()
        {
            TargetDuration = TimeSpan.FromSeconds(Math.Min(5, profile.MaximumCueSeconds)),
            MaximumDuration = TimeSpan.FromSeconds(profile.MaximumCueSeconds),
            ZeroDurationTolerance = zeroDurationTolerance ?? TimeSpan.FromMilliseconds(500)
        });
        // 保留 CueBuilder 对零时长词的处理；只在可用静音中延长显示，不修改原始词时码。
        var cues = result.Cues.Select((cue, index) =>
        {
            var available = index + 1 < result.Cues.Count ? result.Cues[index + 1].Start : duration;
            var target = cue.Start + TimeSpan.FromSeconds(profile.MinimumCueSeconds);
            var end = target > cue.End ? new[] { target, available }.Min() : cue.End;
            return cue with { End = end > cue.End ? end : cue.End };
        }).ToArray();
        result = result with { Cues = cues }; result.ValidateSource(words.Count);
        return result;
    }
}
