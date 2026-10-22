using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

public sealed class FeedbackButton : Button
{
    private bool _showing;
    private FeedbackDialog? _dialog;
    private Func<IntPtr>? _windowHandle;

    public FeedbackButton()
    {
        Style = (Style)Application.Current.Resources["SubtleButtonStyle"];
        Width = 40;
        Height = 32;
        Margin = new Thickness(0, 0, 8, 0);
        Content = new FontIcon { Glyph = "\uE939", FontSize = 16 };
        string label = LocalizationHelper.GetString("Feedback_Button");
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetAutomationId(this, "TitleBarFeedback");
        ToolTipService.SetToolTip(this, label);
        Click += (_, _) => AsyncEventHandlerGuard.Run(ShowFeedbackAsync, new AppLogger(), "Feedback");
        Unloaded += (_, _) => _dialog?.Hide();
    }

    public void Initialize(Func<IntPtr> windowHandle) => _windowHandle = windowHandle;

    private async Task ShowFeedbackAsync()
    {
        if (_showing)
            return;
        _showing = true;
        try
        {
            _dialog = new FeedbackDialog(XamlRoot,
                () => _windowHandle?.Invoke() ?? IntPtr.Zero);
            await _dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Feedback dialog failed ({ex.GetType().Name}).");
            string failed = LocalizationHelper.GetString("Feedback_Failed");
            ToolTipService.SetToolTip(this, failed);
            AutomationProperties.SetHelpText(this, failed);
        }
        finally
        {
            _dialog = null;
            _showing = false;
        }
    }
}
