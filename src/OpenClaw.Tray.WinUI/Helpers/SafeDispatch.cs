using Microsoft.UI.Dispatching;

namespace OpenClawTray.Helpers;

/// <summary>
/// Extension helper for safe UI dispatch via <see cref="DispatcherQueue"/>.
/// </summary>
public static class SafeDispatch
{
    /// <summary>
    /// Enqueues an action on the dispatcher queue, ignoring a null queue.
    /// <c>DispatcherQueue.TryEnqueue</c> returns <c>false</c> on failure rather than
    /// throwing, so no try/catch is needed at the call site.
    /// </summary>
    public static void SafeEnqueue(this DispatcherQueue? queue, DispatcherQueueHandler action)
    {
        queue?.TryEnqueue(action);
    }
}
