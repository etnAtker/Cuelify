using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Subtitles;
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
public enum AppPage { Workspace, Logs, Settings }
public enum WorkflowStep { File, Options, Results }

public partial class CueRow(SubtitleCue cue, int number) : ObservableObject
{
    public SubtitleCue Cue { get; } = cue;
    public int Number { get; } = number;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Translation))] private CueTranslationState state;
    public string Id => Cue.Id;
    public string Time => $"{Cue.Start:hh\\:mm\\:ss\\.fff} → {Cue.End:hh\\:mm\\:ss\\.fff}";
    public string Source => Cue.SourceText;
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
    [ObservableProperty] private string status = "待开始";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string engineStatus = "尚未测试连接";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunButtonText)), NotifyPropertyChangedFor(nameof(CanResume))] private bool canContinue;
    [ObservableProperty] private string preview = "识别完成后，可在这里查看翻译请求。";
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string systemTemplate = "";
    [ObservableProperty] private string userTemplate = "";
    [ObservableProperty] private string promptVariableStatus = "";
    [ObservableProperty] private CueRow? selectedCue;
    [ObservableProperty] private string? presetSelection;
    public ObservableCollection<CueRow> Rows { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public SelectionModel<string> LogSelection { get; } = new() { SingleSelect = false };
    public bool CanCopyLogs => LogSelection.Count > 0;
    public bool HasLogs => Log.Count > 0;
    public string[] ProviderNames { get; } = ["OpenAI 兼容服务", "DeepSeek", "内嵌模型推理"];
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
    public string RunButtonText => CanContinue ? "继续处理" : "开始处理";
    public string TestEngineButtonText => IsLocal ? "测试本地翻译" : "测试连接";
    public string EngineTestHint => IsLocal ? "使用所选模型试译一句短句。" : "测试会发送一句短句，可能产生费用。";
    public string SystemPromptHint => IsLocal ? "默认翻译要求已包含在用户模板中，可按需补充。" : "设置翻译要求、语气和输出格式。";
    public string TopPHint => IsDeepSeek ? "仅在思考模式下使用，范围为 0.95～1。" : "留空使用服务默认值。";
    public bool CanEditTopP => !IsDeepSeek || Settings.ThinkingEnabled;
    public string[] PresetNames { get; } = ["通用字幕批量翻译", "通用简洁字幕", "内嵌模型简单翻译", "内嵌模型字幕翻译"];
    public int ProviderIndex { get => (int)Settings.Provider; set { if (value < 0 || value == (int)Settings.Provider) return; CaptureProfile(); Settings.Provider = (TranslationProvider)value; } }
    public bool IsLocal => Settings.Provider == TranslationProvider.Local;
    public bool IsCloud => !IsLocal;
    public bool IsDeepSeek => Settings.Provider == TranslationProvider.DeepSeek;
    public bool IsCompatible => Settings.Provider == TranslationProvider.Compatible;
    public bool CanEditSampling => !IsDeepSeek || !Settings.ThinkingEnabled;
    public bool CanRun => CanConfigure && !string.IsNullOrWhiteSpace(InputPath) && string.IsNullOrEmpty(RunCredentialHint);
    public bool CanExport => !IsBusy && _isComplete && Rows.Count > 0 && Rows.All(row => row.Cue.TranslatedText is not null);
    public string[] AvailablePresets => IsLocal ? [PromptPresets.LocalSimple.Name, PromptPresets.Local.Name] : [PromptPresets.Cloud.Name, PromptPresets.Concise.Name];
    public bool CanRetryCue => CanConfigure && SelectedCue is not null && _jobSettings is not null && string.IsNullOrEmpty(TranslationCredentialHint(_jobSettings.Provider, _jobSettings.BaseUrl));
    public bool CanPreview => !IsBusy && Rows.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public bool CanConfigure => !IsBusy && !IsInitializing && !IsCredentialBusy && !_disposed;
    public string ResultSummary => Rows.Count == 0 ? "字幕预览" : $"共 {Rows.Count} 条字幕 · 已翻译 {Rows.Count(row => row.Cue.TranslatedText is not null)} 条";
    public IReadOnlyList<PromptVariableItem> PromptVariables => PromptVariableItem.All;
    public bool IsWorkspace => Page == AppPage.Workspace;
    public bool IsSettings => Page == AppPage.Settings;
    public bool IsLogs => Page == AppPage.Logs;
    public bool IsFileStep => Step == WorkflowStep.File;
    public bool IsOptionsStep => Step == WorkflowStep.Options;
    public bool IsResultsStep => Step == WorkflowStep.Results;
    public bool CanAcceptDrop => IsWorkspace && IsFileStep && CanConfigure;
    public bool CanGoToOptions => CanNext && !string.IsNullOrEmpty(MediaSummary);
    public bool CanGoToResults => CanConfigure && _jobSettings is not null;
    public bool IsProcessing => IsBusy && _isMediaJob;
    public bool CanResume => CanContinue && IsResultsStep && CanRun;
    public bool CanStartNew => CanConfigure && IsResultsStep && _jobSettings is not null;
    public string FileName => string.IsNullOrWhiteSpace(InputPath) ? "尚未选择文件" : Path.GetFileName(InputPath);
    public string TaskPromptSummary => (TaskProviderIndex == (int)TranslationProvider.Local ? _savedSettings.GetLocalProfile() :
        TaskProviderIndex == (int)TranslationProvider.DeepSeek ? _savedSettings.DeepSeekProfile : _savedSettings.CompatibleProfile).Name;
    public string TaskServiceSummary => TaskProviderIndex == (int)TranslationProvider.Local ? $"内嵌模型推理 · {EmbeddedModelCatalog.Get(_savedSettings.LocalOptions().ModelId).Name}" : $"{ProviderNames[Math.Clamp(TaskProviderIndex, 0, 2)]} · {_savedSettings.CloudModel}";
    public string SettingsHint => IsBusy ? "任务正在进行，相关设置暂时无法修改。" : HasUnsavedSettings ? "有未保存的修改。保存后用于新的处理。" : "已保存的设置用于新的处理。";

    public MainWindowViewModel(IDesktopJobService jobs, ConfigurationStore store, IWindowDialogs dialogs, CredentialStore? credentials = null, IModelDownloadService? downloads = null)
    {
        _jobs = jobs; _store = store; _dialogs = dialogs; _credentials = credentials ?? new CredentialStore(store.Root);
        Settings = store.CreateDefaults(); InitializeModelDownloads(downloads);
        LoadCredentials();
        Settings.PropertyChanged += SettingsChanged;
        LogSelection.Source = Log;
        LogSelection.SelectionChanged += (_, _) => { OnPropertyChanged(nameof(CanCopyLogs)); CopyLogsCommand.NotifyCanExecuteChanged(); };
        Log.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasLogs)); SelectAllLogsCommand.NotifyCanExecuteChanged(); };
        LoadProfile();
        _savedSettings = ConfigurationStore.Snapshot(Settings);
        HasUnsavedSettings = false;
    }
    private AppSettings TaskSettings()
    {
        var snapshot = ConfigurationStore.Snapshot(_savedSettings);
        snapshot.Provider = (TranslationProvider)TaskProviderIndex;
        var source = SpeechLanguages.Resolve(TaskSourceLanguage);
        snapshot.SourceCode = SpeechLanguages.AsrCode(source, _savedSettings.SourceCode);
        snapshot.SourceLanguage = source?.Name ?? "自动识别";
        snapshot.TargetLanguage = TaskTargetLanguage;
        return snapshot;
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        ModelSettingsChanged(args);
        if (args.PropertyName == nameof(AppSettings.SourceLanguage) || _loadingSettings) OnPropertyChanged(nameof(DefaultSourceLanguage));
        if (args.PropertyName == nameof(AppSettings.Provider) || args.PropertyName == nameof(AppSettings.EmbeddedModelId) && IsLocal)
        {
            LoadProfile(); OnPropertyChanged(nameof(AvailablePresets)); OnPropertyChanged(nameof(ProviderIndex)); OnPropertyChanged(nameof(IsLocal)); OnPropertyChanged(nameof(IsCloud)); OnPropertyChanged(nameof(IsDeepSeek)); OnPropertyChanged(nameof(IsCompatible));
        }
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
        OnPropertyChanged(nameof(TestEngineButtonText)); OnPropertyChanged(nameof(EngineTestHint)); OnPropertyChanged(nameof(SystemPromptHint)); OnPropertyChanged(nameof(TopPHint));
    }
    private void LoadProfile()
    { var profile = Settings.GetProfile(); ProfileName = profile.Name; SystemTemplate = profile.SystemTemplate; UserTemplate = profile.UserTemplate; PresetSelection = null; }
    private void CaptureProfile() => Settings.SetProfile(new(ProfileName, SystemTemplate, UserTemplate, IsLocal ? TranslationOutputFormat.PlainText : TranslationOutputFormat.CueIdJson));
    partial void OnInputPathChanged(string value)
    { Rows.Clear(); SelectedCue = null; _isComplete = false; _jobSettings = null; CanContinue = false; Step = WorkflowStep.File; Error = ""; Status = "待开始"; MediaSummary = ""; OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(HasRows)); RefreshCommands(); }
    partial void OnSelectedCueChanged(CueRow? value) => RefreshCommands();
    partial void OnPresetSelectionChanged(string? value) { if (value is not null && AvailablePresets.Contains(value)) ResetPrompt(value); }
    partial void OnIsBusyChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(IsProcessing)); OnPropertyChanged(nameof(SettingsHint)); }
    partial void OnIsInitializingChanged(bool value) => RefreshCommands();
    partial void OnProfileNameChanged(string value) => PromptChanged();
    partial void OnSystemTemplateChanged(string value) => PromptChanged();
    partial void OnUserTemplateChanged(string value) => PromptChanged();
    private void PromptChanged() { if (!_loadingSettings) HasUnsavedSettings = true; ResetEngineStatus(); }
    partial void OnHasUnsavedSettingsChanged(bool value) { OnPropertyChanged(nameof(SettingsHint)); if (value) SettingsStatus = ""; }
    partial void OnTaskProviderIndexChanged(int value) { OnPropertyChanged(nameof(TaskPromptSummary)); OnPropertyChanged(nameof(TaskServiceSummary)); RefreshCommands(); }
    partial void OnPageChanged(AppPage value) { OnPropertyChanged(nameof(IsWorkspace)); OnPropertyChanged(nameof(IsSettings)); OnPropertyChanged(nameof(IsLogs)); OnPropertyChanged(nameof(CanAcceptDrop)); }
    partial void OnStepChanged(WorkflowStep value) { OnPropertyChanged(nameof(IsFileStep)); OnPropertyChanged(nameof(IsOptionsStep)); OnPropertyChanged(nameof(IsResultsStep)); RefreshCommands(); }
    private void RefreshCommands()
    {
        RefreshCredentialCommands();
        OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanRetryCue)); OnPropertyChanged(nameof(CanPreview));
        RunCommand.NotifyCanExecuteChanged(); RetryCueCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); PreviewRequestCommand.NotifyCanExecuteChanged();
        ChooseMediaCommand.NotifyCanExecuteChanged(); ChooseModelCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged(); TestEngineCommand.NotifyCanExecuteChanged(); ResetPromptCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
        CopyPromptVariableCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAcceptDrop)); OnPropertyChanged(nameof(CanGoToOptions)); OnPropertyChanged(nameof(CanGoToResults)); OnPropertyChanged(nameof(CanStartNew));
        OnPropertyChanged(nameof(CanResume));
        NextStepCommand.NotifyCanExecuteChanged(); FileStepCommand.NotifyCanExecuteChanged(); OptionsStepCommand.NotifyCanExecuteChanged(); ResultsStepCommand.NotifyCanExecuteChanged(); NewTaskCommand.NotifyCanExecuteChanged();
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
    [RelayCommand] private void ConfigurePrompt() { if (CanConfigure) ProviderIndex = TaskProviderIndex; SettingsSectionIndex = 2; ShowSettings(); }
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void FileStep() => Step = WorkflowStep.File;
    [RelayCommand(CanExecute = nameof(CanGoToOptions))] private void OptionsStep() { if (CanGoToOptions) Step = WorkflowStep.Options; }
    [RelayCommand(CanExecute = nameof(CanGoToResults))] private void ResultsStep() => Step = WorkflowStep.Results;
    [RelayCommand(CanExecute = nameof(CanStartNew))] private void NewTask() { InputPath = ""; ApplyTaskDefaults(); }
    private void ApplyTaskDefaults() { TaskProviderIndex = (int)_savedSettings.Provider; TaskSourceLanguage = _savedSettings.SourceLanguage; TaskTargetLanguage = _savedSettings.TargetLanguage; }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextStepAsync() => ExecuteAsync(async token =>
    {
        if (!IsUnlocked || !string.IsNullOrEmpty(SpeechCredentialHint)) throw new ServiceCredentialException("ElevenLabs");
        var media = await _jobs.ProbeAsync(InputPath, _savedSettings, token);
        MediaSummary = $"时长 {media.Duration:hh\\:mm\\:ss}";
        Step = WorkflowStep.Options;
    }, probing: true);
    [RelayCommand]
    private Task InitializeAsync() => _initialization ??= LoadSettingsAsync();
    private async Task LoadSettingsAsync()
    {
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
            _savedSettings = ConfigurationStore.Snapshot(Settings); ApplyTaskDefaults(); HasUnsavedSettings = false;
            AddLog("设置已加载。");
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
            if (file is not null) await VerifySelectedModelAsync(file);
        }
        catch (Exception exception) { ReportError(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task SaveSettingsAsync()
    {
        try { CaptureProfile(); await _store.SaveAsync(Settings, Secrets); _savedSettings = ConfigurationStore.Snapshot(Settings); HasUnsavedSettings = false; SettingsStatus = "设置已保存"; OnPropertyChanged(nameof(TaskPromptSummary)); OnPropertyChanged(nameof(TaskServiceSummary)); Error = ""; AddLog("设置和提示词已保存。"); RefreshCommands(); }
        catch (Exception exception) { ReportError(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void ResetPrompt(string? name)
    {
        var profile = IsLocal ? name == PromptPresets.LocalSimple.Name ? PromptPresets.LocalSimple :
            name == PromptPresets.Local.Name ? PromptPresets.Local : SelectedEmbeddedModel.DefaultProfile :
            name == PromptPresets.Concise.Name ? PromptPresets.Concise : PromptPresets.Cloud;
        Settings.SetProfile(profile);
        LoadProfile();
    }
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => ExecuteAsync(async token =>
    {
        if (!IsUnlocked || !string.IsNullOrEmpty(RunCredentialHint)) return;
        var snapshot = TaskSettings(); ConfigurationStore.Validate(snapshot);
        if (string.IsNullOrWhiteSpace(ElevenLabsKey)) throw new ServiceCredentialException("ElevenLabs");
        if (snapshot.Provider != TranslationProvider.Local && string.IsNullOrWhiteSpace(KeyFor(snapshot.Provider))) throw new ServiceCredentialException("翻译服务");
        if (snapshot.Provider == TranslationProvider.Local) await Task.Run(() => EmbeddedModelOptions.VerifyIdentityAsync(snapshot.ModelPath, token, snapshot.ModelSha256), token);
        var media = await _jobs.ProbeAsync(InputPath, snapshot, token);
        MediaSummary = $"时长 {media.Duration:hh\\:mm\\:ss}";
        _jobSettings = snapshot; _jobInputPath = InputPath; _isComplete = false; Step = WorkflowStep.Results;
        SetRows([]);
        _hasStartedProcessing = true;
        Status = "识别中";
        var source = await _jobs.TranscribeAsync(InputPath, snapshot, ElevenLabsKey, new Progress<TranscriptionProgress>(value => Post(token, () => { Status = Stage(value.Stage); AddLog($"{Status} {value.ChunkIndex?.ToString() ?? ""}/{value.TotalChunks?.ToString() ?? ""}"); })), token);
        SetRows(source.Cues); AddLog($"识别完成：请求 {source.AsrRequests}，缓存 {source.CacheHits}。");
        if (source.Cues.Count == 0) { CanContinue = false; Status = "未识别到语音"; return; }
        await TranslateAsync(snapshot, null, token);
    }, mediaJob: true);
    [RelayCommand(CanExecute = nameof(CanRetryCue))]
    private async Task RetryCueAsync()
    {
        if (!CanRetryCue) return;
        var id = SelectedCue!.Id;
        if (!await _dialogs.ConfirmAsync($"重新翻译第 {SelectedCue.Number} 条字幕？现有译文将被清除。" + (_jobSettings!.Provider != TranslationProvider.Local ? "云端翻译可能产生费用。" : ""))) return;
        if (!CanRetryCue) return;
        await ExecuteAsync(async token => { var snapshot = ConfigurationStore.Snapshot(_jobSettings!); _isComplete = false; SetRows(Rows.Select(row => row.Id == id ? row.Cue with { TranslatedText = null } : row.Cue).ToArray()); _hasStartedProcessing = true; await TranslateAsync(snapshot, new HashSet<string> { id }, token); }, mediaJob: true);
    }
    private async Task TranslateAsync(AppSettings snapshot, IReadOnlySet<string>? force, CancellationToken token)
    {
        Status = "翻译中";
        foreach (var row in Rows.Where(row => row.Cue.TranslatedText is null && (force is null || force.Contains(row.Id)))) row.State = CueTranslationState.Waiting;
        var result = await _jobs.TranslateAsync(Rows.Select(row => row.Cue).ToArray(), snapshot, KeyFor(snapshot.Provider), force,
            new Progress<TranslationProgress>(value => Post(token, () => ApplyTranslationProgress(value))), token);
        token.ThrowIfCancellationRequested(); SetRows(result.Cues, result.FailedIds.ToHashSet());
        CanContinue = !result.IsComplete;
        if (result.IsComplete) { _isComplete = true; Status = "翻译完成，可以导出字幕"; }
        else
        {
            Status = "部分字幕翻译失败";
            Error = $"{result.FailedIds.Count} 条字幕翻译失败。" + string.Join(" ", result.FailureReasons.Values.Select(value => UserErrorMessages.FromCategory(value)).Distinct().Take(3)) + "解决问题后可继续处理，或选中失败的字幕重新翻译。";
            foreach (var id in result.FailedIds) AddLog($"字幕 {id}：{result.FailureReasons.GetValueOrDefault(id, "译文校验失败")}");
        }
        AddLog($"翻译完成：请求 {result.EngineCalls}，缓存 {result.CacheHits}。");
        RefreshCommands();
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
        if (value.Stage == "Translating")
            foreach (var row in Rows.Where(row => value.CueIds.Contains(row.Id) && row.Cue.TranslatedText is null)) row.State = CueTranslationState.Translating;
        if (value.CueIds.Count > 0) AddLog($"{Status}：{string.Join(", ", value.CueIds)}");
    }
    [RelayCommand]
    private void Cancel() { if (IsBusy) { if (_isMediaJob) Status = "正在取消…"; _cancellation?.Cancel(); } }
    [RelayCommand(CanExecute = nameof(CanTestEngine))]
    private Task TestEngineAsync() => ExecuteAsync(async token =>
    {
        if (!string.IsNullOrEmpty(EngineCredentialHint)) return;
        CaptureProfile(); var snapshot = ConfigurationStore.Snapshot(Settings); ConfigurationStore.Validate(snapshot);
        EngineStatus = IsLocal ? "正在测试本地翻译…" : "正在测试连接…";
        EngineStatus = await _jobs.TestEngineAsync(snapshot, KeyFor(snapshot.Provider), token); AddLog(EngineStatus);
        if (!string.IsNullOrWhiteSpace(_jobs.EngineDiagnostic)) AddLog(_jobs.EngineDiagnostic);
    });
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewRequestAsync() => ExecuteAsync(async token =>
    {
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
            await AtomicFile.WriteTextAsync(file, SrtSerializer.SerializeTranslated(Rows.Select(row => row.Cue).ToArray()), true, CancellationToken.None);
            AddLog("SRT 已导出：" + file);
        }
        catch (Exception exception) { ReportError(exception); }
    }
    private Task ExecuteAsync(Func<CancellationToken, Task> action, bool mediaJob = false, bool probing = false)
    {
        if (IsBusy || IsCredentialBusy || _disposed) return Task.CompletedTask;
        _isMediaJob = mediaJob; IsBusy = true; Error = ""; _cancellation = new();
        if (mediaJob) _hasStartedProcessing = false;
        _operation = RunGuardedAsync(action, _cancellation.Token, mediaJob, probing);
        return _operation;
    }
    private async Task RunGuardedAsync(Func<CancellationToken, Task> action, CancellationToken token, bool mediaJob, bool probing)
    {
        Action? configureAfter = null;
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (mediaJob) { CanContinue = _hasStartedProcessing; MarkUnfinishedRows(CueTranslationState.Cancelled); }
            else if (!probing && !IsModelDownloading) EngineStatus = "测试已取消";
            if (mediaJob) Status = "已取消"; AddLog("已取消");
        }
        catch (Exception exception)
        {
            if (mediaJob) { CanContinue = exception is TranscriptionFailedException || (_hasStartedProcessing && exception is TimeoutException or HttpRequestException); MarkUnfinishedRows(CueTranslationState.Failed); }
            else if (!probing && !IsModelDownloading) EngineStatus = "测试失败";
            if (mediaJob) Status = "处理未完成"; ReportError(exception);
            if (mediaJob && exception is ServiceCredentialException credential)
            { configureAfter = credential.Service == "ElevenLabs" ? ConfigureSpeech : ConfigureTranslation; }
            if (probing && exception is Win32Exception { NativeErrorCode: 2 or 3 }) configureAfter = ConfigureSpeech;
        }
        finally { IsBusy = false; _cancellation?.Dispose(); _cancellation = null; configureAfter?.Invoke(); }
    }
    private void MarkUnfinishedRows(CueTranslationState state)
    { foreach (var row in Rows.Where(row => row.Cue.TranslatedText is null && row.State == CueTranslationState.Translating)) row.State = state; }
    private void SetRows(IReadOnlyList<SubtitleCue> cues, IReadOnlySet<string>? failedIds = null)
    {
        var selectedId = SelectedCue?.Id;
        var states = Rows.ToDictionary(row => row.Id, row => row.State);
        var sameSequence = Rows.Count == cues.Count && Rows.Select(row => row.Id).SequenceEqual(cues.Select(cue => cue.Id));
        if (!sameSequence) Rows.Clear();
        for (var index = 0; index < cues.Count; index++)
        {
            var state = failedIds?.Contains(cues[index].Id) == true ? CueTranslationState.Failed : states.GetValueOrDefault(cues[index].Id, CueTranslationState.Waiting);
            if (!sameSequence) Rows.Add(new(cues[index], index + 1) { State = state });
            else if (Rows[index].Cue != cues[index]) Rows[index] = new(cues[index], index + 1) { State = state };
            else Rows[index].State = state;
        }
        SelectedCue = Rows.FirstOrDefault(row => row.Id == selectedId);
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
    }
    private string Sanitize(string value)
    { foreach (var key in Secrets.Where(key => !string.IsNullOrWhiteSpace(key))) value = value.Replace(key, "[凭据已隐藏]", StringComparison.Ordinal); return value; }
    private void Post(CancellationToken token, Action action) => Dispatcher.UIThread.Post(() => { if (IsBusy && _cancellation?.Token == token && !token.IsCancellationRequested) action(); });
    private static string Stage(string stage) => stage switch
    { "AudioPreparing" => "准备音频", "VadAnalyzing" => "分析语音", "ChunkPlanning" => "准备识别", "Transcribing" => "识别中", "RetryWaiting" => "等待重试", "BuildingCues" => "整理字幕", "Exporting" => "保存识别结果", "Translating" => "翻译中", "Completed" => "处理完成", "PartialFailure" => "部分字幕未完成", "Cancelled" => "已取消", "Failed" => "处理未完成", _ => "处理中" };
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _cancellation?.Cancel(); _credentialCancellation.Cancel();
        if (_operation is not null) await _operation;
        if (_credentialOperation is not null) await _credentialOperation;
        await _jobs.DisposeAsync(); _credentials.Dispose(); LoadCredentials(); _credentialCancellation.Dispose();
        _modelHttp?.Dispose();
        Settings.PropertyChanged -= SettingsChanged;
    }
}
