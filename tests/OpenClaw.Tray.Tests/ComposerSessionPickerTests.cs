namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract guards for the composer pickers. These assert the composer builds its session
/// and model dropdowns through the reconciled FunctionalUI ComboBox primitive rather than
/// hand-rolling a native <c>ComboBox</c> inside a setter — the imperative escape hatch that caused
/// the #970 "dropdown slams shut on status render" regression.
/// </summary>
public sealed class ComposerSessionPickerTests
{
    private static string ComposerSource() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Chat",
        "OpenClawComposer.cs"));

    [Fact]
    public void SessionPicker_UsesReconciledItemComboBoxPrimitive()
    {
        var composer = ComposerSource();

        Assert.Contains("var sessionItems = new List<ComboItem>();", composer);
        Assert.Contains("ComboBox(sessionItems, Props.ChannelId ?? \"\"", composer);
    }

    [Fact]
    public void ModelPicker_UsesReconciledItemComboBoxPrimitive()
    {
        var composer = ComposerSource();

        Assert.Contains("ComboBox(modelItems, modelSelectedId", composer);
        Assert.Contains("if (id == ClearModelId)", composer);
    }

    [Fact]
    public void Composer_DoesNotHandRollNativePickersOrSnapshots()
    {
        var composer = ComposerSource();

        // The escape-hatch patterns that produced #970 must not return.
        Assert.DoesNotContain("border.Child = cb;", composer);
        Assert.DoesNotContain("SessionPickerSnapshot", composer);
        Assert.DoesNotContain("Native(", composer);
    }
}
