using System.Text.Json;
using Cuelify.Core.Speech;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class GenerationContractTests
{
    private static SubtitlePipelineProfile Profile => SubtitlePipelinePresets.TwoStage;
    [Fact]
    public void ModelCanChooseUnpunctuatedParticleAndShortCommaBoundaries()
    {
        WordToken[] words = [new("文", TimeSpan.Zero, TimeSpan.FromSeconds(1), "a"),
            new("の", TimeSpan.FromSeconds(1.3), TimeSpan.FromSeconds(2), "a"),
            new("続き,", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.2), "b"),
            new("本文", TimeSpan.FromSeconds(4.3), TimeSpan.FromSeconds(5), "b")];
        var profile = Profile;
        var block = PipelinePromptBuilder.Plan(words, profile).Single();
        Assert.Equal(4, PipelinePromptBuilder.Parse("{\"texts\":[\"文\",\"の\",\"続き,\",\"本文\"]}", words, block, profile).Count);
        Assert.Single(PipelinePromptBuilder.Parse("{\"texts\":[\"文の続き,本文\"]}", words, block, profile));
        Assert.Same(block.OutputContract, PipelinePromptBuilder.Build(words, block, profile, new()).OutputContract);
    }

    [Fact]
    public void LongPauseBeforePunctuationDoesNotBlockCompleteSegmentation()
    {
        WordToken[] words = [new("本", TimeSpan.FromSeconds(1328.432), TimeSpan.FromSeconds(1328.592), null),
            new("当", TimeSpan.FromSeconds(1328.592), TimeSpan.FromSeconds(1328.592), null),
            new("？", TimeSpan.FromSeconds(1339.172), TimeSpan.FromSeconds(1339.192), null),
            new("見", TimeSpan.FromSeconds(1339.192), TimeSpan.FromSeconds(1339.192), null),
            new("て", TimeSpan.FromSeconds(1339.332), TimeSpan.FromSeconds(1339.332), null),
            new("。", TimeSpan.FromSeconds(1339.332), TimeSpan.FromSeconds(1339.332), null)];
        var profile = Profile;
        var block = PipelinePromptBuilder.Plan(words, profile).Single();
        Assert.Equal([new SourceWordRange(0, 1), new SourceWordRange(2, 5)],
            PipelinePromptBuilder.Parse("{\"texts\":[\"本当\",\"？見て。\"]}", words, block, profile));
        Assert.Equal(3, PipelinePromptBuilder.Parse("{\"texts\":[\"本当\",\"？\",\"見て。\"]}", words, block, profile).Count);
        Assert.Throws<InvalidDataException>(() => PipelinePromptBuilder.Parse("{\"texts\":[\"本当？見て。\"]}", words, block, profile));
    }

    [Fact]
    public void SegmentationParserAndLocalGrammarUseTheSameLegalPaths()
    {
        var contract = new GenerationOutputContract { Kind = GenerationOutputKind.Segmentation,
            SegmentationFormat = SegmentationOutputFormat.SourceTexts, SourceWords = ["a", "b", "c", "d"],
            FirstWord = 0, LastWord = 3, AllowedEnds = new() { [0] = [0, 1], [1] = [3], [2] = [2], [3] = [3] } };
        Assert.Equal(2, contract.ParseRanges("{\"texts\":[\"a\",\"bcd\"]}").Count);
        Assert.Equal(3, contract.ParseRanges("{\"texts\":[\"ab\",\"c\",\"d\"]}").Count);
        Assert.Throws<InvalidDataException>(() => contract.ParseRanges("{\"texts\":[\"abc\",\"d\"]}"));
        Assert.Throws<InvalidDataException>(() => contract.ParseRanges("{\"segments\":[{\"end\":3,\"text\":\"译文\"}]}"));
        var grammar = OutputGrammarCompiler.Compile(contract);
        Assert.Contains("s0 ::=", grammar);
        Assert.Contains("s1 ::=", grammar);
        Assert.DoesNotContain("string", grammar);
    }

    [Fact]
    public void TranslationContractKeepsValidIdsAndRejectsEmptyControlsAndOverlength()
    {
        var contract = new GenerationOutputContract { Kind = GenerationOutputKind.Translation, CueIds = ["a", "b"], MaximumTextCharacters = 4 };
        var accepted = contract.Align("{\"a\":\"你好\",\"b\":\"超过四个字符\"}");
        Assert.Equal("你好", accepted.Translations["a"]); Assert.Equal(new[] { "b" }, accepted.FailedIds);
        Assert.False(contract.IsValidText(" ")); Assert.False(contract.IsValidText("a\u0000"));
        Assert.Empty(contract.Align("{\"a\":\"你好\",\"unknown\":\"错位\"}").Translations);
        Assert.Contains("character{1,4}", OutputGrammarCompiler.Compile(contract));
        Assert.True((contract with { Format = TranslationOutputFormat.PlainText, CueIds = ["a"] }).Align("你好").FailedIds.Count == 0);
    }

    [Fact]
    public async Task TranslationEditsDoNotInvalidateSegmentationAndMalformedCloudOutputHasFiniteRetries()
    {
        using var directory = new TestDirectory(); var engine = new Engine();
        WordToken[] words = [new("你好。", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)];
        var service = new SubtitlePipelineOrchestrator(engine, directory.Path); var pipeline = Profile;
        await service.GenerateAsync(words, TimeSpan.FromSeconds(2), pipeline, new());
        var changed = pipeline with { Translation = pipeline.Translation with { UserTemplate = "改变翻译 {cues_json}" }, LineCharacters = 30 };
        var cached = await service.GenerateAsync(words, TimeSpan.FromSeconds(2), changed, new());
        Assert.Equal(0, cached.EngineCalls); Assert.Equal(1, cached.CacheHits);
        engine.Broken = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => new SubtitlePipelineOrchestrator(engine, directory.File("other"))
            .GenerateAsync(words, TimeSpan.FromSeconds(2), pipeline, new() { MaximumAttempts = 2, RetryDelay = TimeSpan.Zero }));
        Assert.Equal(3, engine.Calls);
    }
    private sealed class Engine : ITranslationEngine
    {
        public string CacheIdentity => "contract-fixture";
        public int Calls; public bool Broken;
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal(GenerationOutputKind.Segmentation, request.OutputContract!.Kind);
            return Task.FromResult(new TranslationResponse(Broken ? "{\"texts\":[\"改写\"]}" : JsonSerializer.Serialize(new { texts = request.OutputContract.SourceWords })));
        }
    }
}
