using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class EmbeddedContextTests
{
    [Fact]
    public void FollowingContainsOnlySourceAndSourceVariablesAreNotReinterpreted()
    {
        var cue = TranslationPromptTests.Cue(text: "{context_after} said \"hello\".");
        var request = new PromptBuilder().Build(PromptPresets.Local, new(), [cue],
            [TranslationPromptTests.Cue("before", "before-source") with { TranslatedText = "前文译文" }],
            [TranslationPromptTests.Cue("after", "after-source") with { TranslatedText = "不应发送的后文译文" }]);
        Assert.Contains("before-source", request.Messages[0].Content); Assert.Contains("前文译文", request.Messages[0].Content);
        Assert.Contains("after-source", request.Messages[0].Content); Assert.DoesNotContain("不应发送", request.Messages[0].Content);
        Assert.Contains(cue.SourceText, request.Messages[0].Content);
    }

    [Fact]
    public void TokenBudgetRemovesDistantReferencesWithoutTruncatingCurrentCueOrInstructions()
    {
        var current = TranslationPromptTests.Cue(text: "CURRENT");
        var profile = new PromptProfile("budget", "", "INSTRUCTION {context_before} {context_after} {source_text}", TranslationOutputFormat.PlainText);
        var before = new[] { TranslationPromptTests.Cue("far", new string('a', 400)), TranslationPromptTests.Cue("near", "NEAR") };
        var after = new[] { TranslationPromptTests.Cue("after", new string('b', 400)) };
        var request = new PromptBuilder().Build(profile, new(), [current], before, after);
        var prepared = EmbeddedPromptBudget.Prepare(request, text => text.Length, messages => messages.Single().Content, 200, 50);
        Assert.Contains("CURRENT", prepared.Prompt); Assert.Contains("INSTRUCTION", prepared.Prompt); Assert.Contains("NEAR", prepared.Prompt);
        Assert.DoesNotContain(new string('a', 400), prepared.Prompt); Assert.DoesNotContain(new string('b', 400), prepared.Prompt);
        Assert.Equal(2, prepared.RemovedContextCues); Assert.True(prepared.Tokens + 50 <= 200);
        Assert.Contains(new string('a', 400), request.Messages.Single().Content);
    }

    [Fact]
    public void OversizedCurrentCueFailsInsteadOfSilentlyChangingSource()
    {
        var request = new PromptBuilder().Build(PromptPresets.Local, new(), [TranslationPromptTests.Cue(text: new string('x', 1000))], []);
        Assert.Throws<InvalidDataException>(() => EmbeddedPromptBudget.Prepare(request, text => text.Length, messages => messages.Single().Content, 512, 100));
    }

    [Fact]
    public async Task EmbeddedOrchestrationSuppliesPreviousTranslationAndFutureSourceWithStableCueIdentity()
    {
        using var directory = new TestDirectory();
        var cues = new[] { TranslationPromptTests.Cue("a", "FIRST", 0), TranslationPromptTests.Cue("b", "SECOND", 2), TranslationPromptTests.Cue("c", "THIRD", 4) };
        var engine = new Engine();
        var result = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(cues, PromptPresets.Local, new() { BatchSize = 1, Concurrency = 1 });
        Assert.True(result.IsComplete);
        var middle = engine.Requests[1]; Assert.Single(middle.Cues); Assert.Equal("b", middle.Cues[0].Id);
        Assert.Contains("FIRST", middle.Messages[0].Content); Assert.Contains("译文a", middle.Messages[0].Content); Assert.Contains("THIRD", middle.Messages[0].Content);
        Assert.Equal(cues.Select(cue => (cue.Id, cue.Start, cue.End)), result.Cues.Select(cue => (cue.Id, cue.Start, cue.End)));
        var hits = await new TranslationOrchestrator(engine, directory.File("cache")).TranslateAsync(cues, PromptPresets.Local, new() { BatchSize = 1, Concurrency = 1 });
        Assert.Equal(0, hits.EngineCalls); Assert.Equal(3, hits.CacheHits);
    }
    private sealed class Engine : ITranslationEngine
    {
        public TranslationOutputFormat OutputFormat => TranslationOutputFormat.PlainText;
        public string CacheIdentity => "embedded-context-test";
        public List<TranslationRequest> Requests { get; } = [];
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken token)
        { Requests.Add(request); return Task.FromResult(new TranslationResponse("译文" + request.Cues[0].Id)); }
    }
}
