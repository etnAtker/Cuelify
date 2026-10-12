using System.Text.Json;
using Cuelify.Core.Projects;
using Cuelify.Core.Subtitles;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Storage;
using Cuelify.Infrastructure.Transcription;
using Cuelify.Infrastructure.Translation;

namespace Cuelify.Desktop.Services;

public sealed record ProjectTaskProgress(TaskStage Stage, string Message, IReadOnlyList<SubtitleCue>? Cues = null,
    IReadOnlyList<string>? FailedIds = null, IReadOnlyList<string>? CueIds = null)
{
    public StageWorkProgress? WorkProgress { get; init; }
}

public sealed class ProjectTaskService(IDesktopJobService jobs, ProjectStore store,
    Func<string> speechKey, Func<AppSettings, string> translationKey, Func<AppSettings, string>? credentialHint = null,
    Func<AppSettings, CancellationToken, Task>? prepareTranslation = null)
{
    public static string ConfigurationIdentity(TaskStage kind, AppSettings settings, bool includeSegmentationModel = true)
    {
        var pipeline = settings.GetPipeline();
        return kind switch
        {
            TaskStage.Chunking => AtomicFile.Hash(new { settings.FfmpegPath, settings.FfprobePath, settings.VadPath,
                settings.VadThreshold, settings.MinimumSpeechMs, settings.MinimumSilenceMs, settings.ChunkTargetSeconds,
                settings.ChunkMaximumSeconds, settings.ChunkSearchSeconds, settings.OverlapMs, settings.MaximumUploadBytes, settings.AlwaysChunk }),
            TaskStage.Asr => AtomicFile.Hash(new { settings.AsrModel, settings.SourceCode }),
            TaskStage.Subtitles => AtomicFile.Hash(new { Pipeline = pipeline.SegmentationIdentity, settings.ZeroDurationToleranceMs,
                settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle,
                Model = pipeline.Method == SegmentationMethod.Program || !includeSegmentationModel ? null : ModelProfile.ExecutionParameters(settings) }),
            _ => AtomicFile.Hash(new { Model = ModelProfile.ExecutionParameters(settings), Pipeline = pipeline.TranslationIdentity, settings.SourceLanguage, settings.TargetLanguage, settings.TargetStyle })
        };
    }
    public static bool ConfigurationChanged(StageRecord stage, TaskStage kind, AppSettings settings)
    {
        if (stage.Settings is not { } snapshot) return false;
        var current = ConfigurationStore.Snapshot(settings);
        if (kind == TaskStage.Subtitles) current.UseSourceTextSegmentation();
        return ConfigurationIdentity(kind, ConfigurationStore.ReadSnapshot(snapshot)!) != ConfigurationIdentity(kind, current);
    }

    public async Task RunAllAsync(ProjectDocument project, MediaTaskDocument task, AppSettings settings,
        IProgress<ProjectTaskProgress> progress, CancellationToken token)
    {
        foreach (var kind in TaskStagePlan.Steps())
        {
            token.ThrowIfCancellationRequested();
            if (!task.Stage(kind).IsReady)
                await RunStageAsync(project, task, kind, ProcessingMode.Complete, settings, progress, token);
        }
    }
    public async Task RunStageAsync(ProjectDocument project, MediaTaskDocument task, TaskStage kind, ProcessingMode mode, AppSettings requested,
        IProgress<ProjectTaskProgress> progress, CancellationToken token, IReadOnlySet<string>? selectedIds = null, bool prepare = true,
        bool useGeneratedTranslations = false, bool allowFrozenSubtitles = false, StageOperation operation = StageOperation.Generate)
    {
        var pipelineMode = requested.GetPipeline().Method;
        if (!TaskStagePlan.Steps().Contains(kind)) throw new ArgumentException("当前管线没有此步骤。");
        var previous = task.Stage(kind);
        var upstreamKind = TaskStagePlan.Upstream(kind);
        var upstream = upstreamKind is { } parent ? task.Stage(parent) : null;
        if (upstream is not null && !upstream.IsReady) throw new ArgumentException("请先完成上游步骤并设为当前。");
        if (mode == ProcessingMode.RetryFailed && previous.FailedUnits.Count == 0) throw new ArgumentException("本步骤没有失败部分。");
        if (mode == ProcessingMode.RetryFailed && previous.WorkPlanKnown && previous.HasPendingWork)
            throw new ArgumentException("本步骤还有未处理部分，请继续处理。");
        if (mode != ProcessingMode.Restart && previous.Version.Length > 0 &&
            (previous.UpstreamStage != upstreamKind || previous.UpstreamVersionId != (upstream?.Version ?? "")))
            throw new ArgumentException("当前版本与上游不一致，请重新选择版本。");
        if (mode != ProcessingMode.Restart && previous.State == ProcessingState.Completed)
        {
            if (previous.IsReady && mode == ProcessingMode.Complete && selectedIds is null) return;
            throw new ArgumentException("此版本已完成，请重新执行本步骤以生成新版本。");
        }
        var creating = mode == ProcessingMode.Restart || previous.Version.Length == 0;
        var settings = creating ? requested.ForStep(kind, task) : ConfigurationStore.Snapshot(ConfigurationStore.ReadSnapshot(previous.Settings) ?? requested.ForStep(kind, task));
        if (!creating) operation = previous.Operation;
        else if (selectedIds is not null || allowFrozenSubtitles) operation = StageOperation.Retranslate;
        var executionMode = settings.GetPipeline().Method;
        var needsModel = kind == TaskStage.Translation || kind == TaskStage.Subtitles && executionMode != SegmentationMethod.Program;
        ConfigurationStore.ValidateSecrets(settings, [speechKey(), translationKey(settings)]);
        if (kind == TaskStage.Asr && string.IsNullOrWhiteSpace(speechKey())) throw new Cuelify.Infrastructure.ServiceCredentialException("ElevenLabs");
        IReadOnlyList<SubtitleCue>? accepted = null;
        IReadOnlyList<SourceWordRange> ranges = [];
        if (kind == TaskStage.Translation && previous.ResultPath.Length > 0 && (!creating || operation == StageOperation.Retranslate))
        {
            var saved = await store.ReadResultAsync<TranslationResult>(project.Id, task.Id, previous.ResultPath, token);
            accepted = saved?.Cues; ranges = saved?.Ranges ?? [];
        }
        if (operation == StageOperation.Retranslate && accepted is null) throw new ArgumentException("请先生成字幕结果。");
        var originalFailures = previous.FailedUnits.ToHashSet(StringComparer.Ordinal);
        var stage = creating ? new StageRecord { Version = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
            UpstreamStage = upstreamKind, UpstreamVersionId = upstream?.Version ?? "", InputResultPath = upstream?.ResultPath ?? "",
            InputIdentity = upstream?.OutputIdentity ?? task.InputPath, Settings = StageSettingsSnapshot.Capture(kind, settings),
            Operation = operation, BaseResultVersionId = operation == StageOperation.Retranslate ? previous.Version : "",
            PreviousResultPath = previous.ResultPath, PreviousSettings = previous.Settings, ReuseSharedCache = mode != ProcessingMode.Restart } : previous;
        if (creating && previous.Version.Length > 0)
            await AtomicFile.WriteJsonAsync(Path.Combine(store.StageDirectory(project.Id, task.Id, kind, previous.Version), "stage.json"), previous, token);
        task.SetStage(kind, stage);
        var directory = store.StageDirectory(project.Id, task.Id, kind, stage.Version);
        var resultFile = Path.Combine(directory, "result.json"); Directory.CreateDirectory(directory);
        stage.ResultPath = Path.GetRelativePath(store.MediaDirectory(project.Id, task.Id), resultFile);
        stage.State = ProcessingState.Running; stage.NeedsUpdate = false; stage.ResultUnavailable = false; stage.Error = "";
        await store.SaveTaskAsync(project.Id, task, token);
        Task writes = Task.CompletedTask; var gate = new object();
        void Report(TranslationProgress value)
        {
            lock (gate)
            {
                if (value.UnitStates is not null)
                {
                    stage.WorkPlanKnown = true;
                    foreach (var unit in value.UnitStates) stage.UnitStates[unit.Key] = unit.Value;
                }
                if (kind == TaskStage.Translation && value.Cues is not null)
                {
                    var priorTranslations = accepted?.ToDictionary(c => c.Id, c => c.TranslatedText);
                    accepted = value.Cues.Select(c => c.TranslatedText is null ? c with { TranslatedText = priorTranslations?.GetValueOrDefault(c.Id) } : c).ToArray();
                    var partial = new TranslationResult(accepted, value.FailedIds ?? [], 0, 0) { Ranges = ranges };
                    writes = writes.ContinueWith(async earlier => { await earlier; await AtomicFile.WriteJsonAsync(resultFile, partial, CancellationToken.None); },
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                }
                foreach (var id in value.CueIds) stage.UnitStates[id] = ProcessingState.Running;
                foreach (var cue in value.Cues ?? []) if (cue.TranslatedText is not null) stage.UnitStates[cue.Id] = ProcessingState.Completed;
                foreach (var id in value.FailedIds ?? []) stage.UnitStates[id] = ProcessingState.Failed;
            }
            progress.Report(new(kind, value.Stage, value.Cues, value.FailedIds, value.CueIds) { WorkProgress = value.WorkProgress });
        }
        try
        {
            progress.Report(new(kind, "开始处理"));
            if (needsModel)
            {
                ConfigurationStore.Validate(settings, validateAllProfiles: false, step: kind);
                if (settings.Provider != TranslationProvider.Local && (string.IsNullOrWhiteSpace(translationKey(settings)) || !string.IsNullOrEmpty(credentialHint?.Invoke(settings))))
                    throw new Cuelify.Infrastructure.ServiceCredentialException("翻译服务");
                if (prepare && prepareTranslation is not null) await prepareTranslation(settings, token);
            }
            switch (kind)
            {
                case TaskStage.Chunking:
                    stage.WorkPlanKnown = true; stage.UnitStates["chunking"] = ProcessingState.Running;
                    var chunks = await jobs.PrepareChunksAsync(task.InputPath, settings, directory, !stage.ReuseSharedCache,
                        new InlineProgress<TranscriptionProgress>(v => progress.Report(new(kind, v.Stage) { WorkProgress = v.WorkProgress })), token);
                    await AtomicFile.WriteJsonAsync(resultFile, chunks, CancellationToken.None);
                    stage.OutputIdentity = chunks.Identity; task.SourceSha256 = chunks.SourceSha256; task.Duration = chunks.Duration;
                    stage.UnitStates["chunking"] = ProcessingState.Completed;
                    break;
                case TaskStage.Asr:
                    var prepared = await store.ReadResultAsync<ChunkingResult>(project.Id, task.Id, stage.InputResultPath, token) ?? throw new InvalidDataException("分片结果缺失。");
                    stage.WorkPlanKnown = true;
                    foreach (var file in prepared.Files) stage.UnitStates.TryAdd(file.Chunk.Index.ToString(), ProcessingState.NotStarted);
                    void ReportAsr(string message)
                    {
                        lock (gate)
                        {
                            var states = prepared.Files.Select(f => stage.UnitStates.GetValueOrDefault(f.Chunk.Index.ToString())).ToArray();
                            progress.Report(new(kind, message) { WorkProgress = new(states.Count(s => s == ProcessingState.Completed), states.Length,
                                states.Count(s => s == ProcessingState.Running), states.Count(s => s == ProcessingState.Failed)) });
                        }
                    }
                    ReportAsr("Transcribing");
                    var source = await jobs.RecognizeChunksAsync(prepared, settings, speechKey(), directory, stage.ReuseSharedCache,
                        mode == ProcessingMode.RetryFailed ? originalFailures.Select(int.Parse).ToHashSet() : null,
                        new InlineProgress<TranscriptionProgress>(v =>
                        {
                            if (v.ChunkIndex is { } index && v.State is { } state) lock (gate) stage.UnitStates[index.ToString()] = state;
                            ReportAsr(v.Stage);
                        }), token);
                    source = source with { Cues = [] };
                    await AtomicFile.WriteJsonAsync(resultFile, source, CancellationToken.None);
                    stage.OutputIdentity = AtomicFile.Hash(new { source.Words, source.Duration });
                    break;
                case TaskStage.Subtitles:
                    var asr = await store.ReadResultAsync<TranscriptionResult>(project.Id, task.Id, stage.InputResultPath, token) ?? throw new InvalidDataException("识别结果缺失。");
                    var segmented = await jobs.GenerateStageAsync(asr, settings, translationKey(settings), directory, stage.ReuseSharedCache,
                        mode == ProcessingMode.RetryFailed ? originalFailures : null, new InlineProgress<TranslationProgress>(Report), token);
                    await AtomicFile.WriteJsonAsync(resultFile, segmented, CancellationToken.None);
                    stage.OutputIdentity = AtomicFile.Hash(new { segmented.Cues, segmented.Ranges });
                    progress.Report(new(kind, "Completed", segmented.Cues));
                    break;
                case TaskStage.Translation:
                    var engineCalls = 0; var cacheHits = 0;
                    if (accepted is null)
                    {
                        var segmentation = await store.ReadResultAsync<SubtitlePipelineResult>(project.Id, task.Id, stage.InputResultPath, token) ?? throw new InvalidDataException("分句结果缺失。");
                        accepted = segmentation.Cues; ranges = segmentation.Ranges;
                    }
                    if (accepted is null) throw new InvalidDataException("字幕结果缺失。");
                    var forced = selectedIds ?? (mode == ProcessingMode.RetryFailed ? originalFailures : null);
                    if (creating && stage.Operation == StageOperation.Retranslate) forced ??= accepted.Select(c => c.Id).ToHashSet();
                    if (creating && stage.Operation == StageOperation.Generate)
                        accepted = accepted.Select(c => c with { TranslatedText = null }).ToArray();
                    if (forced is not null) accepted = accepted.Select(c => forced.Contains(c.Id) ? c with { TranslatedText = null } : c).ToArray();
                    await AtomicFile.WriteJsonAsync(resultFile, new TranslationResult(accepted, [], 0, 0) { Ranges = ranges }, CancellationToken.None);
                    progress.Report(new(kind, "CuesReady", accepted));
                    var translated = forced is null && accepted.All(c => c.TranslatedText is not null)
                        ? new TranslationResult(accepted, [], 0, 0)
                        : await jobs.TranslateStageAsync(accepted, settings, translationKey(settings), directory, stage.ReuseSharedCache, forced, new InlineProgress<TranslationProgress>(Report), token);
                    await writes;
                    translated = translated with { Ranges = ranges, EngineCalls = translated.EngineCalls + engineCalls, CacheHits = translated.CacheHits + cacheHits };
                    await AtomicFile.WriteJsonAsync(resultFile, translated, CancellationToken.None);
                    stage.FailedUnits = translated.FailedIds.ToList();
                    stage.UnitErrors = translated.FailureReasons.ToDictionary(k => k.Key, k => UserErrorMessages.FromCategory(k.Value));
                    stage.OutputIdentity = AtomicFile.Hash(new { translated.Cues, translated.Ranges });
                    stage.State = translated.IsComplete ? ProcessingState.Completed : ProcessingState.PartialFailure;
                    foreach (var cue in translated.Cues) stage.UnitStates[cue.Id] = translated.FailedIds.Contains(cue.Id) ? ProcessingState.Failed : ProcessingState.Completed;
                    if (!translated.IsComplete) stage.Error = $"{translated.FailedIds.Count} 条字幕翻译失败。";
                    progress.Report(new(kind, translated.IsComplete ? "Completed" : "PartialFailure", translated.Cues, translated.FailedIds));
                    break;
            }
            if (kind != TaskStage.Translation) { stage.State = ProcessingState.Completed; stage.WorkPlanKnown = true; stage.FailedUnits.Clear(); stage.UnitErrors.Clear(); }
        }
        catch (Exception e)
        {
            await writes; stage.State = token.IsCancellationRequested ? ProcessingState.Cancelled : ProcessingState.Failed;
            if (e is TranscriptionFailedException failed)
            {
                stage.FailedUnits = failed.FailedChunks.Select(i => i.ToString()).ToList();
                stage.UnitErrors = failed.Categories.ToDictionary(k => k.Key.ToString(), k => UserErrorMessages.FromCategory(k.Value));
            }
            else if (kind == TaskStage.Subtitles)
                stage.FailedUnits = Directory.GetFiles(directory, "*.failed", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)[..^7]).ToList();
            else if (kind == TaskStage.Chunking && !token.IsCancellationRequested) stage.FailedUnits = ["chunking"];
            stage.Error = UserErrorMessages.FromException(e); throw;
        }
        finally
        {
            stage.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var id in stage.UnitStates.Where(u => u.Value == ProcessingState.Running).Select(u => u.Key).ToArray())
                stage.UnitStates[id] = token.IsCancellationRequested ? ProcessingState.Cancelled : ProcessingState.Failed;
            await AtomicFile.WriteJsonAsync(Path.Combine(directory, "stage.json"), stage, CancellationToken.None);
            if (creating) await store.SelectDownstreamAsync(project.Id, task, kind, CancellationToken.None);
            RefreshValidity(task); await store.SaveTaskAsync(project.Id, task, CancellationToken.None);
        }
    }
    public static void RefreshValidity(MediaTaskDocument task)
    {
        foreach (var stage in task.Stages.Where(s => s.Version.Length > 0 && s.UpstreamStage is not null))
            stage.NeedsUpdate = stage.UpstreamStage is { } upstream && (!task.Stage(upstream).IsReady || stage.UpstreamVersionId != task.Stage(upstream).Version);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
