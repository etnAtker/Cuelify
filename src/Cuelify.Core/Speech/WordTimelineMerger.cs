using Cuelify.Core.Media;

namespace Cuelify.Core.Speech;

public sealed record ChunkTranscript(AudioChunk Chunk, AsrTranscript Transcript);

public sealed class WordTimelineMerger
{
    public IReadOnlyList<WordToken> Merge(IReadOnlyList<ChunkTranscript> chunks)
    {
        var result = new List<(WordToken Word, int ChunkIndex)>();
        AudioChunk? previous = null;
        foreach (var item in chunks.OrderBy(item => item.Chunk.Index))
        {
            var global = ToGlobal(item);
            foreach (var word in global)
            {
                var duplicate = -1;
                if (previous is not null && item.Chunk.Start < previous.End && word.Start < previous.End)
                {
                    for (var index = result.Count - 1; index >= 0; index--)
                    {
                        var candidate = result[index];
                        if (candidate.ChunkIndex != previous.Index || candidate.Word.End <= item.Chunk.Start) continue;
                        if (IsBoundaryDuplicate(candidate.Word, word)) { duplicate = index; break; }
                    }
                }
                if (duplicate < 0) result.Add((word, item.Chunk.Index));
                else
                {
                    var existing = result[duplicate];
                    result[duplicate] = (existing.Word with { Start = Min(existing.Word.Start, word.Start), End = Max(existing.Word.End, word.End) }, existing.ChunkIndex);
                }
            }
            previous = item.Chunk;
        }
        return result.OrderBy(item => item.Word.Start).Select(item => item.Word).ToArray();
    }

    public static IReadOnlyList<WordToken> ToGlobal(ChunkTranscript item)
    {
        if (item.Chunk.Index < 0 || item.Chunk.Start < TimeSpan.Zero || item.Chunk.Duration <= TimeSpan.Zero || item.Transcript.Words is null)
            throw new InvalidDataException("分片时间戳或转写数据无效。");
        var result = new List<WordToken>();
        var seen = new HashSet<WordToken>();
        foreach (var word in item.Transcript.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.Start < TimeSpan.Zero || word.End <= word.Start ||
                word.Start >= item.Chunk.Duration || word.End > item.Chunk.Duration + TimeSpan.FromMilliseconds(100))
                throw new InvalidDataException($"分片 {item.Chunk.Index} 返回了不合法的词时间戳。");
            if (seen.Add(word)) result.Add(word with { Start = item.Chunk.Start + word.Start, End = item.Chunk.Start + Min(word.End, item.Chunk.Duration) });
        }
        return result;
    }

    private static bool IsBoundaryDuplicate(WordToken left, WordToken right)
    {
        if (!string.Equals(left.Text.Trim().Normalize(), right.Text.Trim().Normalize(), StringComparison.OrdinalIgnoreCase) ||
            (left.SpeakerId is not null && right.SpeakerId is not null && left.SpeakerId != right.SpeakerId)) return false;
        var intersection = Min(left.End, right.End) - Max(left.Start, right.Start);
        var shorter = Min(left.End - left.Start, right.End - right.Start);
        return intersection > TimeSpan.Zero && intersection.Ticks >= shorter.Ticks / 2 &&
            Math.Abs((left.Start + (left.End - left.Start) / 2 - right.Start - (right.End - right.Start) / 2).TotalMilliseconds) <= 200;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
