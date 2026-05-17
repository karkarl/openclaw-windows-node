using System.IO;

namespace OpenClaw.Tray.Tests;

public sealed class AsyncListLoadingPageWiringTests
{
    [Theory]
    [InlineData("SessionsPage.xaml", "Loading sessions")]
    [InlineData("CronPage.xaml", "Loading cron jobs")]
    [InlineData("UsagePage.xaml", "Loading daily costs")]
    [InlineData("BindingsPage.xaml", "Loading bindings")]
    public void BigListPages_HaveFirstLoadPlaceholders(string fileName, string loadingText)
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", fileName);

        Assert.Contains("<ProgressRing", source);
        Assert.Contains(loadingText, source);
    }

    [Theory]
    [InlineData("SessionsPage.xaml.cs")]
    [InlineData("CronPage.xaml.cs")]
    [InlineData("UsagePage.xaml.cs")]
    [InlineData("BindingsPage.xaml.cs")]
    public void BigListPages_DisableListInteractionsDuringRefresh(string fileName)
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", fileName);

        Assert.Contains("AsyncListLoadingState", source);
        Assert.Contains(".BeginRefresh()", source);
        Assert.Contains(".CanEdit", source);
    }

    private static string ReadSource(params string[] relativePathParts)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(relativePathParts).ToArray()));
    }
}
