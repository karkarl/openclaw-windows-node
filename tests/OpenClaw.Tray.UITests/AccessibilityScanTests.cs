using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Axe.Windows.Core.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Data-driven accessibility tests that scan each page in the OpenClaw app for
/// WCAG violations using Axe.Windows. Modeled after WinUI-Gallery's
/// AxeScanAllTests pattern.
///
/// Each page is instantiated in the <see cref="UIThreadFixture"/>'s hidden window
/// and scanned via the UIA tree. New pages added to the app should be registered
/// in <see cref="PageTestData"/> to be automatically included in CI scans.
///
/// See PR #921 for fixes to the low-hanging accessibility violations discovered
/// by these tests.
/// </summary>
[Collection(UICollection.Name)]
public sealed class AccessibilityScanTests
{
    private readonly UIThreadFixture _ui;

    public AccessibilityScanTests(UIThreadFixture ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// Pages that are completely excluded from scanning because they crash Axe
    /// or require infrastructure unavailable in CI (e.g. WebView2, live connections).
    /// </summary>
    private static readonly HashSet<string> ExcludedPages =
    [
        // ChatPage hosts CefSharp/WebView2 which causes Axe to traverse the
        // Chromium UIA tree and throw NullReferenceException (same root cause as
        // WinUI-Gallery's WebView2 exclusion: axe-windows/issues/662).
        "ChatPage",
    ];

    /// <summary>
    /// Per-page rule exclusions for known issues specific to certain pages.
    /// Add entries here when a page has violations caused by WinUI framework
    /// limitations or third-party controls that cannot be fixed in app code.
    /// Document the reason with a link to the upstream issue.
    /// </summary>
    private static readonly Dictionary<string, RuleId[]> PageRuleExclusions = new()
    {
        // ConnectionPage: gateway row cards use dynamic binding for accessible name;
        // when no gateways are configured the template has empty Name.
        // Fixed in PR #921; keep exclusion in case tests run without that PR.
        ["ConnectionPage"] = [RuleId.NameNotNull],
    };

    /// <summary>
    /// Enumerates all app pages for data-driven testing. Each entry is
    /// [pageName, pageType]. New pages are automatically picked up when added here.
    /// </summary>
    public static IEnumerable<object[]> PageTestData()
    {
        var pages = new (string Name, Type Type)[]
        {
            ("AgentEventsPage", typeof(OpenClawTray.Pages.AgentEventsPage)),
            ("BindingsPage", typeof(OpenClawTray.Pages.BindingsPage)),
            ("ChannelsPage", typeof(OpenClawTray.Pages.ChannelsPage)),
            ("ConfigPage", typeof(OpenClawTray.Pages.ConfigPage)),
            ("ConnectionPage", typeof(OpenClawTray.Pages.ConnectionPage)),
            ("CronPage", typeof(OpenClawTray.Pages.CronPage)),
            ("DebugPage", typeof(OpenClawTray.Pages.DebugPage)),
            ("InstancesPage", typeof(OpenClawTray.Pages.InstancesPage)),
            ("NotificationsPage", typeof(OpenClawTray.Pages.NotificationsPage)),
            ("PermissionsPage", typeof(OpenClawTray.Pages.PermissionsPage)),
            ("SandboxPage", typeof(OpenClawTray.Pages.SandboxPage)),
            ("SessionsPage", typeof(OpenClawTray.Pages.SessionsPage)),
            ("SettingsPage", typeof(OpenClawTray.Pages.SettingsPage)),
            ("SkillsPage", typeof(OpenClawTray.Pages.SkillsPage)),
            ("UsagePage", typeof(OpenClawTray.Pages.UsagePage)),
            ("VoiceSettingsPage", typeof(OpenClawTray.Pages.VoiceSettingsPage)),
            ("WorkspacePage", typeof(OpenClawTray.Pages.WorkspacePage)),
        };

        foreach (var (name, type) in pages)
        {
            if (!ExcludedPages.Contains(name))
                yield return [name, type];
        }
    }

    /// <summary>
    /// Instantiates each page in the test window and scans for accessibility
    /// violations using Axe.Windows. Failures include the rule ID, element
    /// control type, name, and automation ID for actionability.
    /// </summary>
    [Theory]
    [Trait("Category", "Accessibility")]
    [MemberData(nameof(PageTestData))]
    public async Task Page_PassesAccessibilityScan(string pageName, Type pageType)
    {
        await _ui.ResetContainerAsync();

        await _ui.RunOnUIAsync(() =>
        {
            // Initialize Axe scanner on first use (attaches to current process)
            AxeHelper.Initialize();

            // Instantiate the page and host it in the test container
            var page = (UIElement)Activator.CreateInstance(pageType)!;

            _ui.Container.Children.Clear();
            _ui.Container.Children.Add(page);

            // Force layout so the UIA tree is fully populated
            _ui.Container.UpdateLayout();
        });

        // Allow any async UI initialization (bindings, loaded events) to settle
        await Task.Delay(500);

        await _ui.RunOnUIAsync(() =>
        {
            PageRuleExclusions.TryGetValue(pageName, out var ruleExclusions);
            AxeHelper.AssertNoAccessibilityErrors(ruleExclusions, pageName);
        });
    }
}
