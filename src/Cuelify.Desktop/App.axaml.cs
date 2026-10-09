using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cuelify.Desktop.Services;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;

namespace Cuelify.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(new DesktopJobService(), new ConfigurationStore(), new WindowDialogs(window));
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
