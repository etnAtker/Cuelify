using System.Diagnostics;
using System.Text.Json;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Tests;

public sealed class LocalGpuFactAttribute : FactAttribute
{
    public LocalGpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")))
            Skip = "需显式指定 CUELIFY_LOCAL_MODEL，使用真实 GGUF 与 Vulkan；普通测试不自动下载运行包。";
    }
}

public sealed class LocalTemplateGpuFactAttribute : FactAttribute
{
    public LocalTemplateGpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")))
            Skip = "需显式指定 CUELIFY_LOCAL_MODEL 与 CUELIFY_LOCAL_SERVER，验证真实单条及批量 GPU 推理。";
    }
}

public sealed class LocalGpuAcceptanceTests
{
    [LocalTemplateGpuFact]
    public async Task LlamaRejectsDamagedModelAfterStartingProcessAndReleasesIt()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/model-loading/gpu");
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var binary = await LlamaServerBinary.InspectAsync(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")!, timeout.Token);
        using var directory = new TestDirectory(); var path = directory.File("损坏模型.gguf");
        await File.WriteAllTextAsync(path, "not a GGUF model", timeout.Token);
        var session = new LlamaServerSession(new() { ModelPath = path }, binary);
        await using (session)
        {
            var progress = new PreparationCapture();
            var error = await Assert.ThrowsAsync<LocalTranslationException>(() => session.EnsureStartedAsync(timeout.Token, progress));
            Assert.Equal("ModelLoad", error.Category);
            Assert.Contains(progress.Events, item => item.Stage == LocalPreparationStage.StartingService);
            Assert.Contains(progress.Events, item => item.Stage == LocalPreparationStage.LoadingModel);
            Assert.False(session.IsRunning); Assert.Null(session.ProcessId); Assert.NotEmpty(error.Diagnostic);
            Assert.False(await session.IsReadyAsync(timeout.Token));
            await AtomicFile.WriteTextAsync(Path.Combine(root, "damaged-model-server.log"), error.Diagnostic, true, timeout.Token);
        }
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [LocalTemplateGpuFact]
    public async Task PreparationReportsRealStagesAndReusesRunningVulkanService()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/inference-ui/gpu");
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var binary = await LlamaServerBinary.InspectAsync(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")!, timeout.Token);
        var path = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")!;
        var options = new EmbeddedModelOptions { ModelPath = path, ServerPath = binary.Path,
            FileVersion = ModelFileVersion.Read(path), Concurrency = 2 };
        var session = new LlamaServerSession(options, binary);
        await using var engine = new LocalTranslationEngine(options, binary, session);
        var progress = new PreparationCapture();
        await engine.WarmupAsync(timeout.Token, progress);
        var cold = progress.Events.ToArray();
        Assert.Equal(new[] { LocalPreparationStage.CheckingRuntime,
            LocalPreparationStage.StartingService, LocalPreparationStage.LoadingModel,
            LocalPreparationStage.CheckingService, LocalPreparationStage.Ready }, cold.Select(item => item.Stage));
        var pid = session.ProcessId; Assert.NotNull(pid); Assert.True(engine.Evidence?.HasGpuOffload);
        progress.Events.Clear(); await engine.WarmupAsync(timeout.Token, progress);
        Assert.Equal(pid, session.ProcessId); Assert.Equal(LocalPreparationStage.ReusingService, Assert.Single(progress.Events).Stage);
        var cue = TranslationPromptTests.Cue("preparation-test", "Please close the window.");
        var result = await new TranslationOrchestrator(engine, Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N")))
            .TranslateAsync([cue], PromptPresets.SingleSimple, new() { SourceLanguage = "英语", Concurrency = 2 }, cancellationToken: timeout.Token);
        Assert.True(result.IsComplete); Assert.Equal(1, result.EngineCalls); Assert.Equal(0, result.CacheHits);
        var inference = Assert.Single(engine.Inferences); Assert.True(inference.GeneratedTokens > 0); Assert.Equal("eos", inference.StopType);
        var evidence = engine.Evidence; var log = session.Diagnostic;
        await engine.DisposeAsync(); Assert.False(ProcessExists(pid!.Value));
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "preparation-evidence.json"), new
        {
            binary.Version, ColdStages = cold.Select(item => new { Stage = item.Stage.ToString(), Seconds = Stopwatch.GetElapsedTime(cold[0].Timestamp, item.Timestamp).TotalSeconds }),
            WarmStages = progress.Events.Select(item => item.Stage.ToString()), Evidence = evidence, Inference = inference, result.EngineCalls, result.CacheHits, result.Cues, ProcessReleased = true
        }, timeout.Token);
        await AtomicFile.WriteTextAsync(Path.Combine(root, "preparation-server.log"), log, true, timeout.Token);
    }
    private sealed class PreparationCapture : IProgress<LocalPreparationProgress>
    {
        public List<LocalPreparationProgress> Events { get; } = [];
        public void Report(LocalPreparationProgress value) => Events.Add(value);
    }

