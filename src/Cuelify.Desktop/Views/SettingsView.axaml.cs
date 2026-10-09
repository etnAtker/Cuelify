using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Cuelify.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void OnPromptVariableGotFocus(object? sender, GotFocusEventArgs args)
    { if (sender is Button button && args.NavigationMethod != NavigationMethod.Pointer) ToolTip.SetIsOpen(button, true); }
    private void OnPromptVariableLostFocus(object? sender, RoutedEventArgs args)
    { if (sender is Button button) ToolTip.SetIsOpen(button, false); }
    private void OnPromptVariableKeyDown(object? sender, KeyEventArgs args)
    { if (sender is Button button && args.Key == Key.Escape && ToolTip.GetIsOpen(button)) { ToolTip.SetIsOpen(button, false); args.Handled = true; } }
}
