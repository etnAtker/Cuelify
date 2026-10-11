using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Cuelify.Desktop.ViewModels;

namespace Cuelify.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private async void OnCredentialLostFocus(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainWindowViewModel model) await model.SaveCredentialDraftAsync();
    }
    private void OnPromptSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        // 模态确认期间立即恢复高亮；模型仍保留原选择和草稿。
        if (sender is ListBox list && DataContext is MainWindowViewModel model && !model.CanManagePrompts &&
            !Equals(list.SelectedItem, model.VisibleSelectedPipeline))
            list.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, model.VisibleSelectedPipeline);
    }
    private void OnPromptVariableGotFocus(object? sender, GotFocusEventArgs args)
    {
        // 浮层会自动聚焦第一个变量，仅真正的键盘导航主动显示说明。
        if (sender is Button button && args.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            ToolTip.SetIsOpen(button, true);
    }
    private void OnPromptVariableLostFocus(object? sender, RoutedEventArgs args)
    { if (sender is Button button) ToolTip.SetIsOpen(button, false); }
    private void OnPromptVariableKeyDown(object? sender, KeyEventArgs args)
    { if (sender is Button button && args.Key == Key.Escape && ToolTip.GetIsOpen(button)) { ToolTip.SetIsOpen(button, false); args.Handled = true; } }
}
