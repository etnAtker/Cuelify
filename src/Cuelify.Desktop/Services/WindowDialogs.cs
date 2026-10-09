using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;

namespace Cuelify.Desktop.Services;

public interface IWindowDialogs
{
    Task<string?> OpenAsync(string title, string[] patterns);
    Task<string?> SaveSrtAsync(string suggestedName);
    Task<bool> ConfirmAsync(string message, CancellationToken token = default);
    Task<bool> PasswordAsync(PasswordPurpose purpose, Func<string, string, Task> submit, CancellationToken token = default);
    Task CopyTextAsync(string text) => throw new NotSupportedException("剪贴板不可用。");
}

public sealed class WindowDialogs(Window owner) : IWindowDialogs
{
    public async Task<bool> PasswordAsync(PasswordPurpose purpose, Func<string, string, Task> submit, CancellationToken token = default)
    {
        var model = new MasterPasswordViewModel(purpose, submit);
        var dialog = new MasterPasswordDialog { DataContext = model, RequestedThemeVariant = owner.ActualThemeVariant };
        model.Completed += () => dialog.Close(true);
        model.Cancelled += () => dialog.Close(false);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => { if (!model.IsBusy) dialog.Close(false); }));
        try { token.ThrowIfCancellationRequested(); return await dialog.ShowDialog<bool>(owner); }
        finally { model.Clear(); dialog.DataContext = null; }
    }
    public Task CopyTextAsync(string text) => owner.Clipboard?.SetTextAsync(text) ??
        throw new InvalidOperationException("剪贴板不可用。");

    public async Task<string?> OpenAsync(string title, string[] patterns)
    {
        if (!owner.StorageProvider.CanOpen) throw new InvalidOperationException("文件选择器不可用。");
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = false, FileTypeFilter = [new("可选文件") { Patterns = patterns }, FilePickerFileTypes.All] });
        if (files.Count == 0) return null;
        using var file = files[0];
        return file.TryGetLocalPath() ?? throw new InvalidOperationException("请选择本地文件。");
    }
    public async Task<string?> SaveSrtAsync(string suggestedName)
    {
        if (!owner.StorageProvider.CanSave) throw new InvalidOperationException("保存对话框不可用。");
        using var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "导出 SRT", SuggestedFileName = suggestedName, DefaultExtension = "srt", FileTypeChoices = [new("SRT 字幕") { Patterns = ["*.srt"] }], ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }
    public async Task<bool> ConfirmAsync(string message, CancellationToken token = default)
    {
        var confirm = new Button { Content = "确认", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        var dialog = new Window { Title = "确认", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 20, Children =
        { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Children = { cancel, confirm } } } };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        token.ThrowIfCancellationRequested();
        return await dialog.ShowDialog<bool>(owner);
    }
}
