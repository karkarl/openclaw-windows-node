using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Shared.Mcp;

namespace OpenClawTray.Services;

internal static class DeepLinkPipeAuthenticator
{
    internal const string TokenFileName = "deep-link-pipe-token.txt";

    public static string GetTokenPath(string settingsDirectory) =>
        Path.Combine(settingsDirectory, TokenFileName);

    public static string CreateSessionTokenFile(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            throw new ArgumentException("Settings directory cannot be empty.", nameof(settingsDirectory));

        Directory.CreateDirectory(settingsDirectory);
        McpAuthToken.TryRestrictDataDirectoryAcl(settingsDirectory);

        var token = GenerateToken();
        var path = GetTokenPath(settingsDirectory);
        var tempPath = Path.Combine(settingsDirectory, $".{TokenFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, token, Encoding.UTF8);
            McpAuthToken.TryRestrictSensitiveFileAcl(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTokenFile(tempPath);
            throw;
        }

        McpAuthToken.TryRestrictSensitiveFileAcl(path);
        return token;
    }

    public static string? TryReadSessionToken(string settingsDirectory)
    {
        try
        {
            var path = GetTokenPath(settingsDirectory);
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    public static void TryDeleteTokenFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    public static bool TokenMatches(string expectedToken, string? presentedToken)
    {
        if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(presentedToken))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
        return expectedBytes.Length == presentedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServerStream(string pipeName) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: CreatePipeSecurity());

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreatePipeSecurity()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current user SID.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    private static string GenerateToken()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
