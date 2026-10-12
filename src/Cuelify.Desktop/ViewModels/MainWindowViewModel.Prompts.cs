using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private SubtitlePipelineProfile? _selectedPrompt;
    private bool _loadingPrompt;
    private bool _refreshingPrompts;
    private bool _switchingPrompt;
    [ObservableProperty] private string promptDescription = "";
    [ObservableProperty] private bool batchTranslation;
    [ObservableProperty] private int promptBatchSize = 12;
    [ObservableProperty] private int promptMaximumBatchCharacters = 6000;
    [ObservableProperty] private bool hasPromptEdits;
    [ObservableProperty] private string promptSearch = "";
    [ObservableProperty] private int promptFilterIndex;
    [ObservableProperty] private string promptSamplePreview = "";
    [ObservableProperty] private string promptSystemPreview = "";
    [ObservableProperty] private string promptUserPreview = "";
    [ObservableProperty] private string promptPreviewError = "";
    [ObservableProperty] private string promptPreviewStatus = "";
    [ObservableProperty] private int promptSectionIndex;
    public ObservableCollection<SubtitlePipelineProfile> PipelineItems { get; } = [];
    public ObservableCollection<SubtitlePipelineProfile> VisiblePipelineItems { get; } = [];
    public Task PromptSwitchTask { get; private set; } = Task.CompletedTask;
    public string[] PromptFilterNames { get; } = ["全部", "内置", "自定义"];
    public bool IsBuiltInPrompt => _selectedPrompt is not null && SubtitlePipelinePresets.IsBuiltIn(_selectedPrompt.Id);
    public bool CanEditPrompt => CanConfigure && _selectedPrompt is not null && !IsBuiltInPrompt;
    public bool CanManagePrompts => CanConfigure && !_switchingPrompt;
    public bool CanDeletePrompt => CanEditPrompt && !_switchingPrompt;
    public bool PromptHasReferences => _selectedPrompt?.Id == Settings.DefaultPipelineId;
    public bool CanCopyPromptPreview => CanManagePrompts && !string.IsNullOrEmpty(PromptSamplePreview);
    public bool HasPromptPreviewError => !string.IsNullOrEmpty(PromptPreviewError);
    public bool HasPromptPreviewStatus => !string.IsNullOrEmpty(PromptPreviewStatus);
    public string SettingsFooterStatus => HasPromptEdits || HasUnsavedSettings ? "有未保存的修改" : SettingsStatus;
    public string PromptKind => IsBuiltInPrompt ? "内置管线 · 创建副本后可编辑" : "自定义管线";
    public string PromptReferences => PromptHasReferences ? "新媒体默认使用此管线" : "";
    public string PromptNameError => string.IsNullOrWhiteSpace(ProfileName) ? "请填写管线名称。" :
        Settings.AllPipelines.Any(other => other.Id != _selectedPrompt?.Id && other.Name.Equals(ProfileName.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "管线名称已存在，请使用其他名称。" : "";
    public string PromptContentError => string.IsNullOrEmpty(PromptNameError) ? PromptValidationError : "";
    public string PromptVariableHint => BatchTranslation ? "用户模板需要包含 {cues_json}，并要求返回字幕 ID 对应译文的 JSON。" : "用户模板需要包含 {source_text}，并要求只返回当前字幕译文。";
    public string PromptValidationError
    {
        get
        {
            if (_selectedPrompt is null) return "";
            if (!string.IsNullOrEmpty(PromptNameError)) return PromptNameError;
            try
            {
                var draft = PromptDraft(); draft.Validate();
                return "";
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException) { return exception.Message; }
        }
    }
    public SubtitlePipelineProfile? SelectedPipeline
    {
        get => _selectedPrompt;
        set
        {
            if (!CanManagePrompts || _loadingPrompt || _refreshingPrompts || value is null || value.Id == _selectedPrompt?.Id) return;
            PromptSwitchTask = SwitchPromptAsync(value);
        }
    }
    public SubtitlePipelineProfile? VisibleSelectedPipeline
    {
        get => VisiblePipelineItems.FirstOrDefault(profile => profile.Id == _selectedPrompt?.Id);
        set => SelectedPipeline = value;
    }
    public SubtitlePipelineProfile? DefaultPipeline
    {
        get
        {
            try { var id = Settings.GetPipeline().Id; return PipelineItems.FirstOrDefault(profile => profile.Id == id); }
            catch (ArgumentException) { return null; }
        }
        set
        {
            if (!CanConfigure || _refreshingPrompts || value is null || value.Id == DefaultPipeline?.Id) return;
            try { Settings.SetDefaultPipeline(value.Id); }
            catch (Exception exception) { ReportError(exception); }
        }
    }
    private void InitializePipelineLibrary()
    {
        _loadingPrompt = true;
        try { HasPromptEdits = false; RefreshPipelineItems(); }
        finally { _loadingPrompt = false; }
        OpenPrompt(PipelineItems.FirstOrDefault(profile => profile.Id == _selectedPrompt?.Id) ?? SubtitlePipelinePresets.TwoStage);
    }
    private void RefreshPipelineItems()
    {
        _refreshingPrompts = true;
        try
        {
            SynchronizePrompts(PipelineItems, Settings.AllPipelines.ToArray());
            RefreshPromptFilter();
        }
        finally { _refreshingPrompts = false; }
        RefreshPromptAssociations();
    }
    private void RefreshPromptFilter()
    {
        var wasRefreshing = _refreshingPrompts; _refreshingPrompts = true;
        try
        {
            var visible = PipelineItems.Where(profile =>
                (PromptFilterIndex == 0 || SubtitlePipelinePresets.IsBuiltIn(profile.Id) == (PromptFilterIndex == 1)) &&
                (profile.Name.Contains(PromptSearch, StringComparison.OrdinalIgnoreCase) || profile.Description.Contains(PromptSearch, StringComparison.OrdinalIgnoreCase))).ToList();
            if (_selectedPrompt is not null && !PipelineItems.Any(profile => profile.Id == _selectedPrompt.Id)) visible.Add(_selectedPrompt);
            SynchronizePrompts(VisiblePipelineItems, visible);
        }
        finally { _refreshingPrompts = wasRefreshing; }
        OnPropertyChanged(nameof(SelectedPipeline)); OnPropertyChanged(nameof(VisibleSelectedPipeline));
    }
    private static void SynchronizePrompts(ObservableCollection<SubtitlePipelineProfile> items, IReadOnlyList<SubtitlePipelineProfile> desired)
    {
        // 保留未变化条目的对象与选择，避免重建 ItemsSource 引起双向绑定回写。
        for (var index = items.Count - 1; index >= 0; index--)
            if (!desired.Any(profile => profile.Id == items[index].Id)) items.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var existing = items.Select((profile, position) => (profile, position)).FirstOrDefault(item => item.profile.Id == desired[index].Id);
            if (existing.profile is null) items.Insert(index, desired[index]);
            else
            {
                if (existing.position != index) items.Move(existing.position, index);
                if (items[index] != desired[index]) items[index] = desired[index];
            }
        }
    }
    private void OpenPrompt(SubtitlePipelineProfile profile)
    {
        _loadingPrompt = true;
        try
        {
            _selectedPrompt = profile; ProfileName = profile.Name; PromptDescription = profile.Description;
            PipelineEditingStage = 0;
            SystemTemplate = profile.Translation.SystemTemplate; UserTemplate = profile.Translation.UserTemplate;
            SegmentationSystem = profile.Segmentation.SystemTemplate; SegmentationUser = profile.Segmentation.UserTemplate;
            SegmentationMethodIndex = (int)profile.Method; PipelineBlockSeconds = profile.BlockSeconds; PipelineContextCharacters = profile.ContextCharacters;
            PipelineBeforeCues = profile.BeforeCues; PipelineAfterCues = profile.AfterCues; PipelineContextBudget = profile.MaximumContextCharacters;
            PipelineMaximumSeconds = profile.MaximumCueSeconds; PipelineMinimumSeconds = profile.MinimumCueSeconds;
            PipelineLineCharacters = profile.LineCharacters; PipelineReadingSpeed = profile.ReadingCharactersPerSecond;
            LoadStageParameters(profile);
            BatchTranslation = profile.Translation.BatchTranslation; PromptBatchSize = profile.Translation.BatchSize; PromptMaximumBatchCharacters = profile.Translation.MaximumBatchCharacters;
            HasPromptEdits = !SubtitlePipelinePresets.IsBuiltIn(profile.Id) && !Settings.PipelineLibrary.ContainsKey(profile.Id);
            ClearPromptPreview(); PromptSectionIndex = 0;
            RefreshPipelineItems(); LoadPipelineModels();
        }
        finally { _loadingPrompt = false; }
        OnPropertyChanged(nameof(SelectedPipeline)); OnPropertyChanged(nameof(VisibleSelectedPipeline)); RefreshPromptCommands();
    }
    private SubtitlePipelineProfile PromptDraft() => new(ProfileName.Trim(), (SegmentationMethod)SegmentationMethodIndex)
    {
        Id = _selectedPrompt!.Id, Description = PromptDescription, DefaultModels = new(Settings.PipelineModelDefaults.GetValueOrDefault(_selectedPrompt.Id) ?? _selectedPrompt.DefaultModels),
        Translation = new(ProfileName.Trim(), SystemTemplate, UserTemplate, BatchTranslation)
        { BatchSize = PromptBatchSize, MaximumBatchCharacters = PromptMaximumBatchCharacters },
        Segmentation = new(SegmentationSystem, SegmentationUser) { Parameters = ReadStageParameters(true) },
        TranslationParameters = ReadStageParameters(false), BlockSeconds = PipelineBlockSeconds, ContextCharacters = PipelineContextCharacters,
        BeforeCues = PipelineBeforeCues, AfterCues = PipelineAfterCues, MaximumContextCharacters = PipelineContextBudget,
        MaximumCueSeconds = PipelineMaximumSeconds, MinimumCueSeconds = PipelineMinimumSeconds,
        LineCharacters = PipelineLineCharacters, ReadingCharactersPerSecond = PipelineReadingSpeed
    };
    private bool CommitPromptEditor(AppSettings snapshot)
    {
        if (!HasPromptEdits) return true;
        if (IsBuiltInPrompt || !string.IsNullOrEmpty(PromptValidationError))
        { Error = IsBuiltInPrompt ? "内置管线不可修改，请先创建副本。" : PromptValidationError; return false; }
        snapshot.SavePipeline(PromptDraft()); return true;
    }
    private void PromptChanged()
    {
        if (_loadingPrompt) return;
        HasPromptEdits = true; ClearPromptPreview(); RefreshPromptCommands();
    }
    private void RefreshPromptAssociations()
    {
        OnPropertyChanged(nameof(DefaultPipeline)); OnPropertyChanged(nameof(PromptReferences)); OnPropertyChanged(nameof(PromptHasReferences));
    }
    private void RefreshPromptCommands()
    {
        OnPropertyChanged(nameof(IsBuiltInPrompt)); OnPropertyChanged(nameof(CanEditPrompt)); OnPropertyChanged(nameof(CanEditSegmentationSettings)); OnPropertyChanged(nameof(CanDeletePrompt));
        OnPropertyChanged(nameof(PromptKind)); OnPropertyChanged(nameof(CanManagePrompts)); OnPropertyChanged(nameof(CanCopyPromptPreview)); OnPropertyChanged(nameof(PromptVariableHint)); OnPropertyChanged(nameof(PromptValidationError)); OnPropertyChanged(nameof(PromptNameError)); OnPropertyChanged(nameof(PromptContentError));
        NewPromptCommand.NotifyCanExecuteChanged(); CopyPromptCommand.NotifyCanExecuteChanged(); DeletePromptCommand.NotifyCanExecuteChanged();
        SavePromptCommand.NotifyCanExecuteChanged(); PreviewPromptSampleCommand.NotifyCanExecuteChanged(); CopyPromptPreviewCommand.NotifyCanExecuteChanged();
        ViewDefaultPipelineCommand.NotifyCanExecuteChanged();
    }
    partial void OnPromptDescriptionChanged(string value) => PromptChanged();
    partial void OnBatchTranslationChanged(bool value) => PromptChanged();
    partial void OnPromptBatchSizeChanged(int value) => PromptChanged();
    partial void OnPromptMaximumBatchCharactersChanged(int value) => PromptChanged();
    partial void OnPromptSearchChanged(string value) => RefreshPromptFilter();
    partial void OnPromptFilterIndexChanged(int value) => RefreshPromptFilter();
    partial void OnHasPromptEditsChanged(bool value) { OnPropertyChanged(nameof(SettingsHint)); OnPropertyChanged(nameof(SettingsFooterStatus)); }
    partial void OnSettingsStatusChanged(string value) => OnPropertyChanged(nameof(SettingsFooterStatus));
    partial void OnPromptSectionIndexChanged(int value) { if (value == 2 && !_loadingPrompt) PreviewPromptSample(); }
    partial void OnPromptSamplePreviewChanged(string value) { OnPropertyChanged(nameof(CanCopyPromptPreview)); CopyPromptPreviewCommand.NotifyCanExecuteChanged(); }
    partial void OnPromptPreviewErrorChanged(string value) => OnPropertyChanged(nameof(HasPromptPreviewError));
    partial void OnPromptPreviewStatusChanged(string value) => OnPropertyChanged(nameof(HasPromptPreviewStatus));
    private string UniquePromptName(string name)
    {
        var candidate = name; var suffix = 2;
        while (Settings.AllPipelines.Any(profile => profile.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = name + " " + suffix++;
        return candidate;
    }
    private async Task SwitchPromptAsync(SubtitlePipelineProfile next)
    {
        if (!CanManagePrompts) return;
        if (!HasPromptEdits) { OpenPrompt(next); return; }
        _switchingPrompt = true; RefreshPromptCommands();
        try
        {
            // 等双向选择回写结束后恢复原选择，再等待模态确认。
            await Task.Yield();
            OnPropertyChanged(nameof(SelectedPipeline)); OnPropertyChanged(nameof(VisibleSelectedPipeline));
            var choice = await _dialogs.ConfirmPromptSwitchAsync(Sanitize(ProfileName));
            if (choice == PromptSwitchChoice.Cancel) return;
            if (choice == PromptSwitchChoice.Save)
            {
                await SaveSettingsAsync();
                if (!string.IsNullOrEmpty(Error) || HasPromptEdits) return;
            }
            OpenPrompt(next);
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _switchingPrompt = false;
            OnPropertyChanged(nameof(SelectedPipeline)); OnPropertyChanged(nameof(VisibleSelectedPipeline)); RefreshPromptCommands();
        }
    }
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void NewPrompt() => SelectedPipeline = SubtitlePipelinePresets.Direct with { Id = Guid.NewGuid().ToString("N"), Name = UniquePromptName("新管线"), Translation = PromptPresets.SingleSimple with { UserTemplate = "" } };
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void CopyPrompt()
    {
        if (_selectedPrompt is not null) SelectedPipeline = PromptDraft() with { Id = Guid.NewGuid().ToString("N"), Name = UniquePromptName(ProfileName + " 副本") };
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private Task SavePromptAsync() => SaveSettingsAsync();
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void ViewDefaultPipeline() { SelectedPipeline = DefaultPipeline; SettingsSectionIndex = 2; ShowSettings(); }
    [RelayCommand(CanExecute = nameof(CanDeletePrompt))]
    private async Task DeletePromptAsync()
    {
        if (!CanDeletePrompt) return;
        var id = _selectedPrompt!.Id;
        var message = $"删除管线“{Sanitize(ProfileName)}”？" + (PromptHasReferences ? $"\n{PromptReferences}\n新媒体的默认管线将切换为模型分句后翻译。" : "");
        if (!await _dialogs.ConfirmAsync(message) || !CanDeletePrompt || id != _selectedPrompt?.Id) return;
        if (!Settings.PipelineLibrary.ContainsKey(id))
        {
            OpenPrompt(SubtitlePipelinePresets.TwoStage); Error = ""; SettingsStatus = "未保存的管线已删除"; return;
        }
        IsSavingSettings = true;
        using var feedback = BeginFeedback("正在删除字幕管线…");
        try
        {
            var snapshot = Services.ConfigurationStore.Snapshot(Settings);
            snapshot.DeletePipeline(id);
            await _store.SaveAsync(snapshot, Secrets);
            Settings.PipelineLibrary = snapshot.PipelineLibrary; Settings.DefaultPipelineId = snapshot.DefaultPipelineId; Settings.PipelineModelDefaults = snapshot.PipelineModelDefaults;
            _savedSettings = Services.ConfigurationStore.Snapshot(snapshot);
            RefreshTaskPipelines();
            HasUnsavedSettings = false; Error = ""; SettingsStatus = "管线已删除";
            OpenPrompt(SubtitlePipelinePresets.TwoStage); ResetEngineStatus(); OnPropertyChanged(nameof(TaskPromptSummary)); RefreshCommands();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsSavingSettings = false; }
    }
    private void ClearPromptPreview()
    {
        PromptSamplePreview = ""; PromptSystemPreview = ""; PromptUserPreview = ""; PromptPreviewError = ""; PromptPreviewStatus = "";
    }
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void PreviewPromptSample()
    {
        try
        {
            var pipeline = PromptDraft(); pipeline.Validate();
            var profile = pipeline.Translation with { Parameters = pipeline.TranslationParameters, MaximumTextCharacters = pipeline.LineCharacters * 2, ReadingCharactersPerSecond = pipeline.ReadingCharactersPerSecond };
            SubtitleCue[] sample = [new("sample-1", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null),
                new("sample-2", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "How are you?", null)];
            var settings = new TranslationSettings { SourceLanguage = "英语", TargetLanguage = "中文", TargetStyle = Settings.TargetStyle };
            var request = new PromptBuilder().Build(profile, settings, profile.BatchTranslation ? sample : sample.Take(1).ToArray(), []);
            if (pipeline.Method != SegmentationMethod.Program && PipelineEditingStage == 0)
            {
                var words = new Core.Speech.WordToken[] { new("Hello.", TimeSpan.Zero, TimeSpan.FromSeconds(1), null), new("How are you?", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), null) };
                request = PipelinePromptBuilder.Build(words, PipelinePromptBuilder.Plan(words, pipeline)[0], pipeline, settings);
            }
            PromptSamplePreview = Sanitize(JsonSerializer.Serialize(request.Messages, new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            PromptSystemPreview = Sanitize(string.Join(Environment.NewLine, request.Messages.Where(message => message.Role == "system").Select(message => message.Content)));
            PromptUserPreview = Sanitize(string.Join(Environment.NewLine, request.Messages.Where(message => message.Role == "user").Select(message => message.Content)));
            PromptPreviewError = ""; PromptPreviewStatus = "";
        }
        catch (Exception exception) { ClearPromptPreview(); PromptPreviewError = Sanitize(exception.Message); }
    }
    [RelayCommand(CanExecute = nameof(CanCopyPromptPreview))]
    private async Task CopyPromptPreviewAsync()
    {
        try { await _dialogs.CopyTextAsync(PromptSamplePreview); PromptPreviewStatus = "已复制预览"; }
        catch (Exception exception) { PromptPreviewStatus = Sanitize(exception.Message); }
    }
}
