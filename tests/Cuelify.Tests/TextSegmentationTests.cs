using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Speech;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class TextSegmentationTests
{
    private static readonly WordToken[] Words = [new("挨拶。", TimeSpan.Zero, TimeSpan.FromSeconds(1), null),
        new("麦", TimeSpan.FromSeconds(4.9), TimeSpan.FromSeconds(5.1), null),
        new("茶", TimeSpan.FromSeconds(5.1), TimeSpan.FromSeconds(5.2), null),
        new("です。", TimeSpan.FromSeconds(5.2), TimeSpan.FromSeconds(5.5), null)];
    private static readonly SubtitlePipelineProfile Profile = SubtitlePipelinePresets.TwoStage with { BlockSeconds = 5 };

    [Fact]
    public void SourceTextsMapExactNonzeroWordRangesAndKeepAllLegalBoundaries()
    {
        var block = PipelinePromptBuilder.CreateBlock(Words, 1, 3, Profile);
        Assert.Equal(new[] { new SourceWordRange(1, 3) }, block.OutputContract!.ParseRanges("{\"texts\":[\"麦茶です。\"]}"));
        Assert.Equal(3, block.OutputContract.ParseRanges("{\"texts\":[\"麦\",\"茶\",\"です。\"]}").Count);
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"麦茶、です。\"]}"));
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"茶麦です。\"]}"));
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"麦茶で\",\"す。\"]}"));
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"麦茶\"]}"));
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"麦茶です。\",\"麦茶です。\"]}"));
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"麦茶です。\"],\"extra\":1}"));
        var grammar = OutputGrammarCompiler.Compile(block.OutputContract);
        Assert.Contains("s1 ::=", grammar); Assert.Contains("s2 ::=", grammar); Assert.Contains("s3 ::=", grammar);
        Assert.Contains("麦茶です。", JsonSerializer.Deserialize<string>(grammar.Split('\n').Single(line => line.StartsWith("s1 ::=", StringComparison.Ordinal)).Split(" | ")[^1])!);
    }

    [Fact]
    public void LongPauseAndSameTextRepeatsStillUseExactWordCoverageAndDuration()
    {
        WordToken[] words = [new("はい。", TimeSpan.Zero, TimeSpan.FromSeconds(1), null),
            new("はい。", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), null)];
        var block = PipelinePromptBuilder.CreateBlock(words, 0, 1, Profile);
        Assert.Equal(2, block.OutputContract!.ParseRanges("{\"texts\":[\"はい。\",\"はい。\"]}").Count);
        Assert.Throws<InvalidDataException>(() => block.OutputContract.ParseRanges("{\"texts\":[\"はい。はい。\"]}"));
    }

    [Fact]
    public async Task HeldTailRejoinsNextBlockAndProgressCountsOnlyCommittedWords()
    {
        using var directory = new TestDirectory(); var engine = new Engine(); var progress = new ProgressSink();
        var result = await new SubtitlePipelineOrchestrator(engine, directory.Path).GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile,
            new(), progress);
        Assert.Equal(new[] { "挨拶。", "麦茶です。" }, result.Cues.Select(c => c.SourceText));
        Assert.Equal(new[] { new SourceWordRange(0, 0), new SourceWordRange(1, 3) }, result.Ranges);
        Assert.Equal(2, result.EngineCalls);
        Assert.Contains(progress.Values, p => p.WorkProgress is { Completed: 1, Total: 4, PendingWords: 1 });
        Assert.Equal(100, progress.Values[^1].WorkProgress!.Percent);
        Assert.Equal(5.1, Words[1].End.TotalSeconds);
        var resumed = await new SubtitlePipelineOrchestrator(engine, directory.Path).GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile, new());
        Assert.Equal(0, resumed.EngineCalls); Assert.Equal(2, resumed.CacheHits); Assert.Equal(result.Ranges, resumed.Ranges);
    }

    [Fact]
    public async Task CancellationAfterPrefixReplaysAcceptedTailWithoutAnotherPrefixRequest()
    {
        using var directory = new TestDirectory(); using var cancellation = new CancellationTokenSource();
        var engine = new Engine { Cancel = cancellation, CancelOnCall = 2 }; var service = new SubtitlePipelineOrchestrator(engine, directory.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile,
            new(), token: cancellation.Token));
        engine.CancelOnCall = 0;
        var resumed = await service.GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile, new());
        Assert.Equal(1, resumed.CacheHits); Assert.Equal(1, resumed.EngineCalls);
        Assert.Equal("挨拶。麦茶です。", string.Concat(resumed.Cues.Select(c => c.SourceText)));
    }

    [Fact]
    public async Task CapacityReductionKeepsBoundaryProvisionalAndCachesItsPlan()
    {
        using var directory = new TestDirectory(); var engine = new Engine { ReduceFirst = true };
        var profile = Profile with { BlockSeconds = 6 }; // 初块含全部词，容量检查缩到“麦”。
        var result = await new SubtitlePipelineOrchestrator(engine, directory.Path).GenerateAsync(Words, [], TimeSpan.FromSeconds(7), profile, new());
        Assert.Equal(new[] { "挨拶。", "麦茶です。" }, result.Cues.Select(c => c.SourceText));
        Assert.Single(Directory.GetFiles(directory.Path, "*.capacity.json", SearchOption.AllDirectories));
        var cached = await new SubtitlePipelineOrchestrator(engine, directory.Path).GenerateAsync(Words, [], TimeSpan.FromSeconds(7), profile, new());
        Assert.Equal(0, cached.EngineCalls); Assert.Equal(2, cached.CacheHits);
    }

    [Fact]
    public async Task CloudRewriteHasFiniteRetriesAndFailedUnitIsTheActualRequest()
    {
        using var directory = new TestDirectory(); var engine = new Engine { Rewrite = true }; var progress = new ProgressSink();
        await Assert.ThrowsAsync<InvalidDataException>(() => new SubtitlePipelineOrchestrator(engine, directory.Path).GenerateAsync(Words, [],
            TimeSpan.FromSeconds(7), Profile, new() { MaximumAttempts = 2, RetryDelay = TimeSpan.Zero }, progress));
        Assert.Equal(2, engine.Calls);
        Assert.Single(Directory.GetFiles(directory.Path, "*.failed", SearchOption.AllDirectories));
        Assert.Contains(progress.Values, p => p.UnitStates?.Values.Contains(ProcessingState.Failed) == true);
        Assert.Contains(progress.Values, p => p.WorkProgress is { Failed: 1, Completed: 0 });
    }

    [Fact]
    public async Task FailedFinalWindowRetriesOnlyItsFailureAndKeepsCommittedPrefix()
    {
        using var directory = new TestDirectory(); var engine = new Engine { FailOnCall = 2 }; var service = new SubtitlePipelineOrchestrator(engine, directory.Path);
        var progress = new ProgressSink();
        await Assert.ThrowsAsync<IOException>(() => service.GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile, new(), progress));
        var states = new Dictionary<string, ProcessingState>();
        foreach (var value in progress.Values) foreach (var state in value.UnitStates ?? new Dictionary<string, ProcessingState>()) states[state.Key] = state.Value;
        Assert.DoesNotContain(ProcessingState.NotStarted, states.Values);
        var failed = Assert.Single(Directory.GetFiles(directory.Path, "*.failed", SearchOption.AllDirectories));
        engine.FailOnCall = 0;
        var result = await service.GenerateAsync(Words, [], TimeSpan.FromSeconds(7), Profile, new(), retryBlocks: new HashSet<string> { Path.GetFileName(failed)[..^7] });
        Assert.Equal(1, result.EngineCalls); Assert.Equal(1, result.CacheHits);
        Assert.Equal(new[] { "挨拶。", "麦茶です。" }, result.Cues.Select(c => c.SourceText));
        Assert.False(File.Exists(failed));
    }

    private sealed class ProgressSink : IProgress<TranslationProgress>
    {
        public List<TranslationProgress> Values { get; } = [];
        public void Report(TranslationProgress value) => Values.Add(value);
    }
    private sealed class Engine : ITranslationEngine
    {
        public string CacheIdentity => "source-text-fixture";
        public int Calls; public int CancelOnCall; public int FailOnCall; public CancellationTokenSource? Cancel;
        public bool ReduceFirst, Rewrite;
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            Calls++;
            if (Calls == FailOnCall) throw new IOException("测试中的最后一个请求失败。");
            if (Calls == CancelOnCall) { Cancel!.Cancel(); throw new OperationCanceledException(token); }
            var contract = request.OutputContract!;
            if (ReduceFirst && contract.FirstWord == 0 && contract.LastWord > 1) throw new PromptCapacityException();
            var texts = Rewrite ? new[] { "改写了原文" } : contract.FirstWord == 0 ? new[] { "挨拶。", "麦" } : new[] { "麦茶です。" };
            return Task.FromResult(new TranslationResponse(JsonSerializer.Serialize(new { texts })));
        }
    }
}
