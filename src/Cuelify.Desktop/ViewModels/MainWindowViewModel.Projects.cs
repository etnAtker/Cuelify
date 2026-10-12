using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;

namespace Cuelify.Desktop.ViewModels;

public partial class MediaTaskItem(MediaTaskDocument document) : ObservableObject
{
    public MediaTaskDocument Document { get; } = document;
    public string Name => Path.GetFileName(Document.InputPath);
    [ObservableProperty] private bool isChecked;
    public string StatusText => Document.Stages.Any(s => s.State == ProcessingState.Running) ? "处理中" :
        Document.Stages.Any(s => s.NeedsUpdate) ? "需更新" : Document.Stages.Any(s => s.State is ProcessingState.Failed or ProcessingState.PartialFailure) ? "有失败部分" :
        Document.Translation.IsReady ? "已完成" : Document.Stages.Any(s => s.State is ProcessingState.Interrupted or ProcessingState.Cancelled) ? "已中断" :
        Document.Asr.IsReady ? "识别完成" : Document.Stages.Any(s => s.Version.Length > 0) ? "待继续" : "未开始";
    public string Detail => $"{(Document.Duration is { } duration ? duration.ToString(@"hh\:mm\:ss") : "时长待读取")} · {ConfigurationStore.ReadSnapshot(Document.Options)?.TargetLanguage ?? "中文"}";
    public string Summary => $"分片：{Describe(Document.Chunking)} · ASR：{Describe(Document.Asr)} · 翻译：{Describe(Document.Translation)}";
    public static string Describe(StageRecord stage) => StateText(stage.State) + (stage.ResultUnavailable ? " · 结果不可用" : stage.NeedsUpdate ? " · 需更新" : "");
    private static string StateText(ProcessingState state) => state switch
    { ProcessingState.Running => "处理中", ProcessingState.Completed => "完成", ProcessingState.PartialFailure => "部分失败",
        ProcessingState.Failed => "失败", ProcessingState.Cancelled => "已取消", ProcessingState.Interrupted => "已中断", _ => "未开始" };
    public void Refresh() { OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(Detail)); }
}

public partial class MainWindowViewModel
{
    private ProjectStore _projects = null!;
    private ProjectTaskService _taskExecutor = null!;
    private AppSettings? _taskOptionBase;
    private bool _restoringTask;
    private bool _loadingProjects;
    private bool _settingMedia;
    private int _mediaNavigationRevision;
    private ProjectDocument? _openedProject;
    private string? _loadedMediaId;
    private MediaTaskItem? _mediaOperationTarget;
    private Task _navigation = Task.CompletedTask;
    public ObservableCollection<ProjectDocument> Projects { get; } = [];
    public ObservableCollection<MediaTaskItem> MediaTasks { get; } = [];
    [ObservableProperty] private ProjectDocument? selectedProject;
    [ObservableProperty] private MediaTaskItem? selectedMedia;
    [ObservableProperty] private string projectName = "新项目";
    [ObservableProperty] private bool showProjectEditor;
    [ObservableProperty] private bool showStages;
    [ObservableProperty] private bool isImporting;
    [RelayCommand] private void ToggleProjectEditor() => ShowProjectEditor = !ShowProjectEditor;
    [RelayCommand] private void ToggleStages() => ShowStages = !ShowStages;
    public bool HasProject => SelectedProject is not null;
    public bool HasMedia => SelectedMedia is not null;
    public bool CanManageProject => CanConfigure && !IsImporting;
    partial void OnIsImportingChanged(bool value) { OnPropertyChanged(nameof(ProjectSummary)); RefreshCommands(); Workspace.Refresh(); }
    public bool CanBatch => CanConfigure && HasProject && MediaTasks.Count > 0;
    public bool CanOperateStage => CanConfigure && HasMedia;
    public string ChunkingSummary => SelectedMedia is null ? "未开始" : MediaTaskItem.Describe(SelectedMedia.Document.Chunking);
    public string AsrSummary => SelectedMedia is null ? "未开始" : MediaTaskItem.Describe(SelectedMedia.Document.Asr);
    public string SubtitlesSummary => SelectedMedia is null ? "未开始" : MediaTaskItem.Describe(SelectedMedia.Document.Subtitles);
    public string TranslationSummary => SelectedMedia is null ? "未开始" : MediaTaskItem.Describe(SelectedMedia.Document.Translation);
    public bool HasPreviousTranslation => SelectedMedia is not null && (SelectedMedia.Document.Translation.PreviousResultPath.Length > 0 ||
        SelectedMedia.Document.Translation.NeedsUpdate && SelectedMedia.Document.Translation.ResultPath.Length > 0);
    public string ProjectSummary => IsImporting ? "正在读取媒体信息…" : $"共 {MediaTasks.Count} 个媒体 · 完成 {MediaTasks.Count(m => m.Document.Translation.IsReady)} 个";
    public bool TaskOptionsChanged
    {
        get
        {
            if (SelectedMedia is null) return false;
            try { return Enum.GetValues<TaskStage>().Any(s => ProjectTaskService.ConfigurationChanged(SelectedMedia.Document.Stage(s), s, TaskSettings())); }
            catch (ArgumentException) { return true; }
        }
    }

