using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private HttpClient? _llamaHttp;
    private ILlamaPackageService _packages = null!;
    private bool _updatingLlama;
    [ObservableProperty] private bool isLlamaInstalling;
    [ObservableProperty] private string llamaInstallStatus = "";
    [ObservableProperty] private double llamaInstallPercent;
    [ObservableProperty] private bool llamaInstallIndeterminate = true;
    public string LlamaRuntimeStatus => File.Exists(Settings.LlamaServerPath)
        ? string.IsNullOrWhiteSpace(Settings.LlamaServerVersion) ? "已指定运行程序" : $"运行版本：{Settings.LlamaServerVersion}"
        : "尚未下载或选择运行程序";

    private void InitializeLlamaPackages(ILlamaPackageService? packages)
    {
        if (packages is null)
        {
            _llamaHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
            _packages = new LlamaPackageService(_llamaHttp);
        }
        else _packages = packages;
    }
    private void LlamaSettingsChanged(string? property)
    {
        if (property == nameof(Services.AppSettings.LlamaServerPath) && !_updatingLlama && !_loadingSettings)
        { Settings.LlamaServerVersion = ""; LlamaInstallStatus = ""; }
        if (property is nameof(Services.AppSettings.LlamaServerPath) or nameof(Services.AppSettings.LlamaServerVersion) || _loadingSettings)
            OnPropertyChanged(nameof(LlamaRuntimeStatus));
    }
    private IProgress<ModelDownloadProgress> LlamaProgress(CancellationToken token) => new Progress<ModelDownloadProgress>(value => Post(token, () =>
    {
        LlamaInstallIndeterminate = value.TotalBytes is null or 0 || value.Stage != "下载中";
        LlamaInstallPercent = value.TotalBytes is > 0 ? Math.Clamp(100d * value.Bytes / value.TotalBytes.Value, 0, 100) : 0;
        LlamaInstallStatus = value.Stage == "下载中" ? $"已下载 {value.Bytes / 1_000_000d:F1} MB" +
            (value.TotalBytes is { } total ? $" / {total / 1_000_000d:F1} MB" : "") : value.Stage;
    }));

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private Task DownloadLlamaAsync() => RunLlamaOperationAsync(async token =>
    {
        await _jobs.ReleaseEmbeddedModelAsync();
        var progress = LlamaProgress(token);
        var installation = await Task.Run(() => _packages.DownloadAsync(_store.Root, progress, token), token);
        ApplyLlamaServer(installation.ServerPath, installation.Version);
        LlamaInstallStatus = "llama.cpp 已下载并解压。保存设置后用于新的处理，可测试本地翻译。";
        AddLog($"llama.cpp 已安装：{installation.Version}（Windows x64 Vulkan）。");
    });

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ChooseLlamaServerAsync()
    {
        try
        {
            var path = await _dialogs.OpenAsync("选择 llama-server.exe", ["llama-server.exe", "*.exe"]);
            if (path is null) return;
            await RunLlamaOperationAsync(async token =>
            {
                LlamaInstallStatus = "检查运行程序与 Vulkan 支持…";
                var binary = await Task.Run(() => _packages.InspectAsync(path, token), token);
                ApplyLlamaServer(binary.Path, binary.Version);
                LlamaInstallStatus = "运行程序检查通过。保存设置后用于新的处理，可测试本地翻译。";
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }
    private void ApplyLlamaServer(string path, string version)
    {
        _updatingLlama = true;
        try { Settings.LlamaServerPath = path; Settings.LlamaServerVersion = version; }
        finally { _updatingLlama = false; }
        OnPropertyChanged(nameof(LlamaRuntimeStatus));
    }
    private async Task RunLlamaOperationAsync(Func<CancellationToken, Task> action)
    {
        if (!CanConfigure) return;
        IsLlamaInstalling = true; LlamaInstallIndeterminate = true; LlamaInstallStatus = "准备 llama.cpp…";
        try
        {
            await ExecuteAsync(async token =>
            {
                try { await action(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { LlamaInstallStatus = "已取消，可继续下载或重新选择。"; throw; }
                catch (Exception exception)
                {
                    LlamaInstallStatus = "操作未完成，请检查错误提示后重试。";
                    if (exception is HttpRequestException)
                        throw new InvalidDataException("无法下载官方 llama.cpp 运行包，请检查网络或 GitHub 访问限制后重试。", exception);
                    if (exception is IOException)
                        throw new InvalidDataException("无法保存 llama.cpp 运行包，请检查设置文件夹的权限、可用空间和文件占用。", exception);
                    throw;
                }
            });
        }
        finally { IsLlamaInstalling = false; OnPropertyChanged(nameof(LlamaRuntimeStatus)); }
    }
}
