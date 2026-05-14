using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DeepLinkPipeAuthenticatorTests : IDisposable
{
    private readonly string _sandboxDir;

    public DeepLinkPipeAuthenticatorTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"openclaw-deeplink-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateSessionTokenFile_WritesReadableToken()
    {
        var token = DeepLinkPipeAuthenticator.CreateSessionTokenFile(_sandboxDir);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(token, DeepLinkPipeAuthenticator.TryReadSessionToken(_sandboxDir));
        Assert.True(File.Exists(DeepLinkPipeAuthenticator.GetTokenPath(_sandboxDir)));
    }

    [Fact]
    public void CreateSessionTokenFile_RotatesTokenForNewSession()
    {
        var first = DeepLinkPipeAuthenticator.CreateSessionTokenFile(_sandboxDir);
        var second = DeepLinkPipeAuthenticator.CreateSessionTokenFile(_sandboxDir);

        Assert.NotEqual(first, second);
        Assert.Equal(second, DeepLinkPipeAuthenticator.TryReadSessionToken(_sandboxDir));
    }

    [Fact]
    public void TokenMatches_RequiresExactToken()
    {
        var token = DeepLinkPipeAuthenticator.CreateSessionTokenFile(_sandboxDir);

        Assert.True(DeepLinkPipeAuthenticator.TokenMatches(token, token));
        Assert.False(DeepLinkPipeAuthenticator.TokenMatches(token, token + "x"));
        Assert.False(DeepLinkPipeAuthenticator.TokenMatches(token, null));
        Assert.False(DeepLinkPipeAuthenticator.TokenMatches(token, ""));
    }
}
