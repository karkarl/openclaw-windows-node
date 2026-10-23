using OpenClaw.TestSupport.Gateway;
using OpenClaw.SetupEngine;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public Task SkillsAcceptedToggleRefreshesGatewayState() => SkillsAcceptedToggleAsync(nativeSetup: false);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public Task NativeSetupSkillsAcceptedToggleRefreshesBoundGatewayState() => SkillsAcceptedToggleAsync(nativeSetup: true);

    private Task SkillsAcceptedToggleAsync(bool nativeSetup)
    {
        var scenario = GatewayScenario.CreateSkillsEditing((method, _) => method switch
        {
            "openclaw.setup.verify" => new { ok = true, modelRef = "fixture/browse", latencyMs = 1 },
            _ => throw new InvalidOperationException($"Unexpected synthetic setup request: {method}")
        });
        return WithAppAsync(async run =>
        {
            if (!nativeSetup)
                await run.InvokeAsync("app.navigate", new { page = "skills" });
            await WaitUiAsync(run, () => FindById(run, "SkillsPageToggle:fixture-calendar") is not null,
                "populated Skills destination");
            if (nativeSetup)
                Assert.Contains(run.Gateway.Requests, request => request.Method == "openclaw.setup.verify");
            Invoke(FindById(run, "SkillsPageToggle:fixture-calendar")!);
            await WaitUiAsync(run, () => FindText(run, "Enabled (0)") is { Current.IsOffscreen: false },
                "Gateway-acknowledged disable reflected by refreshed report");
            Assert.NotNull(FindText(run, "Disabled (2)"));
            Assert.Single(run.Gateway.Requests, request => request.Method == "skills.update" && request.Outcome == "ok");
            Assert.True(run.Gateway.Requests.Count(request => request.Method == "skills.status") >= 2);
            await CaptureIfRequestedAsync(run, nativeSetup ? "skills-native-accepted.png" : "skills-accepted.png");
        }, scenario: scenario, prepareSetupHandoff: nativeSetup
            ? profile => IssueSyntheticHandoff(profile, SetupNativeDestination.Skills)
            : null);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SkillsRejectionKeepsAcknowledgedStateAndShowsRecovery()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "skills" });
            await WaitUiAsync(run, () => FindById(run, "SkillsPageToggle:fixture-calendar") is not null,
                "populated native Skills page");
            await CaptureIfRequestedAsync(run, "skills-before-rejection.png");
            var toggle = FindById(run, "SkillsPageToggle:fixture-calendar")!;
            Invoke(toggle);

            const string error = "Could not load or update skills. Check the Gateway connection and permissions, then try again.";
            await WaitUiAsync(run, () => FindText(run, error) is { Current.IsOffscreen: false },
                "visible skill mutation rejection");
            Assert.NotNull(FindText(run, "Enabled (1)"));
            Assert.True(FindById(run, "SkillsPageToggle:fixture-calendar")!.Current.IsEnabled);
            Assert.Single(run.Gateway.Requests, request => request.Method == "skills.update" && request.Outcome == "error:FIXTURE_READ_ONLY");
            await CaptureIfRequestedAsync(run, "skills-rejected-state-preserved.png");
        }, scenario: GatewayScenario.CreateBrowse());
    }
}
