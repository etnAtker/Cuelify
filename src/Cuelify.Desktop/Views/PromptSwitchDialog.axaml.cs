using Avalonia.Controls;
using Avalonia.Interactivity;
using Cuelify.Desktop.Services;

namespace Cuelify.Desktop.Views;

public partial class PromptSwitchDialog : Window
{
    public string Message { get; }
    public PromptSwitchDialog() : this("") { }
    public PromptSwitchDialog(string templateName)
    {
        Message = $"“{templateName}”有未保存的修改。保存或放弃后才能切换，取消将继续编辑。";
        InitializeComponent(); DataContext = this;
    }
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(PromptSwitchChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs args) => Close(PromptSwitchChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs args) => Close(PromptSwitchChoice.Save);
}
