using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using Xunit;

namespace OpenClaw.Tray.Tests.Helpers;

public class SafeEventInvokeTests
{
    // ── Action<T> overload ──────────────────────────────────────────────────

    [Fact]
    public void SafeInvokeT_NullHandler_DoesNotThrow()
    {
        Action<string>? handler = null;
        // Should not throw
        handler.SafeInvoke("value");
    }

    [Fact]
    public void SafeInvokeT_SingleHandler_IsInvoked()
    {
        var received = new List<string>();
        Action<string>? handler = s => received.Add(s);

        handler.SafeInvoke("hello");

        Assert.Equal(["hello"], received);
    }

    [Fact]
    public void SafeInvokeT_MultipleHandlers_AllAreInvoked()
    {
        var received = new List<int>();
        Action<int>? handler = null;
        handler += x => received.Add(x * 1);
        handler += x => received.Add(x * 2);
        handler += x => received.Add(x * 3);

        handler.SafeInvoke(10);

        Assert.Equal([10, 20, 30], received);
    }

    [Fact]
    public void SafeInvokeT_ThrowingHandler_DoesNotPropagateException()
    {
        var secondCalled = false;
        Action<string>? handler = null;
        handler += _ => throw new InvalidOperationException("boom");
        handler += _ => secondCalled = true;

        // Should not throw, and subsequent handlers should still fire
        handler.SafeInvoke("x");

        Assert.True(secondCalled, "Handler after the throwing one should still be invoked");
    }

    [Fact]
    public void SafeInvokeT_ThrowingHandler_LogsWarning()
    {
        var warnings = new List<string>();
        var logger = new FakeLogger(warn: msg => warnings.Add(msg));

        Action<string>? handler = _ => throw new ArgumentException("bad arg");
        handler.SafeInvoke("x", logger);

        Assert.Single(warnings);
        Assert.Contains("ArgumentException", warnings[0]);
        Assert.Contains("bad arg", warnings[0]);
    }

    // ── Action overload ─────────────────────────────────────────────────────

    [Fact]
    public void SafeInvoke_NullHandler_DoesNotThrow()
    {
        Action? handler = null;
        handler.SafeInvoke();
    }

    [Fact]
    public void SafeInvoke_SingleHandler_IsInvoked()
    {
        var called = false;
        Action? handler = () => called = true;

        handler.SafeInvoke();

        Assert.True(called);
    }

    [Fact]
    public void SafeInvoke_ThrowingHandler_DoesNotPropagateException()
    {
        var secondCalled = false;
        Action? handler = null;
        handler += () => throw new Exception("oops");
        handler += () => secondCalled = true;

        handler.SafeInvoke();

        Assert.True(secondCalled);
    }

    [Fact]
    public void SafeInvoke_ThrowingHandler_LogsWarning()
    {
        var warnings = new List<string>();
        var logger = new FakeLogger(warn: msg => warnings.Add(msg));

        Action? handler = () => throw new NotSupportedException("nope");
        handler.SafeInvoke(logger);

        Assert.Single(warnings);
        Assert.Contains("NotSupportedException", warnings[0]);
    }

    // ── helper ──────────────────────────────────────────────────────────────

    private sealed class FakeLogger(Action<string> warn) : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Error(string message, Exception? ex = null) { }
        public void Warn(string message) => warn(message);
        public void Debug(string message) { }
    }
}
