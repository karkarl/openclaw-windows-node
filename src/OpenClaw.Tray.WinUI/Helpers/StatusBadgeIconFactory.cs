using OpenClawTray.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

namespace OpenClawTray.Helpers;

/// <summary>
/// Builds application icons that mirror the companion-app connection status dot:
/// the lobster mascot with a coloured status dot rendered in the bottom-right
/// corner. Composed icons are cached per <see cref="ConnectionStatusAccent"/> and
/// written to a temp folder as multi-resolution .ico files so both the tray icon
/// (<see cref="WinUIEx.TrayIcon.SetIcon(string)"/>) and the desktop/taskbar window
/// icon (<c>Window.SetIcon(string)</c>) can consume them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StatusBadgeIconFactory
{
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "OpenClawTray", "StatusIcons");
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ConnectionStatusAccent, string> Cache = new();

    // Cover the sizes the shell requests for the tray (16-32 by DPI) and the
    // taskbar/window (up to 256), so LoadImage always finds a crisp match.
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    /// <summary>
    /// Returns a filesystem path to a lobster icon badged with the status dot for
    /// <paramref name="accent"/>. Falls back to the plain app icon on failure.
    /// </summary>
    public static string GetBadgedIconPath(ConnectionStatusAccent accent)
    {
        if (Cache.TryGetValue(accent, out var cached) && File.Exists(cached))
            return cached;

        lock (Gate)
        {
            if (Cache.TryGetValue(accent, out cached) && File.Exists(cached))
                return cached;

            var (path, isFallback) = Build(accent);

            // Only cache successfully composed icons. Caching the fallback would
            // permanently pin the un-badged icon after a transient GDI+ failure.
            if (!isFallback)
                Cache[accent] = path;

            return path;
        }
    }

    /// <summary>Maps a status accent to its dot colour (mirrors the companion-app dot).</summary>
    public static Color DotColor(ConnectionStatusAccent accent) => accent switch
    {
        ConnectionStatusAccent.Success => Color.FromArgb(76, 175, 80),   // Green  – connected
        ConnectionStatusAccent.Caution => Color.FromArgb(255, 193, 7),   // Amber  – connecting / attention
        ConnectionStatusAccent.Critical => Color.FromArgb(244, 67, 54),  // Red    – error
        _ => Color.FromArgb(158, 158, 158),                              // Gray   – disconnected / neutral
    };

    private static (string Path, bool IsFallback) Build(ConnectionStatusAccent accent)
    {
        var fallback = Path.Combine(AssetsPath, "openclaw.ico");
        try
        {
            Directory.CreateDirectory(OutputDir);
            var outputPath = Path.Combine(OutputDir, $"openclaw-{accent}".ToLowerInvariant() + ".ico");

            using var baseImage = LoadBaseImage();
            File.WriteAllBytes(outputPath, CreateIcoBytes(baseImage, DotColor(accent), Sizes));
            return (outputPath, false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to build status-badged icon for {accent}: {ex.Message}");
            return (fallback, true);
        }
    }

    /// <summary>
    /// Composes the badged lobster at every requested size and packs the frames
    /// into a single multi-resolution .ico byte array. Exposed for testing.
    /// </summary>
    internal static byte[] CreateIcoBytes(Bitmap baseImage, Color dotColor, IReadOnlyList<int> sizes)
    {
        var frames = new List<byte[]>(sizes.Count);
        foreach (var size in sizes)
        {
            using var composed = Compose(baseImage, size, dotColor);
            using var ms = new MemoryStream();
            composed.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
        }

        return BuildIco(frames, sizes);
    }

    /// <summary>The icon sizes baked into every composed .ico container.</summary>
    internal static IReadOnlyList<int> IconSizes => Sizes;

    private static Bitmap LoadBaseImage()
    {
        // Prefer the high-resolution unplated mascot PNG (transparent background)
        // for crisp scaling; fall back to the multi-size app .ico.
        var png = Path.Combine(AssetsPath, "Square44x44Logo.targetsize-256_altform-unplated.png");
        if (File.Exists(png))
            return new Bitmap(png);

        var ico = Path.Combine(AssetsPath, "openclaw.ico");
        using var icon = new Icon(ico, 256, 256);
        return icon.ToBitmap();
    }

    /// <summary>
    /// The dot diameter as a fraction of the icon size. Tiny tray icons need a
    /// proportionally larger dot to stay legible at 16-32px, while large taskbar
    /// / alt-tab icons look cleaner with a subtler corner dot. Interpolates
    /// between the two extremes. Exposed for testing.
    /// </summary>
    internal static double DotFraction(int size)
    {
        const double maxFraction = 0.44; // <= 32px  (tray legibility)
        const double minFraction = 0.26; // >= 256px (subtle corner dot)
        const int minSize = 32;
        const int maxSize = 256;

        if (size <= minSize)
            return maxFraction;
        if (size >= maxSize)
            return minFraction;

        var t = (double)(size - minSize) / (maxSize - minSize);
        return maxFraction + (t * (minFraction - maxFraction));
    }

    internal static Bitmap Compose(Bitmap baseImage, int size, Color dotColor)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        g.DrawImage(baseImage, new Rectangle(0, 0, size, size));

        // Dot geometry: bottom-right corner with a white ring for contrast against
        // both the red mascot and arbitrary taskbar backgrounds. The dot scales by
        // size so it is legible on tiny tray icons yet subtle on large icons; the
        // ring is tied to the dot so it shrinks proportionally too.
        float dot = size * (float)DotFraction(size);
        float ring = Math.Max(1f, dot * 0.14f);
        float pad = size * 0.02f;
        float outer = dot + (ring * 2f);
        float outerX = size - outer - pad;
        float outerY = size - outer - pad;

        using (var ringBrush = new SolidBrush(Color.White))
            g.FillEllipse(ringBrush, outerX, outerY, outer, outer);

        using (var dotBrush = new SolidBrush(dotColor))
            g.FillEllipse(dotBrush, outerX + ring, outerY + ring, dot, dot);

        return bmp;
    }

    /// <summary>
    /// Packs PNG frames into a Vista-style .ico container (PNG-compressed entries,
    /// supported by LoadImage on Windows 10+).
    /// </summary>
    private static byte[] BuildIco(IReadOnlyList<byte[]> pngFrames, IReadOnlyList<int> sizes)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var count = (ushort)pngFrames.Count;
        writer.Write((ushort)0); // idReserved
        writer.Write((ushort)1); // idType = icon
        writer.Write(count);     // idCount

        var offset = 6 + (16 * count);
        for (var i = 0; i < pngFrames.Count; i++)
        {
            var dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]); // 0 encodes 256
            writer.Write(dim);              // bWidth
            writer.Write(dim);              // bHeight
            writer.Write((byte)0);          // bColorCount
            writer.Write((byte)0);          // bReserved
            writer.Write((ushort)1);        // wPlanes
            writer.Write((ushort)32);       // wBitCount
            writer.Write((uint)pngFrames[i].Length); // dwBytesInRes
            writer.Write((uint)offset);     // dwImageOffset
            offset += pngFrames[i].Length;
        }

        foreach (var frame in pngFrames)
            writer.Write(frame);

        writer.Flush();
        return ms.ToArray();
    }
}
