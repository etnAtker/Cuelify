using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuelify.Core.Projects;

namespace Cuelify.Desktop.ViewModels;

// 单次操作拥有一个序号，已结束操作的通知不能修改下一次操作。
public partial class OperationProgressViewModel : ObservableObject
{
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private bool isVisible;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private bool isCancelling;
    [ObservableProperty] private bool reduceMotion;
    [ObservableProperty] private string message = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCount))] private string countText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExtra))] private string extraText = "";
    [ObservableProperty] private string elapsed = "";
    [ObservableProperty] private double percent;
    [ObservableProperty] private bool hasTotal;
    private long _revision, _started;
    private DispatcherTimer? _timer;
    public bool IsIndeterminate => !HasTotal && !ReduceMotion;
    public double BarValue => Percent;
    public bool HasCount => CountText.Length > 0;
    public bool HasExtra => ExtraText.Length > 0;
    partial void OnHasTotalChanged(bool value) { OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(BarValue)); }
    partial void OnReduceMotionChanged(bool value) { OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(BarValue)); }
    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(BarValue));

    public Scope Begin(string text, bool cancellable = false, bool immediate = false, bool onlyIfIdle = false)
    {
        if (onlyIfIdle && IsActive) return new(this, 0);
        _timer?.Stop(); var revision = ++_revision; _started = Stopwatch.GetTimestamp();
        IsActive = true; IsVisible = immediate; CanCancel = cancellable; IsCancelling = false;
        Message = text; CountText = ""; ExtraText = ""; HasTotal = false; Percent = 0;
        Tick(); _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick(); _timer.Start();
        return new(this, revision);
    }
    private void Tick()
    {
        var time = Stopwatch.GetElapsedTime(_started);
        Elapsed = "已用时 " + time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        if (time.TotalMilliseconds >= 300) IsVisible = true;
    }
    public void Update(string text, StageWorkProgress? work = null, string unit = "", bool reset = false)
    {
        if (!IsActive || IsCancelling) return;
        Message = text;
        if (reset) { HasTotal = false; Percent = 0; CountText = ""; }
        if (work is null) return;
        HasTotal = work.Total > 0; Percent = work.Percent; CountText = Format(work, unit);
    }
    public void Cancel()
    {
        if (!IsActive) return;
        IsCancelling = true; CanCancel = false; Message = "正在取消…";
    }
    public static string Format(StageWorkProgress work, string fallbackUnit = "")
    {
        if (work.Total <= 0) return "";
        var unit = work.Unit.Length > 0 ? work.Unit : fallbackUnit;
        var bytes = work.Total < 1000 ? $"{work.Completed}/{work.Total} B" : work.Total < 1_000_000
            ? $"{work.Completed / 1000d:0.0}/{work.Total / 1000d:0.0} KB" : $"{work.Completed / 1_000_000d:0.0}/{work.Total / 1_000_000d:0.0} MB";
        var text = unit == "字节" ? $"{bytes} · {work.Percent:0.0}%"
            : $"{work.Completed}/{work.Total} {unit} · {work.Percent:0.0}%";
        if (work.Running > 0) text += $" · {work.Running} 项处理中";
        if (work.Failed > 0) text += $" · {work.Failed} 项失败";
        var waiting = Math.Max(0, work.Total - work.Completed - work.Running - work.Failed);
        if (work.ShowWaiting && waiting > 0) text += $" · {waiting} 项等待";
        if (work.PendingWords > 0) text += $" · {work.PendingWords} 词待确认";
        return text;
    }
    public sealed class Scope(OperationProgressViewModel owner, long revision) : IDisposable
    {
        public void Update(string text, StageWorkProgress? work = null, string unit = "", bool reset = false)
        { if (revision != 0 && revision == owner._revision) owner.Update(text, work, unit, reset); }
        public void Dispose()
        {
            if (revision == 0 || revision != owner._revision) return;
            owner._timer?.Stop(); owner._timer = null; owner.IsActive = false; owner.IsVisible = false; owner.CanCancel = false;
        }
    }
}
