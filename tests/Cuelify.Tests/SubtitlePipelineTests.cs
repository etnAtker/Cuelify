using System.Text.Json;
using Cuelify.Core.Speech;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Xunit;

namespace Cuelify.Tests;

public sealed class SubtitlePipelineTests
{
    private static WordToken[] Words(int count = 8) => Enumerable.Range(0, count).Select(i =>
        new WordToken($"台词{i}。", TimeSpan.FromSeconds(i * 6), TimeSpan.FromSeconds(i * 6 + 5), null)).ToArray();

    [Fact]
    public void BoundariesCannotLoseRepeatReorderWordsOrCrossMaximumDuration()
    {
        var words = Words(); var pipe = SubtitlePipelinePresets.TwoStage;
        var block = PipelinePromptBuilder.Plan(words, pipe)[0];
        Assert.Equal(4, block.Last);
        var parsed = PipelinePromptBuilder.Parse("{\"ends\":[0,1,2,3,4]}", words, block, pipe);
        Assert.Equal(5, parsed.Count);
        foreach (var invalid in new[] { "{\"ends\":[0,2,3,4]}", "{\"ends\":[0,1,1,2,3,4]}", "{\"ends\":[0,1,2,3]}", "{\"ends\":[4]}", "{\"ends\":[0,1,2,3,4],\"extra\":1}" })
            Assert.Throws<InvalidDataException>(() => PipelinePromptBuilder.Parse(invalid, words, block, pipe));
        Assert.Throws<InvalidDataException>(() => PipelinePromptBuilder.Plan([new("一整段没有切点", TimeSpan.Zero, TimeSpan.FromSeconds(9), null)], pipe));
    }

