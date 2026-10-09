using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Cuelify.Desktop.Services;

public interface IWindowDialogs
{
    Task<string?> OpenAsync(string title, string[] patterns);
    Task<string?> SaveSrtAsync(string suggestedName);
    Task<bool> ConfirmAsync(string message);
}

public sealed class WindowDialogs(Window owner) : IWindowDialogs
{
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
    public async Task<bool> ConfirmAsync(string message)
    {
        var confirm = new Button { Content = "确认", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        var dialog = new Window { Title = "确认", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 20, Children =
        { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Children = { cancel, confirm } } } };
        return await dialog.ShowDialog<bool>(owner);
    }
}
