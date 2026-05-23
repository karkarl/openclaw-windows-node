using System.Text.Json;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class WizardSelectionTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void SelectWithoutInitialValue_LeavesStepInputEmptyAndNoSelectedIndex()
    {
        var step = WizardStepParser.Parse(Parse("""{"step":{"type":"select","id":"provider","options":["BlueBubbles","Matrix"]}}"""));

        Assert.Equal("", step.InitialValue);
        Assert.Equal(-1, WizardStepSelection.SelectedIndex(step.InitialValue, step.OptionValues));
    }

    [Fact]
    public void SelectWithExplicitInitialValue_UsesMatchingSelectedIndex()
    {
        var step = WizardStepParser.Parse(Parse("""{"step":{"type":"select","id":"provider","initialValue":"Matrix","options":["BlueBubbles","Matrix"]}}"""));

        Assert.Equal("Matrix", step.InitialValue);
        Assert.Equal(1, WizardStepSelection.SelectedIndex(step.InitialValue, step.OptionValues));
    }

    [Fact]
    public void EmptySelectInput_DoesNotBuildTrueOrFirstOptionAnswer()
    {
        var values = new[] { "bluebubbles" };

        var valid = WizardStepSelection.TryBuildAnswerValue("select", "", values, out var answerValue);

        Assert.False(valid);
        Assert.NotEqual("true", answerValue);
        Assert.NotEqual("bluebubbles", answerValue);
    }

    [Theory]
    [InlineData("select", "", true)]
    [InlineData("select", "bogus", true)]
    [InlineData("select", "matrix", false)]
    [InlineData("multiselect", "", true)]
    [InlineData("multiselect", "matrix,bogus", true)]
    [InlineData("multiselect", "matrix,bluebubbles", false)]
    public void ContinueDisabled_ForSelectAndMultiselectInvalidInput(string stepType, string input, bool expectedDisabled)
    {
        var values = new[] { "bluebubbles", "matrix" };

        Assert.Equal(expectedDisabled, WizardStepSelection.ShouldDisableContinue(stepType, input, values));
    }

    [Theory]
    [InlineData("note")]
    [InlineData("confirm")]
    [InlineData("action")]
    public void EmptyAcknowledgeSteps_AllowContinueAndBuildTrueAnswer(string stepType)
    {
        var values = Array.Empty<string>();

        Assert.False(WizardStepSelection.ShouldDisableContinue(stepType, "", values));
        Assert.True(WizardStepSelection.TryBuildAnswerValue(stepType, "", values, out var answerValue));
        Assert.Equal("true", answerValue);
    }

    [Theory]
    [InlineData("confirm", "false")]
    [InlineData("select", "__skip__")]
    [InlineData("multiselect", "__skip__")]
    [InlineData("note", "true")]
    [InlineData("text", "true")]
    public void BuildSkipAnswerValue_UsesGatewaySkipSentinelForSelectionSteps(string stepType, string expected)
    {
        Assert.Equal(expected, WizardStepSelection.BuildSkipAnswerValue(stepType));
    }

    [Fact]
    public void BuildSkipAnswerValue_UsesDoneSentinelWhenSelectOptionsContainFinished()
    {
        var optionValues = new[] { "feishu", "telegram", "__done__" };

        Assert.Equal("__done__", WizardStepSelection.BuildSkipAnswerValue("select", optionValues));
    }

    [Fact]
    public void BuildSkipAnswerValue_PrefersExplicitSkipSentinelWhenAvailable()
    {
        var optionValues = new[] { "feishu", "__skip__", "__done__" };

        Assert.Equal("__skip__", WizardStepSelection.BuildSkipAnswerValue("select", optionValues));
    }
}
