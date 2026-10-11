using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Cuelify.Desktop.ViewModels;

namespace Cuelify.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => RestoreWidth();
        DataContextChanged += (_, _) => RestoreWidth();
        this.FindControl<GridSplitter>("MediaSplitter")!.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            if (DataContext is not MainWindowViewModel model) return;
            var grid = this.FindControl<Grid>("MediaColumns")!;
            model.Workspace.MediaPaneWidth = Math.Clamp(grid.ColumnDefinitions[0].ActualWidth, 160, 360);
            grid.ColumnDefinitions[0].Width = new GridLength(model.Workspace.MediaPaneWidth);
            model.ObserveWorkspaceTask(model.SaveWorkspaceAsync());
        });
    }
    private void RestoreWidth()
    {
        if (DataContext is MainWindowViewModel model)
            this.FindControl<Grid>("MediaColumns")!.ColumnDefinitions[0].Width = new GridLength(model.Workspace.MediaPaneWidth);
    }
}
