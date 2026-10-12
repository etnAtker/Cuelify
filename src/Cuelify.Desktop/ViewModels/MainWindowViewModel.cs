using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Speech;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.ViewModels;

public enum CueTranslationState { Waiting, Translating, Failed, Cancelled }
public enum AppPage { Projects, Workspace, Logs, Settings }
public enum WorkflowStep { File, Options, Results }

public partial class CueRow(SubtitleCue cue, int number) : ObservableObject
{
    public SubtitleCue Cue { get; } = cue;
    public int Number { get; } = number;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Translation)), NotifyPropertyChangedFor(nameof(IsFailed))] private CueTranslationState state;
    public bool IsFailed => Cue.TranslatedText is null && State == CueTranslationState.Failed;
    public string Id => Cue.Id;
    public string Time => $"{Cue.Start:hh\\:mm\\:ss\\.fff} → {Cue.End:hh\\:mm\\:ss\\.fff}";
    public string Source => Cue.SourceText;
    public string WordRange { get; init; } = "";
    [ObservableProperty] private string failureReason = "";
    public string Translation => Cue.TranslatedText ?? State switch
    { CueTranslationState.Translating => "翻译中…", CueTranslationState.Failed => "翻译失败", CueTranslationState.Cancelled => "已取消", _ => "等待翻译" };
}

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDesktopJobService _jobs;
    private readonly ConfigurationStore _store;
    private readonly IWindowDialogs _dialogs;
    private CancellationTokenSource? _cancellation;
    private Task? _operation;
    private AppSettings _savedSettings = new();
    private AppSettings? _jobSettings;
    private TranscriptionResult? _jobSource;
    private bool _hasFinalCues;
    private string _jobInputPath = "";
    private bool _isComplete;
    private bool _isMediaJob;
    private bool _loadingSettings;
    private Task? _initialization;
    private bool _disposed;
    private bool _hasStartedProcessing;
    public string Diagnostic { get; private set; } = "";
    [ObservableProperty] private AppSettings settings = new();
    [ObservableProperty] private AppPage page;
    [ObservableProperty] private WorkflowStep step;
    [ObservableProperty] private int settingsSectionIndex;
    [ObservableProperty] private bool hasUnsavedSettings;
    [ObservableProperty] private string settingsStatus = "";
    [ObservableProperty] private string taskSourceLanguage = "自动识别";
    [ObservableProperty] private string taskTargetLanguage = "中文";
    [ObservableProperty] private int taskProviderIndex = (int)TranslationProvider.DeepSeek;
    [ObservableProperty] private string inputPath = "";
    [ObservableProperty] private string mediaSummary = "";
    [ObservableProperty] private string elevenLabsKey = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isInitializing;
    [ObservableProperty] private bool isSavingSettings;
    [ObservableProperty] private string status = "待开始";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string settingsError = "";
    [ObservableProperty] private string engineStatus = "尚未测试连接";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunButtonText)), NotifyPropertyChangedFor(nameof(CanResume))] private bool canContinue;
    [ObservableProperty] private string preview = "识别完成后，可在这里查看翻译请求。";
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string systemTemplate = "";
    [ObservableProperty] private string userTemplate = "";
    [ObservableProperty] private string promptVariableStatus = "";
    public ObservableCollection<CueRow> Rows { get; } = [];
    public SelectionModel<CueRow> CueSelection { get; } = new() { SingleSelect = false };
    public bool HasCueSelection => CueSelection.Count > 0;
    public string CueSelectionSummary => $"已选 {CueSelection.Count} 条字幕";
    public ObservableCollection<string> Log { get; } = [];
    public SelectionModel<string> LogSelection { get; } = new() { SingleSelect = false };
    public bool CanCopyLogs => LogSelection.Count > 0;
    public bool HasLogs => Log.Count > 0;
    public string[] ProviderNames { get; } = ["OpenAI 兼容服务", "DeepSeek", "本地模型推理"];
    public string[] SourceLanguageNames => SpeechLanguages.Names;
    public string DefaultSourceLanguage
    {
        get => Settings.SourceLanguage;
        set
        {
            Settings.SourceLanguage = value;
            OnPropertyChanged();
        }
    }
    public string[] ThemeNames { get; } = ["系统", "浅色", "深色"];
    public string[] EffortNames { get; } = ["低", "高", "最高"];
    public string[] ReasoningCapabilityNames { get; } = ["不设置", "服务自定义参数", "标准思考强度参数"];
    public int ThinkingEffortIndex { get => Array.IndexOf(new[] { "low", "high", "max" }, Settings.ThinkingEffort); set { if (value is >= 0 and <= 2) Settings.ThinkingEffort = new[] { "low", "high", "max" }[value]; } }
    public int ReasoningCapabilityIndex { get => (int)Settings.ReasoningCapability; set { if (value is >= 0 and <= 2) Settings.ReasoningCapability = (ReasoningCapability)value; } }
    public string RunButtonText => TaskOptionsChanged ? "应用选项并处理" : CanContinue ? "继续未完成部分" : "开始处理";
    public string TestEngineButtonText => IsLocal ? "测试本地翻译" : "测试连接";
    public string EngineTestHint => IsLocal ? "使用所选模型试译一句短句。" : "测试会发送一句短句，可能产生费用。";
    public string TopPHint => IsDeepSeek ? "仅在思考模式下使用，范围为 0.95～1。" : "留空使用服务默认值。";
    public bool CanEditTopP => !IsDeepSeek || Settings.ThinkingEnabled;
    public int ProviderIndex { get => (int)Settings.Provider; set { if (value < 0 || value == (int)Settings.Provider) return; Settings.Provider = (TranslationProvider)value; } }
    public bool IsLocal => Settings.Provider == TranslationProvider.Local;
    public bool IsCloud => !IsLocal;
    public bool IsDeepSeek => Settings.Provider == TranslationProvider.DeepSeek;
    public bool IsCompatible => Settings.Provider == TranslationProvider.Compatible;
    public bool CanEditSampling => !IsDeepSeek || !Settings.ThinkingEnabled;
    public bool CanRun => CanConfigure && !string.IsNullOrWhiteSpace(InputPath) && string.IsNullOrEmpty(RunCredentialHint) &&
        (SelectedMedia is null || !SelectedMedia.Document.Translation.IsReady || TaskOptionsChanged);
    public bool CanExport => !IsBusy && _isComplete && Rows.Count > 0 && Rows.All(row => row.Cue.TranslatedText is not null);
    public bool CanRetryCue => CanConfigure && SelectedMedia?.Document.Translation.ResultPath.Length > 0 && HasCueSelection && _jobSettings is not null && string.IsNullOrEmpty(TranslationCredentialHint(_jobSettings.Provider, _jobSettings.BaseUrl));
    public bool CanPreview => !IsBusy && Rows.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public bool CanConfigure => !IsBusy && !IsImporting && !IsInitializing && !IsSavingSettings && !IsCredentialBusy && !_disposed;
    public string ResultSummary => Rows.Count == 0 ? "字幕预览" : $"共 {Rows.Count} 条字幕 · 已翻译 {Rows.Count(row => row.Cue.TranslatedText is not null)} 条";
    public IReadOnlyList<PromptVariableItem> PromptVariables => PromptVariableItem.All;
    public bool IsWorkspace => Page == AppPage.Workspace;
    public bool IsSettings => Page == AppPage.Settings;
    public bool IsLogs => Page == AppPage.Logs;
    public bool IsFileStep => Step == WorkflowStep.File;
    public bool IsOptionsStep => Step == WorkflowStep.Options;
    public bool IsResultsStep => Step == WorkflowStep.Results;
    public bool CanAcceptDrop => IsWorkspace && HasProject && CanConfigure;
    public bool CanGoToOptions => CanNext && !string.IsNullOrEmpty(MediaSummary);
    public bool CanGoToResults => CanConfigure && _jobSettings is not null;
    public bool IsProcessing => IsBusy && _isMediaJob;
    public bool CanResume => CanContinue && IsResultsStep && CanRun;
    public bool CanStartNew => CanConfigure && IsResultsStep && _jobSettings is not null;
    public string FileName => string.IsNullOrWhiteSpace(InputPath) ? "尚未选择文件" : Path.GetFileName(InputPath);
    public string TaskPromptSummary
    {
        get { var profile = TaskPipeline ?? _savedSettings.GetPipeline((TranslationProvider)TaskProviderIndex); return profile.Name; }
    }
    public string TaskServiceSummary => TaskProviderIndex == (int)TranslationProvider.Local ? $"本地模型推理 · {EmbeddedModelCatalog.Get(_savedSettings.LocalOptions().ModelId).Name}" : $"{ProviderNames[Math.Clamp(TaskProviderIndex, 0, 2)]} · {_savedSettings.CloudModel}";
    public string SettingsHint => IsBusy ? "任务正在进行，相关设置暂时无法修改。" : HasUnsavedSettings || HasPromptEdits ? "有未保存的修改。保存后用于新的处理。" : "已保存的设置用于新的处理。";

    public MainWindowViewModel(IDesktopJobService jobs, ConfigurationStore store, IWindowDialogs dialogs, CredentialStore? credentials = null, IModelDownloadService? downloads = null, ILlamaPackageService? packages = null)
    {
        _jobs = jobs; _store = store; _dialogs = dialogs; _credentials = credentials ?? new CredentialStore(store.Root);
        InitializeProjects();
        Settings = store.CreateDefaults(); InitializeModelDownloads(downloads);
        InitializeLlamaPackages(packages);
        LoadCredentials();
        Settings.PropertyChanged += SettingsChanged;
        CueSelection.Source = Rows;
        CueSelection.SelectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCueSelection)); OnPropertyChanged(nameof(CueSelectionSummary)); RefreshCommands();
        };
        LogSelection.Source = Log;
        LogSelection.SelectionChanged += (_, _) => { OnPropertyChanged(nameof(CanCopyLogs)); CopyLogsCommand.NotifyCanExecuteChanged(); };
        Log.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasLogs)); SelectAllLogsCommand.NotifyCanExecuteChanged(); };
        InitializePipelineLibrary();
        _savedSettings = ConfigurationStore.Snapshot(Settings);
        RefreshTaskPipelines();
        HasUnsavedSettings = false;
    }
    private AppSettings TaskSettings()
    {
        var snapshot = ConfigurationStore.Snapshot(_taskOptionBase ?? _savedSettings);
        snapshot.Provider = (TranslationProvider)TaskProviderIndex;
        snapshot.TaskPipelineId = TaskPipeline?.Id;
        var source = SpeechLanguages.Resolve(TaskSourceLanguage);
        snapshot.SourceCode = SpeechLanguages.AsrCode(source, snapshot.SourceCode);
        snapshot.SourceLanguage = source?.Name ?? "自动识别";
        snapshot.TargetLanguage = TaskTargetLanguage;
        return snapshot;
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshInferenceSettings(args.PropertyName);
        if (args.PropertyName == nameof(AppSettings.ReduceMotion)) Activity.ReduceMotion = Settings.ReduceMotion;
        ModelSettingsChanged(args);
        LlamaSettingsChanged(args.PropertyName);
        if (args.PropertyName == nameof(AppSettings.SourceLanguage) || _loadingSettings) OnPropertyChanged(nameof(DefaultSourceLanguage));
        if (args.PropertyName == nameof(AppSettings.Provider))
        {
            OnPropertyChanged(nameof(ProviderIndex)); OnPropertyChanged(nameof(ProviderIndex)); OnPropertyChanged(nameof(IsLocal)); OnPropertyChanged(nameof(IsCloud)); OnPropertyChanged(nameof(IsDeepSeek)); OnPropertyChanged(nameof(IsCompatible));
        }
        if (args.PropertyName is nameof(AppSettings.Provider) or nameof(AppSettings.LocalModelId) or nameof(AppSettings.BaseUrl) or nameof(AppSettings.CloudModel) or nameof(AppSettings.DefaultPipelineId) or nameof(AppSettings.PipelineLibrary))
            RefreshPromptAssociations();
        if (args.PropertyName is not (nameof(AppSettings.Theme) or nameof(AppSettings.ReduceMotion)))
        { ResetEngineStatus(); }
        if (!_loadingSettings) HasUnsavedSettings = true;
        OnPropertyChanged(nameof(CanEditSampling));
        OnPropertyChanged(nameof(CanEditTopP)); OnPropertyChanged(nameof(ThinkingEffortIndex)); OnPropertyChanged(nameof(ReasoningCapabilityIndex));
        RefreshCommands();
    }
    private void ResetEngineStatus()
    {
        EngineStatus = IsLocal ? "尚未测试本地翻译" : "尚未测试连接";
        OnPropertyChanged(nameof(TestEngineButtonText)); OnPropertyChanged(nameof(EngineTestHint)); OnPropertyChanged(nameof(TopPHint));
    }
    partial void OnInputPathChanged(string value)
    { OnPropertyChanged(nameof(FileName)); if (_restoringTask) return; _settingMedia = true; SelectedMedia = null; _settingMedia = false; _taskOptionBase = null; CueSelection.Clear(); Rows.Clear(); _isComplete = false; _hasFinalCues = false; _jobSettings = null; _jobSource = null; CanContinue = false; Step = WorkflowStep.File; Error = ""; Status = "待开始"; MediaSummary = ""; OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(HasRows)); RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(IsProcessing)); OnPropertyChanged(nameof(ShowActivitySettings)); OnPropertyChanged(nameof(SettingsHint)); Workspace?.Refresh(); }
    partial void OnIsInitializingChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(ShowProjectEmptyState)); }
    partial void OnIsSavingSettingsChanged(bool value) { RefreshCommands(); if (!value) ResumePendingWorkspaceAction(); }
    partial void OnErrorChanged(string value)
    {
        if (value.Length == 0) SettingsError = "";
        else if (Page == AppPage.Settings && !IsProcessing) SettingsError = value;
        OnPropertyChanged(nameof(HasWorkspaceError));
    }
    partial void OnProfileNameChanged(string value) => PromptChanged();
    partial void OnSystemTemplateChanged(string value) => PromptChanged();
    partial void OnUserTemplateChanged(string value) => PromptChanged();
    partial void OnHasUnsavedSettingsChanged(bool value) { OnPropertyChanged(nameof(SettingsHint)); OnPropertyChanged(nameof(SettingsFooterStatus)); if (value) SettingsStatus = ""; }
    partial void OnTaskProviderIndexChanged(int value) { OnPropertyChanged(nameof(TaskPipeline)); OnPropertyChanged(nameof(TaskPromptSummary)); OnPropertyChanged(nameof(TaskServiceSummary)); OnPropertyChanged(nameof(TaskOptionsChanged)); OnPropertyChanged(nameof(RunButtonText)); RefreshCommands(); }
    partial void OnTaskSourceLanguageChanged(string value) { OnPropertyChanged(nameof(TaskOptionsChanged)); OnPropertyChanged(nameof(RunButtonText)); RefreshCommands(); }
    partial void OnTaskTargetLanguageChanged(string value) { OnPropertyChanged(nameof(TaskOptionsChanged)); OnPropertyChanged(nameof(RunButtonText)); RefreshCommands(); }
    partial void OnPageChanged(AppPage value) { OnPropertyChanged(nameof(IsProjects)); OnPropertyChanged(nameof(IsProjectNavigation)); OnPropertyChanged(nameof(IsWorkspace)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsLogs)); OnPropertyChanged(nameof(CanAcceptDrop)); }
    partial void OnStepChanged(WorkflowStep value) { OnPropertyChanged(nameof(IsFileStep)); OnPropertyChanged(nameof(IsOptionsStep)); OnPropertyChanged(nameof(IsResultsStep)); RefreshCommands(); }
    private void RefreshCommands()
    {
        RefreshCredentialCommands();
        OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanRetryCue)); OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanManageProject)); OnPropertyChanged(nameof(CanBatch)); OnPropertyChanged(nameof(CanOperateStage));
        CreateProjectCommand.NotifyCanExecuteChanged(); RenameProjectCommand.NotifyCanExecuteChanged(); AddMediaCommand.NotifyCanExecuteChanged();
        RunProjectCommand.NotifyCanExecuteChanged(); RunStageCommand.NotifyCanExecuteChanged(); UseSavedTaskSettingsCommand.NotifyCanExecuteChanged(); ExportPreviousCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged(); RetryCueCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); PreviewRequestCommand.NotifyCanExecuteChanged();
        ChooseMediaCommand.NotifyCanExecuteChanged(); ChooseModelCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged(); TestEngineCommand.NotifyCanExecuteChanged(); RefreshPromptCommands();
        DownloadModelCommand.NotifyCanExecuteChanged();
        DownloadLlamaCommand.NotifyCanExecuteChanged(); ChooseLlamaServerCommand.NotifyCanExecuteChanged();
        CopyPromptVariableCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAcceptDrop)); OnPropertyChanged(nameof(CanGoToOptions)); OnPropertyChanged(nameof(CanGoToResults)); OnPropertyChanged(nameof(CanStartNew));
        OnPropertyChanged(nameof(CanResume));
        NextStepCommand.NotifyCanExecuteChanged(); FileStepCommand.NotifyCanExecuteChanged(); OptionsStepCommand.NotifyCanExecuteChanged(); ResultsStepCommand.NotifyCanExecuteChanged(); NewTaskCommand.NotifyCanExecuteChanged();
        Workspace?.Refresh();
    }
    [RelayCommand] private void ShowWorkspace() => Page = AppPage.Workspace;
    [RelayCommand] private void ShowSettings() => Page = AppPage.Settings;
    [RelayCommand] private void ShowLogs() => Page = AppPage.Logs;
    [RelayCommand(CanExecute = nameof(CanCopyLogs))]
    private async Task CopyLogsAsync()
    {
        try { await _dialogs.CopyTextAsync(string.Join(Environment.NewLine, LogSelection.SelectedIndexes.Order().Select(index => Log[index]))); }
        catch (Exception exception) { ReportError(exception); }
    }
    [RelayCommand(CanExecute = nameof(HasLogs))]
    private void SelectAllLogs() => LogSelection.SelectAll();
    private bool CanCopyPromptVariable(PromptVariableItem? variable) => CanConfigure && variable is not null && PromptVariables.Contains(variable);
    [RelayCommand(CanExecute = nameof(CanCopyPromptVariable))]
    private async Task CopyPromptVariableAsync(PromptVariableItem? variable)
    {
        if (!CanCopyPromptVariable(variable)) return;
        PromptVariableStatus = "";
        try { await _dialogs.CopyTextAsync(variable!.Placeholder); PromptVariableStatus = $"已复制 {variable.Placeholder}"; }
        catch (Exception exception) { PromptVariableStatus = "复制失败，请重试。"; ReportError(exception); }
    }
    [RelayCommand] private void ConfigureSpeech() { SettingsSectionIndex = 0; ShowSettings(); }
    [RelayCommand] private void ConfigureTranslation() { if (CanConfigure) ProviderIndex = TaskProviderIndex; SettingsSectionIndex = 1; ShowSettings(); }
    [RelayCommand] private void ConfigurePrompt() { if (CanConfigure) { ProviderIndex = TaskProviderIndex; SelectedPipeline = DefaultPipeline; } SettingsSectionIndex = 2; ShowSettings(); }
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void FileStep() => Step = WorkflowStep.File;
    [RelayCommand(CanExecute = nameof(CanGoToOptions))] private void OptionsStep() { if (CanGoToOptions) Step = WorkflowStep.Options; }
    [RelayCommand(CanExecute = nameof(CanGoToResults))] private void ResultsStep() => Step = WorkflowStep.Results;
    [RelayCommand(CanExecute = nameof(CanStartNew))] private void NewTask() { InputPath = ""; ApplyTaskDefaults(); }
    private void ApplyTaskDefaults() { _taskPipelineId = null; TaskProviderIndex = (int)_savedSettings.Provider; TaskSourceLanguage = _savedSettings.SourceLanguage; TaskTargetLanguage = _savedSettings.TargetLanguage; RefreshTaskPipelines(); }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextStepAsync() => ExecuteAsync(async token =>
    {
        if (!IsUnlocked || !string.IsNullOrEmpty(SpeechCredentialHint)) throw new ServiceCredentialException("ElevenLabs");
        await PrepareAsync("正在检查媒体…", async _ =>
        {
            var media = await _jobs.ProbeAsync(InputPath, _savedSettings, token);
            MediaSummary = $"时长 {media.Duration:hh\\:mm\\:ss}";
        }, token);
        Step = WorkflowStep.Options;
    }, probing: true);
    [RelayCommand]
    private Task InitializeAsync() => _initialization ??= LoadSettingsAsync();
    private async Task LoadSettingsAsync()
    {
        using var feedback = BeginFeedback("正在加载设置和项目…");
        IsInitializing = true;
        try
        {
            var loaded = await _store.LoadAsync();
            if (loaded is not null)
            {
                ConfigurationStore.Validate(loaded);
                _loadingSettings = true;
                Settings.PropertyChanged -= SettingsChanged; Settings = loaded; Settings.PropertyChanged += SettingsChanged;
                SettingsChanged(this, new(nameof(AppSettings.Provider)));
                _loadingSettings = false;
            }
            InitializePipelineLibrary();
            _savedSettings = ConfigurationStore.Snapshot(Settings); ApplyTaskDefaults(); HasUnsavedSettings = false;
            AddLog("设置已加载。");
            await LoadProjectsAsync();
            if (!string.IsNullOrWhiteSpace(_store.LastLoadNotice)) { SettingsStatus = _store.LastLoadNotice; AddLog(SettingsStatus); }
        }
        catch (Exception exception) { ReportError(exception); }
        finally { _loadingSettings = false; IsInitializing = false; }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ChooseMediaAsync()
    {
        try { var file = await _dialogs.OpenAsync("选择视频或音频", ["*.mp4", "*.mkv", "*.mov", "*.mp3", "*.wav", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.webm"]); if (file is not null) InputPath = file; }
        catch (Exception exception) { ReportError(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ChooseModelAsync()
    {
        try
        {
            var file = await _dialogs.OpenAsync("选择模型文件", ["*.gguf"]);
            if (file is not null) SelectModelFile(file);
        }
        catch (Exception exception) { ReportError(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task SaveSettingsAsync()
    {
        if (!CanConfigure) return;
        IsSavingSettings = true;
        using var feedback = BeginFeedback("正在保存设置…");
        try
        {
            var snapshot = ConfigurationStore.Snapshot(Settings);
            if (!CommitPromptEditor(snapshot)) return;
            _store.PopulateModels(snapshot); ConfigurationStore.Validate(snapshot); ConfigurationStore.ValidateSecrets(snapshot, Secrets);
            if (!await SaveCredentialDraftAsync()) { Error = CredentialStatus; return; }
            await _store.SaveAsync(snapshot, Secrets);
            Settings.SourceCode = snapshot.SourceCode; Settings.SourceLanguage = snapshot.SourceLanguage;
            Settings.ModelFiles = new(snapshot.ModelFiles, StringComparer.Ordinal);
            Settings.Models = new(snapshot.Models); Settings.PipelineModelDefaults = new(snapshot.PipelineModelDefaults);
            Settings.PipelineLibrary = new(snapshot.PipelineLibrary, StringComparer.Ordinal);
            if (_selectedPrompt is not null && HasPromptEdits) _selectedPrompt = snapshot.FindPipeline(_selectedPrompt.Id);
            HasPromptEdits = false; RefreshPipelineItems();
            OnPropertyChanged(nameof(SelectedPipeline)); OnPropertyChanged(nameof(VisibleSelectedPipeline));
            _savedSettings = snapshot; RefreshTaskPipelines(); HasUnsavedSettings = false; SettingsStatus = "设置已保存";
            OnPropertyChanged(nameof(TaskPromptSummary)); OnPropertyChanged(nameof(TaskServiceSummary));
            Error = ""; AddLog("设置和字幕管线已保存。"); Workspace.RefreshModels(); LoadPipelineModels(); RefreshCommands();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsSavingSettings = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync()
    {
        if (!CanRun) return Task.CompletedTask;
        var item = SelectedMedia;
        return ExecuteAsync(token => RunCurrentTaskAsync(token, target: item), mediaJob: true);
    }
    [RelayCommand(CanExecute = nameof(CanRetryCue))]
    private async Task RetryCueAsync()
    {
        if (!CanRetryCue) return;
        var selected = CueSelection.SelectedIndexes.Order().Select(index => Rows[index]).ToArray();
        var ids = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var jobSettings = _jobSettings!;
        var message = selected.Length == 1 ? $"重新翻译第 {selected[0].Number} 条字幕？" : $"重新翻译所选的 {selected.Length} 条字幕？";
        if (!await _dialogs.ConfirmAsync(message + "所选字幕的现有译文将被清除。" + (jobSettings.Provider != TranslationProvider.Local ? "云端翻译可能产生费用。" : ""))) return;
        if (!CanRetryCue) return;
        if (!ReferenceEquals(jobSettings, _jobSettings) || ids.Any(id => !Rows.Any(row => row.Id == id))) return;
        await ExecuteAsync(async token =>
        {
            _isComplete = false;
            SetRows(Rows.Select(row => ids.Contains(row.Id) ? row.Cue with { TranslatedText = null } : row.Cue).ToArray());
            await RunCurrentTaskAsync(token, TaskStage.Translation, ProcessingMode.Restart, ids, ConfigurationStore.Snapshot(jobSettings));
        }, mediaJob: true);
    }
    private void ApplyTranslationProgress(TranslationProgress value)
    {
        Status = Stage(value.Stage);
        if (value.Cues is not null)
        {
            // 并发批次的通知可能交错；晚到的快照不能清除已显示的有效译文。
            var accepted = Rows.ToDictionary(row => row.Id, row => row.Cue.TranslatedText);
            SetRows(value.Cues.Select(cue => cue.TranslatedText is null ? cue with { TranslatedText = accepted.GetValueOrDefault(cue.Id) } : cue).ToArray(), value.FailedIds?.ToHashSet());
        }
        if (value.Stage is "Translating" or "TargetedRetry")
            foreach (var row in Rows.Where(row => value.CueIds.Contains(row.Id) && row.Cue.TranslatedText is null)) row.State = CueTranslationState.Translating;
        if (value.CueIds.Count > 0) AddLog($"{Status}：{string.Join(", ", value.CueIds)}");
    }
    [RelayCommand]
    private void Cancel() { if (IsBusy) { Activity.Cancel(); if (_isMediaJob) Workspace.ActiveText = "正在取消…"; if (_isMediaJob && (_mediaOperationTarget is null || SelectedMedia == _mediaOperationTarget)) Status = "正在取消…"; if (IsPreparing) { PreparationStatus = "正在取消…"; if (_preparationEngineTest) EngineStatus = "正在取消…"; } _cancellation?.Cancel(); } }
    [RelayCommand(CanExecute = nameof(CanTestEngine))]
    private Task TestEngineAsync() => ExecuteAsync(async token =>
    {
        if (!string.IsNullOrEmpty(EngineCredentialHint)) return;
        var snapshot = ConfigurationStore.Snapshot(Settings);
        string result = "";
        await PrepareAsync("正在检查翻译设置…", async progress =>
        {
            ConfigurationStore.Validate(snapshot, validateAllProfiles: false); ConfigurationStore.ValidateSecrets(snapshot, Secrets);
            result = await _jobs.TestEngineAsync(snapshot, KeyFor(snapshot.Provider), token, progress);
        }, token, engineTest: true);
        EngineStatus = result; AddLog(EngineStatus);
        if (!string.IsNullOrWhiteSpace(_jobs.EngineDiagnostic)) AddLog(_jobs.EngineDiagnostic);
    });
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewRequestAsync() => ExecuteAsync(async token =>
    {
        Activity.Update("正在生成请求预览…", reset: true);
        Preview = await _jobs.PreviewAsync(Rows.Select(row => row.Cue).ToArray(), _jobSettings!, token);
    }, probing: true);
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        try
        {
            var safeLanguage = string.Concat(_jobSettings!.TargetLanguage.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
            var file = await _dialogs.SaveSrtAsync($"{Path.GetFileNameWithoutExtension(_jobInputPath)}.{safeLanguage}.srt");
            if (file is null) return;
            if (Path.GetExtension(file).ToLowerInvariant() != ".srt") throw new ArgumentException("请将字幕保存为 .srt 文件。");
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(_jobInputPath), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择其他保存路径，以免覆盖原媒体文件。");
            if (File.Exists(file) && !await _dialogs.ConfirmAsync("目标 SRT 已存在，确认覆盖？")) return;
            if (!CanExport) throw new ArgumentException("翻译设置或译文已更改，请重新完成翻译后导出。");
            using var feedback = BeginFeedback("正在导出 SRT 字幕…");
            await AtomicFile.WriteTextAsync(file, SrtSerializer.SerializeTranslated(Rows.Select(row => row.Cue).ToArray()), true, CancellationToken.None);
            AddLog("SRT 已导出：" + file);
        }
        catch (Exception exception) { ReportError(exception); }
    }
    private Task ExecuteAsync(Func<CancellationToken, Task> action, bool mediaJob = false, bool probing = false)
    {
        if (IsBusy || IsCredentialBusy || _disposed) return Task.CompletedTask;
        if (mediaJob) Workspace.ResetProgress();
        _isMediaJob = mediaJob; IsBusy = true; Error = ""; _cancellation = new();
        if (mediaJob) { _hasStartedProcessing = false; _mediaOperationTarget = SelectedMedia; _operationProject = SelectedProject; }
        _operation = RunGuardedAsync(action, _cancellation.Token, mediaJob, probing);
        return _operation;
    }
    private async Task RunGuardedAsync(Func<CancellationToken, Task> action, CancellationToken token, bool mediaJob, bool probing)
    {
        Activity.ReduceMotion = Settings.ReduceMotion;
        using var feedback = Activity.Begin(mediaJob ? "正在开始处理…" : "正在处理…", cancellable: true, immediate: true);
        Action? configureAfter = null;
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (mediaJob && _mediaOperationTarget is not null && SelectedMedia != _mediaOperationTarget)
            { AddLog($"{_mediaOperationTarget.Name}：已取消"); return; }
            if (mediaJob && SelectedMedia is not null) _hasStartedProcessing = SelectedMedia.Document.Stages.Any(s => s.Version.Length > 0);
            if (mediaJob) { CanContinue = _hasStartedProcessing; MarkUnfinishedRows(CueTranslationState.Cancelled); }
            else if (!probing && !IsModelDownloading && !IsLlamaInstalling) EngineStatus = "测试已取消";
            if (mediaJob) Status = "已取消"; AddLog("已取消");
        }
        catch (Exception exception)
        {
            if (mediaJob && _mediaOperationTarget is not null && SelectedMedia != _mediaOperationTarget)
            { AddLog($"{_mediaOperationTarget.Name}：{UserErrorMessages.FromException(exception)}"); return; }
            if (mediaJob && SelectedMedia is not null) _hasStartedProcessing = SelectedMedia.Document.Stages.Any(s => s.Version.Length > 0);
            if (mediaJob) { CanContinue = SelectedMedia is not null && SelectedMedia.Document.Stages.Any(s => s.Version.Length > 0) && !SelectedMedia.Document.Translation.IsReady; MarkUnfinishedRows(CueTranslationState.Failed); }
            else if (!probing && !IsModelDownloading && !IsLlamaInstalling) EngineStatus = "测试失败";
            if (mediaJob) Status = "处理未完成"; ReportError(exception);
            if (mediaJob && exception is ServiceCredentialException credential)
            { configureAfter = credential.Service == "ElevenLabs" ? ConfigureSpeech : ConfigureTranslation; }
            if (probing && exception is Win32Exception { NativeErrorCode: 2 or 3 }) configureAfter = ConfigureSpeech;
        }
        finally { _mediaOperationTarget = null; _operationProject = null; IsBusy = false; _cancellation?.Dispose(); _cancellation = null; configureAfter?.Invoke(); Workspace.Refresh(); }
    }
    private void MarkUnfinishedRows(CueTranslationState state)
    { foreach (var row in Rows.Where(row => row.Cue.TranslatedText is null && row.State == CueTranslationState.Translating)) row.State = state; }
    private void SetRows(IReadOnlyList<SubtitleCue> cues, IReadOnlySet<string>? failedIds = null)
    {
        var selectedIds = CueSelection.SelectedIndexes.Select(index => Rows[index].Id).ToHashSet(StringComparer.Ordinal);
        var states = Rows.ToDictionary(row => row.Id, row => row.State);
        var sameSequence = Rows.Count == cues.Count && Rows.Select(row => row.Id).SequenceEqual(cues.Select(cue => cue.Id));
        CueSelection.Clear();
        if (!sameSequence) Rows.Clear();
        for (var index = 0; index < cues.Count; index++)
        {
            var state = failedIds?.Contains(cues[index].Id) == true ? CueTranslationState.Failed : states.GetValueOrDefault(cues[index].Id, CueTranslationState.Waiting);
            if (!sameSequence) Rows.Add(new(cues[index], index + 1) { State = state });
            else if (Rows[index].Cue != cues[index]) Rows[index] = new(cues[index], index + 1) { State = state };
            else Rows[index].State = state;
        }
        for (var index = 0; index < Rows.Count; index++)
            if (selectedIds.Contains(Rows[index].Id)) CueSelection.Select(index);
        OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(HasRows)); RefreshCommands();
    }
    private void AddLog(string message)
    { Log.Add($"{DateTime.Now:HH:mm:ss} {Sanitize(message)}"); while (Log.Count > 200) Log.RemoveAt(0); }
    private void ReportError(Exception exception)
    {
        Diagnostic = Sanitize(exception.ToString());
        Error = Sanitize(UserErrorMessages.FromException(exception));
        AddLog(Error);
        if (exception is TranscriptionFailedException failure)
            foreach (var item in failure.Categories) AddLog($"音频片段 {item.Key + 1}：{item.Value}");
        AddLog(Diagnostic);
        if (exception is LocalTranslationException local && !string.IsNullOrWhiteSpace(local.Diagnostic)) AddLog(local.Diagnostic);
    }
    private string Sanitize(string value)
    { foreach (var key in Secrets.Where(key => !string.IsNullOrWhiteSpace(key))) value = value.Replace(key, "[凭据已隐藏]", StringComparison.Ordinal); return value; }
    private void Post(CancellationToken token, Action action) => Dispatcher.UIThread.Post(() => { if (IsBusy && _cancellation?.Token == token && !token.IsCancellationRequested) action(); });
    private static string Stage(string stage) => stage switch
    { "SourceChecking" => "校验媒体文件", "AudioChecking" => "校验音频", "AudioPreparing" => "提取音频", "VadAnalyzing" => "分析语音", "ChunkPlanning" => "导出分片", "AsrUploading" => "上传识别音频", "AsrWaiting" => "等待识别结果", "AsrValidating" => "校验识别结果", "Transcribing" => "识别中", "RetryWaiting" => "等待重试", "BuildingCues" => "整理字幕", "Segmenting" => "语义分句中", "CuesReady" => "分句完成", "Exporting" => "保存识别结果", "Translating" => "翻译中", "TargetedRetry" => "重新翻译中", "Completed" => "处理完成", "PartialFailure" => "部分字幕未完成", "Cancelled" => "已取消", "Failed" => "处理未完成", _ => stage.StartsWith("Transcribing ", StringComparison.Ordinal) ? "识别中 " + stage[13..] : stage == "开始处理" ? "正在开始处理…" : "处理中" };
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        using var feedback = Activity.Begin("正在停止任务并释放资源…", immediate: true);
        _disposed = true; _cancellation?.Cancel(); _credentialCancellation.Cancel();
        await _credentialSave;
        CancelPendingRun();
        if (_operation is not null) await _operation;
        if (_credentialOperation is not null) await _credentialOperation;
        await _navigation; await _navigationTask;
        await WaitForWorkspaceAsync();
        await _jobs.DisposeAsync(); _credentials.Dispose(); LoadCredentials(); _credentialCancellation.Dispose();
        _modelHttp?.Dispose();
        _llamaHttp?.Dispose();
        Settings.PropertyChanged -= SettingsChanged;
    }
}
