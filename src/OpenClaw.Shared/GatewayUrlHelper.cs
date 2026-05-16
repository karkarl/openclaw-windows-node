using System;
using System.Buffers;
using System.Net;

namespace OpenClaw.Shared;

public static class GatewayUrlHelper
{
    public const string ValidationMessage = "Gateway URL must be a valid URL (ws://, wss://, http://, or https://).";
    public const string InsecureRemoteWarning = "⚠️ Non-TLS gateway URL: traffic may be intercepted on shared networks. Use wss:// for remote gateways.";

    private static readonly SearchValues<char> s_authorityTerminators =
        SearchValues.Create("/?#");

    public static bool IsValidGatewayUrl(string? gatewayUrl) =>
        TryNormalizeWebSocketUrl(gatewayUrl, out _);

    /// <summary>
    /// Returns true when the URL is a plain ws:// (non-TLS) connection to a non-loopback,
    /// non-private-network host — a configuration that is safe only for local development.
    /// </summary>
    public static bool IsInsecureRemoteGatewayUrl(string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return false;

        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsLocalHost(uri.Host);
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        // Explicit loopback names
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var addr))
            return false;

        // IPv6 loopback (::1)
        if (IPAddress.IsLoopback(addr))
            return true;

        var bytes = addr.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // 127.x.x.x
            if (bytes[0] == 127)
                return true;
            // 10.x.x.x
            if (bytes[0] == 10)
                return true;
            // 172.16-31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            // 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
        }
        else if (bytes.Length == 16)
        {
            // fc00::/7 (ULA — unique local addresses)
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;
        }

        return false;
    }

    public static string NormalizeForWebSocket(string? gatewayUrl) =>
        TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedUrl)
            ? normalizedUrl
            : gatewayUrl?.Trim() ?? string.Empty;

    /// <summary>
    /// Extract credentials from gateway URL user-info (username:password).
    /// The returned value may include URL-encoded characters and should be decoded before
    /// constructing an Authorization header.
    /// </summary>
    public static string? ExtractCredentials(string gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo;
    }

    /// <summary>
    /// Decode URL-encoded credentials from URL user-info format (username:password).
    /// Username-only input is normalized to username: for HTTP Basic Auth.
    /// Returns the original value if decoding fails.
    /// </summary>
    public static string DecodeCredentials(string credentials)
    {
        if (string.IsNullOrEmpty(credentials))
        {
            return credentials;
        }

        var separatorIndex = credentials.IndexOf(':');
        if (separatorIndex < 0)
        {
            try
            {
                return $"{Uri.UnescapeDataString(credentials)}:";
            }
            catch (UriFormatException)
            {
                return $"{credentials}:";
            }
        }

        var username = credentials[..separatorIndex];
        var password = credentials[(separatorIndex + 1)..];

        try
        {
            return $"{Uri.UnescapeDataString(username)}:{Uri.UnescapeDataString(password)}";
        }
        catch (UriFormatException)
        {
            return credentials;
        }
    }

    /// <summary>
    /// Remove user-info credentials from a URL for safe logging and display.
    /// </summary>
    public static string SanitizeForDisplay(string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return gatewayUrl?.Trim() ?? string.Empty;
        }

        return RemoveUserInfo(gatewayUrl.Trim());
    }

    public static bool TryNormalizeWebSocketUrl(string? gatewayUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return false;
        }

        var trimmed = gatewayUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        string candidate;
        if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed;
        }
        else
        {
            var schemeSeparator = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator < 0)
            {
                return false;
            }

            var remainder = trimmed[schemeSeparator..];
            if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "ws" + remainder;
            }
            else if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "wss" + remainder;
            }
            else
            {
                return false;
            }
        }

        normalizedUrl = RemoveUserInfo(candidate);
        return true;
    }

    private static string RemoveUserInfo(string url)
    {
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return url;
        }

        var authorityStart = schemeSeparator + 3;
        var relativeEnd = url.AsSpan(authorityStart).IndexOfAny(s_authorityTerminators);
        var authorityEnd = relativeEnd < 0 ? url.Length : authorityStart + relativeEnd;

        var atIndex = url.IndexOf('@', authorityStart);
        if (atIndex < 0 || atIndex >= authorityEnd)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, authorityStart), url.AsSpan(atIndex + 1));
    }
}

