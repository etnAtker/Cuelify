using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuelify.Infrastructure.Translation.Local;

namespace Cuelify.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private HttpClient? _modelHttp;
    private IModelDownloadService _downloads = null!;
    private bool _updatingModelFile;
    [ObservableProperty] private bool isModelDownloading;
    [ObservableProperty] private string modelDownloadStatus = "";
    [ObservableProperty] private double modelDownloadPercent;
    [ObservableProperty] private bool modelDownloadIndeterminate = true;
    public IReadOnlyList<EmbeddedModel> EmbeddedModels => Settings.ModelFiles.ContainsKey(EmbeddedModelCatalog.Legacy.Id) || Settings.EmbeddedModelId == EmbeddedModelCatalog.Legacy.Id
        ? EmbeddedModelCatalog.Presets.Append(EmbeddedModelCatalog.Legacy).ToArray() : EmbeddedModelCatalog.Presets;
    public string ModelFileStatus => File.Exists(Settings.ModelPath) ? "模型文件已存在" : "尚未下载或选择模型文件";
    public EmbeddedModel SelectedEmbeddedModel
    {
        get => EmbeddedModelCatalog.Get(Settings.LocalOptions().ModelId);
        set
        {
            if (!CanConfigure || value is null || value.Id == Settings.LocalOptions().ModelId) return;
            if (IsLocal) CaptureProfile();
            RememberModelFile();
            _updatingModelFile = true;
            try
            {
                Settings.EmbeddedModelId = value.Id;
                var file = Settings.ModelFiles.GetValueOrDefault(value.Id) ?? new(_store.DefaultModelPath(value), "");
                Settings.ModelPath = file.Path; Settings.ModelSha256 = file.Sha256;
            }
            finally { _updatingModelFile = false; }
            RememberModelFile(); ModelDownloadStatus = ""; RefreshModelProperties();
        }
    }
    private void InitializeModelDownloads(IModelDownloadService? downloads)
    {
        if (downloads is null)
        { _modelHttp = new() { Timeout = Timeout.InfiniteTimeSpan }; _downloads = new ModelDownloadService(_modelHttp); }
        else _downloads = downloads;
    }
    private void RememberModelFile() => Settings.ModelFiles[Settings.LocalOptions().ModelId] = new(Settings.ModelPath, Settings.ModelSha256);
    private void RefreshModelProperties()
    { OnPropertyChanged(nameof(EmbeddedModels)); OnPropertyChanged(nameof(SelectedEmbeddedModel)); OnPropertyChanged(nameof(ModelFileStatus)); }
    private void ModelSettingsChanged(PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Services.AppSettings.ModelPath) && !_updatingModelFile && !_loadingSettings)
        { Settings.ModelSha256 = ""; RememberModelFile(); ModelDownloadStatus = ""; }
        if (args.PropertyName is nameof(Services.AppSettings.Provider) or nameof(Services.AppSettings.EmbeddedModelId) or nameof(Services.AppSettings.ModelPath))
            RefreshModelProperties();
    }
    private void ApplyModelFile(EmbeddedModelFile file)
    {
        _updatingModelFile = true;
        try { Settings.ModelPath = file.Path; Settings.ModelSha256 = file.Sha256; RememberModelFile(); }
        finally { _updatingModelFile = false; }
        RefreshModelProperties();
    }
    private IProgress<ModelDownloadProgress> ModelProgress(CancellationToken token) => new Progress<ModelDownloadProgress>(value => Post(token, () =>
    {
        ModelDownloadIndeterminate = value.TotalBytes is null || value.TotalBytes == 0 || value.Stage != "下载中";
        ModelDownloadPercent = value.TotalBytes is > 0 ? Math.Clamp(100d * value.Bytes / value.TotalBytes.Value, 0, 100) : 0;
        ModelDownloadStatus = value.Stage == "下载中" ? $"已下载 {value.Bytes / 1_000_000d:F1} MB" +
            (value.TotalBytes is { } total ? $" / {total / 1_000_000d:F1} MB" : "") : value.Stage;
    }));

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private Task DownloadModelAsync() => RunModelOperationAsync(async token =>
    {
        await _jobs.ReleaseEmbeddedModelAsync();
        var model = SelectedEmbeddedModel;
        var progress = ModelProgress(token);
        var file = await Task.Run(() => _downloads.DownloadAsync(model, _store.Root, progress, token), token);
        ApplyModelFile(file); ModelDownloadStatus = "模型已下载并校验，保存设置后用于新的处理。";
        AddLog($"模型已下载并校验：{model.Name}。");
    });
    private Task VerifySelectedModelAsync(string path) => RunModelOperationAsync(async token =>
    {
        var progress = ModelProgress(token);
        var file = await Task.Run(() => _downloads.VerifyAsync(SelectedEmbeddedModel, path, progress, token), token);
        ApplyModelFile(file); ModelDownloadStatus = "模型已校验，保存设置后用于新的处理。";
    });
    private async Task RunModelOperationAsync(Func<CancellationToken, Task> action)
    {
        if (!CanConfigure) return;
        IsModelDownloading = true; ModelDownloadIndeterminate = true; ModelDownloadStatus = "准备模型…";
        try
        {
            await ExecuteAsync(async token =>
            {
                try { await action(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { ModelDownloadStatus = "已取消，可继续下载或重新选择。"; throw; }
                catch (Exception exception)
                {
                    ModelDownloadStatus = "操作未完成，请检查错误提示后重试。";
                    if (exception is HttpRequestException)
                        throw new InvalidDataException("无法访问官方模型仓库，请检查网络后重试。", exception);
                    if (exception is IOException)
                        throw new InvalidDataException("无法保存模型，请检查配置文件夹的读写权限、可用空间和文件占用。", exception);
                    throw;
                }
            });
        }
        finally { IsModelDownloading = false; RefreshModelProperties(); }
    }
}
