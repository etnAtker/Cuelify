using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Cuelify.Desktop.ViewModels;

namespace Cuelify.Desktop.Views;

public partial class WorkspaceDialogView : UserControl
{
    private IInputElement? _previousFocus;
    public WorkspaceDialogView() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (IsVisible)
        {
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                var model = DataContext as ProjectWorkspaceViewModel;
                var control = model?.NameDialog == true ? this.FindControl<TextBox>("ProjectNameInput") as Control :
                    model?.SettingsDialog == true ? this.FindControl<ComboBox>("MediaPipelineSelector") :
                    this.FindControl<Border>("DialogPanel");
                control?.Focus();
            });
        }
        else Dispatcher.UIThread.Post(() => { _previousFocus?.Focus(); _previousFocus = null; });
    }
}
