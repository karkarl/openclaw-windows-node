using OpenClaw.Shared;
using OpenClawTray.Services.Connection;

namespace OpenClaw.Tray.Tests.Connection;

public class GatewayConnectionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly MockCredentialResolver _resolver;
    private readonly MockClientFactory _factory;
    private readonly GatewayConnectionManager _manager;

    public GatewayConnectionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-mgr-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _resolver = new MockCredentialResolver();
        _factory = new MockClientFactory();
        _manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
        Assert.Null(_manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task ConnectAsync_WithNoGateway_DoesNothing()
    {
        await _manager.ConnectAsync();
        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task ConnectAsync_WithNoCredential_TransitionsToError()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = null;

        GatewayConnectionSnapshot? lastSnap = null;
        _manager.StateChanged += (_, s) => lastSnap = s;

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Error, _manager.CurrentSnapshot.OverallState);
        Assert.NotNull(lastSnap);
    }

    [Fact]
    public async Task ConnectAsync_WithCredential_TransitionsToConnecting()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
        Assert.Equal("wss://test", _manager.ActiveGatewayUrl);
        Assert.Equal("gw-1", _manager.CurrentSnapshot.GatewayId);
    }

    [Fact]
    public async Task ConnectAsync_CreatesClient()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        Assert.Single(_factory.CreatedClients);
        Assert.NotNull(_manager.OperatorClient);
    }

    [Fact]
    public async Task DisconnectAsync_TransitionsToIdle()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        await _manager.DisconnectAsync();

        Assert.Equal(OverallConnectionState.Idle, _manager.CurrentSnapshot.OverallState);
        Assert.Null(_manager.OperatorClient);
    }

    [Fact]
    public async Task SwitchGatewayAsync_DisconnectsAndReconnects()
    {
        SetupGateway("gw-1", "wss://test1");
        SetupGateway("gw-2", "wss://test2");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        await _manager.SwitchGatewayAsync("gw-2");

        Assert.Equal("gw-2", _manager.CurrentSnapshot.GatewayId);
        Assert.Equal("wss://test2", _manager.ActiveGatewayUrl);
    }

    [Fact]
    public async Task StateChanged_Fires_OnConnect()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        var snapshots = new List<GatewayConnectionSnapshot>();
        _manager.StateChanged += (_, s) => snapshots.Add(s);

        await _manager.ConnectAsync("gw-1");

        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, s => s.OverallState == OverallConnectionState.Connecting);
    }

    [Fact]
    public async Task DiagnosticEvent_Fires_OnCredentialResolution()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test.source");

        var events = new List<ConnectionDiagnosticEvent>();
        _manager.DiagnosticEvent += (_, e) => events.Add(e);

        await _manager.ConnectAsync("gw-1");

        Assert.Contains(events, e => e.Category == "credential");
    }

    [Fact]
    public async Task Dispose_CleansUp()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");
        await _manager.ConnectAsync("gw-1");

        _manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _manager.ConnectAsync("gw-1").GetAwaiter().GetResult());
    }

    [Fact]
    public void Diagnostics_IsAccessible()
    {
        Assert.NotNull(_manager.Diagnostics);
        Assert.Equal(0, _manager.Diagnostics.Count);
    }

    [Fact]
    public async Task HandshakeSucceeded_SuppressesManagerNodeConnector_WhenLocalNodeServiceOwnsIdentity()
    {
        SetupGateway("gw-local", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-local");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(0, nodeConnector.ConnectCount);
    }

    [Fact]
    public async Task HandshakeSucceeded_StartsManagerNodeConnector_WhenNoLocalNodeServiceOwnsIdentity()
    {
        SetupGateway("gw-remote", "wss://remote.example", isLocal: false);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");
        _resolver.NodeCredential = new GatewayCredential("node-tok", false, "test");
        var nodeConnector = new CountingNodeConnector();
        using var manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: nodeConnector,
            shouldStartNodeConnection: (record, _) => !record.IsLocal);

        await manager.ConnectAsync("gw-remote");
        await InvokeHandshakeSucceededAsync(manager);

        Assert.Equal(1, nodeConnector.ConnectCount);
        Assert.Equal("wss://remote.example", nodeConnector.LastGatewayUrl);
    }

    [Fact]
    public async Task ChatPageNavigationReadiness_DoesNotCompleteUntilHandshakeSucceeded()
    {
        SetupGateway("gw-chat", "ws://localhost:18789", isLocal: true);
        _resolver.OperatorCredential = new GatewayCredential("op-tok", false, "test");

        await _manager.ConnectAsync("gw-chat");

        var readiness = ChatNavigationReadiness.WaitForOperatorHandshakeAsync(
            _manager,
            TimeSpan.FromSeconds(5));

        Assert.False(readiness.IsCompleted);

        await InvokeHandshakeSucceededAsync(_manager);

        Assert.True(await readiness);
    }

    // ─── Helpers ───

    private void SetupGateway(string id, string url, bool isLocal = false)
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = id, Url = url, IsLocal = isLocal });
        _registry.SetActive(id);
    }

    private static async Task InvokeHandshakeSucceededAsync(GatewayConnectionManager manager)
    {
        var method = typeof(GatewayConnectionManager).GetMethod(
            "HandleHandshakeSucceededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(manager, [1L])!;
        await task;
    }

    // ─── Mocks ───

    private sealed class MockCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class MockClientFactory : IGatewayClientFactory
    {
        public List<MockLifecycle> CreatedClients { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var mock = new MockLifecycle(gatewayUrl);
            CreatedClients.Add(mock);
            return mock;
        }
    }

    internal sealed class MockLifecycle : IGatewayClientLifecycle
    {
        private readonly MockGatewayClient _client;

        public MockLifecycle(string url)
        {
            _client = new MockGatewayClient(url);
        }

        public OpenClawGatewayClient DataClient => _client;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public void SimulateStatusChanged(ConnectionStatus status) =>
            StatusChanged?.Invoke(this, status);

        public void SimulateAuthFailed(string msg) =>
            AuthenticationFailed?.Invoke(this, msg);

        public void SimulateV2SignatureFallback() =>
            _client.SimulateV2SignatureFallback();

        public void SimulateHandshake() =>
            _client.SimulateHandshakeSucceeded();

        public void Dispose() { }
    }

    private sealed class MockGatewayClient : OpenClawGatewayClient
    {
        public MockGatewayClient(string url)
            : base(url, "mock-token", NullLogger.Instance) { }

        /// <summary>Simulate a successful hello-ok handshake for testing.</summary>
        public void SimulateHandshakeSucceeded()
        {
            // Fire the HandshakeSucceeded event to trigger the manager's handler
            OnHandshakeSucceeded();
        }

        /// <summary>Simulate the gateway rejecting the v3 device signature, triggering v2 fallback.</summary>
        public void SimulateV2SignatureFallback()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(V2SignatureFallback),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }

        // Protected invoker — OpenClawGatewayClient.HandshakeSucceeded is a public event.
        // We use reflection because the event doesn't have a virtual invoker.
        private void OnHandshakeSucceeded()
        {
            var field = typeof(OpenClawGatewayClient).GetField(
                nameof(HandshakeSucceeded),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            // Events compiled as backing fields in C# are named the same as the event.
            // In case the compiler generates a different name, fall back to raising through the base.
            if (field != null)
            {
                var handler = field.GetValue(this) as EventHandler;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [Fact]
    public async Task HandshakeSucceeded_StampsLastConnectedOnGatewayRecord()
    {
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        // Simulate successful handshake
        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();

        // Allow async handler to complete
        await Task.Delay(100);

        var record = _registry.GetById("gw-1");
        Assert.NotNull(record?.LastConnected);
    }

    [Fact]
    public async Task HandshakeSucceeded_PreservesOtherRecordFields()
    {
        _registry.AddOrUpdate(new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared-tok",
            FriendlyName = "TestGW"
        });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");

        var lifecycle = _factory.CreatedClients[0];
        lifecycle.SimulateHandshake();
        await Task.Delay(100);

        var record = _registry.GetById("gw-1")!;
        Assert.True(record.LastConnected.HasValue);
        Assert.Equal("shared-tok", record.SharedGatewayToken);
        Assert.Equal("TestGW", record.FriendlyName);
    }

    [Fact]
    public async Task V2SignatureFallback_NewConnectionUsesV2()
    {
        // After V2SignatureFallback fires, the next reconnect should have UseV2Signature = true.
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        var first = _factory.CreatedClients[0];

        // Initially v3 (default)
        Assert.False(first.DataClient.UseV2Signature);

        // Simulate gateway rejecting v3
        first.SimulateV2SignatureFallback();

        // Reconnect — second client should be told to use v2
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);

        Assert.Equal(2, _factory.CreatedClients.Count);
        Assert.True(_factory.CreatedClients[1].DataClient.UseV2Signature);
    }

    [Fact]
    public async Task V2SignatureFallback_AfterThresholdSuccesses_ProbesV3()
    {
        // After the default threshold (3) successful v2 handshakes, the next reconnect
        // should probe v3 (UseV2Signature = false).
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        var first = _factory.CreatedClients[0];
        first.SimulateV2SignatureFallback();

        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);
        var second = _factory.CreatedClients[1];
        Assert.True(second.DataClient.UseV2Signature); // using v2

        // Simulate 3 successful v2 handshakes — should trigger probe reset
        second.SimulateHandshake();
        second.SimulateHandshake();
        second.SimulateHandshake();
        await Task.Delay(500); // generous delay for fire-and-forget async handlers

        // Verify the private state has been reset before reconnecting
        var needsV2Field = typeof(GatewayConnectionManager).GetField("_gatewayNeedsV2Signature",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var countField = typeof(GatewayConnectionManager).GetField("_v2SuccessCount",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.False((bool)(needsV2Field!.GetValue(_manager)!),
            $"_gatewayNeedsV2Signature should be false after 3 successes; _v2SuccessCount={countField!.GetValue(_manager)}");

        // Next reconnect should probe v3
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);

        Assert.Equal(3, _factory.CreatedClients.Count);
        Assert.False(_factory.CreatedClients[2].DataClient.UseV2Signature); // v3 probe
    }

    [Fact]
    public async Task V2SignatureFallback_DoublesThresholdOnProbeFailure()
    {
        // After a v3 probe fails (V2SignatureFallback fires again), the threshold doubles.
        // This test verifies only 3 successes (not 6) don't trigger a probe yet.
        SetupGateway("gw-1", "wss://test");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        _factory.CreatedClients[0].SimulateV2SignatureFallback();

        // Succeed threshold (3) times to trigger v3 probe
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);
        var v2Client = _factory.CreatedClients[1];
        v2Client.SimulateHandshake();
        v2Client.SimulateHandshake();
        v2Client.SimulateHandshake();
        await Task.Delay(100);

        // v3 probe — fires V2SignatureFallback again (gateway still rejects v3)
        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);
        _factory.CreatedClients[2].SimulateV2SignatureFallback(); // probe failed, threshold now 6

        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);
        var afterDoubled = _factory.CreatedClients[3];
        Assert.True(afterDoubled.DataClient.UseV2Signature); // back to v2

        // 3 successes (< 6 threshold) should NOT trigger a probe yet
        afterDoubled.SimulateHandshake();
        afterDoubled.SimulateHandshake();
        afterDoubled.SimulateHandshake();
        await Task.Delay(100);

        await _manager.DisconnectAsync();
        await _manager.ConnectAsync("gw-1");
        await Task.Delay(50);

        Assert.Equal(5, _factory.CreatedClients.Count);
        Assert.True(_factory.CreatedClients[4].DataClient.UseV2Signature); // still v2; need 6 successes
    }

    [Fact]
    public async Task SwitchGateway_ResetsV2ProbeState()
    {
        // Switching to a new gateway resets the v2 signature state entirely.
        SetupGateway("gw-1", "wss://test1");
        SetupGateway("gw-2", "wss://test2");
        _resolver.OperatorCredential = new GatewayCredential("tok", false, "test");

        await _manager.ConnectAsync("gw-1");
        _factory.CreatedClients[0].SimulateV2SignatureFallback();

        await _manager.SwitchGatewayAsync("gw-2");
        await Task.Delay(50);

        // New gateway should start fresh with v3
        var newClient = _factory.CreatedClients.Last();
        Assert.False(newClient.DataClient.UseV2Signature);
    }

    private sealed class CountingNodeConnector : INodeConnector
    {
        public int ConnectCount { get; private set; }
        public string? LastGatewayUrl { get; private set; }
        public bool IsConnected => ConnectCount > 0;
        public PairingStatus PairingStatus { get; private set; } = PairingStatus.Unknown;
        public string? NodeDeviceId => "test-node";
        public NodeConnectionMode Mode => IsConnected ? NodeConnectionMode.Gateway : NodeConnectionMode.Disabled;

#pragma warning disable CS0067 // Events required by interface but not fired in tests
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
        {
            ConnectCount++;
            LastGatewayUrl = gatewayUrl;
            PairingStatus = PairingStatus.Paired;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void Dispose() { }
    }
}
