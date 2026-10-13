using Cuelify.Core.Projects;
using Cuelify.Infrastructure.Storage;

namespace Cuelify.Desktop.Services;

internal static class StageResultReader
{
    public static async Task<T?> ReadAsync<T>(ProjectStore store, string projectId, MediaTaskDocument task,
        StageRecord stage, CancellationToken token = default) where T : class
    {
        if (stage.ResultPath.Length == 0) return null;
        if (!File.Exists(store.ResolveResult(projectId, task.Id, stage.ResultPath)))
        {
            if (stage.State == ProcessingState.Completed)
            { stage.ResultUnavailable = true; stage.Error = "阶段结果文件缺失，请恢复或重新执行本步骤。"; }
            return null;
        }
        try
        {
            var result = await store.ReadResultAsync<T>(projectId, task.Id, stage.ResultPath, token);
            if (result is not null)
            {
                if (stage.ResultUnavailable && stage.State == ProcessingState.Completed) stage.Error = "";
                stage.ResultUnavailable = false;
            }
            return result;
        }
        catch (InvalidDataException error)
        { stage.ResultUnavailable = true; stage.Error = error.Message; return null; }
    }
}
