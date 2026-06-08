using System.Reflection;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayClientFactoryTests
{
    /// <summary>
    /// Regression test for issue #720: the operator (chat) client must connect as "operator"
    /// role even when the credential is a bootstrap token.  Setting bootstrapPairAsNode: false
    /// for the operator client ensures chat works immediately after QR-code or setup-code pairing.
    /// </summary>
    [Fact]
    public void Create_BootstrapCredential_PairsAsOperator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var lifecycle = new GatewayClientFactory().Create(
                "ws://127.0.0.1:18789",
                new GatewayCredential("bootstrap-token", IsBootstrapToken: true, Source: "test"),
                tempDir,
                NullLogger.Instance);

            // Operator client always connects as "operator", even with a bootstrap token.
            Assert.Equal("operator", GetConnectRole(lifecycle.DataClient));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Create_SharedCredential_PairsAsOperator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-gateway-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var lifecycle = new GatewayClientFactory().Create(
                "ws://127.0.0.1:18789",
                new GatewayCredential("shared-token", IsBootstrapToken: false, Source: "test"),
                tempDir,
                NullLogger.Instance);

            Assert.Equal("operator", GetConnectRole(lifecycle.DataClient));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string GetConnectRole(OpenClawGatewayClient client)
    {
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "GetConnectRole",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(client, null));
    }
}
