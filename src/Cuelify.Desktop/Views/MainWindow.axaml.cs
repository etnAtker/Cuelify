using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using System.ComponentModel;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;

namespace Cuelify.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _closeRequested;
    private AppSettings? _themeSettings;
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += OnOpened;
        Closing += OnClosing;
    }
    private async void OnOpened(object? sender, EventArgs args)
    {
        if (DataContext is not MainWindowViewModel model) return;
        await model.InitializeCommand.ExecuteAsync(null);
        ApplyTheme(model.Settings.Theme);
        _themeSettings = model.Settings;
        _themeSettings.PropertyChanged += OnSettingsChanged;
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(AppSettings.Theme) && sender is AppSettings settings) ApplyTheme(settings.Theme); }
    private void ApplyTheme(string theme) => RequestedThemeVariant = theme switch { "浅色" => ThemeVariant.Light, "深色" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    private void OnDrop(object? sender, DragEventArgs args)
    {
        if (DataContext is not MainWindowViewModel { CanAcceptDrop: true } model) return;
#pragma warning disable CS0618
        var files = args.Data.GetFiles()?.ToArray();
#pragma warning restore CS0618
        if (files?.Length == 1 && files[0] is Avalonia.Platform.Storage.IStorageFile file)
        {
            var path = Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
            if (path is not null) model.InputPath = path;
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closing || DataContext is not MainWindowViewModel model) return;
        args.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        IsEnabled = false;
        await model.DisposeAsync();
        if (_themeSettings is not null) _themeSettings.PropertyChanged -= OnSettingsChanged;
        _closing = true;
        // Closing 回调先结束，再关闭；资源同步释放时不能重入当前关闭事件。
        Dispatcher.UIThread.Post(Close);
    }
}
