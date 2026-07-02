namespace OpenClawTray;

/// <summary>
/// Compile-time app identity constants that vary between Dev and Release builds,
/// enabling side-by-side installation of both variants (similar to WinUI Gallery).
/// </summary>
internal static class AppIdentity
{
#if DEV_BUILD
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion (Dev)";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray (Dev)";

    /// <summary>MSIX package identity name (must differ from release for side-by-side).</summary>
    public const string PackageIdentityName = "OpenClaw.Companion.Dev";

    /// <summary>Windows Registry auto-start value name (must differ so both can auto-start).</summary>
    public const string AutoStartRegistryName = "OpenClawTray-Dev";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray-Dev";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw-dev";

    /// <summary>Whether this is a development build.</summary>
    public const bool IsDev = true;
#else
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray";

    /// <summary>MSIX package identity name.</summary>
    public const string PackageIdentityName = "OpenClaw.Companion";

    /// <summary>Windows Registry auto-start value name.</summary>
    public const string AutoStartRegistryName = "OpenClawTray";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw";

    /// <summary>Whether this is a development build.</summary>
    public const bool IsDev = false;
#endif
}
