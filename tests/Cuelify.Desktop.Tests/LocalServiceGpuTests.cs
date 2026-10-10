using System.Collections.Concurrent;
using Cuelify.Core.Subtitles;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class LocalServiceGpuFactAttribute : FactAttribute
{
    public LocalServiceGpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")))
            Skip = "需显式指定已有 GGUF 与 Vulkan 运行程序，验证实际桌面服务工厂复用。";
    }
}

public sealed class LocalServiceGpuTests
{
    [LocalServiceGpuFact]
    public async Task ServiceReusesLoadedModelAcrossWarmupTestAndPreviewIgnoringStoredSha()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CUELIFY_LOCAL_ARTIFACTS") ?? "artifacts/model-loading/gpu");
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var settings = new AppSettings { Provider = TranslationProvider.Local,
            ModelPath = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_MODEL")!,
            LlamaServerPath = Environment.GetEnvironmentVariable("CUELIFY_LOCAL_SERVER")!, ModelSha256 = "过时且无效的身份哈希" };
        await using var jobs = new DesktopJobService(root);
        var progress = new Stages();
        await jobs.ValidateLocalAsync(settings, timeout.Token, progress);
        Assert.Contains(progress.Events, item => item.Stage == LocalPreparationStage.LoadingModel);
        var cold = progress.Events.Select(item => item.Stage.ToString()).ToArray();
        progress.Events.Clear(); await jobs.ValidateLocalAsync(settings, timeout.Token, progress);
        Assert.Equal(LocalPreparationStage.ReusingService, Assert.Single(progress.Events).Stage);
        progress.Events.Clear(); Assert.Equal("本地翻译测试成功", await jobs.TestEngineAsync(settings, "", timeout.Token, progress));
        Assert.Equal(new[] { LocalPreparationStage.ReusingService, LocalPreparationStage.TestingTranslation }, progress.Events.Select(item => item.Stage));
        var diagnostic = jobs.EngineDiagnostic;
        var preview = await jobs.PreviewAsync([new SubtitleCue("a", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null)], settings, timeout.Token);
        Assert.Contains("Hello.", preview);
        progress.Events.Clear(); settings.ModelSha256 = "另一个旧哈希"; await jobs.ValidateLocalAsync(settings, timeout.Token, progress);
        Assert.Equal(LocalPreparationStage.ReusingService, Assert.Single(progress.Events).Stage);
        await jobs.ReleaseEmbeddedModelAsync();
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "desktop-service-evidence.json"), new { ColdStages = cold,
            ReusedWarmupTestAndPreview = true, IgnoredStoredSha = true, Diagnostic = diagnostic, FreshTestRequests = 1, CacheHits = 0, ServiceReleased = true }, timeout.Token);
    }
    private sealed class Stages : IProgress<LocalPreparationProgress>
    {
        public ConcurrentQueue<LocalPreparationProgress> Events { get; } = new();
        public void Report(LocalPreparationProgress value) => Events.Enqueue(value);
    }
}
