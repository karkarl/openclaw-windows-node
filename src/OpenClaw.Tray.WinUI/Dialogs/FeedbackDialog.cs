using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace OpenClawTray.Dialogs;

internal sealed class FeedbackDialog : ContentDialog
{
    private readonly Func<IntPtr> _windowHandle;
    private readonly Func<CancellationToken, Task<NativeGatewayLogBundle>> _collectLogs;
    private readonly TextBox _feedback = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 240
    };
    private readonly InfoBar _status = new() { IsClosable = false };
    private readonly Button _addLogs;
    private readonly Button _copyLogs;
    private readonly Button _saveLogs;
    private readonly Button _openLogsFolder;
    private readonly TextBox _logDirectory = new() { IsReadOnly = true };
    private readonly Grid _logLocation;
    private readonly StackPanel _logActions;
    private readonly CancellationTokenSource _lifetime = new();
    private NativeGatewayLogBundle? _bundle;
    private bool _busy;
    private bool _closed;

    private static string L(string key) => LocalizationHelper.GetString($"Feedback_{key}");

    public FeedbackDialog(XamlRoot root, Func<IntPtr> windowHandle,
        Func<CancellationToken, Task<NativeGatewayLogBundle>>? collectLogs = null)
    {
        XamlRoot = root;
        _windowHandle = windowHandle;
        _collectLogs = collectLogs ?? CollectGatewayLogsAsync;
        if (root.Content is FrameworkElement content)
            RequestedTheme = content.ActualTheme;
        Title = L("Title");
        PrimaryButtonText = LocalizationHelper.GetString("ChatPage_Cancel");
        SecondaryButtonText = L("Share");
        SecondaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        DefaultButton = ContentDialogButton.None;
        AutomationProperties.SetAutomationId(this, "FeedbackDialog");
        _feedback.PlaceholderText = L("Placeholder");
        AutomationProperties.SetName(_feedback, L("Placeholder"));
        AutomationProperties.SetAutomationId(_feedback, "FeedbackText");
        _addLogs = ActionButton("AddLogs", CollectLogsAsync);
        _addLogs.HorizontalAlignment = HorizontalAlignment.Left;
        _copyLogs = ActionButton("CopyLogs", CopyLogsAsync);
        _saveLogs = ActionButton("SaveLogs", SaveLogsAsync);
        _openLogsFolder = ActionButton("OpenLogsFolder", OpenLogsFolderAsync);
        _openLogsFolder.Content = new SymbolIcon(Symbol.OpenFile);
        _openLogsFolder.VerticalAlignment = VerticalAlignment.Bottom;
        AutomationProperties.SetName(_openLogsFolder, L("OpenLogsFolder"));
        ToolTipService.SetToolTip(_openLogsFolder, L("OpenLogsFolder"));
        _logDirectory.Header = L("LogDirectory");
        AutomationProperties.SetName(_logDirectory, L("LogDirectory"));
        AutomationProperties.SetAutomationId(_logDirectory, "FeedbackLogDirectory");
        _logLocation = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { _logDirectory, _openLogsFolder }
        };
        Grid.SetColumn(_openLogsFolder, 1);
        _logActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _copyLogs, _saveLogs }
        };
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _feedback,
                    _addLogs,
                    _logLocation,
                    _logActions,
                    new TextBlock { Text = L("Privacy"), TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                    _status
                }
            }
        };
        _feedback.TextChanged += (_, _) => UpdateAvailability();
        SecondaryButtonClick += (_, args) =>
        {
            // Keep the draft and log actions available while the user signs in in their browser.
            args.Cancel = true;
            Run(ShareAsync);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (!_busy)
                _lifetime.Dispose();
        };
        Opened += (_, _) => _feedback.Focus(FocusState.Programmatic);
        UpdateAvailability();
    }

    private Button ActionButton(string key, Func<Task> action)
    {
        var button = new Button { Content = L(key) };
        AutomationProperties.SetAutomationId(button, $"Feedback{key}");
        button.Click += (_, _) => Run(action);
        return button;
    }

    private void Run(Func<Task> action) =>
        AsyncEventHandlerGuard.Run(() => RunAsync(action), new AppLogger(), "Feedback");

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed)
            return;
        _busy = true;
        _status.IsOpen = false;
        UpdateAvailability();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (NativeGatewayPackageNotInstalledException)
        {
            Logger.Warn("Feedback log collection requires an installed native Gateway package.");
            ShowStatus("NoGateway", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Feedback action failed ({ex.GetType().Name}).");
            ShowStatus("Failed", InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            if (_closed)
                _lifetime.Dispose();
            else
                UpdateAvailability();
        }
    }

    private void UpdateAvailability()
    {
        IsSecondaryButtonEnabled = !_busy && !string.IsNullOrWhiteSpace(_feedback.Text);
        _feedback.IsReadOnly = _busy;
        _addLogs.IsEnabled = !_busy && _bundle is null;
        _logLocation.Visibility = _logActions.Visibility =
            _bundle is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { _openLogsFolder, _copyLogs, _saveLogs })
            button.IsEnabled = !_busy;
    }

    private void ShowStatus(string key, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closed)
            return;
        _status.Severity = severity;
        _status.Message = L(key);
        _status.IsOpen = true;
    }

    private async Task CollectLogsAsync()
    {
        ShowStatus("Collecting");
        _bundle = await _collectLogs(_lifetime.Token);
        if (_closed)
            return;
        _logDirectory.Text = Path.GetDirectoryName(_bundle.Path)
            ?? throw new InvalidDataException("The log bundle has no parent directory.");
        ShowStatus(_bundle.HostOnly || _bundle.HasNotes ? "LogsPartial" : "LogsReady",
            _bundle.HostOnly || _bundle.HasNotes ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }

    private static Task<NativeGatewayLogBundle> CollectGatewayLogsAsync(CancellationToken cancellationToken)
    {
        var app = (App)Application.Current;
        var collector = new FeedbackLogCollector(new NativeGatewayPackageResolver(), new NativeGatewayPackageClient());
        string directory = Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "Feedback");
        string? family = app.Registry?.GetActive()?.NativePackageFamilyName;
        return collector.CollectAsync(directory, family, cancellationToken);
    }

    private async Task CopyLogsAsync()
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(_bundle!.Path);
        if (_closed)
            return;
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetStorageItems(new[] { file });
        Clipboard.SetContent(data);
        Clipboard.Flush();
        ShowStatus("LogsCopied");
    }

    private async Task SaveLogsAsync()
    {
        IntPtr handle = _windowHandle();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("The feedback window is no longer available.");
        string? selected = await Win32FilePickerHelper.PickSaveFileAsync(handle,
            title: L("SaveLogs"), suggestedFileName: Path.GetFileName(_bundle!.Path), defaultExtension: "zip");
        if (string.IsNullOrWhiteSpace(selected) || _closed)
            return;
        if (!string.Equals(Path.GetFullPath(selected), _bundle.Path, StringComparison.OrdinalIgnoreCase))
            await Task.Run(() => File.Copy(_bundle.Path, selected, overwrite: true), _lifetime.Token);
        ShowStatus("Saved");
    }

    private Task OpenLogsFolderAsync()
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.Arguments = $"/select,\"{_bundle!.Path}\"";
        using var process = Process.Start(start);
        return Task.CompletedTask;
    }

    private async Task ShareAsync()
    {
        FeedbackSubmission submission = FeedbackSubmission.Create(_feedback.Text);
        if (submission.RequiresPaste)
            ClipboardHelper.CopyText(submission.Body, flush: true);
        if (!await Launcher.LaunchUriAsync(submission.IssueUri))
            throw new InvalidOperationException("The browser could not be opened.");
        ShowStatus(submission.RequiresPaste ? "PasteInBrowser" : "OpenedBrowser");
    }
}