    [LocalTemplateGpuFact]
    public async Task IndependentTemplatesRunSingleContextAndBatchOnRealVulkan()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/prompt-refactor/gpu");
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var binary = await LlamaServerBinary.InspectAsync(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")!, timeout.Token);
        var path = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")!;
        var options = new EmbeddedModelOptions { ModelPath = path, ServerPath = binary.Path,
            FileVersion = ModelFileVersion.Read(path), MaximumTokens = 512, Concurrency = 2 };
        var session = new LlamaServerSession(options, binary);
        await using var engine = new LocalTranslationEngine(options, binary, session);
        var cues = new[] { TranslationPromptTests.Cue("a", "The train arrives at nine."), TranslationPromptTests.Cue("b", "Please close the window.", 2) };
        var results = new List<object>();
        int? pid = null;
        try
        {
            foreach (var profile in PromptPresets.All)
            {
                var result = await new TranslationOrchestrator(engine, Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N")))
                    .TranslateAsync(cues, profile, new() { SourceLanguage = "英语", Concurrency = 2 }, cancellationToken: timeout.Token);
                Assert.True(result.IsComplete, JsonSerializer.Serialize(result.FailureReasons));
                Assert.Equal(profile.BatchTranslation ? 1 : 2, result.EngineCalls); Assert.Equal(0, result.CacheHits);
                results.Add(new { profile.Name, profile.BatchTranslation, result.EngineCalls, result.CacheHits, result.Cues });
            }
            pid = session.ProcessId; Assert.True(engine.Evidence?.HasGpuOffload);
            Assert.Contains(engine.Inferences, item => item.CueIds.Count == 2);
            Assert.All(engine.Inferences, item => { Assert.True(item.GeneratedTokens > 0); Assert.Equal("eos", item.StopType); });
            var evidence = engine.Evidence; var inferences = engine.Inferences; var log = session.Diagnostic;
            await engine.DisposeAsync(); Assert.False(ProcessExists(pid!.Value));
            await AtomicFile.WriteJsonAsync(Path.Combine(root, "template-evidence.json"), new { binary.Version, binary.Identity,
                options.ModelId, options.FileVersion, Evidence = evidence, Inferences = inferences, Results = results, ProcessReleased = true }, timeout.Token);
            await AtomicFile.WriteTextAsync(Path.Combine(root, "template-server.log"), log, true, timeout.Token);
        }
        catch
        {
            await AtomicFile.WriteTextAsync(Path.Combine(root, "failed-template-server.log"), session.Diagnostic, true, CancellationToken.None);
            throw;
        }
    }

    [LocalGpuFact]
    public async Task OfficialPackageRunsConcurrentTranslationsCancelsAndReleasesProcess()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/local-acceptance");
        Directory.CreateDirectory(root);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var package = await new LlamaPackageService(http).DownloadAsync(root, new Sink<ModelDownloadProgress>(_ => { }), timeout.Token);
        var binary = await LlamaServerBinary.InspectAsync(package.ServerPath, timeout.Token);
        var modelPath = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")!;
        var options = new EmbeddedModelOptions
        {
            ModelPath = modelPath, FileVersion = ModelFileVersion.Read(modelPath),
            ModelId = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL_ID") ?? EmbeddedModelCatalog.Default.Id,
            ServerPath = binary.Path, Concurrency = 2
        };
        var session = new LlamaServerSession(options, binary);
        using var cancelInference = new CancellationTokenSource();
        var cancelArmed = false;
        var engine = new LocalTranslationEngine(options, binary, session,
            new Sink<LocalInferenceProgress>(value => { if (cancelArmed && value.CueIds.Contains("cancel") && value.GeneratedTokens >= 2) cancelInference.Cancel(); }));
        int? pid = null;
        try
        {
            var cues = new[]
            {
                TranslationPromptTests.Cue("a", "The train arrives at nine.", 0),
                TranslationPromptTests.Cue("b", "Please close the window.", 2),
                TranslationPromptTests.Cue("c", "I left the keys on the table.", 4),
                TranslationPromptTests.Cue("d", "We can discuss it tomorrow.", 6)
            };
            var result = await new TranslationOrchestrator(engine, Path.Combine(root, "Translations-" + Guid.NewGuid().ToString("N")))
                .TranslateAsync(cues, PromptPresets.Get(options.Model.DefaultPromptId), new() { BatchSize = 1, Concurrency = 2 }, cancellationToken: timeout.Token);
            Assert.True(result.IsComplete, JsonSerializer.Serialize(result.FailureReasons));
            Assert.Equal(4, result.EngineCalls); Assert.Equal(0, result.CacheHits);
            Assert.True(engine.Evidence?.HasGpuOffload); Assert.All(engine.Inferences, item => { Assert.True(item.GeneratedTokens > 0); Assert.Equal("eos", item.StopType); });
            pid = session.ProcessId; Assert.True(session.IsRunning);
            var request = new PromptBuilder().Build(PromptPresets.Get(options.Model.DefaultPromptId), new() { BatchSize = 1 }, [cues[0]], []);
            var preview = await engine.PrepareAsync(request, timeout.Token);
            Assert.Contains(cues[0].SourceText, preview.Prompt); Assert.True(preview.Tokens > 0);
            cancelArmed = true;
            var longCue = TranslationPromptTests.Cue("cancel", string.Join(' ', Enumerable.Repeat("The travelers waited near the old station and talked about their journey.", 50)));
            var longRequest = new PromptBuilder().Build(PromptPresets.Get(options.Model.DefaultPromptId), new() { BatchSize = 1 }, [longCue], []);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelInference.Token, timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync(longRequest, linked.Token));
            Assert.True(cancelInference.IsCancellationRequested);
            cancelArmed = false;
            Assert.False(string.IsNullOrWhiteSpace((await engine.TranslateAsync(request, timeout.Token)).Content));
            Assert.Equal(pid, session.ProcessId);
            var diagnostic = session.Diagnostic;
            var evidence = engine.Evidence;
            var inferences = engine.Inferences;
            await engine.DisposeAsync();
            Assert.False(ProcessExists(pid!.Value));
            await AtomicFile.WriteJsonAsync(Path.Combine(root, "evidence.json"), new
            {
                binary.Version, binary.Identity, options.ModelId, options.FileVersion,
                options.Concurrency, Evidence = evidence, Inferences = inferences,
                CancellationObserved = true, ProcessReleased = true, Requests = result.EngineCalls, CacheHits = result.CacheHits,
                PreviewTokens = preview.Tokens
            }, timeout.Token);
            await AtomicFile.WriteTextAsync(Path.Combine(root, "server.log"), diagnostic, true, timeout.Token);
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(root, "failed-server.log"), session.Diagnostic);
            throw;
        }
        finally { await engine.DisposeAsync(); }
    }

    private static bool ProcessExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private sealed class Sink<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
