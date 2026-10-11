using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Projects;
using Cuelify.Core.Speech;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;

namespace Cuelify.Desktop.ViewModels;

public sealed record ProjectListItem(ProjectDocument Document, string Summary)
{
    public string Name => Document.Name;
    public string Updated => Document.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
public sealed record WordRow(int Number, WordToken Word)
{
    public string Text => Word.Text;
    public string Start => Word.Start.ToString(@"hh\:mm\:ss\.fff");
    public string End => Word.End.ToString(@"hh\:mm\:ss\.fff");
    public string Speaker => Word.SpeakerId ?? "—";
}
public sealed record ChunkRow(PreparedChunk File, string State)
{
    public int Number => File.Chunk.Index + 1;
    public string Time => $@"{File.Chunk.Start:hh\:mm\:ss\.fff} → {File.Chunk.End:hh\:mm\:ss\.fff}";
    public string Detail => $"{File.Chunk.Duration.TotalSeconds:0.0} 秒 · {File.Bytes / 1024d / 1024d:0.00} MB";
    public string Path => File.Path;
}
public sealed record ResultVersion(StageRecord Record, bool Current)
{
    public string Label => $"{(Current ? "当前" : "历史")} · {Record.UpdatedAt?.ToLocalTime():MM-dd HH:mm:ss} · {MediaTaskItem.Describe(Record)}{(Record.ImportedFromVersionId.Length > 0 ? " · 旧联合结果导入" : "")}";
}
public sealed record ModelChoice(string Name, AppSettings Settings) { public override string ToString() => Name; }
public sealed record ProcessChoice(string Name, string Operation, string Scope, bool Available, string Reason = "")
{
    public override string ToString() => Name;
}
public sealed record BatchPreviewRow(string MediaId, string Name, string Settings, string Plan, bool Included);
public sealed record FailedUnitRow(string Name, string Reason);
public sealed record StepItem(TaskStage Kind, string Name);

// 页面只管理可见结果、版本和确认草稿；持久化与实际执行由主窗口协调服务完成。
public partial class ProjectWorkspaceViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private readonly ProjectStore _store;
    private int _resultRevision;
    private bool _loadingVersions;
    private bool _selectingMedia;
    private bool _loadedOutput;
    private string? _mediaId;
    private string? _projectId;
    private MediaTaskDocument? _task;
    private ProjectDocument? _dialogProject;
    private MediaTaskItem? _dialogMedia;
    private SegmentationMethod _segmentationMethod;
    private readonly Dictionary<TaskStage, string> _previews = [];
    private bool _loadingModel;
    private string? _loadedStepModelId;
    public ProjectWorkspaceViewModel(MainWindowViewModel owner, ProjectStore store)
    {
        _owner = owner; _store = store;
        Selection.Source = Cues;
        Selection.SelectionChanged += (_, _) => { RebuildStepOperations(); Refresh(); };
        OpenProjectCommand = new AsyncRelayCommand<ProjectListItem>(async item => { if (item is not null) await owner.OpenWorkspaceProjectAsync(item.Document); });
        NewProjectCommand = new RelayCommand(() => OpenName(false));
        RenameCommand = new RelayCommand(() => OpenName(true));
        SetCurrentCommand = new AsyncRelayCommand(SetCurrentAsync, () => CanSetCurrent);
        StepOperationCommand = new AsyncRelayCommand<ProcessChoice>(ExecuteStepOperationAsync, choice => CanProcess && choice?.Available == true);
        SaveStepModelCommand = new AsyncRelayCommand(SaveStepModelAsync, () => _owner.CanManageProject && StepModel is not null);
        SaveSpeechModelCommand = new AsyncRelayCommand(async () =>
        {
            if (!_owner.CanManageProject || _owner.SelectedProject is not { } project || _owner.SelectedMedia is not { } media || string.IsNullOrWhiteSpace(SpeechModel)) return;
            var settings = _owner.MediaSettings(media); settings.AsrModel = SpeechModel.Trim();
            await _owner.SaveMediaSettingsAsync(project, media, settings);
        }, () => _owner.CanManageProject);
        SettingsCommand = new RelayCommand(OpenSettings);
        CloseDialogCommand = new RelayCommand(CloseDialog);
        SubmitDialogCommand = new AsyncRelayCommand(SubmitDialogAsync);
        BatchCommand = new RelayCommand(() => OpenBatch(false));
        ResumeBatchCommand = new RelayCommand(() => OpenBatch(true));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
        RetranslateSelectedCommand = new AsyncRelayCommand(() => ExecuteStepOperationAsync(StepOperations.FirstOrDefault(c => c.Operation == "TranslationSelected")), () => CanRetranslateSelected);
        RemoveCommand = new AsyncRelayCommand(async () => await owner.RemoveSelectedMediaAsync());
        UndoRemoveCommand = new AsyncRelayCommand(async () => await owner.UndoRemoveAsync());
        LocateCommand = new AsyncRelayCommand(async () => await owner.LocateSelectedMediaAsync());
    }
    public ObservableCollection<ProjectListItem> ProjectItems { get; } = [];
    public ObservableCollection<WordRow> Words { get; } = [];
    public ObservableCollection<ChunkRow> Chunks { get; } = [];
    public ObservableCollection<FailedUnitRow> FailedUnits { get; } = [];
    [ObservableProperty] private double mediaPaneWidth = 190;
    public ObservableCollection<CueRow> Cues { get; } = [];
    private IReadOnlyList<CueRow> _allCues = [];
    public SelectionModel<CueRow> Selection { get; } = new() { SingleSelect = false };
    public ObservableCollection<ResultVersion> Versions { get; } = [];
    public ObservableCollection<ModelChoice> Models { get; } = [];
    public ObservableCollection<SubtitlePipelineProfile> Pipelines { get; } = [];
    public ObservableCollection<ProcessChoice> StepOperations { get; } = [];
    public ObservableCollection<StepItem> Steps { get; } = [];
    [ObservableProperty] private StepItem? selectedStep;
    [ObservableProperty] private ModelChoice? stepModel;
    [ObservableProperty] private string speechModel = "scribe_v2";
    public IAsyncRelayCommand SaveSpeechModelCommand { get; }
    public ObservableCollection<BatchPreviewRow> BatchRows { get; } = [];
    [ObservableProperty] private int resultTab = 2;
    [ObservableProperty] private bool segmentationView;
    [ObservableProperty] private bool failedOnly;
    [ObservableProperty] private ResultVersion? version;
    [ObservableProperty] private string resultSettings = "";
    [ObservableProperty] private string resultError = "";
    [ObservableProperty] private string flow = "";
    [ObservableProperty] private bool isOldVersion;
    [ObservableProperty] private bool canExport;

