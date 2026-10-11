using System.Collections.ObjectModel;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private string? _taskPipelineId;
    public ObservableCollection<SubtitlePipelineProfile> TaskPipelineItems { get; } = [];
    public SubtitlePipelineProfile? TaskPipeline
    {
        get
        {
            var id = _taskPipelineId ?? _savedSettings.GetPipeline((TranslationProvider)TaskProviderIndex).Id;
            return TaskPipelineItems.FirstOrDefault(p => p.Id == id);
        }
        set
        {
            if (!CanConfigure || value is null || value.Id == TaskPipeline?.Id) return;
            _taskPipelineId = value.Id; OnPropertyChanged(); OnPropertyChanged(nameof(TaskPromptSummary));
        }
    }
    private void RefreshTaskPipelines()
    {
        if (_taskPipelineId is not null && !_savedSettings.AllPipelines.Any(p => p.Id == _taskPipelineId)) _taskPipelineId = null;
        SynchronizePrompts(TaskPipelineItems, _savedSettings.AllPipelines.ToArray());
        OnPropertyChanged(nameof(TaskPipeline)); OnPropertyChanged(nameof(TaskPromptSummary));
    }
}
