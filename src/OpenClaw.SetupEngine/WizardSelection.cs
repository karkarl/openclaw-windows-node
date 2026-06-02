namespace OpenClaw.SetupEngine;

public static class WizardSelection
{
    public const int DefaultStepTimeoutMs = 30_000;
    public const int SlowStepTimeoutMs = 300_000;

    public static bool RequiresSelection(string stepType) => stepType is "select" or "multiselect";
    public static bool RequiresAnswer(string stepType) => RequiresSelection(stepType) || stepType == "text";

    public static bool HasSelectableOptions(string stepType, IReadOnlyCollection<string> optionValues) =>
        !RequiresSelection(stepType) || optionValues.Any(value => !string.IsNullOrWhiteSpace(value));

    public static int SelectedIndex(string? stepInput, IReadOnlyList<string> optionValues)
    {
        if (string.IsNullOrEmpty(stepInput))
            return -1;

        for (var i = 0; i < optionValues.Count; i++)
        {
            if (optionValues[i] == stepInput)
                return i;
        }

        return -1;
    }

    public static bool HasValidSelection(string stepType, IReadOnlyCollection<string> selectedValues, IReadOnlyCollection<string> optionValues)
    {
        if (!HasSelectableOptions(stepType, optionValues))
            return false;

        if (stepType == "select")
            return selectedValues.Count == 1 && optionValues.Contains(selectedValues.First());

        if (stepType == "multiselect")
            return selectedValues.Count > 0 && selectedValues.All(optionValues.Contains);

        return true;
    }

    public static bool ShouldDisableContinue(string stepType, IReadOnlyCollection<string> selectedValues, IReadOnlyCollection<string> optionValues) =>
        RequiresSelection(stepType) && !HasValidSelection(stepType, selectedValues, optionValues);

    public static bool ShouldDisableContinue(string stepType, string? textInput) =>
        stepType == "text" && string.IsNullOrWhiteSpace(textInput);

    public static int TimeoutForStep(string? title, string? message, params string?[] additionalText)
    {
        var text = string.Join(' ', new[] { title, message }.Concat(additionalText).Where(value => !string.IsNullOrWhiteSpace(value)));
        return text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("channel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("plugin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("install", StringComparison.OrdinalIgnoreCase)
            || text.Contains("download", StringComparison.OrdinalIgnoreCase)
            || text.Contains("teams", StringComparison.OrdinalIgnoreCase)
            ? SlowStepTimeoutMs
            : DefaultStepTimeoutMs;
    }
}