    [Fact]
    public void ZeroDurationTextRetainsWordReferencesAndDisplayDoesNotOverlap()
    {
        WordToken[] words = [new("你好", TimeSpan.Zero, TimeSpan.FromSeconds(1), null), new("。", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null),
            new("嗯。", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.1), null)];
        var segments = new (SourceWordRange, string?)[] { (new(0, 1), "你好"), (new(2, 2), "嗯") };
        var (cues, ranges) = PipelineDisplay.Build(words, segments, SubtitlePipelinePresets.TwoStage, TimeSpan.FromSeconds(4));
        Assert.Equal("你好。嗯。", string.Concat(cues.Select(c => c.SourceText)));
        Assert.Equal(new SourceWordRange(0, 1), ranges[0]); Assert.Equal(TimeSpan.FromSeconds(2.7), cues[1].End);
        Assert.True(cues[0].End <= cues[1].Start); Assert.Equal(TimeSpan.FromSeconds(2.1), words[^1].End);
        _ = SrtSerializer.SerializeTranslated(cues);
    }

    [Theory]
    [InlineData(SubtitlePipelineMode.SegmentThenTranslate)]
    [InlineData(SubtitlePipelineMode.JointSegmentationTranslation)]
    public async Task StagesResumeFromValidatedCacheAndRenameDoesNotRegenerate(SubtitlePipelineMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "Cuelify-pipeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pipe = SubtitlePipelinePresets.TwoStage with { Mode = mode };
            var words = Words(); var engine = new Engine(mode);
            var service = new SubtitlePipelineOrchestrator(engine, root);
            var first = await service.GenerateAsync(words, [], TimeSpan.FromSeconds(48), pipe, new());
            Assert.Equal(2, first.EngineCalls); Assert.Equal(8, first.Cues.Count);
            Assert.Equal(string.Concat(words.Select(w => w.Text)), string.Concat(first.Cues.Select(c => c.SourceText)));
            Assert.Equal(new SourceWordRange(0, 0), first.Ranges[0]);
            Assert.All(first.Cues, c => Assert.Equal(mode == SubtitlePipelineMode.JointSegmentationTranslation, c.TranslatedText is not null));
            var second = await service.GenerateAsync(words, [], TimeSpan.FromSeconds(48), pipe with { Name = "改名", Id = "new-id", Description = "备注" }, new());
            Assert.Equal(0, second.EngineCalls); Assert.Equal(2, second.CacheHits);
            var changed = await service.GenerateAsync(words, [], TimeSpan.FromSeconds(48), pipe with
                { Segmentation = pipe.Segmentation with { Parameters = new() { Temperature = .2 } } }, new());
            Assert.Equal(2, changed.EngineCalls); Assert.Equal(.2, engine.LastRequest!.Parameters.Temperature);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirectTranslationDoesNotAskModelToGenerateBoundaries()
    {
        var engine = new Engine(SubtitlePipelineMode.DirectTranslation);
        SubtitleCue[] cues = [new("old", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.")];
        var result = await new SubtitlePipelineOrchestrator(engine, "unused").GenerateAsync(Words(), cues, TimeSpan.FromSeconds(48), SubtitlePipelinePresets.Direct, new());
        Assert.Same(cues, result.Cues); Assert.Null(engine.LastRequest); Assert.Equal(0, result.EngineCalls);
    }

    [Fact]
    public async Task CancellationKeepsSuccessfulBlocksForNextRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cuelify-pipeline-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        try
        {
            var pipe = SubtitlePipelinePresets.TwoStage with { BlockSeconds = 5 };
            var engine = new Engine(pipe.Mode) { CancelOnCall = 2, Cancellation = cancel };
            var service = new SubtitlePipelineOrchestrator(engine, root);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(Words(4), [], TimeSpan.FromSeconds(24), pipe, new() { Concurrency = 1 }, token: cancel.Token));
            engine.CancelOnCall = 0;
            var resumed = await service.GenerateAsync(Words(4), [], TimeSpan.FromSeconds(24), pipe, new() { Concurrency = 1 });
            Assert.Equal(1, resumed.CacheHits); Assert.Equal(3, resumed.EngineCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task JointGeneratedCuesCanBeRetranslatedWithoutChangingUnselectedCues()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cuelify-pipeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var wrapped = PipelineDisplay.Wrap(new string('字', 43) + "。", 22);
            SubtitleCue[] previous = [new("a", TimeSpan.Zero, TimeSpan.FromSeconds(1), "A", wrapped), new("b", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "B", "旧译文B")];
            var engine = new TranslationEngine();
            var result = await new TranslationOrchestrator(engine, root).TranslateAsync(previous, PromptPresets.SingleSimple with { MaximumTextCharacters = 44 }, new(),
                new HashSet<string> { "b" }, previousCues: previous);
            Assert.True(result.IsComplete); Assert.Equal(wrapped, result.Cues[0].TranslatedText); Assert.Equal("新译文", result.Cues[1].TranslatedText);
            Assert.Equal(previous[1].Start, result.Cues[1].Start); Assert.Equal(previous[1].End, result.Cues[1].End); Assert.Single(engine.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WrappingBalancesLinesAndDoesNotOrphanTrailingPunctuation()
    {
        var lines = PipelineDisplay.Wrap(new string('字', 22) + "，", 22).Split('\n');
        Assert.Equal(2, lines.Length); Assert.All(lines, line => Assert.InRange(line.Length, 2, 22));
        Assert.All(lines, line => Assert.Contains('字', line));
        Assert.Equal(23, PipelineDisplay.CharacterCount(string.Join('\n', lines)));
    }

    [Fact]
    public async Task InvalidStageGetsSpecificFeedbackAndSuccessfulRetryIsCached()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cuelify-feedback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new CorrectingEngine(); var service = new SubtitlePipelineOrchestrator(engine, root);
            var result = await service.GenerateAsync(Words(2), [], TimeSpan.FromSeconds(12), SubtitlePipelinePresets.TwoStage, new() { RetryDelay = TimeSpan.Zero });
            Assert.Equal(2, result.EngineCalls); Assert.Equal(2, result.Cues.Count);
            Assert.Equal(0, (await service.GenerateAsync(Words(2), [], TimeSpan.FromSeconds(12), SubtitlePipelinePresets.TwoStage, new())).EngineCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SpeakerChangesAndLongSilenceAreMandatoryEvenWithoutPunctuation()
    {
        WordToken[] words = [new("你好", TimeSpan.Zero, TimeSpan.FromSeconds(1), "a"), new("是的", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "b"),
            new("再见", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "b")];
        var profile = SubtitlePipelinePresets.TwoStage;
        var block = PipelinePromptBuilder.Plan(words, profile)[0];
        Assert.Equal(3, PipelinePromptBuilder.Parse("{\"ends\":[0,1,2]}", words, block, profile).Count);
        Assert.Throws<InvalidDataException>(() => PipelinePromptBuilder.Parse("{\"ends\":[2]}", words, block, profile));
    }

    private sealed class CorrectingEngine : ITranslationEngine
    {
        private int _calls;
        public string CacheIdentity => "correcting-fixture";
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            if (++_calls == 1) return Task.FromResult(new TranslationResponse("{\"ends\":[1]}"));
            Assert.Contains("分句起点 0 不能选择末词 1", request.Messages[^1].Content);
            return Task.FromResult(new TranslationResponse("{\"ends\":[0,1]}"));
        }
    }

    [Fact]
    public async Task JointRepairKeepsAcceptedTargetsAndOnlyRequestsMissingCue()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cuelify-joint-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            SubtitleCue[] cues = [new("a", TimeSpan.Zero, TimeSpan.FromSeconds(1), "A", "联合译文"), new("b", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "B")];
            var engine = new TranslationEngine(); var service = new TranslationOrchestrator(engine, root);
            var result = await service.TranslateAsync(cues, PromptPresets.SingleSimple, new(), previousCues: cues);
            Assert.True(result.IsComplete); Assert.Equal("联合译文", result.Cues[0].TranslatedText);
            Assert.Equal("b", Assert.Single(Assert.Single(engine.Requests).Cues).Id);
            var resumed = await service.TranslateAsync(cues, PromptPresets.SingleSimple, new(), previousCues: cues);
            Assert.Equal(0, resumed.EngineCalls); Assert.Equal(result.Cues, resumed.Cues);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TranslationEngine : ITranslationEngine
    {
        public string CacheIdentity => "translation-fixture";
        public List<TranslationRequest> Requests { get; } = [];
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(new TranslationResponse("新译文")); }
    }
    private sealed class Engine(SubtitlePipelineMode mode) : ITranslationEngine
    {
        public string CacheIdentity => "pipeline-fixture";
        public TranslationRequest? LastRequest;
        public int CancelOnCall, Calls;
        public CancellationTokenSource? Cancellation;
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            if (++Calls == CancelOnCall) { Cancellation!.Cancel(); throw new OperationCanceledException(cancellationToken); }
            LastRequest = request; Assert.NotNull(request.Grammar);
            var text = request.Messages.Last().Content;
            using var json = JsonDocument.Parse(text[(text.LastIndexOf("待处理词：", StringComparison.Ordinal) + 5)..]);
            var ids = json.RootElement.EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).ToArray();
            return Task.FromResult(new TranslationResponse(mode == SubtitlePipelineMode.JointSegmentationTranslation
                ? JsonSerializer.Serialize(new { segments = ids.Select(id => new { end = id, text = "测试译文" }) })
                : JsonSerializer.Serialize(new { ends = ids })));
        }
    }
}
