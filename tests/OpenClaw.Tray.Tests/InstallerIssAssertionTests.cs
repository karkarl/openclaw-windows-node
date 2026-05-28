namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on installer.iss.  These pin contracts that cannot
/// be exercised by an in-process unit test because they require ISCC + the
/// resulting unins000.exe to verify end-to-end.
///
/// Round 2 (Scott #5) — AppMutex coordination prevents the Inno uninstaller
/// from racing the running tray on shared state (settings.json,
/// gateways.json, device-key-ed25519.json, Logs/).  The mutex name must
/// match App.xaml.cs's single-instance mutex.
/// </summary>
public sealed class InstallerIssAssertionTests
{
    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    [Fact]
    public void Installer_HasAppMutexMatchingTraySingleInstance()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));
        Assert.Contains("AppMutex=OpenClawTray", iss);

        // The matching tray-side mutex name must be present in App.xaml.cs.
        var appXamlCs = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("var mutexName = \"OpenClawTray\";", appXamlCs);
    }

    [Fact]
    public void Installer_DoesNotShipCommandPaletteExtension()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.DoesNotContain("cmdpalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommandPalette", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-AppxPackage", iss, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_CreatesStartMenuEntrypointsForTraySetupAndSupport()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"#define MyAppName ""OpenClaw Companion""", iss);
        Assert.Contains("OutputBaseFilename=OpenClawCompanion-Setup-{#MyAppArch}", iss);
        Assert.Contains(@"Name: ""{group}\{#MyAppName}""; Filename: ""{app}\{#MyAppExeName}""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Gateway Setup""; Filename: ""{app}\SetupEngine\OpenClaw.SetupEngine.UI.exe""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Companion Settings""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://commandcenter""", iss);
        Assert.Contains(@"Name: ""{group}\OpenClaw Chat""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://chat""", iss);
        Assert.Contains(@"Name: ""{group}\Check for Updates""; Filename: ""{app}\{#MyAppExeName}""; Parameters: ""openclaw://check-updates""", iss);
    }

    [Fact]
    public void Installer_RegistersOpenClawProtocol()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.Contains(@"Subkey: ""Software\Classes\openclaw""", iss);
        Assert.Contains(@"ValueName: ""URL Protocol""", iss);
        Assert.Contains(@"Subkey: ""Software\Classes\openclaw\shell\open\command""", iss);
        Assert.Contains(@"{app}\{#MyAppExeName}", iss);
        Assert.Contains(@"""%1""", iss);
    }

    [Fact]
    public void ReleaseBuildCopiesSetupEngineIntoInstallerPayload()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));
        var ci = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains(@"FileExists(publish + ""\SetupEngine\OpenClaw.SetupEngine.UI.exe"")", iss);
        Assert.Contains("Publish SetupEngine.UI", ci);
        Assert.Contains(@"dotnet publish src/OpenClaw.SetupEngine.UI", ci);
        Assert.Contains(@"mkdir publish\SetupEngine", ci);
        Assert.Contains(@"copy publish-setup\* publish\SetupEngine\ -Recurse", ci);
    }

}
