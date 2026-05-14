using System;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Shared.Net;

public static class LoopbackClassifier
{
    public static bool IsLoopbackHostString(string? host)
    {
        if (!TryExtractHost(host, out var extractedHost))
            return false;

        return extractedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            extractedHost.Equals("127.0.0.1", StringComparison.Ordinal) ||
            extractedHost.Equals("::1", StringComparison.Ordinal);
    }

    public static bool IsLoopbackEndpoint(IPEndPoint? ep)
    {
        return ep is not null && IPAddress.IsLoopback(ep.Address);
    }

    public static bool IsLocalGatewayUrl(string url)
    {
        return TryGetUriHost(url, out var host) && IsLoopbackHostString(host);
    }

    public static bool IsPrivateNetworkUrl(string url)
    {
        if (!TryGetUriHost(url, out var host) || !IPAddress.TryParse(host, out var address))
            return false;

        return IsPrivateNetworkAddress(address);
    }

    private static bool TryGetUriHost(string url, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        host = uri.Host;
        return !string.IsNullOrWhiteSpace(host);
    }

    private static bool TryExtractHost(string? host, out string extractedHost)
    {
        extractedHost = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var trimmed = host.Trim();
        if (trimmed.StartsWith('['))
        {
            var closeBracket = trimmed.IndexOf(']');
            if (closeBracket < 0)
                return false;

            extractedHost = trimmed.Substring(1, closeBracket - 1).Trim();
            return extractedHost.Length > 0;
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            extractedHost = trimmed;
            return true;
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon)
            trimmed = trimmed.Substring(0, colon).Trim();

        extractedHost = trimmed;
        return extractedHost.Length > 0;
    }

    private static bool IsPrivateNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPrivateNetworkAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xFE) == 0xFC;
    }
}
