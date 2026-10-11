using Cuelify.Core.Projects;
using Cuelify.Infrastructure.Storage;
using Xunit;

namespace Cuelify.Tests;

public sealed class ProjectStoreTests
{
    [Fact]
    public async Task ProjectListComesFromManifestsAndRenameKeepsDirectoryAndOrder()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path);
        var project = new ProjectDocument { Name = "测试项目", MediaIds = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")] };
        await store.SaveAsync(project);
        var path = store.ProjectDirectory(project.Id); project.Name = "新名称"; await store.SaveAsync(project);
        var loaded = Assert.Single(await store.ListAsync());
        Assert.Equal("新名称", loaded.Name); Assert.Equal(project.MediaIds, loaded.MediaIds); Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(Path.Combine(directory.Path, "projects.json")));
    }
    [Fact]
    public async Task RunningTaskRestoresAsInterruptedWithoutErasingDownstreamResults()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        var task = new MediaTaskDocument { InputPath = "missing.mp4" };
        task.Asr.State = ProcessingState.Running; task.Translation.State = ProcessingState.Completed;
        task.Asr.UnitStates = new() { ["0"] = ProcessingState.Completed, ["1"] = ProcessingState.Running };
        task.Translation.ResultPath = "Results/Translation/old.json";
        await store.SaveTaskAsync(project.Id, task);
        using var projectLock = store.AcquireProject(project.Id);
        var recovered = await store.LoadTaskAsync(project.Id, task.Id);
        Assert.Equal(ProcessingState.Interrupted, recovered.Asr.State);
        Assert.Equal(ProcessingState.Interrupted, recovered.Asr.UnitStates["1"]);
        Assert.Equal(ProcessingState.Completed, recovered.Asr.UnitStates["0"]);
        Assert.Equal(ProcessingState.Completed, recovered.Translation.State);
        Assert.Equal(task.Translation.ResultPath, recovered.Translation.ResultPath);
    }
    [Fact]
    public async Task DamagedAndFutureProjectAreReportedWithoutBeingOverwritten()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        await store.SaveAsync(project); var path = Path.Combine(store.ProjectDirectory(project.Id), "project.json");
        await File.WriteAllTextAsync(path, "{broken"); Assert.Empty(await store.ListAsync()); Assert.Single(store.Notices);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        await AtomicFile.WriteJsonAsync(path, new { SchemaVersion = 99, project.Id, project.Name }, CancellationToken.None);
        Assert.Empty(await store.ListAsync()); Assert.Single(store.Notices);
        Assert.Contains("99", await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void ResultPathsAndIdsCannotEscapeTheirMediaDirectory()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path);
        var project = Guid.NewGuid().ToString("N"); var task = Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidDataException>(() => store.ProjectDirectory("../other"));
        Assert.Throws<InvalidDataException>(() => store.ResolveResult(project, task, "../../../credentials.json"));
        Assert.Throws<InvalidDataException>(() => store.ResolveResult(project, task, Path.Combine(directory.Path, "secret.json")));
    }
    [Fact]
    public async Task ProjectLockExcludesAnotherWriterAndWorkspaceIsIndependent()
    {
        using var directory = new TestDirectory(); var store = new ProjectStore(directory.Path); var project = new ProjectDocument();
        using var projectLock = store.AcquireProject(project.Id);
        Assert.Throws<IOException>(() => store.AcquireProject(project.Id));
        await store.SaveWorkspaceAsync(new(project.Id, null)); Assert.Equal(project.Id, (await store.LoadWorkspaceAsync())!.ProjectId);
    }
}
