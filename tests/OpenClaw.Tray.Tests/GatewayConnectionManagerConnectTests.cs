using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayConnectionManagerConnectTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly FakeCredentialResolver _resolver = new();
    private readonly FakeClientFactory _factory = new();
    private readonly GatewayConnectionManager _manager;

    public GatewayConnectionManagerConnectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-connmgr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _manager = new GatewayConnectionManager(_resolver, _factory, _registry, NullLogger.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ConnectAsync_WhileAlreadyConnecting_DoesNotCancelCurrentConnection()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-1", Url = "ws://localhost:18789", IsLocal = true });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("operator-token", IsBootstrapToken: false, Source: "test");

        await _manager.ConnectAsync("gw-1");
        var currentLifecycle = _factory.CreatedClients.Single();
        var currentClient = _manager.OperatorClient;

        await _manager.ConnectAsync("gw-1");

        Assert.Single(_factory.CreatedClients);
        Assert.False(currentLifecycle.IsDisposed);
        Assert.Same(currentClient, _manager.OperatorClient);
        Assert.Equal(OverallConnectionState.Connecting, _manager.CurrentSnapshot.OverallState);
    }

    [Fact]
    public async Task DisconnectAsync_CancelsInFlightLifecycleConnect()
    {
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-1", Url = "ws://localhost:18789", IsLocal = true });
        _registry.SetActive("gw-1");
        _resolver.OperatorCredential = new GatewayCredential("operator-token", IsBootstrapToken: false, Source: "test");

        await _manager.ConnectAsync("gw-1");
        var lifecycle = _factory.CreatedClients.Single();
        var token = await lifecycle.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(token.IsCancellationRequested);

        await _manager.DisconnectAsync();

        await WaitForConditionAsync(
            () => token.IsCancellationRequested,
            TimeSpan.FromSeconds(1));
    }

    private sealed class FakeCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;

        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => null;
    }

    private sealed class FakeClientFactory : IGatewayClientFactory
    {
        public List<FakeLifecycle> CreatedClients { get; } = [];

        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
        {
            var lifecycle = new FakeLifecycle(gatewayUrl);
            CreatedClients.Add(lifecycle);
            return lifecycle;
        }
    }

    private sealed class FakeLifecycle(string gatewayUrl) : IGatewayClientLifecycle
    {
        public OpenClawGatewayClient DataClient { get; } = new(gatewayUrl, "mock-token", NullLogger.Instance);
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource<CancellationToken> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable CS0067 // Events required by interface but not fired in this regression test.
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectStarted.TrySetResult(ct);
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public void Dispose() => IsDisposed = true;
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not met before the timeout.");

            await Task.Delay(25);
        }
    }
}
