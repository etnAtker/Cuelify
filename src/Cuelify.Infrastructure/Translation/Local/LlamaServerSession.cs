using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuelify.Core.Media;
using Cuelify.Core.Translation;
using Cuelify.Infrastructure.Media;

namespace Cuelify.Infrastructure.Translation.Local;

public sealed class LlamaServerSession(EmbeddedModelOptions options, LlamaServerBinary binary) : ILlamaServerSession
{
    private readonly SemaphoreSlim _startup = new(1);
    private HttpClient? _http;
    private readonly object _logSync = new();
    private readonly StringBuilder _log = new();
    private string _lastDiagnostic = "";
    private Process? _process;
    private bool _processStarted;
    private WindowsProcessJob? _job;
    private Task? _stdout;
    private Task? _stderr;
    private FileStream? _modelFile;
    private LlamaServerProtocol? _protocol;
    private bool _disposed;
    private bool _started;
    public VulkanEvidence? Evidence { get; private set; }
    public int? ProcessId => _processStarted ? _process?.Id : null;
    public bool IsRunning => _processStarted && _process is { HasExited: false };
    public string Diagnostic { get { lock (_logSync) return _log.Length == 0 ? _lastDiagnostic : _log.ToString(); } }

    public static IReadOnlyList<string> Arguments(EmbeddedModelOptions options, LlamaServerBinary binary) =>
    [
        "--model", Path.GetFullPath(options.ModelPath), "--host", "127.0.0.1", "--port", "0",
        "--device", binary.Device, "--gpu-layers", options.GpuLayers.ToString(CultureInfo.InvariantCulture),
        "--parallel", options.Concurrency.ToString(CultureInfo.InvariantCulture),
        "--ctx-size", checked((long)options.ContextSize * options.Concurrency).ToString(CultureInfo.InvariantCulture),
        "--no-kv-unified", "--fit", "off", "--no-webui", "--log-colors", "off",
        "--cors-origins", "",
        "--log-verbosity", "4"
    ];

    public Task EnsureStartedAsync(CancellationToken token) => EnsureStartedAsync(token, null);

