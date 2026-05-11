using OpenClaw.Chat;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Chat;

/// <summary>
/// Helper for hosting the <see cref="OpenClawChatRoot"/> Reactor tree
/// inside an existing XAML window/page. The Reactor render loop renders
/// into a target <see cref="Border"/> (via <see cref="ReactorHost.ContentTarget"/>)
/// rather than replacing <see cref="Window.Content"/>, so the surrounding
/// XAML chrome (TitleBar, NavigationView, popup header, ...) is preserved.
/// </summary>
public static class ReactorChatHostExtensions
{
    /// <summary>
    /// Builds an "post to UI thread" callback suitable for
    /// <see cref="OpenClawChatDataProvider"/>'s <c>post</c> argument from
    /// the supplied window's dispatcher queue.
    /// </summary>
    public static Action<Action> AsPost(this DispatcherQueue dispatcher) =>
        action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
                action();
        };

    /// <summary>
    /// Mount <see cref="OpenClawChatRoot"/> into <paramref name="target"/>.
    /// Returns an <see cref="IDisposable"/> that releases the Reactor host
    /// when the page/window unloads.
    /// </summary>
    public static IDisposable MountReactorChat(
        this Window window,
        Border target,
        IChatDataProvider provider,
        string? initialThreadId = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(provider);

        var host = new ReactorHost(window) { ContentTarget = target };
        host.Mount(new OpenClawChatRoot(provider, initialThreadId));
        return host;
    }
}
