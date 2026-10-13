using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Desktop.Services;
using Cuelify.Infrastructure;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ProjectWorkspaceViewModel Workspace { get; private set; } = null!;
    private readonly Dictionary<string, List<MediaTaskItem>> _projectMediaCache = [];
    private ProjectDocument? _operationProject;
    internal AppSettings SavedWorkspaceSettings => ConfigurationStore.Snapshot(_savedSettings);
    public bool IsProjects => Page == AppPage.Projects;
    public bool IsProjectNavigation => IsProjects || IsWorkspace;
    public string CurrentMediaSettings => SelectedMedia is null ? "" : ProjectWorkspaceViewModel.DescribeSettings(MediaSettings(SelectedMedia));
    public bool HasWorkspaceError => !string.IsNullOrEmpty(Error);
    private Func<Task>? _pendingWorkspaceAction;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string pendingRunDescription = "";
    public bool HasPendingRun => _pendingWorkspaceAction is not null;
    [RelayCommand] private void CancelPendingRun() { _pendingWorkspaceAction = null; PendingRunDescription = ""; OnPropertyChanged(nameof(HasPendingRun)); }
    private (bool Speech, bool Translation) RequiredCredentials(MediaTaskItem media, string operation)
    {
        var steps = OperationSteps(media, operation).ToArray();
        return (steps.Contains(TaskStage.Asr), steps.Any(k => NeedsCloud(media, k, operation)));
    }
    private IEnumerable<TaskStage> OperationSteps(MediaTaskItem media, string operation) => operation == "Complete"
        ? StageExecutionPolicy.NeededStages(media.Document) : [operation == "TranslationSelected" ? TaskStage.Translation : Enum.Parse<TaskStage>(operation.Split(':')[0])];
    private AppSettings OperationSettings(MediaTaskItem media, TaskStage kind, string operation)
    {
        var mode = operation.EndsWith("Complete", StringComparison.Ordinal) ? ProcessingMode.Complete :
            operation.EndsWith("RetryFailed", StringComparison.Ordinal) ? ProcessingMode.RetryFailed : ProcessingMode.Restart;
        return StageExecutionPolicy.ResolveSettings(media.Document, kind, mode, MediaSettings(media));
    }
    private bool NeedsCloud(MediaTaskItem media, TaskStage kind, string operation) =>
        StageExecutionPolicy.NeedsCloud(kind, OperationSettings(media, kind, operation));
    internal async Task<bool> EnsureWorkspaceCredentialsAsync(ProjectDocument project, MediaTaskItem media, string operation, IReadOnlySet<string> ids)
    {
        var required = RequiredCredentials(media, operation);
        if ((required.Speech || required.Translation) && !IsUnlocked)
        { await ManageMasterPasswordAsync(); if (!IsUnlocked) return false; }
        var missingCloud = OperationSteps(media, operation).Where(k => NeedsCloud(media, k, operation)).Select(k => OperationSettings(media, k, operation))
            .FirstOrDefault(s => TranslationCredentialHint(s.Provider, s.BaseUrl).Length > 0);
        if (required.Speech && SpeechCredentialHint.Length > 0 || missingCloud is not null)
        {
            _pendingWorkspaceAction = async () => { if (await EnsureWorkspaceCredentialsAsync(project, media, operation, ids)) await ExecuteWorkspaceMediaAsync(project, media, operation, ids); };
            PendingRunDescription = $"配置密钥后继续处理：{media.Name}"; OnPropertyChanged(nameof(HasPendingRun));
            if (required.Speech && SpeechCredentialHint.Length > 0) ConfigureSpeech();
            else { ProviderIndex = (int)missingCloud!.Provider; SettingsSectionIndex = 1; ShowSettings(); }
            Workspace.CloseDialogCommand.Execute(null); return false;
        }
        return true;
    }
    internal async Task<bool> EnsureBatchCredentialsAsync(ProjectDocument project, BatchRunDocument plan)
    {
        var items = ProjectItemsFor(project).ToDictionary(i => i.Document.Id);
        var needsCredentials = plan.Items.Where(e => e.State != ProcessingState.Completed && e.SkipReason.Length == 0).Any(entry =>
        {
            if (!items.TryGetValue(entry.MediaId, out var media)) return false;
            var requested = ConfigurationStore.ReadSnapshot(entry.Settings) ?? MediaSettings(media);
            return entry.Stages.Any(kind => kind == TaskStage.Asr || StageExecutionPolicy.NeedsCloud(kind,
                StageExecutionPolicy.ResolveSettings(media.Document, kind, plan.FailedOnly ? ProcessingMode.RetryFailed : ProcessingMode.Complete, requested)));
        });
        if (needsCredentials && !IsUnlocked)
        { await ManageMasterPasswordAsync(); if (!IsUnlocked) return false; }
        return true;
    }
    private void ResumePendingWorkspaceAction()
    {
        if (_pendingWorkspaceAction is null || !CanConfigure) return;
        var pending = _pendingWorkspaceAction; CancelPendingRun(); ObserveWorkspaceTask(pending());
    }
    [RelayCommand] private void ShowProjects() => Page = AppPage.Projects;
    [RelayCommand] private async Task ViewActiveMediaAsync()
    {
        var project = _operationProject; var media = _mediaOperationTarget;
        if (project is null || media is null) return;
        await OpenWorkspaceProjectAsync(project); SelectedMedia = media; await _navigationTask;
    }
    internal async Task OpenWorkspaceProjectAsync(ProjectDocument project)
    {
        SelectedProject = project; await _navigation; Page = AppPage.Workspace;
    }
    private readonly HashSet<Task> _workspaceTasks = [];
    internal async void ObserveWorkspaceTask(Task task)
    { _workspaceTasks.Add(task); try { await task; } catch (Exception e) { if (!_disposed) ReportError(e); } finally { _workspaceTasks.Remove(task); } }
    public async Task WaitForWorkspaceAsync()
    { while (_workspaceTasks.Count > 0) await Task.WhenAll(_workspaceTasks.ToArray()); }
    internal AppSettings MediaSettings(MediaTaskItem item)
    {
        // 当前服务与模型资产来自全局，媒体持有自己的执行选项与管线快照。
        // 旧媒体缺少选项时兼容原项目快照；新媒体始终保存添加时的全局默认值。
        var result = ConfigurationStore.ReadSnapshot(item.Document.Options) ?? ConfigurationStore.ReadSnapshot(SelectedProject?.DefaultSettings) ?? SavedWorkspaceSettings;
        result = ConfigurationStore.Snapshot(result);
        result.ModelFiles = new(_savedSettings.ModelFiles);
        foreach (var model in _savedSettings.Models) result.Models[model.Key] = model.Value;
        foreach (var pipeline in _savedSettings.AllPipelines)
            if (_savedSettings.PipelineModelDefaults.TryGetValue(pipeline.Id, out var defaults))
                result.PipelineModelDefaults[pipeline.Id] = new(defaults);
        RefreshWorkspaceAssets(result);
        return result;
    }
    private void RefreshWorkspaceAssets(AppSettings settings)
    {
        settings.LlamaServerPath = _savedSettings.LlamaServerPath; settings.LlamaServerVersion = _savedSettings.LlamaServerVersion;
        if (settings.Provider == TranslationProvider.Local)
        {
            var file = _savedSettings.ModelFiles.GetValueOrDefault(settings.LocalOptions().ModelId);
            if (file is not null) { settings.ModelPath = file.Path; settings.ModelSha256 = file.Sha256; }
        }
    }
    internal IEnumerable<ModelChoice> WorkspaceModels(AppSettings current)
    {
        var profiles = current.Models.Values.Append(ModelProfile.Capture(current)).DistinctBy(m => m.Id);
        foreach (var model in profiles)
        {
            var settings = ConfigurationStore.Snapshot(current); model.Apply(settings); RefreshWorkspaceAssets(settings);
            yield return new(model.Name, settings);
        }
    }
    internal async Task SaveStepModelAsync(ProjectDocument project, MediaTaskItem media, TaskStage kind, ModelChoice choice)
    {
        using var feedback = BeginFeedback("正在保存步骤模型…");
        if (!CanManageProject || kind is not (TaskStage.Subtitles or TaskStage.Translation)) return;
        using var projectLock = _projects.AcquireProject(project.Id);
        var settings = MediaSettings(media); var profile = ModelProfile.Capture(choice.Settings);
        settings.Models[profile.Id] = profile; ConfigurationStore.ValidateSecrets(settings, Secrets);
        media.Document.ModelOverrides[kind] = profile.Id;
        media.Document.Options = JsonSerializer.SerializeToElement(settings);
        await _projects.SaveTaskAsync(project.Id, media.Document);
        RefreshProjectState();
    }
    internal async Task SetWorkspaceCurrentAsync(string projectId, MediaTaskDocument task, TaskStage kind, StageRecord record)
    {
        using var feedback = BeginFeedback("正在切换当前版本…");
        if (!CanManageProject) return;
        using var projectLock = _projects.AcquireProject(projectId);
        await _projects.SetCurrentAsync(projectId, task, kind, record);
        ProjectTaskService.RefreshValidity(task); await _projects.SaveTaskAsync(projectId, task);
        if (SelectedMedia?.Document == task) await RestoreSelectedTaskAsync(SelectedMedia);
        await RefreshProjectListAsync(); RefreshProjectState();
    }
    internal async Task SaveWorkspaceProjectNameAsync(ProjectDocument? project, string name, bool create)
    {
        using var feedback = BeginFeedback("正在保存项目…");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请填写项目名称。");
        if (create)
        { ProjectName = name.Trim(); await CreateProjectAsync(); Page = AppPage.Workspace; }
        else if (project is not null)
        {
            using var projectLock = _projects.AcquireProject(project.Id);
            project.Name = name.Trim(); await _projects.SaveAsync(project); ProjectName = project.Name;
        }
        await RefreshProjectListAsync();
    }
    internal async Task SaveMediaSettingsAsync(ProjectDocument project, MediaTaskItem media, AppSettings settings)
    {
        using var feedback = BeginFeedback("正在保存媒体设置…");
        if (!CanManageProject) return;
        if (string.IsNullOrWhiteSpace(settings.TargetLanguage)) throw new ArgumentException("请填写目标语言。");
        _ = settings.GetPipeline(); ConfigurationStore.ValidateSecrets(settings, Secrets);
        using var projectLock = _projects.AcquireProject(project.Id);
        var previousPipeline = MediaSettings(media).GetPipeline();
        var changedPipeline = previousPipeline.Id != settings.GetPipeline().Id || previousPipeline.Method != settings.GetPipeline().Method;
        media.Document.Options = JsonSerializer.SerializeToElement(settings);
        if (changedPipeline) await _projects.SelectDownstreamAsync(project.Id, media.Document, TaskStage.Asr);
        ProjectTaskService.RefreshValidity(media.Document); await _projects.SaveTaskAsync(project.Id, media.Document);
        if (media == SelectedMedia) await RestoreSelectedTaskAsync(media);
        await RefreshProjectListAsync(); RefreshProjectState();
    }
    internal IEnumerable<ProcessChoice> StepProcessChoices(MediaTaskItem media, TaskStage kind)
    {
        var task = media.Document;
        if (!TaskStagePlan.Steps().Contains(kind)) yield break;
        var stage = task.Stage(kind); var parent = TaskStagePlan.Upstream(kind);
        var ready = parent is { } upstream ? task.Stage(upstream).IsReady : File.Exists(task.InputPath);
        var reason = parent is null ? "请先重新定位源文件。" : "请先完成上游步骤并设为当前。";
        var name = StageName(kind);
        var retired = StageExecutionPolicy.IsRetired(kind, stage);
        var hasPending = stage.State != ProcessingState.Completed && stage.FailedUnits.Count == 0 ||
            (stage.WorkPlanKnown ? stage.HasPendingWork : stage.State is not (ProcessingState.Completed or ProcessingState.PartialFailure) &&
            !(kind == TaskStage.Asr && stage.State == ProcessingState.Failed && stage.FailedUnits.Count > 0));
        if (!retired && !stage.IsReady && stage.State != ProcessingState.Completed && hasPending)
            yield return new(stage.Version.Length == 0 ? "开始" + name : "继续" + name, $"{kind}:Complete", "处理当前步骤。", ready, ready ? "" : reason);
        else if (!retired && stage.FailedUnits.Count > 0 && stage.State != ProcessingState.Completed)
            yield return new("重试失败部分", $"{kind}:RetryFailed", $"重试本步骤的 {stage.FailedUnits.Count} 处失败。", ready && !stage.NeedsUpdate, ready && !stage.NeedsUpdate ? "" : reason);
        yield return new("重新" + name, $"{kind}:Restart", retired ? StageExecutionPolicy.LegacySegmentationMessage : "使用当前上游和步骤设置生成新版本。", ready, ready ? "" : reason);
        if (kind == TaskStage.Translation && stage.ResultPath.Length > 0 && !stage.ResultUnavailable && SelectedProject is { } project && File.Exists(_projects.ResolveResult(project.Id, task.Id, stage.ResultPath)))
        {
            yield return new("重新翻译全部字幕", "Translation:Retranslate", "保留分句和时间，重新翻译全部字幕。", ready, ready ? "" : reason);
            yield return new("重新翻译所选字幕", "TranslationSelected", $"保留分句和时间，重新翻译所选的 {Workspace.Selection.Count} 条字幕。", ready && Workspace.CanRetranslateSelected, "请先选择当前版本的字幕。");
        }
    }
    private static string StageName(TaskStage stage) => stage switch
    { TaskStage.Chunking => "分片", TaskStage.Asr => "识别词", TaskStage.Subtitles => "分句", _ => "生成结果" };
    internal Task ExecuteWorkspaceMediaAsync(ProjectDocument project, MediaTaskItem media, string operation, IReadOnlySet<string> selectedIds) => ExecuteAsync(async token =>
    {
        TaskStage? kind = null;
        var mode = ProcessingMode.Complete;
        var retranslate = operation is "TranslationSelected" or "Translation:Retranslate";
        if (operation != "Complete")
        {
            var parts = operation.Split(':');
            kind = operation == "TranslationSelected" ? TaskStage.Translation : Enum.Parse<TaskStage>(parts[0]);
            mode = retranslate ? ProcessingMode.Restart : Enum.Parse<ProcessingMode>(parts[1]);
        }
        await RunMediaCoreAsync(project, media, MediaSettings(media), kind, mode,
            operation == "TranslationSelected" ? selectedIds : null, retranslate ? StageOperation.Retranslate : StageOperation.Generate, token);
    }, mediaJob: true);
    private async Task RunMediaCoreAsync(ProjectDocument project, MediaTaskItem media, AppSettings settings, TaskStage? kind,
        ProcessingMode mode, IReadOnlySet<string>? selectedIds, StageOperation operation, CancellationToken token, bool saveOptions = false)
    {
        _operationProject = project; _mediaOperationTarget = media;
        if (SelectedMedia == media)
        {
            Workspace.FollowCurrent();
            _isComplete = false; _jobInputPath = media.Document.InputPath;
            if (kind is { } step) Workspace.SelectedStep = Workspace.Steps.FirstOrDefault(s => s.Kind == step);
        }
        using var projectLock = _projects.AcquireProject(project.Id);
        try
        {
            ConfigurationStore.ValidateSecrets(settings, Secrets);
            if (saveOptions)
            {
                media.Document.Options = JsonSerializer.SerializeToElement(settings);
                await _projects.SaveTaskAsync(project.Id, media.Document, token);
            }
            Workspace.ActiveText = media.Name + " · 准备处理";
            if (kind is null) await _taskExecutor.RunAllAsync(project, media.Document, settings, TaskProgress(media, token), token);
            else await _taskExecutor.RunStageAsync(project, media.Document, kind.Value, mode, settings, TaskProgress(media, token), token,
                selectedIds, operation);
        }
        finally { await FinishWorkspaceOperationAsync(project); }
    }
    internal IEnumerable<BatchPreviewRow> BatchPreview(ProjectDocument project, bool failedOnly, bool resume, bool keepSettings)
    {
        var items = ProjectItemsFor(project);
        var checkedItems = items.Where(i => i.IsChecked).ToArray();
        var scope = resume ? project.Batch?.Items.Select(p => p.MediaId).ToHashSet() : checkedItems.Length > 0 ? checkedItems.Select(i => i.Document.Id).ToHashSet() : null;
        foreach (var item in items.Where(i => scope is null || scope.Contains(i.Document.Id)))
        {
            var previous = resume ? project.Batch?.Items.FirstOrDefault(p => p.MediaId == item.Document.Id) : null;
            var settings = resume && keepSettings && previous?.Settings is { } saved ? ConfigurationStore.ReadSnapshot(saved)! : MediaSettings(item);
            var stages = failedOnly ? StageExecutionPolicy.FailedStages(item.Document) : StageExecutionPolicy.NeededStages(item.Document);
            var included = stages.Count > 0 && previous?.State != ProcessingState.Completed;
            yield return new(item.Document.Id, item.Name, ProjectWorkspaceViewModel.DescribeSettings(settings), included ? string.Join(" → ", stages.Select(k => StageName(k))) : previous?.State == ProcessingState.Completed ? "本批次已完成" : failedOnly ? "没有可重试的失败部分" : "已完成", included);
        }
    }
    private List<MediaTaskItem> ProjectItemsFor(ProjectDocument project) => project == SelectedProject ? MediaTasks.ToList() : _projectMediaCache.GetValueOrDefault(project.Id) ?? [];
    internal BatchRunDocument CreateBatchPlan(ProjectDocument project, IReadOnlyList<string> ids, bool failedOnly, bool resume, bool keepSettings)
    {
        var plan = new BatchRunDocument { FailedOnly = failedOnly };
        foreach (var id in ids)
        {
            var item = ProjectItemsFor(project).First(i => i.Document.Id == id);
            var old = resume ? project.Batch?.Items.FirstOrDefault(i => i.MediaId == id) : null;
            var settings = keepSettings && old?.Settings is { } saved ? ConfigurationStore.ReadSnapshot(saved)! : MediaSettings(item);
            var stages = failedOnly ? StageExecutionPolicy.FailedStages(item.Document) : StageExecutionPolicy.NeededStages(item.Document);
            if (!(keepSettings && old?.Settings is not null))
                foreach (var kind in stages.Where(k => (k is TaskStage.Subtitles or TaskStage.Translation) && item.Document.Stage(k).Version.Length == 0))
                {
                    if (!StageExecutionPolicy.NeedsModel(kind, settings)) continue;
                    settings.ExecutionModels[kind] = settings.ForStep(kind, item.Document).ModelKey();
                }
            plan.Items.Add(new() { MediaId = id, Settings = JsonSerializer.SerializeToElement(settings), Stages = stages });
        }
        if (resume && project.Batch is { } previous)
        {
            var newItems = plan.Items.ToDictionary(i => i.MediaId);
            plan.Items = previous.Items.Select(i => i.State == ProcessingState.Completed ? i : newItems.GetValueOrDefault(i.MediaId) ??
                new BatchMediaPlan { MediaId = i.MediaId, Settings = i.Settings, Stages = i.Stages, State = ProcessingState.Cancelled, SkipReason = "已移出或没有可执行步骤" }).ToList();
        }
        return plan;
    }
    internal Task ExecuteWorkspaceBatchAsync(ProjectDocument project, BatchRunDocument plan) =>
        ExecuteAsync(token => RunBatchCoreAsync(project, plan, token), mediaJob: true);
    private async Task RunBatchCoreAsync(ProjectDocument project, BatchRunDocument plan, CancellationToken token)
    {
        _operationProject = project; var items = ProjectItemsFor(project);
        using var projectLock = _projects.AcquireProject(project.Id);
        project.Batch = plan; plan.State = ProcessingState.Running;
        await _projects.SaveAsync(project, token);
        try
        {
            foreach (var entry in plan.Items.Where(i => i.State != ProcessingState.Completed && i.SkipReason.Length == 0))
            {
                token.ThrowIfCancellationRequested();
                var item = items.FirstOrDefault(i => i.Document.Id == entry.MediaId);
                if (item is null) { entry.State = ProcessingState.Failed; continue; }
                _mediaOperationTarget = item; entry.State = ProcessingState.Running;
                Workspace.ResetProgress();
                Workspace.ActiveText = $"{item.Name} · {plan.Items.IndexOf(entry) + 1}/{plan.Items.Count}";
                Activity.Update(Workspace.ActiveText, reset: true); UpdateBatchActivity();
                await _projects.SaveAsync(project, token);
                try
                {
                    var settings = entry.Settings?.Deserialize<AppSettings>() ?? MediaSettings(item); RefreshWorkspaceAssets(settings);
                    if (!plan.FailedOnly) await _taskExecutor.RunAllAsync(project, item.Document, settings, TaskProgress(item, token), token);
                    else foreach (var kind in entry.Stages)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Document.Stage(kind).FailedUnits.Count == 0 || item.Document.Stage(kind).NeedsUpdate) continue;
                        await _taskExecutor.RunStageAsync(project, item.Document, kind, ProcessingMode.RetryFailed, settings, TaskProgress(item, token), token);
                    }
                    entry.State = item.Document.Stages.Any(s => s.FailedUnits.Count > 0 || s.State == ProcessingState.Failed) ? ProcessingState.PartialFailure : ProcessingState.Completed;
                }
                catch (Exception e) when (UserErrorMessages.CredentialFailure(e) is not null) { entry.State = ProcessingState.Failed; throw; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { entry.State = ProcessingState.Cancelled; throw; }
                catch (Exception e) { entry.State = ProcessingState.Failed; AddLog($"{item.Name}：{UserErrorMessages.FromException(e)}"); }
                finally { await _projects.SaveAsync(project, CancellationToken.None); item.Refresh(); UpdateBatchActivity(); }
            }
            plan.State = plan.Items.All(i => i.State == ProcessingState.Completed || i.SkipReason.Length > 0) ? ProcessingState.Completed : ProcessingState.PartialFailure;
        }
        catch { plan.State = token.IsCancellationRequested ? ProcessingState.Cancelled : ProcessingState.Failed; throw; }
        finally { await _projects.SaveAsync(project, CancellationToken.None); await FinishWorkspaceOperationAsync(project); }
    }
    private async Task FinishWorkspaceOperationAsync(ProjectDocument project)
    {
        await FlushPreparationAsync();
        await _navigationTask;
        if (SelectedMedia is not null) await RestoreSelectedTaskAsync(SelectedMedia);
        foreach (var item in ProjectItemsFor(project)) item.Refresh();
        await RefreshProjectListAsync(); RefreshProjectState();
    }
    internal async Task ExportWorkspaceVersionAsync(MediaTaskDocument task, StageRecord stage, IReadOnlyList<SubtitleCue> cues)
    {
        try
        {
            if (stage.State != ProcessingState.Completed || cues.Count == 0 || cues.Any(c => !AlignmentValidator.IsValid(c.TranslatedText ?? ""))) return;
            var settings = ConfigurationStore.ReadSnapshot(stage.Settings) ?? throw new InvalidDataException("该版本缺少处理设置。");
            var path = await _exporter.ExportAsync(task.InputPath, settings.TargetLanguage, cues,
                () => stage.State == ProcessingState.Completed, () => BeginFeedback("正在导出 SRT 字幕…"));
            if (path is not null) AddLog("SRT 已导出：" + path);
        }
        catch (Exception e) { ReportError(e); }
    }
    internal async Task RemoveSelectedMediaAsync()
    {
        using var feedback = BeginFeedback("正在移出媒体…");
        if (!CanManageProject || SelectedProject is null || SelectedMedia is null) return;
        var project = SelectedProject; var item = SelectedMedia;
        using var projectLock = _projects.AcquireProject(project.Id);
        project.MediaIds.Remove(item.Document.Id); project.RemovedMediaIds.Add(item.Document.Id);
        await _projects.SaveAsync(project); MediaTasks.Remove(item); SelectedMedia = MediaTasks.FirstOrDefault();
        await _navigationTask; await RefreshProjectListAsync(); RefreshProjectState();
    }
    internal async Task UndoRemoveAsync()
    {
        using var feedback = BeginFeedback("正在恢复媒体…");
        if (!CanManageProject || SelectedProject is null || SelectedProject.RemovedMediaIds.Count == 0) return;
        var project = SelectedProject;
        using var projectLock = _projects.AcquireProject(project.Id);
        var id = project.RemovedMediaIds[^1]; var task = await _projects.LoadTaskAsync(project.Id, id);
        project.RemovedMediaIds.Remove(id); project.MediaIds.Add(id); await _projects.SaveAsync(project);
        var item = new MediaTaskItem(task); MediaTasks.Add(item); SelectedMedia = item;
        await _navigationTask; await RefreshProjectListAsync(); RefreshProjectState();
    }
    internal async Task LocateSelectedMediaAsync()
    {
        if (!CanManageProject || SelectedProject is null || SelectedMedia is null) return;
        var project = SelectedProject; var media = SelectedMedia;
        try
        {
            var path = await _dialogs.OpenAsync("重新定位媒体", ["*.mp4", "*.mkv", "*.mov", "*.mp3", "*.wav", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.webm"]);
            if (path is null) return;
            await ExecuteAsync(async token =>
            {
            Activity.Update("正在校验重新定位的文件…", reset: true);
            var hash = await AtomicFile.HashFileAsync(path, token, new Progress<StageWorkProgress>(value => Post(token,
                () => { if (Activity.Message == "正在校验重新定位的文件…") Activity.Update("正在校验重新定位的文件…", value); })));
            var changed = media.Document.SourceSha256 is null || hash != media.Document.SourceSha256;
            if (changed && !await _dialogs.ConfirmAsync("文件内容无法确认与原文件一致。重新定位后需要更新分片、识别和字幕，旧结果保留。", token)) return;
            Activity.Update("正在读取媒体信息…", reset: true);
            var info = await _jobs.ProbeAsync(path, MediaSettings(media), token);
            using var projectLock = _projects.AcquireProject(project.Id);
            media.Document.InputPath = Path.GetFullPath(path); media.Document.SourceSha256 = hash; media.Document.Duration = info.Duration;
            if (changed) media.Document.Chunking.NeedsUpdate = true;
            ProjectTaskService.RefreshValidity(media.Document); await _projects.SaveTaskAsync(project.Id, media.Document);
            await RestoreSelectedTaskAsync(media); RefreshProjectState();
            }, probing: true);
        }
        catch (Exception e) { ReportError(e); }
    }
    private async Task RefreshProjectListAsync()
    {
        var list = new List<ProjectListItem>();
        foreach (var project in Projects)
        {
            var tasks = new List<MediaTaskDocument>();
            foreach (var id in project.MediaIds)
            {
                var cached = _projectMediaCache.GetValueOrDefault(project.Id)?.FirstOrDefault(i => i.Document.Id == id)?.Document;
                try { tasks.Add(cached ?? await _projects.ReadTaskAsync(project.Id, id)); }
                catch (Exception e) when (e is IOException or InvalidDataException) { AddLog(e.Message); }
            }
            list.Add(new(project, $"{project.MediaIds.Count} 个媒体 · 完成 {tasks.Count(t => t.Translation.State == ProcessingState.Completed)} · 失败 {tasks.Count(t => t.Stages.Any(s => s.State is ProcessingState.Failed or ProcessingState.PartialFailure))} · 需更新 {tasks.Count(t => t.Stages.Any(s => s.NeedsUpdate))}"));
        }
        Workspace.ProjectItems.Clear(); foreach (var item in list.OrderByDescending(p => p.Document.LastOpenedAt)) Workspace.ProjectItems.Add(item);
        Workspace.Refresh();
    }
}
