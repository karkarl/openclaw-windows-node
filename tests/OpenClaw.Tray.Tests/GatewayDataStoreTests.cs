using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class GatewayDataStoreTests
{
    [Fact]
    public void SetSessions_UpdatesSnapshotAndRaisesPropertyChanged()
    {
        var store = new GatewayDataStore();
        var changed = new List<string?>();
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:one", Status = "active" }
        };

        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        store.SetSessions(sessions);

        Assert.Same(sessions, store.Sessions);
        Assert.Contains(nameof(GatewayDataStore.Sessions), changed);
    }

    [Fact]
    public void SetSessions_RemovesStaleSessionPreviews()
    {
        var store = new GatewayDataStore();
        store.UpsertSessionPreviews(new[]
        {
            new SessionPreviewInfo { Key = "keep" },
            new SessionPreviewInfo { Key = "stale" }
        });

        store.SetSessions(new[] { new SessionInfo { Key = "keep" } });

        var previews = store.SessionPreviews;
        Assert.True(previews.ContainsKey("keep"));
        Assert.False(previews.ContainsKey("stale"));
    }

    [Fact]
    public void AddAgentEvent_TrimsOldestEvents()
    {
        var store = new GatewayDataStore();

        store.AddAgentEvent(new AgentEventInfo { RunId = "old" }, maxEvents: 2);
        store.AddAgentEvent(new AgentEventInfo { RunId = "middle" }, maxEvents: 2);
        store.AddAgentEvent(new AgentEventInfo { RunId = "new" }, maxEvents: 2);

        Assert.Collection(
            store.AgentEvents,
            evt => Assert.Equal("new", evt.RunId),
            evt => Assert.Equal("middle", evt.RunId));
    }
}
