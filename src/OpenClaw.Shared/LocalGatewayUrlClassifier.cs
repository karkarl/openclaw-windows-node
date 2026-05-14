using OpenClaw.Shared.Net;

namespace OpenClaw.Shared;

/// <summary>
/// Shared literal-host classifier for gateway URLs that point at the local machine.
/// </summary>
public static class LocalGatewayUrlClassifier
{
    public static bool IsLocalGatewayUrl(string url)
    {
        return LoopbackClassifier.IsLocalGatewayUrl(url);
    }
}
