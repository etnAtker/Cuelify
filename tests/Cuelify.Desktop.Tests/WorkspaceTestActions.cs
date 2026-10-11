using Cuelify.Core.Projects;
using Cuelify.Desktop.ViewModels;
using Xunit;

namespace Cuelify.Desktop.Tests;

internal static class WorkspaceTestActions
{
    public static async Task ExecuteAsync(MainWindowViewModel model, string operation)
    {
        var kind = operation == "TranslationSelected" ? TaskStage.Translation : Enum.Parse<TaskStage>(operation.Split(':')[0]);
        model.Workspace.SelectedStep = model.Workspace.Steps.Single(s => s.Kind == kind);
        await model.WaitForWorkspaceAsync();
        var choice = model.Workspace.StepOperations.Single(c => c.Operation == operation);
        Assert.True(choice.Available, choice.Reason);
        await model.Workspace.StepOperationCommand.ExecuteAsync(choice);
        await model.WaitForWorkspaceAsync();
    }
}