    [ObservableProperty] private string dialogKind = "";
    [ObservableProperty] private string dialogTitle = "";
    [ObservableProperty] private string dialogText = "";
    [ObservableProperty] private string dialogError = "";
    [ObservableProperty] private string nameDraft = "";
    [ObservableProperty] private string sourceDraft = "自动识别";
    [ObservableProperty] private string targetDraft = "中文";
    [ObservableProperty] private SubtitlePipelineProfile? pipelineDraft;
    [ObservableProperty] private bool batchFailedOnly;
    [ObservableProperty] private bool resumeBatch;
    [ObservableProperty] private bool keepResumeSettings = true;
    [ObservableProperty] private string activeText = "";
    public bool HasDialog => DialogKind.Length > 0;
    public bool NameDialog => DialogKind is "new" or "rename";
    public bool SettingsDialog => DialogKind == "settings";
    public bool BatchDialog => DialogKind == "batch";
    public string DialogMediaName => _dialogMedia?.Name ?? _dialogProject?.Name ?? "";
    public bool CanSubmit => _owner.CanManageProject && (NameDialog ? !string.IsNullOrWhiteSpace(NameDraft) :
        SettingsDialog ? PipelineDraft is not null && !string.IsNullOrWhiteSpace(TargetDraft) :
        BatchDialog && BatchRows.Any(r => r.Included));
    public string SubmitText => NameDialog ? "保存" : SettingsDialog ? "保存设置" : ResumeBatch ? "继续批量处理" : "开始处理";
    public bool HasProjects => ProjectItems.Count > 0;
    public bool HasWords => Words.Count > 0;
    public bool HasChunks => Chunks.Count > 0;
    public bool HasFailedUnits => FailedUnits.Count > 0;
    public bool HasCues => Cues.Count > 0;
    public bool HasVersions => Versions.Count > 0;
    public bool HasResultError => !string.IsNullOrEmpty(ResultError);
    public bool ShowSubtitleTools => ResultStage == TaskStage.Translation;
    public bool ShowWords => ResultStage == TaskStage.Asr;
    public bool ShowChunks => ResultStage == TaskStage.Chunking;
    public bool ShowCues => ResultStage is TaskStage.Subtitles or TaskStage.Translation;
    public bool ShowTranslation => ResultStage == TaskStage.Translation;
    public bool NeedsModel => ResultStage == TaskStage.Translation || ResultStage == TaskStage.Subtitles && _segmentationMethod != SegmentationMethod.Program;
    public bool CanSetCurrent => _owner.CanManageProject && Version is { Record.Archived: false } && _task?.Stage(ResultStage).Version != Version.Record.Version;
    public string CurrentVersionText => Version is null ? "未开始" : Version.Record.Version == _task?.Stage(ResultStage).Version ? "当前版本" : "预览历史版本";
    public IAsyncRelayCommand SetCurrentCommand { get; }
    public IAsyncRelayCommand<ProcessChoice> StepOperationCommand { get; }
    public IAsyncRelayCommand SaveStepModelCommand { get; }
    public string EmptySubtitleText => FailedOnly ? "没有失败的字幕" : SegmentationView ? "尚未生成分句" : "尚未生成字幕";
    public bool CanRetranslateSelected => _owner.CanManageProject && !IsOldVersion && Selection.Count > 0 && _task?.Translation.ResultPath.Length > 0;
    public string ExportText => IsOldVersion ? "导出此旧版本…" : "导出 SRT…";
    public string BatchSummary => $"处理 {BatchRows.Count(r => r.Included)} 个 · 跳过 {BatchRows.Count(r => !r.Included)} 个";
    public bool HasInterruptedBatch => _owner.SelectedProject?.Batch is { State: ProcessingState.Interrupted or ProcessingState.Cancelled or ProcessingState.Failed or ProcessingState.PartialFailure };
    public bool HasRemovedMedia => _owner.SelectedProject?.RemovedMediaIds.Count > 0;
    public bool SourceMissing => _task is not null && !File.Exists(_task.InputPath);
    public IAsyncRelayCommand<ProjectListItem> OpenProjectCommand { get; }
    public IRelayCommand NewProjectCommand { get; }
    public IRelayCommand RenameCommand { get; }
    public bool CanProcess => _owner.CanManageProject;
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand CloseDialogCommand { get; }
    public IAsyncRelayCommand SubmitDialogCommand { get; }
    public IRelayCommand BatchCommand { get; }
    public IRelayCommand ResumeBatchCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand RetranslateSelectedCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand UndoRemoveCommand { get; }
    public IAsyncRelayCommand LocateCommand { get; }
    public string[] SourceLanguages => _owner.SourceLanguageNames;
    public void Refresh()
    {
        foreach (var name in new[] { nameof(CanSubmit), nameof(HasProjects), nameof(HasWords), nameof(HasChunks), nameof(HasCues), nameof(HasVersions), nameof(HasFailedUnits),
            nameof(CanRetranslateSelected), nameof(ExportText), nameof(BatchSummary), nameof(HasInterruptedBatch), nameof(HasRemovedMedia), nameof(SourceMissing), nameof(HasResultError), nameof(EmptySubtitleText), nameof(ShowSubtitleTools), nameof(CanProcess) }) OnPropertyChanged(name);
        foreach (var name in new[] { nameof(ShowWords), nameof(ShowChunks), nameof(ShowCues), nameof(ShowTranslation), nameof(NeedsModel), nameof(CanSetCurrent), nameof(CurrentVersionText) }) OnPropertyChanged(name);
        SetCurrentCommand.NotifyCanExecuteChanged(); SaveStepModelCommand.NotifyCanExecuteChanged();
        SaveSpeechModelCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged(); RetranslateSelectedCommand.NotifyCanExecuteChanged();
        StepOperationCommand.NotifyCanExecuteChanged();
    }
    partial void OnDialogKindChanged(string value)
    {
        foreach (var name in new[] { nameof(HasDialog), nameof(NameDialog), nameof(SettingsDialog), nameof(BatchDialog), nameof(SubmitText), nameof(DialogMediaName) }) OnPropertyChanged(name);
        Refresh();
    }
    partial void OnNameDraftChanged(string value) => Refresh();
    partial void OnTargetDraftChanged(string value) => Refresh();
    partial void OnPipelineDraftChanged(SubtitlePipelineProfile? value) => Refresh();
    partial void OnBatchFailedOnlyChanged(bool value) { if (BatchDialog) RebuildBatch(); }
    partial void OnKeepResumeSettingsChanged(bool value) { if (BatchDialog) RebuildBatch(); }
    partial void OnResultTabChanged(int value) { if (!_selectingMedia && value >= 0 && value < Steps.Count) SelectedStep = Steps[value]; }
    partial void OnSegmentationViewChanged(bool value) { if (!_selectingMedia) SelectedStep = Steps.FirstOrDefault(s => s.Kind == (value ? TaskStage.Subtitles : TaskStage.Translation)) ?? SelectedStep; }
    partial void OnVersionChanged(ResultVersion? value) { if (!_loadingVersions && value is not null) _owner.ObserveWorkspaceTask(LoadVersionAsync(value)); }
    partial void OnFailedOnlyChanged(bool value) => FilterCues();
    public TaskStage ResultStage => SelectedStep?.Kind ?? TaskStage.Translation;
    internal void FollowCurrent()
    {
        _previews.Clear(); _loadingVersions = true; Version = null; _loadingVersions = false;
    }
    partial void OnSelectedStepChanged(StepItem? value)
    {
        if (_selectingMedia || _task is null) return;
        _selectingMedia = true; ResultTab = value is null ? 0 : Steps.IndexOf(value); SegmentationView = value?.Kind == TaskStage.Subtitles; _selectingMedia = false;
        LoadStepModel(); RebuildStepOperations(); _owner.ObserveWorkspaceTask(LoadVersionsAsync()); Refresh();
    }
    partial void OnStepModelChanged(ModelChoice? value) { if (!_loadingModel) SaveStepModelCommand.NotifyCanExecuteChanged(); }
    private void LoadStepModel()
    {
        if (_owner.SelectedMedia is not { } media) return;
        _loadingModel = true;
        var settings = _owner.MediaSettings(media);
        var selectedId = settings.StepModelId(ResultStage, media.Document);
        SpeechModel = settings.AsrModel;
        Models.Clear(); foreach (var choice in _owner.WorkspaceModels(settings)) Models.Add(choice);
        StepModel = Models.FirstOrDefault(m => m.Settings.ModelKey() == selectedId);
        _loadedStepModelId = StepModel?.Settings.ModelKey();
        _loadingModel = false;
    }
    internal void RefreshModels()
    {
        var draft = StepModel?.Settings.ModelKey(); var hasDraft = draft is not null && draft != _loadedStepModelId;
        if (_owner.SelectedMedia is not null) LoadStepModel();
        if (hasDraft) StepModel = Models.FirstOrDefault(m => m.Settings.ModelKey() == draft) ?? StepModel;
        RebuildStepOperations(); Refresh();
    }
    private async Task ExecuteStepOperationAsync(ProcessChoice? choice)
    {
        if (!CanProcess || choice?.Available != true || _owner.SelectedProject is not { } project || _owner.SelectedMedia is not { } media) return;
        if (!_owner.StepProcessChoices(media, ResultStage).Any(c => c.Operation == choice.Operation && c.Available)) return;
        var ids = Selection.SelectedItems.Select(c => c!.Id).ToHashSet();
        if (!await _owner.EnsureWorkspaceCredentialsAsync(project, media, choice.Operation, ids)) return;
        await _owner.ExecuteWorkspaceMediaAsync(project, media, choice.Operation, ids);
    }
    private void RebuildStepOperations()
    {
        StepOperations.Clear();
        if (_owner.SelectedMedia is { } media)
            foreach (var choice in _owner.StepProcessChoices(media, ResultStage)) StepOperations.Add(choice);
    }
    private async Task SaveStepModelAsync()
    {
        if (_owner.SelectedProject is not { } project || _owner.SelectedMedia is not { } media || StepModel is null) return;
        await _owner.SaveStepModelAsync(project, media, ResultStage, StepModel);
        _loadedStepModelId = StepModel.Settings.ModelKey();
        RebuildStepOperations(); Refresh();
    }
    private async Task SetCurrentAsync()
    {
        if (!CanSetCurrent || Version is null || _task is null || _projectId is null) return;
        await _owner.SetWorkspaceCurrentAsync(_projectId, _task, ResultStage, Version.Record);
        _previews.Clear(); await LoadVersionsAsync(); RebuildStepOperations(); Refresh();
    }
    public async Task SelectAsync(ProjectDocument? project, MediaTaskItem? media)
    {
        var changed = _mediaId != media?.Document.Id || _projectId != project?.Id;
        _mediaId = media?.Document.Id; _projectId = project?.Id; _task = media?.Document;
        if (_task is null) { ++_resultRevision; _loadedOutput = false; Words.Clear(); Chunks.Clear(); Cues.Clear(); Versions.Clear(); CanExport = false; Refresh(); return; }
        var settings = _owner.MediaSettings(media!);
        _segmentationMethod = settings.GetPipeline().Method;
        Flow = FlowText(settings);
        var priorKind = SelectedStep?.Kind;
        _selectingMedia = true;
        Steps.Clear(); foreach (var kind in TaskStagePlan.Steps()) Steps.Add(new(kind, kind switch { TaskStage.Chunking => "分片", TaskStage.Asr => "识别词", TaskStage.Subtitles => "分句", _ => "结果" }));
        if (changed) { Selection.Clear(); _previews.Clear(); FailedOnly = false; }
        SelectedStep = !changed ? Steps.FirstOrDefault(s => s.Kind == priorKind) : null;
        SelectedStep ??= Steps.LastOrDefault(s => _task.Stage(s.Kind).Version.Length > 0) ?? Steps.First();
        ResultTab = Steps.IndexOf(SelectedStep); SegmentationView = SelectedStep.Kind == TaskStage.Subtitles;
        _selectingMedia = false;
        _loadedOutput = _task.Stages.Any(s => s.State == ProcessingState.Completed);
        LoadStepModel(); RebuildStepOperations(); await LoadVersionsAsync(); Refresh();
    }
    private async Task LoadVersionsAsync()
    {
        var revision = ++_resultRevision; var task = _task; var projectId = _projectId; var kind = ResultStage;
        if (task is null || projectId is null) return;
        var versions = await _store.VersionsAsync(projectId, task, kind, matchingUpstreamOnly: true);
        if (revision != _resultRevision) return;
        _loadingVersions = true;
        var selectedId = _previews.GetValueOrDefault(kind) ?? task.Stage(kind).Version;
        Versions.Clear(); foreach (var record in versions) Versions.Add(new(record, record.Version == task.Stage(kind).Version));
        Version = Versions.FirstOrDefault(v => v.Record.Version == selectedId) ?? Versions.FirstOrDefault();
        _loadingVersions = false;
        await LoadVersionAsync(Version, revision); Refresh();
    }
    private async Task LoadVersionAsync(ResultVersion? selected, int? existingRevision = null)
    {
        var revision = existingRevision ?? ++_resultRevision; var task = _task; var projectId = _projectId; var kind = ResultStage;
        if (task is null || projectId is null) return;
        var words = new List<WordRow>(); var chunks = new List<ChunkRow>(); var failures = new List<FailedUnitRow>(); IReadOnlyList<CueRow> cues = [];
        var settingsText = ""; var error = ""; var complete = false;
        try
        {
            if (selected is not null)
            {
                var stage = selected.Record;
                var settings = stage.Settings is { } snapshot ? PipelineSettingsMigration.Upgrade(snapshot).Deserialize<AppSettings>() : null;
                settingsText = settings is null ? "" : DescribeSettings(settings) + (kind == TaskStage.Translation || kind == TaskStage.Subtitles && settings.GetPipeline().Method != SegmentationMethod.Program ? " · " + settings.ModelName() : "");
                error = stage.Error;
                if (kind == TaskStage.Asr) failures.AddRange(stage.FailedUnits.Select(id => new FailedUnitRow(int.TryParse(id, out var index) ? $"分片 {index + 1}" : id, stage.UnitErrors.GetValueOrDefault(id, "识别失败，请重试。"))));
                if (stage.ResultPath.Length > 0 && File.Exists(_store.ResolveResult(projectId, task.Id, stage.ResultPath)))
                {
                if (kind == TaskStage.Asr)
                {
                    var data = await _store.ReadResultAsync<TranscriptionResult>(projectId, task.Id, stage.ResultPath);
                    if (data is not null) words.AddRange(data.Words.Select((word, index) => new WordRow(index + 1, word)));
                }
                else if (kind == TaskStage.Chunking)
                {
                    var data = await _store.ReadResultAsync<ChunkingResult>(projectId, task.Id, stage.ResultPath);
                    if (data is not null) chunks.AddRange(data.Files.Select(f => new ChunkRow(f, "已完成")));
                }
                else
                {
                    var segmentation = kind == TaskStage.Subtitles ? await _store.ReadResultAsync<SubtitlePipelineResult>(projectId, task.Id, stage.ResultPath) : null;
                    var translated = kind == TaskStage.Translation ? await _store.ReadResultAsync<TranslationResult>(projectId, task.Id, stage.ResultPath) : null;
                    var data = segmentation?.Cues ?? translated?.Cues;
                    var sourceRanges = segmentation?.Ranges ?? translated?.Ranges;
                    if (data is not null) cues = data.Select((cue, index) => new CueRow(cue, index + 1)
                    { State = stage.FailedUnits.Contains(cue.Id) ? CueTranslationState.Failed : CueTranslationState.Waiting,
                        WordRange = sourceRanges?.Count > index ? $"识别词 {sourceRanges[index].First + 1}–{sourceRanges[index].Last + 1}" : "",
                        FailureReason = stage.FailedUnits.Contains(cue.Id) ? stage.UnitErrors.GetValueOrDefault(cue.Id, "") : "" }).ToArray();
                    complete = kind == TaskStage.Translation && stage.State == ProcessingState.Completed && data?.Count > 0 && data.All(c => AlignmentValidator.IsValid(c.TranslatedText ?? ""));
                }
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { error = UserErrorMessages.FromException(e); }
        if (revision != _resultRevision) return;
        if (selected is not null) _previews[kind] = selected.Record.Version;
        Words.Clear(); foreach (var word in words) Words.Add(word);
        Chunks.Clear(); foreach (var chunk in chunks) Chunks.Add(chunk);
        FailedUnits.Clear(); foreach (var failure in failures) FailedUnits.Add(failure);
        _allCues = cues; FilterCues();
        ResultSettings = settingsText; ResultError = error; IsOldVersion = selected is { Current: false } || selected?.Record.NeedsUpdate == true;
        CanExport = complete; Refresh();
    }
    private void FilterCues()
    {
        var ids = Selection.SelectedItems.Select(c => c!.Id).ToHashSet();
        Selection.Clear(); Cues.Clear(); foreach (var cue in _allCues.Where(c => !FailedOnly || c.IsFailed)) Cues.Add(cue);
        for (var index = 0; index < Cues.Count; index++) if (ids.Contains(Cues[index].Id)) Selection.Select(index);
        Refresh();
    }
    public void ApplyProgress(MediaTaskItem item, ProjectTaskProgress progress)
    {
        if (item.Document.Id != _mediaId || progress.Cues is null || !ShowCues || Version is { Current: false }) return;
        if (progress.Stage != ResultStage) return;
        _allCues = progress.Cues.Select((cue, index) => new CueRow(cue, index + 1)
        { State = progress.FailedIds?.Contains(cue.Id) == true ? CueTranslationState.Failed : CueTranslationState.Translating }).ToArray();
        CanExport = false; FilterCues();
    }
    private void CaptureTarget()
    { _dialogProject = _owner.SelectedProject; _dialogMedia = _owner.SelectedMedia; DialogError = ""; }
    private void OpenName(bool rename)
    { CaptureTarget(); if (_owner.IsBusy) return; NameDraft = rename ? _dialogProject?.Name ?? "" : ""; DialogTitle = rename ? "重命名项目" : "新建项目"; DialogKind = rename ? "rename" : "new"; }
    private void OpenSettings()
    {
        CaptureTarget();
        if (_owner.IsBusy || _dialogProject is null || _dialogMedia is null) return;
        var current = _owner.MediaSettings(_dialogMedia);
        Pipelines.Clear(); foreach (var pipeline in current.AllPipelines.Concat(_owner.SavedWorkspaceSettings.AllPipelines).DistinctBy(p => p.Id)) Pipelines.Add(pipeline);
        PipelineDraft = Pipelines.FirstOrDefault(p => p.Id == current.GetPipeline().Id);
        SourceDraft = current.SourceLanguage; TargetDraft = current.TargetLanguage;
        DialogTitle = "媒体处理设置"; DialogKind = "settings"; DialogText = "";
    }
    private void OpenBatch(bool resume)
    { CaptureTarget(); if (_owner.IsBusy || _dialogProject is null) return; ResumeBatch = resume; BatchFailedOnly = resume && _dialogProject.Batch?.FailedOnly == true; KeepResumeSettings = true; DialogTitle = resume ? "继续批量处理" : "批量处理"; DialogKind = "batch"; RebuildBatch(); }
    private void RebuildBatch()
    {
        if (_dialogProject is null) return;
        BatchRows.Clear(); foreach (var row in _owner.BatchPreview(_dialogProject, BatchFailedOnly, ResumeBatch, KeepResumeSettings)) BatchRows.Add(row);
        DialogText = "按列表顺序处理。音频上传与云端翻译可能产生费用。";
        Refresh();
    }
    private void CloseDialog()
    { DialogKind = ""; }
    private async Task SubmitDialogAsync()
    {
        if (_owner.IsBusy || _dialogProject is null && DialogKind != "new") return;
        try
        {
            if (NameDialog)
            { await _owner.SaveWorkspaceProjectNameAsync(_dialogProject, NameDraft, DialogKind == "new"); DialogKind = ""; }
            else if (SettingsDialog)
            {
                if (PipelineDraft is null) return;
                var settings = _owner.MediaSettings(_dialogMedia!);
                settings.PipelineLibrary = Pipelines.Where(p => !SubtitlePipelinePresets.IsBuiltIn(p.Id)).ToDictionary(p => p.Id);
                settings.SourceLanguage = SourceDraft; settings.SourceCode = Cuelify.Infrastructure.Speech.SpeechLanguages.AsrCode(Cuelify.Infrastructure.Speech.SpeechLanguages.Resolve(SourceDraft), settings.SourceCode);
                settings.TargetLanguage = TargetDraft.Trim(); settings.TaskPipelineId = PipelineDraft.Id;
                await _owner.SaveMediaSettingsAsync(_dialogProject!, _dialogMedia!, settings);
                CloseDialog();
            }
            else if (BatchDialog)
            {
                var project = _dialogProject!;
                var plan = _owner.CreateBatchPlan(project, BatchRows.Where(r => r.Included).Select(r => r.MediaId).ToArray(), BatchFailedOnly, ResumeBatch, KeepResumeSettings);
                if (!await _owner.EnsureBatchCredentialsAsync(project, plan)) return;
                DialogKind = ""; await _owner.ExecuteWorkspaceBatchAsync(project, plan);
            }
        }
        catch (Exception e) { DialogError = UserErrorMessages.FromException(e); }
        finally { Refresh(); }
    }
    private Task ExportAsync()
    {
        if (!CanExport || _task is null || _projectId is null || Version is null) return Task.CompletedTask;
        return _owner.ExportWorkspaceVersionAsync(_projectId, _task, Version.Record, _allCues.Select(c => c.Cue).ToArray());
    }
    public static string DescribeSettings(AppSettings settings) => $"{settings.GetPipeline().Name} · {settings.SourceLanguage} → {settings.TargetLanguage}";
    public static string FlowText(AppSettings settings) => "分片 → 识别词 → 分句 → 结果";
}
