namespace OpenClaw.SetupEngine.Tests;

public class SetupWizardRunnerTests
{
    [Fact]
    public void TimeoutFor_UsesSelectedMultiselectOptionText()
    {
        var step = new SetupWizardRunner.WizardPayload(
            IsDone: false,
            SessionId: "session",
            StepId: "integration-choice",
            StepType: "multiselect",
            Title: "Choose integrations",
            Message: "Select one or more integrations.",
            InitialValue: "",
            Sensitive: false,
            StepIndex: 0,
            TotalSteps: 1,
            Options:
            [
                new SetupWizardRunner.WizardOption("integration_a", "Microsoft Teams", "channel setup"),
                new SetupWizardRunner.WizardOption("integration_b", "Matrix", "")
            ],
            Error: null);

        Assert.Equal(WizardSelection.SlowStepTimeoutMs, SetupWizardRunner.TimeoutFor(step, "integration_a,integration_b"));
    }
}
