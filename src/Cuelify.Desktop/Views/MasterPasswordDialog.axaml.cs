using Avalonia.Controls;
using Cuelify.Desktop.ViewModels;

namespace Cuelify.Desktop.Views;

public partial class MasterPasswordDialog : Window
{
    public MasterPasswordDialog() { InitializeComponent(); Opened += (_, _) => this.FindControl<TextBox>(DataContext is MasterPasswordViewModel { IsChange: true } ? "CurrentPasswordBox" : "PasswordBox")!.Focus(); }
    protected override void OnClosing(WindowClosingEventArgs args)
    {
        if (DataContext is MasterPasswordViewModel { IsBusy: true }) args.Cancel = true;
        base.OnClosing(args);
    }
}
