using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace OpenClawTray.Windows;

internal sealed class CanvasFullscreenController
{
    private readonly AppWindow _appWindow;
    private AppWindowPresenterKind _previousPresenterKind;
    private RectInt32 _previousBounds;
    private bool _wasMaximized;

    public CanvasFullscreenController(AppWindow appWindow)
    {
        _appWindow = appWindow;
    }

    public bool IsFullscreen { get; private set; }

    public void Toggle()
    {
        if (IsFullscreen)
        {
            Exit();
        }
        else
        {
            Enter();
        }
    }

    public void Exit()
    {
        if (!IsFullscreen)
            return;

        _appWindow.SetPresenter(_previousPresenterKind);
        if (_wasMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        else
            _appWindow.MoveAndResize(_previousBounds);

        IsFullscreen = false;
    }

    private void Enter()
    {
        _previousPresenterKind = _appWindow.Presenter.Kind;
        _wasMaximized = _appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized;
        if (!_wasMaximized)
        {
            _previousBounds = new RectInt32(
                _appWindow.Position.X,
                _appWindow.Position.Y,
                _appWindow.Size.Width,
                _appWindow.Size.Height);
        }

        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        IsFullscreen = true;
    }
}
