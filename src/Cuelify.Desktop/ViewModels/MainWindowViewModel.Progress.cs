using Cuelify.Core.Projects;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public OperationProgressViewModel Activity { get; } = new();
    public bool ShowActivitySettings => IsBusy && !IsProcessing && !IsImporting;
    public bool ShowProjectEmptyState => !IsInitializing && !Workspace.HasProjects;
    internal void RefreshActivityVisibility()
    { OnPropertyChanged(nameof(ShowProjectEmptyState)); OnPropertyChanged(nameof(ShowActivitySettings)); }
    internal OperationProgressViewModel.Scope BeginFeedback(string text, bool immediate = false)
    {
        Activity.ReduceMotion = Settings.ReduceMotion;
        return Activity.Begin(text, immediate: immediate, onlyIfIdle: true);
    }
    internal void UpdateTaskActivity(string message, StageWorkProgress? work, string unit, bool reset)
    {
        Activity.Update(message, work, unit, reset);
        UpdateBatchActivity();
    }
    private void UpdateBatchActivity()
    {
        if (_operationProject?.Batch is not { State: ProcessingState.Running } batch) { Activity.ExtraText = ""; return; }
        var included = batch.Items.Where(i => i.SkipReason.Length == 0).ToArray();
        var completed = included.Count(i => i.State == ProcessingState.Completed);
        var failed = included.Count(i => i.State is ProcessingState.Failed or ProcessingState.PartialFailure);
        var waiting = included.Count(i => i.State is ProcessingState.NotStarted or ProcessingState.Cancelled or ProcessingState.Interrupted);
        var percent = included.Length > 0 ? 100d * completed / included.Length : 0;
        Activity.ExtraText = $"批量：完成 {completed}/{included.Length} 个媒体 · {percent:0.0}% · {failed} 个失败 · {waiting} 个等待";
    }
}
