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
    private PromptProfile? _selectedPrompt;
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
    public ObservableCollection<PromptProfile> PromptItems { get; } = [];
    public ObservableCollection<PromptProfile> VisiblePromptItems { get; } = [];
    public Task PromptSwitchTask { get; private set; } = Task.CompletedTask;
    public string[] PromptFilterNames { get; } = ["全部", "内置", "自定义"];
    public bool IsBuiltInPrompt => _selectedPrompt is not null && PromptPresets.IsBuiltIn(_selectedPrompt.Id);
    public bool CanEditPrompt => CanConfigure && _selectedPrompt is not null && !IsBuiltInPrompt;
    public bool CanManagePrompts => CanConfigure && !_switchingPrompt;
    public bool CanDeletePrompt => CanEditPrompt && !_switchingPrompt;
    public bool PromptHasReferences => _selectedPrompt is not null && Settings.ModelPromptBindings.Values.Any(binding => binding.PromptId == _selectedPrompt.Id);
    public bool CanCopyPromptPreview => CanManagePrompts && !string.IsNullOrEmpty(PromptSamplePreview);
    public bool HasPromptPreviewError => !string.IsNullOrEmpty(PromptPreviewError);
    public bool HasPromptPreviewStatus => !string.IsNullOrEmpty(PromptPreviewStatus);
    public string SettingsFooterStatus => HasPromptEdits || HasUnsavedSettings ? "有未保存的修改" : SettingsStatus;
    public string PromptKind => IsBuiltInPrompt ? "内置模板 · 创建副本后可编辑" : "自定义模板";
    public string PromptReferences
    {
        get
        {
            if (_selectedPrompt is null) return "";
            var names = Settings.ModelPromptBindings.Values.Where(binding => binding.PromptId == _selectedPrompt.Id).Select(binding => binding.ModelName).Distinct(StringComparer.Ordinal).ToList();
            var defaults = new List<string>();
            foreach (var model in Infrastructure.Translation.Local.EmbeddedModelCatalog.Presets)
            {
                if (!Settings.ModelPromptBindings.ContainsKey("local:" + model.Id) && model.DefaultPromptId == _selectedPrompt.Id) defaults.Add(model.Name);
            }
            if (!IsLocal)
            {
                try { if (!Settings.ModelPromptBindings.ContainsKey(Settings.ModelKey()) && Settings.GetProfile().Id == _selectedPrompt.Id) defaults.Add(Settings.ModelName()); }
                catch (ArgumentException) { /* 地址编辑尚未完成时保留库的可编辑性。 */ }
            }
            var lines = new List<string>();
            if (defaults.Count > 0) lines.Add("默认使用此模板：" + string.Join("、", defaults));
            if (names.Count > 0) lines.Add("已指定此模板：" + string.Join("、", names));
            return lines.Count == 0 ? "尚无模型使用此模板" : string.Join(Environment.NewLine, lines);
        }
    }
    public string PromptNameError => string.IsNullOrWhiteSpace(ProfileName) ? "请填写模板名称。" :
        Settings.AllPrompts.Any(other => other.Id != _selectedPrompt?.Id && other.Name.Equals(ProfileName.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "模板名称已存在，请使用其他名称。" : "";
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
                var draft = PromptDraft(); PromptBuilder.Validate(draft);
                return "";
            }
            catch (ArgumentException exception) { return exception.Message; }
        }
    }
    public PromptProfile? SelectedPrompt
    {
        get => _selectedPrompt;
        set
        {
            if (!CanManagePrompts || _loadingPrompt || _refreshingPrompts || value is null || value.Id == _selectedPrompt?.Id) return;
            PromptSwitchTask = SwitchPromptAsync(value);
        }
    }
    public PromptProfile? VisibleSelectedPrompt
    {
        get => VisiblePromptItems.FirstOrDefault(profile => profile.Id == _selectedPrompt?.Id);
        set => SelectedPrompt = value;
    }
    public PromptProfile? AssociatedPrompt
    {
        get
        {
            try { var id = Settings.GetProfile().Id; return PromptItems.FirstOrDefault(profile => profile.Id == id); }
            catch (ArgumentException) { return null; }
        }
        set
        {
            if (!CanConfigure || _refreshingPrompts || value is null || value.Id == AssociatedPrompt?.Id) return;
            try { Settings.AssociatePrompt(value.Id); }
            catch (Exception exception) { ReportError(exception); }
        }
    }
    private void InitializePromptLibrary()
    {
        _loadingPrompt = true;
        try { HasPromptEdits = false; RefreshPromptItems(); }
        finally { _loadingPrompt = false; }
        OpenPrompt(PromptItems.FirstOrDefault(profile => profile.Id == _selectedPrompt?.Id) ?? PromptPresets.BatchSubtitles);
    }
    private void RefreshPromptItems()
    {
        _refreshingPrompts = true;
        try
        {
            SynchronizePrompts(PromptItems, Settings.AllPrompts.ToArray());
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
            var visible = PromptItems.Where(profile =>
                (PromptFilterIndex == 0 || PromptPresets.IsBuiltIn(profile.Id) == (PromptFilterIndex == 1)) &&
                (profile.Name.Contains(PromptSearch, StringComparison.OrdinalIgnoreCase) || profile.Description.Contains(PromptSearch, StringComparison.OrdinalIgnoreCase))).ToList();
            if (_selectedPrompt is not null && !PromptItems.Any(profile => profile.Id == _selectedPrompt.Id)) visible.Add(_selectedPrompt);
            SynchronizePrompts(VisiblePromptItems, visible);
        }
        finally { _refreshingPrompts = wasRefreshing; }
        OnPropertyChanged(nameof(SelectedPrompt)); OnPropertyChanged(nameof(VisibleSelectedPrompt));
    }
    private static void SynchronizePrompts(ObservableCollection<PromptProfile> items, IReadOnlyList<PromptProfile> desired)
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
    private void OpenPrompt(PromptProfile profile)
    {
        _loadingPrompt = true;
        try
        {
            _selectedPrompt = profile; ProfileName = profile.Name; PromptDescription = profile.Description;
            SystemTemplate = profile.SystemTemplate; UserTemplate = profile.UserTemplate;
            BatchTranslation = profile.BatchTranslation; PromptBatchSize = profile.BatchSize; PromptMaximumBatchCharacters = profile.MaximumBatchCharacters;
            HasPromptEdits = !PromptPresets.IsBuiltIn(profile.Id) && !Settings.PromptLibrary.ContainsKey(profile.Id);
            ClearPromptPreview(); PromptSectionIndex = 0;
            RefreshPromptItems();
        }
        finally { _loadingPrompt = false; }
        OnPropertyChanged(nameof(SelectedPrompt)); OnPropertyChanged(nameof(VisibleSelectedPrompt)); RefreshPromptCommands();
    }
    private PromptProfile PromptDraft() => new(ProfileName.Trim(), SystemTemplate, UserTemplate, BatchTranslation)
    { Id = _selectedPrompt!.Id, Description = PromptDescription, BatchSize = PromptBatchSize, MaximumBatchCharacters = PromptMaximumBatchCharacters };
    private bool CommitPromptEditor(AppSettings snapshot)
    {
        if (!HasPromptEdits) return true;
        if (IsBuiltInPrompt || !string.IsNullOrEmpty(PromptValidationError))
        { Error = IsBuiltInPrompt ? "内置模板不可修改，请先创建副本。" : PromptValidationError; return false; }
        snapshot.SavePrompt(PromptDraft()); return true;
    }
    private void PromptChanged()
    {
        if (_loadingPrompt) return;
        HasPromptEdits = true; ClearPromptPreview(); RefreshPromptCommands();
    }
    private void RefreshPromptAssociations()
    {
        OnPropertyChanged(nameof(AssociatedPrompt)); OnPropertyChanged(nameof(PromptReferences)); OnPropertyChanged(nameof(PromptHasReferences));
    }
    private void RefreshPromptCommands()
    {
        OnPropertyChanged(nameof(IsBuiltInPrompt)); OnPropertyChanged(nameof(CanEditPrompt)); OnPropertyChanged(nameof(CanDeletePrompt));
        OnPropertyChanged(nameof(PromptKind)); OnPropertyChanged(nameof(CanManagePrompts)); OnPropertyChanged(nameof(CanCopyPromptPreview)); OnPropertyChanged(nameof(PromptVariableHint)); OnPropertyChanged(nameof(PromptValidationError)); OnPropertyChanged(nameof(PromptNameError)); OnPropertyChanged(nameof(PromptContentError));
        NewPromptCommand.NotifyCanExecuteChanged(); CopyPromptCommand.NotifyCanExecuteChanged(); DeletePromptCommand.NotifyCanExecuteChanged();
        SavePromptCommand.NotifyCanExecuteChanged(); PreviewPromptSampleCommand.NotifyCanExecuteChanged(); CopyPromptPreviewCommand.NotifyCanExecuteChanged();
        ViewAssociatedPromptCommand.NotifyCanExecuteChanged();
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
        while (Settings.AllPrompts.Any(profile => profile.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = name + " " + suffix++;
        return candidate;
    }
    private async Task SwitchPromptAsync(PromptProfile next)
    {
        if (!CanManagePrompts) return;
        if (!HasPromptEdits) { OpenPrompt(next); return; }
        _switchingPrompt = true; RefreshPromptCommands();
        try
        {
            // 等双向选择回写结束后恢复原选择，再等待模态确认。
            await Task.Yield();
            OnPropertyChanged(nameof(SelectedPrompt)); OnPropertyChanged(nameof(VisibleSelectedPrompt));
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
            OnPropertyChanged(nameof(SelectedPrompt)); OnPropertyChanged(nameof(VisibleSelectedPrompt)); RefreshPromptCommands();
        }
    }
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void NewPrompt() => SelectedPrompt = new(UniquePromptName("新提示词"), "", "", false);
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void CopyPrompt()
    {
        if (_selectedPrompt is not null) SelectedPrompt = PromptDraft() with { Id = Guid.NewGuid().ToString("N"), Name = UniquePromptName(ProfileName + " 副本") };
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private Task SavePromptAsync() => SaveSettingsAsync();
    [RelayCommand(CanExecute = nameof(CanManagePrompts))]
    private void ViewAssociatedPrompt() { SelectedPrompt = AssociatedPrompt; SettingsSectionIndex = 2; ShowSettings(); }
    [RelayCommand(CanExecute = nameof(CanDeletePrompt))]
    private async Task DeletePromptAsync()
    {
        if (!CanDeletePrompt) return;
        var id = _selectedPrompt!.Id;
        var message = $"删除提示词“{Sanitize(ProfileName)}”？" + (PromptHasReferences ? $"\n{PromptReferences}\n使用此模板的模型将切换到各自的默认提示词。" : "");
        if (!await _dialogs.ConfirmAsync(message) || !CanDeletePrompt || id != _selectedPrompt?.Id) return;
        if (!Settings.PromptLibrary.ContainsKey(id))
        {
            OpenPrompt(PromptPresets.BatchSubtitles); Error = ""; SettingsStatus = "未保存的提示词已删除"; return;
        }
        IsSavingSettings = true;
        try
        {
            var snapshot = Services.ConfigurationStore.Snapshot(Settings);
            snapshot.DeletePrompt(id);
            await _store.SaveAsync(snapshot, Secrets);
            Settings.PromptLibrary = snapshot.PromptLibrary; Settings.ModelPromptBindings = snapshot.ModelPromptBindings;
            _savedSettings = Services.ConfigurationStore.Snapshot(snapshot);
            HasUnsavedSettings = false; Error = ""; SettingsStatus = "提示词已删除";
            OpenPrompt(PromptPresets.BatchSubtitles); ResetEngineStatus(); OnPropertyChanged(nameof(TaskPromptSummary)); RefreshCommands();
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
            var profile = PromptDraft();
            SubtitleCue[] sample = [new("sample-1", TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello.", null),
                new("sample-2", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "How are you?", null)];
            var settings = new TranslationSettings { SourceLanguage = "英语", TargetLanguage = "中文", TargetStyle = Settings.TargetStyle };
            var request = new PromptBuilder().Build(profile, settings, profile.BatchTranslation ? sample : sample.Take(1).ToArray(), []);
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