    public async Task EnsureStartedAsync(CancellationToken token, IProgress<LocalPreparationProgress>? progress)
    {
        await _startup.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                if (!IsRunning)
                {
                    var failure = Failure("本地服务已退出，请重新测试或开始处理。");
                    await StopAsync();
                    throw failure;
                }
                progress?.Report(new(LocalPreparationStage.ReusingService));
                return;
            }
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new LocalTranslationException("WindowsX64Required", "本地翻译仅支持 Windows x64。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                lock (_logSync) _lastDiagnostic = "";
                Evidence = null;
                progress?.Report(new(LocalPreparationStage.CheckingRuntime));
                if (await LlamaServerBinary.FingerprintAsync(binary.Path, timeout.Token) != binary.Identity)
                    throw new LocalTranslationException("ServerChanged", "llama.cpp 运行包已改变，请重新测试。");
                // 只持有读取句柄，内容、架构与量化是否可加载由 llama.cpp 判断。
                try { _modelFile = new(options.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { throw new LocalTranslationException("ModelFile", "无法读取模型文件，请检查路径、读取权限和文件占用。"); }
                if (options.FileVersion is { } version && ModelFileVersion.Read(options.ModelPath) != version)
                    throw new LocalTranslationException("ModelFile", "模型文件在准备过程中发生变化，请重新开始处理。");
                progress?.Report(new(LocalPreparationStage.StartingService));
                var start = ProcessCommandRunner.CreateStartInfo(new(binary.Path, Arguments(options, binary), TimeSpan.FromMinutes(5)));
                start.WorkingDirectory = Path.GetDirectoryName(binary.Path)!;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
                // 本地运行参数由任务快照决定，不能被父进程中的 llama.cpp 环境选项覆盖。
                foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("LLAMA_ARG_", StringComparison.Ordinal) ||
                    name.StartsWith("AIP_", StringComparison.Ordinal) || name is "GGML_BACKEND_PATH" or "HF_TOKEN").ToArray())
                    start.Environment.Remove(name);
                _process = new() { StartInfo = start };
                _processStarted = _process.Start();
                if (!_processStarted) throw new IOException("无法启动 llama-server。");
                _job = WindowsProcessJob.Attach(_process);
                _stdout = ConsumeAsync(_process.StandardOutput);
                _stderr = ConsumeAsync(_process.StandardError);
                progress?.Report(new(LocalPreparationStage.LoadingModel));
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (_process.HasExited) throw await StartupFailureAsync("本地服务启动失败，请检查运行包、模型和显存。", timeout.Token);
                    var address = Regex.Match(Diagnostic, @"http://127\.0\.0\.1:(\d+)", RegexOptions.CultureInvariant);
                    if (address.Success)
                    {
                        _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                        { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new($"http://127.0.0.1:{address.Groups[1].Value}/") };
                        break;
                    }
                    await Task.Delay(100, timeout.Token);
                }
                while (!await ReadyAsync(timeout.Token))
                {
                    if (_process.HasExited) throw await StartupFailureAsync("本地服务在加载模型时退出。", timeout.Token);
                    await Task.Delay(100, timeout.Token);
                }
                progress?.Report(new(LocalPreparationStage.CheckingService));
                Evidence = VulkanEvidence.Parse(Diagnostic);
                if (!Evidence.HasGpuOffload)
                    throw new LocalTranslationException("VulkanOffload", "本次模型加载没有有效的 Vulkan GPU 卸载记录。");
                _protocol = new(_http, options.ContextSize, options.MaximumTokens);
                await _protocol.ValidateCapacityAsync(options.Concurrency, timeout.Token);
                _started = true;
                progress?.Report(new(LocalPreparationStage.Ready));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { await StopAsync(); throw new TimeoutException("本地模型加载超时，请检查运行包、模型和显存。"); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
            { await StopAsync(); throw new LocalTranslationException("NativeLibrary", "无法启动完整的 llama.cpp 运行包，请检查所选程序及配套库。"); }
            catch { await StopAsync(); throw; }
        }
        finally { _startup.Release(); }
    }

    public Task<PreparedInferencePrompt> PrepareAsync(TranslationRequest request, CancellationToken token) => _protocol!.PrepareAsync(request, token);
    public async Task<LocalCompletion> CompleteAsync(string prompt, IProgress<int>? progress, CancellationToken token)
    {
        try { return await _protocol!.CompleteAsync(prompt, progress, token); }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !token.IsCancellationRequested)
        { throw Failure("本地服务连接中断，请检查运行包和显存后重试。"); }
    }

    public Task<bool> IsReadyAsync(CancellationToken token) => _started && IsRunning ? ReadyAsync(token) : Task.FromResult(false);

    private async Task<bool> ReadyAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await _http!.GetAsync("health", timeout.Token);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return false;
            if (!response.IsSuccessStatusCode) throw new LocalTranslationException("ServerIncompatible", "本地服务的健康检查接口不可用。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok";
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private async Task ConsumeAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_logSync)
            {
                // 常规级别只保留有界的运行诊断，不启用含提示词正文的 verbose 日志。
                const int limit = 1_048_576;
                if (_log.Length + line.Length + 2 > limit)
                    _log.Remove(0, Math.Min(_log.Length, Math.Max(262_144, _log.Length + line.Length + 2 - limit)));
                _log.AppendLine(line.Length <= limit - 2 ? line : line[..(limit - 2)]);
            }
        }
    }
    private async Task<LocalTranslationException> StartupFailureAsync(string message, CancellationToken token)
    {
        if (_stdout is not null && _stderr is not null)
        {
            try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(2), token); }
            catch (TimeoutException) { /* 保留已收集的原生诊断，不因日志管道阻塞丢失加载错误。 */ }
        }
        return Failure(message);
    }
    private LocalTranslationException Failure(string message)
    {
        var log = Diagnostic;
        var category = log.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || log.Contains("VK_ERROR_OUT_OF_DEVICE_MEMORY", StringComparison.Ordinal) ?
            "GpuMemory" : log.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase) ? "ModelArchitecture" :
            log.Contains("failed to load model", StringComparison.OrdinalIgnoreCase) || log.Contains("error loading model", StringComparison.OrdinalIgnoreCase) ? "ModelLoad" : "ServerExited";
        return new(category, category == "ModelLoad" ? "llama.cpp 无法加载模型，请重新选择模型文件或下载模型。" : message, log);
    }

    private async Task StopAsync()
    {
        try
        {
            if (_processStarted && _process is { HasExited: false })
            { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            if (_stdout is not null && _stderr is not null) await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _job?.Dispose(); _job = null;
            _process?.Dispose(); _process = null;
            _processStarted = false;
            _modelFile?.Dispose(); _modelFile = null;
            _stdout = null; _stderr = null; _protocol = null;
            _http?.Dispose(); _http = null;
            _started = false;
            lock (_logSync) { _lastDiagnostic = _log.ToString(); _log.Clear(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _startup.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopAsync(); }
            finally { _http?.Dispose(); }
        }
        finally { _startup.Release(); }
    }
}
