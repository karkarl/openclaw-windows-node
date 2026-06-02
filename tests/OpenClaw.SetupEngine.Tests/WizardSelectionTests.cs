namespace OpenClaw.SetupEngine.Tests;

public class WizardSelectionTests
{
    [Fact]
    public void SelectWithoutInitialValue_LeavesNoSelectedIndex()
    {
        var values = new[] { "bluebubbles", "matrix" };

        Assert.Equal(-1, WizardSelection.SelectedIndex(null, values));
        Assert.Equal(-1, WizardSelection.SelectedIndex("", values));
    }

    [Fact]
    public void SelectWithExplicitInitialValue_UsesMatchingSelectedIndex()
    {
        var values = new[] { "bluebubbles", "matrix" };

        Assert.Equal(1, WizardSelection.SelectedIndex("matrix", values));
    }

    [Theory]
    [InlineData("select", new string[0], true)]
    [InlineData("select", new[] { "bogus" }, true)]
    [InlineData("select", new[] { "matrix" }, false)]
    [InlineData("select", new[] { "matrix", "bluebubbles" }, true)]
    [InlineData("multiselect", new string[0], true)]
    [InlineData("multiselect", new[] { "matrix", "bogus" }, true)]
    [InlineData("multiselect", new[] { "matrix", "bluebubbles" }, false)]
    public void ContinueDisabled_ForInvalidSelectAndMultiselectInput(string stepType, string[] selected, bool expectedDisabled)
    {
        var values = new[] { "bluebubbles", "matrix" };

        Assert.Equal(expectedDisabled, WizardSelection.ShouldDisableContinue(stepType, selected, values));
    }

    [Theory]
    [InlineData("select", new string[0], false)]
    [InlineData("select", new[] { "" }, false)]
    [InlineData("select", new[] { "   " }, false)]
    [InlineData("select", new[] { "matrix" }, true)]
    [InlineData("multiselect", new string[0], false)]
    [InlineData("multiselect", new[] { "matrix" }, true)]
    [InlineData("note", new string[0], true)]
    public void HasSelectableOptions_RequiresNonEmptyChoiceOptions(string stepType, string[] options, bool expected)
    {
        Assert.Equal(expected, WizardSelection.HasSelectableOptions(stepType, options));
    }

    [Theory]
    [InlineData("note")]
    [InlineData("confirm")]
    [InlineData("action")]
    public void EmptyAcknowledgeSteps_AllowContinue(string stepType)
    {
        Assert.False(WizardSelection.ShouldDisableContinue(stepType, [], []));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("value", false)]
    public void ContinueDisabled_ForEmptyTextInput(string? input, bool expectedDisabled)
    {
        Assert.Equal(expectedDisabled, WizardSelection.ShouldDisableContinue("text", input));
    }

    [Theory]
    [InlineData("Authorize device", "", null)]
    [InlineData("Choose a channel", "Select where OpenClaw should send messages", null)]
    [InlineData("Setup", "Downloading plugin package", null)]
    [InlineData("Setup", "Installing integration", null)]
    [InlineData("Choose integration", "", "Microsoft Teams")]
    [InlineData("Choose integration", "", "msteams")]
    public void TimeoutForStep_UsesLongTimeoutForSlowWizardOperations(string title, string message, string? additionalText)
    {
        Assert.Equal(WizardSelection.SlowStepTimeoutMs, WizardSelection.TimeoutForStep(title, message, additionalText));
    }

    [Fact]
    public void TimeoutForStep_UsesDefaultTimeoutForOrdinaryQuestions()
    {
        Assert.Equal(WizardSelection.DefaultStepTimeoutMs, WizardSelection.TimeoutForStep("Choose a model", "Select the default model."));
    }
}
