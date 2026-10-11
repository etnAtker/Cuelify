using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _loadingPipelineModels;
    public ObservableCollection<ModelChoice> PipelineModels { get; } = [];
    [ObservableProperty] private ModelChoice? defaultSegmentationModel;
    [ObservableProperty] private ModelChoice? defaultResultModel;
    public bool PipelineSegmentationNeedsModel => SegmentationMethodIndex == (int)SegmentationMethod.Model;
    private void LoadPipelineModels()
    {
        _loadingPipelineModels = true;
        try
        {
            PipelineModels.Clear(); foreach (var choice in WorkspaceModels(Settings)) PipelineModels.Add(choice);
            var defaults = Settings.PipelineModelDefaults.GetValueOrDefault(_selectedPrompt!.Id) ?? _selectedPrompt.DefaultModels;
            DefaultSegmentationModel = PipelineModels.FirstOrDefault(m => m.Settings.ModelKey() == defaults.GetValueOrDefault(TaskStage.Subtitles));
            DefaultResultModel = PipelineModels.FirstOrDefault(m => m.Settings.ModelKey() == defaults.GetValueOrDefault(TaskStage.Translation));
        }
        finally { _loadingPipelineModels = false; }
        OnPropertyChanged(nameof(PipelineSegmentationNeedsModel));
    }
    partial void OnDefaultSegmentationModelChanged(ModelChoice? value) => ChangePipelineModel(TaskStage.Subtitles, value);
    partial void OnDefaultResultModelChanged(ModelChoice? value) => ChangePipelineModel(TaskStage.Translation, value);
    private void ChangePipelineModel(TaskStage kind, ModelChoice? choice)
    {
        if (_loadingPipelineModels || _selectedPrompt is null || choice is null) return;
        if (!Settings.PipelineModelDefaults.TryGetValue(_selectedPrompt.Id, out var defaults)) Settings.PipelineModelDefaults[_selectedPrompt.Id] = defaults = [];
        defaults[kind] = choice.Settings.ModelKey(); HasUnsavedSettings = true;
    }
}
