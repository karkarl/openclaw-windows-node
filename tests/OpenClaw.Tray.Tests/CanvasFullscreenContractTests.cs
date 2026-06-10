namespace OpenClaw.Tray.Tests;

public sealed class CanvasFullscreenContractTests
{
    [Fact]
    public void Controller_PreservesAndRestoresWindowState()
    {
        var source = ReadSource("CanvasFullscreenController.cs");

        Assert.Contains("_appWindow.Presenter.Kind", source);
        Assert.Contains("_appWindow.Position.X", source);
        Assert.Contains("_appWindow.Position.Y", source);
        Assert.Contains("_appWindow.Size.Width", source);
        Assert.Contains("_appWindow.Size.Height", source);
        Assert.Contains("OverlappedPresenterState.Maximized", source);
        Assert.Contains("SetPresenter(AppWindowPresenterKind.FullScreen)", source);
        Assert.Contains("SetPresenter(_previousPresenterKind)", source);
        Assert.Contains("if (_wasMaximized && _appWindow.Presenter is OverlappedPresenter presenter)", source);
        Assert.Contains("presenter.Maximize()", source);
        Assert.Contains("else\n            _appWindow.MoveAndResize(_previousBounds)", source);
        Assert.Contains("if (!_wasMaximized)", source);
    }

    [Fact]
    public void WebViewCanvas_HandlesFullscreenShortcutsThroughTrustedBridge()
    {
        var source = ReadSource("CanvasWindow.xaml.cs");

        Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync(FullscreenShortcutScript)", source);
        Assert.Contains("event.key === 'F11'", source);
        Assert.Contains("event.key === 'Escape'", source);
        Assert.Contains("if (event.repeat) return;", source);
        Assert.Contains("target.isContentEditable", source);
        Assert.Contains("if (!event.defaultPrevented && !isEditable)", source);
        Assert.Contains("if (!IsTrustedBridgeSource(e.Source))", source);
        Assert.Contains("msg.Type == FullscreenShortcutMessageType", source);
        Assert.Contains("keyElement.ValueKind != System.Text.Json.JsonValueKind.String", source);
        Assert.Contains("catch (System.Text.Json.JsonException)", source);
        Assert.Contains("_fullscreenController.Toggle()", source);
        Assert.Contains("_fullscreenController.Exit()", source);
        Assert.Contains("IsClosed = true;\n        _fullscreenController.Exit();", source);
    }

    [Fact]
    public void NativeCanvas_HandlesFullscreenKeysAndUnsubscribes()
    {
        var source = ReadSource("A2UICanvasWindow.xaml.cs");

        Assert.Contains("RootGrid.AddHandler(UIElement.KeyDownEvent, _canvasKeyDownHandler, true)", source);
        Assert.Contains("VirtualKey.F11", source);
        Assert.Contains("VirtualKey.Escape", source);
        Assert.Contains("if (args.KeyStatus.WasKeyDown)", source);
        Assert.Contains("else if (!args.Handled", source);
        Assert.Contains("_fullscreenController.Toggle()", source);
        Assert.Contains("_fullscreenController.Exit()", source);
        Assert.Contains("IsClosed = true;\n            _fullscreenController.Exit();", source);
        Assert.Contains("RootGrid.RemoveHandler(UIElement.KeyDownEvent, _canvasKeyDownHandler)", source);
    }

    private static string ReadSource(string fileName) => File.ReadAllText(Path.Combine(
        GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Windows",
        fileName)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
