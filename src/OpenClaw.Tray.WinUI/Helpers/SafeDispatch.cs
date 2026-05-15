using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using System;
using System.Reflection;

namespace OpenClawTray.Helpers;

/// <summary>
/// Extension helpers for safe UI dispatch and event invocation.
/// Replaces the proliferating try { DispatcherQueue?.TryEnqueue(...) } catch { }
/// and try { event?.Invoke(...) } catch { } patterns.
/// </summary>
public static class SafeDispatch
{
    /// <summary>
    /// Enqueues an action on the dispatcher queue, ignoring a null queue.
    /// <c>DispatcherQueue.TryEnqueue</c> returns false on failure rather than throwing,
    /// so no try/catch is needed.
    /// </summary>
    public static void SafeEnqueue(this DispatcherQueue? queue, DispatcherQueueHandler action)
    {
        queue?.TryEnqueue(action);
    }

    /// <summary>
    /// Invokes an <see cref="Action{T}"/> event, logging a warning if a handler throws
    /// rather than silently swallowing the exception.
    /// </summary>
    public static void SafeInvoke<T>(this Action<T>? handler, T arg, IOpenClawLogger? log = null)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action<T>)d)(arg);
            }
            catch (Exception ex)
            {
                log?.Warn($"SafeInvoke: handler {d.Method.Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Invokes an <see cref="Action"/> event, logging a warning if a handler throws.
    /// </summary>
    public static void SafeInvoke(this Action? handler, IOpenClawLogger? log = null)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                log?.Warn($"SafeInvoke: handler {d.Method.Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
