using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuelify.Core.Translation;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private int pipelineEditingStage;
    public string[] PipelineStageNames => [SegmentationStageLabel, PipelineModeIndex == 1 ? "补译与重翻" : "翻译"];
    public bool IsEditingSegmentation => HasSegmentationStage && PipelineEditingStage == 0;
    public bool IsEditingTranslation => !HasSegmentationStage || PipelineEditingStage == 1;
    partial void OnPipelineEditingStageChanged(int value)
    { OnPropertyChanged(nameof(IsEditingSegmentation)); OnPropertyChanged(nameof(IsEditingTranslation)); ClearPromptPreview(); }
    [ObservableProperty] private string segmentationSystem = "";
    [ObservableProperty] private string segmentationUser = "";
    [ObservableProperty] private int pipelineModeIndex = 0;
    [ObservableProperty] private int pipelineBlockSeconds = 24;
    [ObservableProperty] private int pipelineContextCharacters = 200;
    [ObservableProperty] private int pipelineBeforeCues = 5;
    [ObservableProperty] private int pipelineAfterCues = 2;
    [ObservableProperty] private int pipelineContextBudget = 3000;
    [ObservableProperty] private double pipelineMaximumSeconds = 7;
    [ObservableProperty] private double pipelineMinimumSeconds = .7;
    [ObservableProperty] private int pipelineLineCharacters = 22;
    [ObservableProperty] private double pipelineReadingSpeed = 12;
    [ObservableProperty] private string segmentationTemperature = "";
    [ObservableProperty] private string segmentationTopP = "";
    [ObservableProperty] private string segmentationTopK = "";
    [ObservableProperty] private string segmentationRepeatPenalty = "";
    [ObservableProperty] private string segmentationMaximumTokens = "";
    [ObservableProperty] private string translationTemperature = "";
    [ObservableProperty] private string translationTopP = "";
    [ObservableProperty] private string translationTopK = "";
    [ObservableProperty] private string translationRepeatPenalty = "";
    [ObservableProperty] private string translationMaximumTokens = "";
    public string[] PipelineModeNames { get; } = ["直接翻译", "联合分句与翻译", "先分句后翻译"];
    public bool HasSegmentationStage => PipelineModeIndex != 0;
    public bool CanEditSegmentationSettings => CanEditPrompt && HasSegmentationStage;
    public string PipelineSteps => PipelineModeIndex switch { 0 => "翻译 → 字幕显示", 1 => "联合分句与翻译 → 字幕显示", _ => "分句 → 翻译 → 字幕显示" };
    public string SegmentationStageLabel => PipelineModeIndex == 1 ? "联合分句与翻译" : "分句";
    private static double? StageNumber(string text) => string.IsNullOrWhiteSpace(text) ? null : double.Parse(text, CultureInfo.InvariantCulture);
    private static int? StageInteger(string text) => string.IsNullOrWhiteSpace(text) ? null : int.Parse(text, CultureInfo.InvariantCulture);
    private InferenceParameters ReadStageParameters(bool segmentation) => new()
    { Temperature = StageNumber(segmentation ? SegmentationTemperature : TranslationTemperature), TopP = StageNumber(segmentation ? SegmentationTopP : TranslationTopP),
      TopK = StageInteger(segmentation ? SegmentationTopK : TranslationTopK), RepeatPenalty = StageNumber(segmentation ? SegmentationRepeatPenalty : TranslationRepeatPenalty),
      MaximumTokens = StageInteger(segmentation ? SegmentationMaximumTokens : TranslationMaximumTokens) };
    private void LoadStageParameters(SubtitlePipelineProfile p)
    {
        SegmentationTemperature = p.Segmentation.Parameters.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        SegmentationTopP = p.Segmentation.Parameters.TopP?.ToString(CultureInfo.InvariantCulture) ?? "";
        SegmentationTopK = p.Segmentation.Parameters.TopK?.ToString(CultureInfo.InvariantCulture) ?? "";
        SegmentationRepeatPenalty = p.Segmentation.Parameters.RepeatPenalty?.ToString(CultureInfo.InvariantCulture) ?? "";
        SegmentationMaximumTokens = p.Segmentation.Parameters.MaximumTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
        TranslationTemperature = p.TranslationParameters.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        TranslationTopP = p.TranslationParameters.TopP?.ToString(CultureInfo.InvariantCulture) ?? "";
        TranslationTopK = p.TranslationParameters.TopK?.ToString(CultureInfo.InvariantCulture) ?? "";
        TranslationRepeatPenalty = p.TranslationParameters.RepeatPenalty?.ToString(CultureInfo.InvariantCulture) ?? "";
        TranslationMaximumTokens = p.TranslationParameters.MaximumTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
    }
    partial void OnSegmentationSystemChanged(string value) { PromptChanged(); }
    partial void OnSegmentationUserChanged(string value) { PromptChanged(); }
    partial void OnPipelineModeIndexChanged(int value) { PromptChanged(); OnPropertyChanged(nameof(HasSegmentationStage)); OnPropertyChanged(nameof(CanEditSegmentationSettings)); OnPropertyChanged(nameof(PipelineSteps)); OnPropertyChanged(nameof(SegmentationStageLabel)); OnPropertyChanged(nameof(PipelineStageNames)); OnPropertyChanged(nameof(IsEditingSegmentation)); OnPropertyChanged(nameof(IsEditingTranslation)); }
    partial void OnPipelineBlockSecondsChanged(int value) { PromptChanged(); }
    partial void OnPipelineContextCharactersChanged(int value) { PromptChanged(); }
    partial void OnPipelineBeforeCuesChanged(int value) { PromptChanged(); }
    partial void OnPipelineAfterCuesChanged(int value) { PromptChanged(); }
    partial void OnPipelineContextBudgetChanged(int value) { PromptChanged(); }
    partial void OnPipelineMaximumSecondsChanged(double value) { PromptChanged(); }
    partial void OnPipelineMinimumSecondsChanged(double value) { PromptChanged(); }
    partial void OnPipelineLineCharactersChanged(int value) { PromptChanged(); }
    partial void OnPipelineReadingSpeedChanged(double value) { PromptChanged(); }
    partial void OnSegmentationTemperatureChanged(string value) { PromptChanged(); }
    partial void OnSegmentationTopPChanged(string value) { PromptChanged(); }
    partial void OnSegmentationTopKChanged(string value) { PromptChanged(); }
    partial void OnSegmentationRepeatPenaltyChanged(string value) { PromptChanged(); }
    partial void OnSegmentationMaximumTokensChanged(string value) { PromptChanged(); }
    partial void OnTranslationTemperatureChanged(string value) { PromptChanged(); }
    partial void OnTranslationTopPChanged(string value) { PromptChanged(); }
    partial void OnTranslationTopKChanged(string value) { PromptChanged(); }
    partial void OnTranslationRepeatPenaltyChanged(string value) { PromptChanged(); }
    partial void OnTranslationMaximumTokensChanged(string value) { PromptChanged(); }
}