    private void InitializeProjects()
    {
        _projects = new(_store.Root);
        Workspace = new(this, _projects);
        _taskExecutor = new(_jobs, _projects, () => ElevenLabsKey, settings => KeyFor(settings.Provider),
            settings => TranslationCredentialHint(settings.Provider, settings.BaseUrl), async (settings, token) =>
            {
                await PrepareAsync("正在检查翻译设置…", async progress =>
                {
                    if (settings.Provider == TranslationProvider.Local) await _jobs.ValidateLocalAsync(settings, token, progress);
                }, token);
                Step = WorkflowStep.Results;
            });
    }
    private async Task LoadProjectsAsync()
    {
        Projects.Clear(); foreach (var project in await _projects.ListAsync()) Projects.Add(project);
        foreach (var notice in _projects.Notices) AddLog(notice);
        WorkspaceState? workspace = null;
        try { workspace = await _projects.LoadWorkspaceAsync(); }
        catch (InvalidDataException e) { AddLog(e.Message); }
        if (workspace is { MediaPaneWidth: >= 160 and <= 360 }) Workspace.MediaPaneWidth = workspace.MediaPaneWidth;
        var previous = Projects.FirstOrDefault(p => p.Id == workspace?.ProjectId);
        if (previous is not null)
        {
            _loadingProjects = true; SelectedProject = previous; _loadingProjects = false;
            await OpenProjectAsync(previous, workspace?.MediaId);
        }
        await RefreshProjectListAsync();
    }
    partial void OnSelectedProjectChanged(ProjectDocument? value)
    {
        if (_loadingProjects) return;
        _navigation = NavigateProjectAsync(value, _navigation);
    }
    private async Task NavigateProjectAsync(ProjectDocument? project, Task previous)
    {
        try
        {
            await previous;
            if (project is not null && SelectedProject == project) await OpenProjectAsync(project);
        }
        catch (Exception e) { _loadingProjects = true; SelectedProject = _openedProject; _loadingProjects = false; ReportError(e); }
    }
    private async Task OpenProjectAsync(ProjectDocument project, string? mediaId = null)
    {
        using var feedback = BeginFeedback($"正在打开项目：{project.Name}…");
        using var projectLock = _operationProject?.Id == project.Id && IsBusy ? null : _projects.AcquireProject(project.Id);
        var items = new List<MediaTaskItem>();
        foreach (var id in project.MediaIds)
        {
            try
            {
                if (_operationProject?.Id == project.Id && IsBusy && _projectMediaCache.TryGetValue(project.Id, out var activeItems))
                { var active = activeItems.FirstOrDefault(i => i.Document.Id == id); if (active is not null) { items.Add(active); continue; } }
                var task = await _projects.LoadTaskAsync(project.Id, id);
                ProjectTaskService.RefreshValidity(task);
                await _projects.SaveTaskAsync(project.Id, task);
                items.Add(new(task));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { ReportError(e); }
        }
        if (SelectedProject != project) return;
        _openedProject = project;
        _projectMediaCache[project.Id] = items;
        MediaTasks.Clear(); SelectedMedia = null;
        foreach (var item in items) MediaTasks.Add(item);
        project.LastOpenedAt = DateTimeOffset.UtcNow; ProjectName = project.Name;
        if (!IsBusy || _operationProject?.Id != project.Id)
        {
            if (project.Batch is { State: ProcessingState.Running } batch)
            { batch.State = ProcessingState.Interrupted; foreach (var entry in batch.Items.Where(i => i.State == ProcessingState.Running)) entry.State = ProcessingState.Interrupted; }
            await _projects.SaveAsync(project);
        }
        SelectedMedia = MediaTasks.FirstOrDefault(m => m.Document.Id == mediaId) ?? MediaTasks.FirstOrDefault();
        await _navigationTask;
        await SaveWorkspaceAsync(); RefreshProjectState();
    }
    private Task _navigationTask = Task.CompletedTask;
    partial void OnSelectedMediaChanged(MediaTaskItem? value)
    {
        if (_settingMedia) return;
        _navigationTask = RestoreSelectedTaskAsync(value);
    }
    private async Task RestoreSelectedTaskAsync(MediaTaskItem? item)
    {
        using var feedback = BeginFeedback("正在读取媒体结果…");
        var revision = ++_mediaNavigationRevision;
        try
        {
            if (item is null)
            {
                _restoringTask = true;
                InputPath = ""; _taskOptionBase = null; _jobSettings = null; _jobSource = null; _isComplete = false; _hasFinalCues = false;
                CanContinue = false; SetRows([]); ApplyTaskDefaults(); Step = WorkflowStep.File; _loadedMediaId = null;
                _restoringTask = false;
                await Workspace.SelectAsync(SelectedProject, null);
                return;
            }
            var task = item.Document; var project = SelectedProject;
            if (project is null) return;
            var projectId = project.Id;
            async Task<T?> Read<T>(TaskStage kind) where T : class
            {
                var stage = task.Stage(kind);
                if (stage.ResultPath.Length == 0) return null;
                if (!File.Exists(_projects.ResolveResult(projectId, task.Id, stage.ResultPath)))
                {
                    if (stage.State == ProcessingState.Completed) { stage.ResultUnavailable = true; stage.Error = "阶段结果文件缺失，请恢复或重新执行本步骤。"; }
                    return null;
                }
                try
                {
                    var result = await _projects.ReadResultAsync<T>(projectId, task.Id, stage.ResultPath);
                    if (result is not null)
                    {
                        if (stage.ResultUnavailable && stage.State == ProcessingState.Completed) stage.Error = "";
                        stage.ResultUnavailable = false;
                    }
                    return result;
                }
                catch (InvalidDataException e) { stage.ResultUnavailable = true; stage.Error = e.Message; return null; }
            }
            var source = await Read<TranscriptionResult>(TaskStage.Asr);
            task.Duration ??= source?.Duration;
            var generated = await Read<SubtitlePipelineResult>(TaskStage.Subtitles);
            var translated = await Read<TranslationResult>(TaskStage.Translation);
            if (revision != _mediaNavigationRevision || SelectedMedia != item || SelectedProject != project) return;
            _restoringTask = true;
            if (_loadedMediaId != task.Id) SetRows([]);
            _loadedMediaId = task.Id;
            InputPath = task.InputPath; _jobInputPath = task.InputPath;
            _taskOptionBase = ConfigurationStore.ReadSnapshot(task.Options) ?? ConfigurationStore.ReadSnapshot(project.DefaultSettings) ?? ConfigurationStore.Snapshot(_savedSettings);
            TaskProviderIndex = (int)_taskOptionBase.Provider; TaskSourceLanguage = _taskOptionBase.SourceLanguage; TaskTargetLanguage = _taskOptionBase.TargetLanguage;
            _taskPipelineId = _taskOptionBase.GetPipeline().Id; RefreshTaskPipelines();
            var active = task.Translation.Settings ?? task.Subtitles.Settings ?? task.Asr.Settings;
            _jobSettings = ConfigurationStore.ReadSnapshot(active); _jobSource = source; _hasFinalCues = generated is not null || translated?.Cues.Count > 0;
            ProjectTaskService.RefreshValidity(task);
            SetRows(translated?.Cues ?? generated?.Cues ?? [], task.Translation.FailedUnits.ToHashSet());
            foreach (var row in Rows) row.FailureReason = task.Translation.UnitErrors.GetValueOrDefault(row.Id, "");
            foreach (var row in Rows.Where(r => r.Cue.TranslatedText is null))
                row.State = task.Translation.UnitStates.GetValueOrDefault(row.Id) switch
                { ProcessingState.Running => CueTranslationState.Translating, ProcessingState.Cancelled => CueTranslationState.Cancelled,
                    ProcessingState.Failed => CueTranslationState.Failed, _ => CueTranslationState.Waiting };
            Error = "";
            _isComplete = task.Translation.IsReady && translated is not null && Rows.All(r => AlignmentValidator.IsValid(r.Cue.TranslatedText ?? ""));
            CanContinue = task.Stages.Any(stage => stage.Version.Length > 0) && !_isComplete;
            Step = task.Stages.Any(stage => stage.Version.Length > 0) ? WorkflowStep.Results : WorkflowStep.Options;
            Status = task.Translation.NeedsUpdate ? "上游结果已更新，请更新字幕" : _isComplete ? "翻译完成，可以导出字幕" : "可按步骤处理";
            MediaSummary = source is null ? "" : $"时长 {source.Duration:hh\\:mm\\:ss}";
            _restoringTask = false;
            await Workspace.SelectAsync(project, item);
            await SaveWorkspaceAsync();
        }
        catch (Exception e)
        {
            if (revision == _mediaNavigationRevision) { _isComplete = false; ReportError(e); }
        }
        finally { _restoringTask = false; RefreshProjectState(); }
    }
    internal Task SaveWorkspaceAsync() => _projects.SaveWorkspaceAsync(new(SelectedProject?.Id, SelectedMedia?.Document.Id, Workspace.MediaPaneWidth));
    private void RefreshProjectState()
    {
        foreach (var item in MediaTasks) item.Refresh();
        if (SelectedProject is not null) _projectMediaCache[SelectedProject.Id] = MediaTasks.ToList();
        Workspace.Refresh();
        OnPropertyChanged(nameof(CurrentMediaSettings)); OnPropertyChanged(nameof(HasWorkspaceError));
        foreach (var name in new[] { nameof(HasProject), nameof(HasMedia), nameof(CanManageProject), nameof(CanBatch), nameof(CanOperateStage),
            nameof(ProjectSummary), nameof(ChunkingSummary), nameof(AsrSummary), nameof(SubtitlesSummary), nameof(TranslationSummary),
            nameof(HasPreviousTranslation), nameof(TaskOptionsChanged), nameof(RunButtonText) }) OnPropertyChanged(name);
        CreateProjectCommand.NotifyCanExecuteChanged(); RenameProjectCommand.NotifyCanExecuteChanged(); AddMediaCommand.NotifyCanExecuteChanged();
        RunProjectCommand.NotifyCanExecuteChanged(); RunStageCommand.NotifyCanExecuteChanged(); ExportPreviousCommand.NotifyCanExecuteChanged();
        RefreshCommands();
    }
    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task CreateProjectAsync()
    {
        try
        {
            var project = new ProjectDocument { Name = string.IsNullOrWhiteSpace(ProjectName) ? "新项目" : ProjectName.Trim() };
            using var projectLock = _projects.AcquireProject(project.Id);
            await _projects.SaveAsync(project); Projects.Insert(0, project);
            _loadingProjects = true; SelectedProject = project; _openedProject = project; _loadingProjects = false;
            MediaTasks.Clear(); SelectedMedia = null; await _navigationTask; await SaveWorkspaceAsync(); RefreshProjectState();
            await RefreshProjectListAsync();
        }
        catch (Exception e) { ReportError(e); }
    }
    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task RenameProjectAsync()
    {
        if (SelectedProject is null) return;
        var project = SelectedProject; var name = project.Name;
        try
        {
            if (string.IsNullOrWhiteSpace(ProjectName)) throw new ArgumentException("请填写项目名称。");
            using var projectLock = _projects.AcquireProject(project.Id);
            project.Name = ProjectName.Trim(); await _projects.SaveAsync(project);
            var index = Projects.IndexOf(project); Projects[index] = project;
        }
        catch (Exception e) { project.Name = name; ReportError(e); }
    }
    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task AddMediaAsync()
    {
        try { await AddMediaPathsAsync(await _dialogs.OpenMediaAsync("添加音视频", ["*.mp4", "*.mkv", "*.mov", "*.mp3", "*.wav", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.webm"])); }
        catch (Exception e) { ReportError(e); }
    }
    public async Task AddMediaPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanManageProject || paths.Count == 0) return;
        await _navigation;
        if (SelectedProject is null) await CreateProjectAsync();
        if (SelectedProject is null) return;
        var project = SelectedProject;
        using var projectLock = _projects.AcquireProject(project.Id);
        var projectItems = MediaTasks.ToList();
        IsImporting = true;
        try
        {
        await ExecuteAsync(async token =>
        {
        MediaTaskItem? first = null;
        var notices = new List<string>();
        var completed = 0; var failed = 0;
        Activity.Update("正在读取媒体信息…", new(0, paths.Count) { Unit = "个文件", ShowWaiting = true });
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            Activity.Update($"正在检查：{Path.GetFileName(path)}", new(completed, paths.Count, Running: 1, Failed: failed) { Unit = "个文件", ShowWaiting = true });
            try
            {
                var full = Path.GetFullPath(path);
                var duplicate = projectItems.FirstOrDefault(m => string.Equals(m.Document.InputPath, full, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null) { first ??= duplicate; notices.Add($"{duplicate.Name} 已在项目中。"); completed++; continue; }
                var options = SavedWorkspaceSettings; options.TaskPipelineId = null;
                var task = new MediaTaskDocument { InputPath = full, Options = JsonSerializer.SerializeToElement(options) };
                var info = await _jobs.ProbeAsync(full, options, token);
                task.Duration = info.Duration;
                await _projects.SaveTaskAsync(project.Id, task); project.MediaIds.Add(task.Id);
                var item = new MediaTaskItem(task); projectItems.Add(item); if (SelectedProject == project) MediaTasks.Add(item); first ??= item;
                completed++;
                _projectMediaCache[project.Id] = projectItems;
                await _projects.SaveAsync(project, CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { failed++; notices.Add($"{Path.GetFileName(path)}：{UserErrorMessages.FromException(e)}"); }
            finally { Activity.Update("读取媒体信息", new(completed, paths.Count, Failed: failed) { Unit = "个文件", ShowWaiting = true }); }
        }
        _projectMediaCache[project.Id] = projectItems;
        await _projects.SaveAsync(project);
        if (first is not null && SelectedProject == project) SelectedMedia = first;
        await _navigationTask; Error = string.Join("\n", notices); await RefreshProjectListAsync(); RefreshProjectState();
        }, probing: true);
        }
        finally { IsImporting = false; }
    }
    public void ReportMediaImportError(Exception exception) => ReportError(exception);
    private async Task<MediaTaskItem> EnsureTaskAsync()
    {
        await _navigation; await _navigationTask;
        if (SelectedMedia is not null) return SelectedMedia;
        var input = InputPath; var options = TaskSettings(); ConfigurationStore.ValidateSecrets(options, Secrets);
        if (SelectedProject is null)
        {
            var project = new ProjectDocument();
            using var newProjectLock = _projects.AcquireProject(project.Id);
            await _projects.SaveAsync(project); Projects.Insert(0, project);
            _loadingProjects = true; SelectedProject = project; _openedProject = project; _loadingProjects = false;
        }
        using var projectLock = _projects.AcquireProject(SelectedProject.Id);
        var task = new MediaTaskDocument { InputPath = Path.GetFullPath(input), Options = JsonSerializer.SerializeToElement(options) };
        await _projects.SaveTaskAsync(SelectedProject.Id, task); SelectedProject.MediaIds.Add(task.Id); await _projects.SaveAsync(SelectedProject);
        var item = new MediaTaskItem(task); MediaTasks.Add(item);
        _settingMedia = true; SelectedMedia = item; _settingMedia = false;
        return item;
    }
    private IProgress<ProjectTaskProgress> TaskProgress(MediaTaskItem item, CancellationToken token) => new Progress<ProjectTaskProgress>(value => Post(token, () =>
    {
        item.Refresh();
        Workspace.ActiveText = $"{item.Name} · {Stage(value.Message)}" + (_operationProject?.Batch is { State: ProcessingState.Running } batch ? $" · {batch.Items.FindIndex(i => i.MediaId == item.Document.Id) + 1}/{batch.Items.Count}" : "");
        Workspace.ApplyProgress(item, value);
        AddLog($"{item.Name} · {value.Stage}：{value.Message}");
        if (SelectedMedia != item) return;
        _hasStartedProcessing = true; Step = WorkflowStep.Results;
        Status = Stage(value.Message);
        ApplyTranslationProgress(new(value.Message, value.CueIds ?? []) { Cues = value.Cues, FailedIds = value.FailedIds });
        OnPropertyChanged(nameof(ChunkingSummary)); OnPropertyChanged(nameof(AsrSummary)); OnPropertyChanged(nameof(SubtitlesSummary)); OnPropertyChanged(nameof(TranslationSummary));
    }));
    private async Task RunCurrentTaskAsync(CancellationToken token, TaskStage? kind = null, ProcessingMode mode = ProcessingMode.Complete,
        IReadOnlySet<string>? selectedIds = null, AppSettings? overrideSettings = null, MediaTaskItem? target = null, bool useGeneratedTranslations = false)
    {
        var item = target ?? await EnsureTaskAsync(); var project = _operationProject ?? SelectedProject!;
        _operationProject = project;
        if (SelectedMedia == item && kind is null) { Page = AppPage.Workspace; Workspace.SelectedStep = Workspace.Steps.FirstOrDefault(s => s.Kind == TaskStage.Translation); }
        _mediaOperationTarget = item;
        if (SelectedMedia == item) Workspace.FollowCurrent();
        var settings = overrideSettings ?? (SelectedMedia == item ? TaskSettings() : ConfigurationStore.ReadSnapshot(item.Document.Options) ?? TaskSettings()); ConfigurationStore.ValidateSecrets(settings, Secrets);
        using var projectLock = _projects.AcquireProject(project.Id);
        if (overrideSettings is null) item.Document.Options = JsonSerializer.SerializeToElement(settings);
        await _projects.SaveTaskAsync(project.Id, item.Document, token);
        _jobInputPath = item.Document.InputPath;
        _isComplete = false;
        try
        {
            if (kind is null) await _taskExecutor.RunAllAsync(project, item.Document, settings, TaskProgress(item, token), token);
            else await _taskExecutor.RunStageAsync(project, item.Document, kind.Value, mode, settings, TaskProgress(item, token), token, selectedIds, useGeneratedTranslations: useGeneratedTranslations);
        }
        finally
        {
            await FlushPreparationAsync();
            await _navigationTask;
            if (SelectedMedia is not null) await RestoreSelectedTaskAsync(SelectedMedia);
            RefreshProjectState();
        }
    }
    private bool CanRunStage(string? operation)
    {
        if (!CanOperateStage || operation is null) return false;
        var parts = operation.Split(':');
        if (parts.Length != 2 || !Enum.TryParse<TaskStage>(parts[0], out var kind)) return false;
        return StepProcessChoices(SelectedMedia!, kind).Any(c => c.Operation == operation && c.Available);
    }
    [RelayCommand(CanExecute = nameof(CanRunStage))]
    private Task RunStageAsync(string operation) => ExecuteAsync(async token =>
    {
        var parts = operation.Split(':');
        if (parts.Length != 2 || !Enum.TryParse<TaskStage>(parts[0], out var kind) || !Enum.TryParse<ProcessingMode>(parts[1], out var mode))
            throw new ArgumentException("处理操作无效。");
        var item = SelectedMedia!; var settings = TaskSettings();
        if (mode == ProcessingMode.Restart && !await _dialogs.ConfirmAsync("重新执行本步骤？旧结果将保留为上一版本。识别及云端翻译可能产生费用。", token)) return;
        item.Document.Options = JsonSerializer.SerializeToElement(settings);
        await RunCurrentTaskAsync(token, kind, mode, overrideSettings: settings, target: item);
    }, mediaJob: true);
    [RelayCommand(CanExecute = nameof(CanBatch))]
    private Task RunProjectAsync() => ExecuteAsync(async token =>
    {
        await _navigation; var project = SelectedProject!;
        using var projectLock = _projects.AcquireProject(project.Id);
        var batch = MediaTasks.Select(item => (Item: item, Settings: ConfigurationStore.ReadSnapshot(item.Document.Options) ?? ConfigurationStore.ReadSnapshot(project.DefaultSettings) ?? TaskSettings())).ToArray();
        _hasStartedProcessing = true;
        try
        {
            foreach (var (item, settings) in batch)
            {
                token.ThrowIfCancellationRequested();
                _mediaOperationTarget = item;
                try { await _taskExecutor.RunAllAsync(project, item.Document, settings, TaskProgress(item, token), token); }
                catch (Cuelify.Infrastructure.ServiceCredentialException) { throw; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) { AddLog($"{item.Name}：{UserErrorMessages.FromException(e)}"); }
                item.Refresh();
            }
        }
        finally
        {
            await FlushPreparationAsync();
            if (SelectedMedia is not null) await RestoreSelectedTaskAsync(SelectedMedia);
            RefreshProjectState();
        }
    }, mediaJob: true);
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void UseSavedTaskSettings()
    { _taskOptionBase = ConfigurationStore.Snapshot(_savedSettings); ApplyTaskDefaults(); OnPropertyChanged(nameof(TaskOptionsChanged)); OnPropertyChanged(nameof(RunButtonText)); }
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ExportPreviousAsync()
    {
        if (SelectedProject is null || SelectedMedia is null) return;
        try
        {
            var task = SelectedMedia.Document;
            var stale = task.Translation.NeedsUpdate;
            var result = await _projects.ReadResultAsync<TranslationResult>(SelectedProject.Id, task.Id, stale ? task.Translation.ResultPath : task.Translation.PreviousResultPath)
                ?? throw new InvalidDataException("上一版本结果缺失。");
            var settings = ConfigurationStore.ReadSnapshot(stale ? task.Translation.Settings : task.Translation.PreviousSettings) ?? throw new InvalidDataException("上一版本配置缺失。");
            var file = await _dialogs.SaveSrtAsync($"{Path.GetFileNameWithoutExtension(task.InputPath)}.{settings.TargetLanguage}.previous.srt");
            if (file is null) return;
            if (Path.GetExtension(file).ToLowerInvariant() != ".srt" || Path.GetFullPath(file).Equals(Path.GetFullPath(task.InputPath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择有效的 SRT 输出路径，不能覆盖原媒体。");
            if (File.Exists(file) && !await _dialogs.ConfirmAsync("目标 SRT 已存在，确认覆盖？")) return;
            await AtomicFile.WriteTextAsync(file, SrtSerializer.SerializeTranslated(result.Cues), true, CancellationToken.None);
        }
        catch (Exception e) { ReportError(e); }
    }
}
