using System.Net;
using OpenClaw.Shared.Net;
using Xunit;

namespace OpenClaw.Shared.Tests.Net;

public class LoopbackClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:8765", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST:8765", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("[::1]:8765", true)]
    [InlineData("127.0.0.2", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("[::2]:8765", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsLoopbackHostString_RecognizesLiteralLoopbackHosts(string host, bool expected)
    {
        Assert.Equal(expected, LoopbackClassifier.IsLoopbackHostString(host));
    }

    [Fact]
    public void IsLoopbackEndpoint_UsesEndpointAddress()
    {
        Assert.True(LoopbackClassifier.IsLoopbackEndpoint(new IPEndPoint(IPAddress.Loopback, 8765)));
        Assert.False(LoopbackClassifier.IsLoopbackEndpoint(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 8765)));
        Assert.False(LoopbackClassifier.IsLoopbackEndpoint(null));
    }

    [Theory]
    [InlineData("ws://localhost:18789", true)]
    [InlineData("ws://127.0.0.1:18789", true)]
    [InlineData("ws://[::1]:18789", true)]
    [InlineData("ws://127.0.0.2:18789", false)]
    [InlineData("ws://10.0.0.5:18789", false)]
    [InlineData("not a url", false)]
    public void IsLocalGatewayUrl_UsesSharedHostParsing(string url, bool expected)
    {
        Assert.Equal(expected, LoopbackClassifier.IsLocalGatewayUrl(url));
    }

    [Theory]
    [InlineData("ws://10.0.0.5:18789", true)]
    [InlineData("ws://172.16.0.1:18789", true)]
    [InlineData("ws://172.31.255.255:18789", true)]
    [InlineData("ws://192.168.1.10:18789", true)]
    [InlineData("ws://[fc00::1]:18789", true)]
    [InlineData("ws://[fd00::1]:18789", true)]
    [InlineData("ws://172.15.255.255:18789", false)]
    [InlineData("ws://172.32.0.1:18789", false)]
    [InlineData("ws://127.0.0.1:18789", false)]
    [InlineData("ws://localhost:18789", false)]
    [InlineData("ws://example.com:18789", false)]
    [InlineData("not a url", false)]
    public void IsPrivateNetworkUrl_ClassifiesPrivateAddressRanges(string url, bool expected)
    {
        Assert.Equal(expected, LoopbackClassifier.IsPrivateNetworkUrl(url));
    }
}
