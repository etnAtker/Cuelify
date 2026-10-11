using Cuelify.Core.Projects;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Transcription;
using System.Text.Json;

namespace Cuelify.Infrastructure.Storage;

public sealed partial class ProjectStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public List<string> Notices { get; } = [];
    private string ProjectsRoot => Path.Combine(Root, "Projects");
    private static string Id(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("项目或任务 ID 无效。");
    public string ProjectDirectory(string projectId) => Path.Combine(ProjectsRoot, Id(projectId));
    public string MediaDirectory(string projectId, string mediaId) => Path.Combine(ProjectDirectory(projectId), "Media", Id(mediaId));
    public string StageDirectory(string projectId, string mediaId, TaskStage stage, string version) =>
        Path.Combine(MediaDirectory(projectId, mediaId), "Results", stage.ToString(), Id(version));
    public FileStream AcquireProject(string projectId)
    {
        var directory = ProjectDirectory(projectId); Directory.CreateDirectory(directory);
        return new(Path.Combine(directory, "project.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken token = default)
    {
        Notices.Clear(); var projects = new List<ProjectDocument>();
        if (!Directory.Exists(ProjectsRoot)) return projects;
        foreach (var directory in Directory.EnumerateDirectories(ProjectsRoot))
        {
            try
            {
                var project = await AtomicFile.ReadJsonAsync<ProjectDocument>(Path.Combine(directory, "project.json"), token);
                if (project is null) continue;
                Validate(project);
                if (Path.GetFileName(directory) != project.Id) throw new InvalidDataException("项目目录与 ID 不一致。");
                projects.Add(project);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            { Notices.Add($"无法读取项目 {Path.GetFileName(directory)}：{e.Message}"); }
        }
        return projects.OrderByDescending(p => p.LastOpenedAt).ToArray();
    }
    public Task SaveAsync(ProjectDocument project, CancellationToken token = default)
    {
        Validate(project); project.ModifiedAt = DateTimeOffset.UtcNow;
        return AtomicFile.WriteJsonAsync(Path.Combine(ProjectDirectory(project.Id), "project.json"), project, token);
    }
    public Task SaveTaskAsync(string projectId, MediaTaskDocument task, CancellationToken token = default)
    {
        Validate(task);
        return AtomicFile.WriteJsonAsync(Path.Combine(MediaDirectory(projectId, task.Id), "task.json"), task, token);
    }
    public async Task<MediaTaskDocument> LoadTaskAsync(string projectId, string mediaId, CancellationToken token = default)
    {
        var task = await ReadTaskAsync(projectId, mediaId, token);
        if (task.SchemaVersion == 1) await UpgradeTaskAsync(projectId, task, token);
        if (task.SchemaVersion == 2) await UpgradeIndependentStagesAsync(projectId, task, token);
        // 仅在持有项目排他锁的恢复入口调用；不会改写其他进程正在执行的任务。
        foreach (var stage in task.Stages.Where(s => s.State == ProcessingState.Running))
        {
            stage.State = ProcessingState.Interrupted; stage.Error = "上次处理已中断。";
            foreach (var unit in stage.UnitStates.Where(u => u.Value == ProcessingState.Running).Select(u => u.Key).ToArray())
                stage.UnitStates[unit] = ProcessingState.Interrupted;
        }
        return task;
    }
    public async Task<MediaTaskDocument> ReadTaskAsync(string projectId, string mediaId, CancellationToken token = default)
    {
        var task = await AtomicFile.ReadJsonAsync<MediaTaskDocument>(Path.Combine(MediaDirectory(projectId, mediaId), "task.json"), token)
            ?? throw new InvalidDataException("媒体任务记录缺失。");
        Validate(task);
        if (task.Id != mediaId) throw new InvalidDataException("媒体目录与 ID 不一致。");
        return task;
    }
    public async Task<IReadOnlyList<StageRecord>> VersionsAsync(string projectId, MediaTaskDocument task, TaskStage kind, CancellationToken token = default, bool matchingUpstreamOnly = false)
    {
        var current = task.Stage(kind);
        var versions = new List<StageRecord>();
        if (current.Version.Length > 0) versions.Add(current);
        var directory = Path.Combine(MediaDirectory(projectId, task.Id), "Results", kind.ToString());
        if (Directory.Exists(directory))
            foreach (var version in Directory.EnumerateDirectories(directory))
            {
                if (!Guid.TryParseExact(Path.GetFileName(version), "N", out _) || Path.GetFileName(version) == current.Version) continue;
                try
                {
                    var record = await AtomicFile.ReadJsonAsync<StageRecord>(Path.Combine(version, "stage.json"), token);
                    if (record is null) continue;
                    _ = ResolveResult(projectId, task.Id, record.ResultPath);
                    versions.Add(record);
                }
                catch (InvalidDataException e) { Notices.Add(e.Message); }
            }
        if (matchingUpstreamOnly)
        {
            var upstreamKind = TaskStagePlan.Upstream(kind);
            var upstreamId = upstreamKind is { } upstream ? task.Stage(upstream).Version : "";
            versions = versions.Where(v => v.UpstreamStage == upstreamKind && v.UpstreamVersionId == upstreamId &&
                (upstreamKind is null || upstreamId.Length > 0)).ToList();
        }
        foreach (var version in versions)
            version.Archived = PipelineSettingsMigration.IsJoint(version.Settings) &&
                (kind == TaskStage.Subtitles || kind == TaskStage.Translation && version.UpstreamStage == TaskStage.Asr);
        if (matchingUpstreamOnly) versions = versions.Where(v => !v.Archived).ToList();
        return versions.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Version, StringComparer.Ordinal).ToArray();
    }
    public async Task SetCurrentAsync(string projectId, MediaTaskDocument task, TaskStage kind, StageRecord record, CancellationToken token = default)
    {
        var candidates = await VersionsAsync(projectId, task, kind, token, matchingUpstreamOnly: true);
        var selected = candidates.FirstOrDefault(v => v.Version == record.Version) ?? throw new InvalidOperationException("该版本不属于当前上游，请重新选择。");
        task.SetStage(kind, selected);
        await SelectDownstreamAsync(projectId, task, kind, token);
        await SaveTaskAsync(projectId, task, token);
    }
    public async Task SelectDownstreamAsync(string projectId, MediaTaskDocument task, TaskStage? after, CancellationToken token = default)
    {
        var steps = TaskStagePlan.Steps();
        var start = after is null ? 1 : steps.ToList().IndexOf(after.Value) + 1;
        foreach (var kind in steps.Skip(start))
            task.SetStage(kind, (await VersionsAsync(projectId, task, kind, token, matchingUpstreamOnly: true)).FirstOrDefault() ?? new());
    }
    private static void NormalizeLegacy(StageRecord record, TaskStage kind)
    {
        if (record.CreatedAt == default) record.CreatedAt = record.UpdatedAt ?? DateTimeOffset.MinValue;
        if (record.InputResultPath.Length == 0) return;
        var parts = record.InputResultPath.Split(['/', '\\']);
        if (parts.Length >= 4 && Enum.TryParse<TaskStage>(parts[^3], out var upstream) && Guid.TryParseExact(parts[^2], "N", out _))
        { record.UpstreamStage = upstream; record.UpstreamVersionId = parts[^2]; }
    }
    private async Task UpgradeTaskAsync(string projectId, MediaTaskDocument task, CancellationToken token)
    {
        var taskPath = Path.Combine(MediaDirectory(projectId, task.Id), "task.json");
        if (!File.Exists(taskPath + ".schema1.backup")) File.Copy(taskPath, taskPath + ".schema1.backup");
        var records = new List<(TaskStage Kind, StageRecord Record, string Path)>();
        foreach (var kind in Enum.GetValues<TaskStage>())
        {
            var directory = Path.Combine(MediaDirectory(projectId, task.Id), "Results", kind.ToString());
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "stage.json", SearchOption.AllDirectories))
            {
                var record = await AtomicFile.ReadJsonAsync<StageRecord>(path, token);
                if (record is null) continue;
                NormalizeLegacy(record, kind); records.Add((kind, record, path));
            }
        }
        foreach (var kind in Enum.GetValues<TaskStage>())
        {
            var current = task.Stage(kind);
            if (current.Version.Length == 0 || records.Any(r => r.Kind == kind && r.Record.Version == current.Version)) continue;
            NormalizeLegacy(current, kind);
            records.Add((kind, current, Path.Combine(StageDirectory(projectId, task.Id, kind, current.Version), "stage.json")));
        }
        foreach (var item in records.Where(r => r.Kind == TaskStage.Subtitles && PipelineSettingsMigration.IsJoint(r.Record.Settings)).ToArray())
        {
            if (records.Any(r => r.Kind == TaskStage.Translation && r.Record.InputResultPath == item.Record.ResultPath)) continue;
            var stage = System.Text.Json.JsonSerializer.Deserialize<StageRecord>(System.Text.Json.JsonSerializer.Serialize(item.Record))!;
            var data = stage.ResultPath.Length > 0 ? await ReadResultAsync<SubtitlePipelineResult>(projectId, task.Id, stage.ResultPath, token) : null;
            var directory = StageDirectory(projectId, task.Id, TaskStage.Translation, stage.Version);
            stage.ResultPath = Path.GetRelativePath(MediaDirectory(projectId, task.Id), Path.Combine(directory, "result.json"));
            if (data is not null) await AtomicFile.WriteJsonAsync(Path.Combine(directory, "result.json"),
                new TranslationResult(data.Cues, [], data.EngineCalls, data.CacheHits) { Ranges = data.Ranges }, token);
            records.Add((TaskStage.Translation, stage, Path.Combine(directory, "stage.json")));
        }
        foreach (var item in records)
        {
            var record = item.Record;
            if (item.Kind == TaskStage.Translation && record.UpstreamStage == TaskStage.Subtitles)
            {
                var parent = records.FirstOrDefault(r => r.Kind == TaskStage.Subtitles && r.Record.Version == record.UpstreamVersionId).Record;
                if (parent is not null)
                {
                    if (PipelineSettingsMigration.IsJoint(parent.Settings))
                    { record.UpstreamStage = TaskStage.Asr; record.UpstreamVersionId = parent.UpstreamVersionId; record.InputResultPath = parent.InputResultPath; record.InputIdentity = parent.InputIdentity; }
                    if (record.ResultPath.Length > 0 && parent.ResultPath.Length > 0)
                    {
                        var result = await ReadResultAsync<TranslationResult>(projectId, task.Id, record.ResultPath, token);
                        var generated = await ReadResultAsync<SubtitlePipelineResult>(projectId, task.Id, parent.ResultPath, token);
                        if (result is not null && generated is not null && result.Ranges.Count == 0)
                        {
                            var resultPath = ResolveResult(projectId, task.Id, record.ResultPath);
                            if (!File.Exists(resultPath + ".schema1.backup")) File.Copy(resultPath, resultPath + ".schema1.backup");
                            await AtomicFile.WriteJsonAsync(resultPath, result with { Ranges = generated.Ranges }, token);
                        }
                    }
                }
            }
            if (File.Exists(item.Path) && !File.Exists(item.Path + ".schema1.backup")) File.Copy(item.Path, item.Path + ".schema1.backup");
            await AtomicFile.WriteJsonAsync(item.Path, record, token);
        }
        foreach (var kind in Enum.GetValues<TaskStage>())
        {
            var current = task.Stage(kind); NormalizeLegacy(current, kind);
            var migrated = records.FirstOrDefault(r => r.Kind == kind && r.Record.Version == current.Version).Record;
            if (migrated is not null) task.SetStage(kind, migrated);
        }
        task.SchemaVersion = 2;
        await SaveTaskAsync(projectId, task, token);
    }
    public Task SaveWorkspaceAsync(WorkspaceState state, CancellationToken token = default) =>
        AtomicFile.WriteJsonAsync(Path.Combine(Root, "workspace.json"), state, token);
    public Task<WorkspaceState?> LoadWorkspaceAsync(CancellationToken token = default) =>
        AtomicFile.ReadJsonAsync<WorkspaceState>(Path.Combine(Root, "workspace.json"), token);
    public string ResolveResult(string projectId, string mediaId, string path)
    {
        var directory = MediaDirectory(projectId, mediaId);
        var full = Path.GetFullPath(Path.Combine(directory, path));
        if (Path.IsPathRooted(path) || !full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("任务结果路径超出项目目录。");
        return full;
    }
    public Task<T?> ReadResultAsync<T>(string projectId, string mediaId, string path, CancellationToken token = default) where T : class =>
        AtomicFile.ReadJsonAsync<T>(ResolveResult(projectId, mediaId, path), token);
    private static void Validate(ProjectDocument project)
    {
        _ = Id(project.Id);
        if (project.SchemaVersion != 1 || string.IsNullOrWhiteSpace(project.Name) || project.MediaIds is null || project.MediaIds.Distinct().Count() != project.MediaIds.Count)
            throw new InvalidDataException("项目格式或版本无效，请检查项目文件。");
        foreach (var id in project.MediaIds) _ = Id(id);
        if (project.RemovedMediaIds is null) throw new InvalidDataException("移出记录无效。");
        foreach (var id in project.RemovedMediaIds) _ = Id(id);
        if (project.Batch is { } batch)
        {
            if (!Enum.IsDefined(batch.State) || batch.Items is null || batch.Items.Select(i => i.MediaId).Distinct().Count() != batch.Items.Count)
                throw new InvalidDataException("批量记录无效。");
            foreach (var item in batch.Items)
            {
                _ = Id(item.MediaId);
                if (!Enum.IsDefined(item.State) || item.Stages is null || item.Stages.Any(s => !Enum.IsDefined(s)))
                    throw new InvalidDataException("批量步骤无效。");
            }
        }
    }
    private static void Validate(MediaTaskDocument task)
    {
        _ = Id(task.Id);
        if (task.SchemaVersion is not (1 or 2 or 3) || string.IsNullOrWhiteSpace(task.InputPath) || task.Chunking is null || task.Asr is null || task.Subtitles is null || task.Translation is null || task.ModelOverrides is null)
            throw new InvalidDataException("媒体任务格式或版本无效。");
        foreach (var stage in task.Stages)
        {
            if (stage.Version is null || stage.InputIdentity is null || stage.InputResultPath is null || stage.OutputIdentity is null ||
                stage.ResultPath is null || stage.PreviousResultPath is null || stage.Error is null)
                throw new InvalidDataException("阶段记录字段无效。");
            if (stage.Version.Length > 0) _ = Id(stage.Version);
            if (stage.UpstreamVersionId.Length > 0) _ = Id(stage.UpstreamVersionId);
            if (stage.UpstreamStage is { } upstream && !Enum.IsDefined(upstream) || !Enum.IsDefined(stage.Operation)) throw new InvalidDataException("上游步骤或执行方式无效。");
            if (!Enum.IsDefined(stage.State) || stage.FailedUnits is null || stage.UnitStates is null || stage.UnitStates.Values.Any(state => !Enum.IsDefined(state))) throw new InvalidDataException("阶段状态无效。");
        }
    }
}
