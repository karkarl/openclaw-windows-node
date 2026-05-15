using OpenClaw.Shared;
using System;

namespace OpenClawTray.Helpers;

/// <summary>
/// Extension helpers for safely invoking event delegates without silently
/// swallowing exceptions. Replaces the <c>try { event?.Invoke(...) } catch { }</c>
/// pattern by logging a warning when a handler throws rather than discarding the exception.
/// </summary>
public static class SafeEventInvoke
{
    /// <summary>
    /// Invokes each subscriber of an <see cref="Action{T}"/> event individually,
    /// logging a warning if any subscriber throws rather than silently swallowing.
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
                log?.Warn($"SafeInvoke: handler '{d.Method.Name}' threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Invokes each subscriber of an <see cref="Action"/> event individually,
    /// logging a warning if any subscriber throws rather than silently swallowing.
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
                log?.Warn($"SafeInvoke: handler '{d.Method.Name}' threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
