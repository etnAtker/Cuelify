using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cuelify.Core.Translation;
using Cuelify.Desktop.ViewModels;
using Cuelify.Desktop.Views;
using Xunit;

namespace Cuelify.Desktop.Tests;

public sealed class PromptVariableTests
{
    [AvaloniaTheory]
    [InlineData(1240, 860, "浅色")]
    [InlineData(900, 640, "深色")]
    public async Task VariablesWrapAndSupportHoverPointerCopyAndKeyboardHelp(int width, int height, string theme)
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var window = new MainWindow { DataContext = model, Width = width, Height = height }; window.Show();
        await model.InitializeCommand.ExecuteAsync(null); model.Settings.Theme = theme;
        model.ShowSettingsCommand.Execute(null); model.SettingsSectionIndex = 2;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        var settings = window.FindControl<SettingsView>("SettingsPage")!;
        var list = settings.FindControl<ItemsControl>("PromptVariableList")!;
        using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
        var buttons = list.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(PromptBuilder.Variables, buttons.Select(button => Assert.IsType<PromptVariableItem>(button.DataContext).Name));
        foreach (var button in buttons)
        {
            var variable = Assert.IsType<PromptVariableItem>(button.DataContext);
            var tip = Assert.IsType<StackPanel>(ToolTip.GetTip(button));
            Assert.Contains(tip.Children.OfType<TextBlock>(), text => text.Text == variable.Description && !string.IsNullOrWhiteSpace(text.Text));
            Assert.Equal(variable.Description, AutomationProperties.GetHelpText(button));
            Assert.Equal(variable.CopyLabel, AutomationProperties.GetName(button));
            Assert.Equal(variable.Placeholder, Assert.IsType<TextBlock>(button.Content).Text);
            Assert.True(button.Bounds.Width > 0);
            var position = button.TranslatePoint(default, list)!.Value;
            Assert.InRange(position.X + button.Bounds.Width, 0, list.Bounds.Width + 1);
        }
        var target = buttons[0]; target.BringIntoView(); Assert.True(target.Focus(NavigationMethod.Pointer));
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        using (var frame = window.CaptureRenderedFrame()) Assert.NotNull(frame);
        var point = target.TranslatePoint(new Point(8, 8), window)!.Value;
        ToolTip.SetShowDelay(target, 0); window.MouseMove(point);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.True(ToolTip.GetIsOpen(target));
        ToolTip.SetIsOpen(target, false);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Assert.NotNull(model.CopyPromptVariableCommand.ExecutionTask); await model.CopyPromptVariableCommand.ExecutionTask!;
        Assert.Equal("{source_language}", fixture.Dialogs.CopiedText); Assert.Equal("已复制 {source_language}", model.PromptVariableStatus);
        Assert.True(buttons[1].Focus(NavigationMethod.Tab)); Assert.True(ToolTip.GetIsOpen(buttons[1])); Assert.False(ToolTip.GetIsOpen(target));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await model.CopyPromptVariableCommand.ExecutionTask!;
        Assert.Equal("{target_language}", fixture.Dialogs.CopiedText);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Assert.False(ToolTip.GetIsOpen(buttons[1]));
        var output = Environment.GetEnvironmentVariable("CUELIFY_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, $"prompt-variables-{width}-{theme}.png"));
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedCopyCanBeRetriedWithoutChangingPromptOrClaimingSuccess()
    {
        using var fixture = new Fixture(); await using var model = fixture.Model();
        var system = model.SystemTemplate; var user = model.UserTemplate; var dirty = model.HasUnsavedSettings;
        var variable = model.PromptVariables.Single(item => item.Name == "cues_json");
        fixture.Dialogs.CopyFailure = new NotSupportedException("剪贴板不可用。");
        await model.CopyPromptVariableCommand.ExecuteAsync(variable);
        Assert.Null(fixture.Dialogs.CopiedText); Assert.Contains("复制失败", model.PromptVariableStatus);
        fixture.Dialogs.CopyFailure = null; await model.CopyPromptVariableCommand.ExecuteAsync(variable);
        Assert.Equal("{cues_json}", fixture.Dialogs.CopiedText); Assert.Equal("已复制 {cues_json}", model.PromptVariableStatus);
        Assert.Equal(system, model.SystemTemplate); Assert.Equal(user, model.UserTemplate); Assert.Equal(dirty, model.HasUnsavedSettings);
    }
}
