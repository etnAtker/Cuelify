using System.Diagnostics;
using Cuelify.Core.Media;

namespace Cuelify.Infrastructure.Media;

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(CommandRequest command, Stream? output, CancellationToken cancellationToken)
    {
        if (command.Timeout <= TimeSpan.Zero) throw new ArgumentException("命令超时必须大于零。");
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Timeout);
        var start = CreateStartInfo(command);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动外部命令。");
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = output is null ? process.StandardOutput.ReadToEndAsync(timeout.Token) : null;
        Task pump = stdout is null ? process.StandardOutput.BaseStream.CopyToAsync(output!, timeout.Token) : stdout;
        var exit = process.WaitForExitAsync(timeout.Token);
        try
        {
            // 单独观察消费任务，写入失败时立即终止进程，避免等待管道 EOF 死锁。
            var pending = new List<Task> { pump, stderr, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            return new(process.ExitCode, stdout is null ? "" : await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("外部命令执行超时，已终止进程树。");
        }
        finally
        {
            if (!process.HasExited)
            {
                timeout.Cancel();
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            // 观察后台消费异常；不覆盖原始失败原因。
            try { await Task.WhenAll(pump, stderr, exit).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
        }
    }

    public static ProcessStartInfo CreateStartInfo(CommandRequest command)
    {
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
