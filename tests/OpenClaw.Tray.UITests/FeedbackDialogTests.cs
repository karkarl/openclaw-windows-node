using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection.NativeGateway;
using OpenClawTray.Dialogs;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class FeedbackDialogTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionRevealsReadOnlyDirectoryAndAccessibleFolderIcon(bool fails)
    {
        var completion = new TaskCompletionSource<NativeGatewayLogBundle>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FeedbackDialog dialog = null!;
        Task<ContentDialogResult>? closed = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                dialog = new FeedbackDialog(ui.Container.XamlRoot, () => IntPtr.Zero, _ =>
                {
                    invoked.SetResult();
                    return completion.Task;
                });
                closed = dialog.ShowAsync().AsTask();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("Cancel", dialog.PrimaryButtonText);
                Assert.Equal("Continue on GitHub", dialog.SecondaryButtonText);
                Assert.Same(Application.Current.Resources["AccentButtonStyle"], dialog.SecondaryButtonStyle);
                Assert.Equal(ContentDialogButton.None, dialog.DefaultButton);
                Assert.False(dialog.IsSecondaryButtonEnabled);
                Assert.DoesNotContain(TestSupport.FindDescendants<Button>(dialog),
                    button => AutomationProperties.GetAutomationId(button) == "FeedbackCopyFeedback");
                Assert.Equal(Visibility.Collapsed, LogLocation(dialog).Visibility);
                Find<TextBox>(dialog, "FeedbackText").Text = "A local feedback draft";
                Assert.True(dialog.IsSecondaryButtonEnabled);
                Invoke(Find<Button>(dialog, "FeedbackAddLogs"));
            });
            await invoked.Task;
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(dialog.IsSecondaryButtonEnabled);
                Assert.False(Find<Button>(dialog, "FeedbackAddLogs").IsEnabled);
                Assert.Equal(Visibility.Collapsed, LogLocation(dialog).Visibility);
            });

            if (fails)
                completion.SetException(new InvalidDataException("Fixture collection failed."));
            else
                completion.SetResult(new NativeGatewayLogBundle(
                    @"C:\feedback proof\collected logs\diagnostics.zip", false, false));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.True(dialog.IsSecondaryButtonEnabled);
                var directory = Find<TextBox>(dialog, "FeedbackLogDirectory");
                Assert.True(directory.IsReadOnly);
                var icon = Find<Button>(dialog, "FeedbackOpenLogsFolder");
                Assert.IsType<SymbolIcon>(icon.Content);
                Assert.Equal("Open folder in File Explorer", AutomationProperties.GetName(icon));
                Assert.Equal("Open folder in File Explorer", ToolTipService.GetToolTip(icon));
                Assert.Equal(fails ? Visibility.Collapsed : Visibility.Visible, LogLocation(dialog).Visibility);
                Assert.Equal(fails ? "" : @"C:\feedback proof\collected logs", directory.Text);
                Assert.Equal(fails, Find<Button>(dialog, "FeedbackAddLogs").IsEnabled);
                Assert.Equal(1, Grid.GetColumn(icon));
                var actions = Assert.IsType<StackPanel>(Find<Button>(dialog, "FeedbackCopyLogs").Parent);
                Assert.Equal(fails ? Visibility.Collapsed : Visibility.Visible, actions.Visibility);
                var status = Assert.Single(TestSupport.FindDescendants<InfoBar>(dialog));
                Assert.True(status.IsOpen);
                Assert.Equal(fails ? InfoBarSeverity.Error : InfoBarSeverity.Informational, status.Severity);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() => dialog?.Hide());
            if (closed is not null)
                await closed;
        }
    }

    private static Grid LogLocation(FeedbackDialog dialog) =>
        Assert.IsType<Grid>(Find<TextBox>(dialog, "FeedbackLogDirectory").Parent);

    private static T Find<T>(FeedbackDialog dialog, string id) where T : FrameworkElement =>
        Assert.Single(TestSupport.FindDescendants<T>(dialog),
            element => AutomationProperties.GetAutomationId(element) == id);

    private static void Invoke(Button button)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
