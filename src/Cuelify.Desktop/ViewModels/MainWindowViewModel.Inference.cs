using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool isPreparing;
    [ObservableProperty] private string preparationStatus = "";
    [ObservableProperty] private string preparationElapsed = "";
    private long _preparationStarted;
    private long _phaseStarted;
    private string _preparationPhase = "";
    private bool _preparationEngineTest;

    public decimal? InferenceConcurrency
    {
        get => IsLocal ? Settings.LocalConcurrency : Settings.TranslationConcurrency;
        set
        {
            if (value is not { } number) return;
            if (number != decimal.Truncate(number)) throw new ArgumentException("并发请求数必须是整数。");
            if (IsLocal) Settings.LocalConcurrency = decimal.ToInt32(number);
            else Settings.TranslationConcurrency = decimal.ToInt32(number);
        }
    }
    public decimal? CloudOutputTokens
    {
        get => int.TryParse(Settings.MaximumTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) ? tokens : null;
        set
        {
            if (value is { } number && number != decimal.Truncate(number)) throw new ArgumentException("最大输出长度必须是整数。");
            Settings.MaximumTokens = value?.ToString("0", CultureInfo.InvariantCulture) ?? "";
        }
    }
    public decimal LocalOutputTokenMaximum => Settings.ContextSize / 2;
    public string ConcurrencyHint => "同时处理的翻译请求数；每个请求的字幕条数由提示词模板决定。" +
        (IsLocal ? "提高并发会增加显存占用。" : "提高并发可能触发服务限流。");
    public string TimeoutHint => IsLocal ? "每次翻译请求单独计时，不包含排队、模型加载和重试等待；服务启动与加载另设 5 分钟上限。" : "每次翻译请求单独计时，不包含排队和重试等待。";
    public string OutputTokensHint => IsLocal ? "每次请求的输出上限，不超过模型上下文容量的一半。批量翻译通常需要更大的输出上限。" : "每次请求的输出上限；留空使用服务默认值。批量翻译通常需要更大的输出上限。";

    private void RefreshInferenceSettings(string? property)
    {
        if (property == nameof(AppSettings.ContextSize) && Settings.ContextSize is >= 512 and <= 32768 && Settings.LocalMaximumTokens > Settings.ContextSize / 2)
            Settings.LocalMaximumTokens = Settings.ContextSize / 2;
        if (property is nameof(AppSettings.Provider) or nameof(AppSettings.LocalConcurrency) or nameof(AppSettings.TranslationConcurrency))
            OnPropertyChanged(nameof(InferenceConcurrency));
        if (property == nameof(AppSettings.MaximumTokens)) OnPropertyChanged(nameof(CloudOutputTokens));
        if (property == nameof(AppSettings.ContextSize)) OnPropertyChanged(nameof(LocalOutputTokenMaximum));
        if (property == nameof(AppSettings.Provider))
        {
            OnPropertyChanged(nameof(CloudOutputTokens)); OnPropertyChanged(nameof(LocalOutputTokenMaximum));
            OnPropertyChanged(nameof(ConcurrencyHint)); OnPropertyChanged(nameof(TimeoutHint)); OnPropertyChanged(nameof(OutputTokensHint));
        }
    }

    private async Task PrepareAsync(string initialStatus, Func<IProgress<LocalPreparationProgress>, Task> action, CancellationToken token, bool engineTest = false)
    {
        _preparationStarted = Stopwatch.GetTimestamp(); _phaseStarted = _preparationStarted;
        _preparationPhase = ""; _preparationEngineTest = engineTest;
        IsPreparing = true; SetPreparationStage(initialStatus, _preparationStarted);
        UpdatePreparationElapsed();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        EventHandler tick = (_, _) => UpdatePreparationElapsed();
        timer.Tick += tick; timer.Start();
        var outcome = "已完成";
        try
        {
            await action(new PreparationProgress(value => Dispatcher.UIThread.Post(() =>
            {
                if (IsPreparing && _cancellation?.Token == token && !token.IsCancellationRequested)
                    SetPreparationStage(PreparationStageText(value.Stage), value.Timestamp);
            })));
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = "已取消"; throw; }
        catch { outcome = "失败"; throw; }
        finally
        {
            // 先处理已上报的阶段，再结束准备，防止晚到的通知覆盖识别或最终测试结果。
            await FlushPreparationAsync();
            var finished = Stopwatch.GetTimestamp();
            FinishPreparationPhase(finished, outcome);
            AddLog($"准备{outcome}（共 {Stopwatch.GetElapsedTime(_preparationStarted, finished).TotalSeconds:F1} 秒）。");
            timer.Stop(); timer.Tick -= tick; IsPreparing = false;
        }
    }
    private static async Task FlushPreparationAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private void UpdatePreparationElapsed() => PreparationElapsed = $"已用时 {Stopwatch.GetElapsedTime(_preparationStarted):mm\\:ss}";
    private void SetPreparationStage(string message, long? timestamp = null)
    {
        if (_preparationPhase == message) return;
        var started = timestamp ?? Stopwatch.GetTimestamp();
        FinishPreparationPhase(started, "已完成");
        _phaseStarted = started; _preparationPhase = message; PreparationStatus = message;
        if (_preparationEngineTest) EngineStatus = message;
        else Status = message;
        AddLog(message);
    }
    private void FinishPreparationPhase(long finished, string outcome)
    {
        if (_preparationPhase.EndsWith('…'))
            AddLog($"{_preparationPhase.TrimEnd('…')}：{outcome}（耗时 {Stopwatch.GetElapsedTime(_phaseStarted, finished).TotalSeconds:F1} 秒）。");
    }
    private static string PreparationStageText(LocalPreparationStage stage) => stage switch
    {
        LocalPreparationStage.CheckingRuntime => "正在检查 llama.cpp 运行程序…",
        LocalPreparationStage.StartingService => "正在启动本地服务…",
        LocalPreparationStage.LoadingModel => "正在加载本地模型…",
        LocalPreparationStage.CheckingService => "正在检查本地服务…",
        LocalPreparationStage.Ready => "本地服务已就绪",
        LocalPreparationStage.ReusingService => "正在复用本地服务…",
        LocalPreparationStage.TestingTranslation => "正在试译测试短句…",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    private sealed class PreparationProgress(Action<LocalPreparationProgress> report) : IProgress<LocalPreparationProgress>
    { public void Report(LocalPreparationProgress value) => report(value); }
}
