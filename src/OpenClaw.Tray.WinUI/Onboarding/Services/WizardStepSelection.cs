namespace OpenClawTray.Onboarding.Services;

public static class WizardStepSelection
{
    public const string SkipValue = "__skip__";

    public static bool RequiresSelection(string stepType) => stepType is "select" or "multiselect";

    public static int SelectedIndex(string stepInput, IReadOnlyList<string> optionValues)
    {
        for (var i = 0; i < optionValues.Count; i++)
        {
            if (optionValues[i] == stepInput)
                return i;
        }

        return -1;
    }

    public static bool HasValidSelection(string stepType, string stepInput, IReadOnlyCollection<string> optionValues)
    {
        if (stepType == "select")
            return optionValues.Contains(stepInput);

        if (stepType == "multiselect")
        {
            var selected = SplitMultiSelectValues(stepInput);
            return selected.Length > 0 && selected.All(optionValues.Contains);
        }

        return true;
    }

    public static bool ShouldDisableContinue(string stepType, string stepInput, IReadOnlyCollection<string> optionValues) =>
        RequiresSelection(stepType) && !HasValidSelection(stepType, stepInput, optionValues);

    public static bool TryBuildAnswerValue(string stepType, string stepInput, IReadOnlyCollection<string> optionValues, out string answerValue)
    {
        if (RequiresSelection(stepType) && !HasValidSelection(stepType, stepInput, optionValues))
        {
            answerValue = "";
            return false;
        }

        answerValue = string.IsNullOrEmpty(stepInput) ? "true" : stepInput;
        return true;
    }

    public static string BuildSkipAnswerValue(string stepType, IReadOnlyCollection<string>? optionValues = null) => stepType switch
    {
        "confirm" => "false",
        "select" or "multiselect" => SelectSkipValue(optionValues),
        _ => "true"
    };

    private static string SelectSkipValue(IReadOnlyCollection<string>? optionValues)
    {
        if (optionValues?.Contains(SkipValue) == true)
            return SkipValue;

        if (optionValues?.Contains("__done__") == true)
            return "__done__";

        return SkipValue;
    }

    private static string[] SplitMultiSelectValues(string stepInput) =>
        stepInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
