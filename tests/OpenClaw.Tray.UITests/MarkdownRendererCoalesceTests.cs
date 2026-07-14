using OpenClawTray.Chat.Markdown;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Xunit;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Tests for the assistant-bubble text coalescing behavior: consecutive
/// paragraph / heading blocks merge into a single <see cref="RichTextBlockElement"/>
/// so the whole prose run is one continuous, drag-selectable scope. Non-text
/// blocks (lists, code, tables) stay as their own selectable sibling controls.
///
/// Assertions live at the FunctionalUI <see cref="Element"/> record level (no
/// WinUI runtime): the Blocks of the RichTextBlock are populated imperatively
/// by a setter at reconcile time, but the element shape is what pins the
/// coalescing contract.
/// </summary>
public sealed class MarkdownRendererCoalesceTests
{
    [Fact]
    public void SingleParagraph_StaysLightweightTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("just one line of prose");

        // A lone text block keeps the single-TextBlock shape (no RichTextBlock
        // overhead) — it is already internally selectable.
        Assert.IsType<TextBlockElement>(element);
    }

    [Fact]
    public void ConsecutiveParagraphs_CoalesceIntoOneRichTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("first paragraph\n\nsecond paragraph");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void HeadingThenParagraph_CoalesceIntoOneRichTextBlock()
    {
        var element = ChatMarkdownRenderer.Render("# Title\n\nbody text under the title");

        Assert.IsType<RichTextBlockElement>(element);
    }

    [Fact]
    public void TextRunFollowedByList_KeepsListAsSeparateSibling()
    {
        // Paragraph + heading coalesce into a RichTextBlock; the list is a
        // separate selectable island. Result is a vertical stack of the two.
        var element = ChatMarkdownRenderer.Render(
            "# Title\n\nintro paragraph\n\n- a bullet\n- another bullet");

        var stack = Assert.IsType<StackElement>(element);
        Assert.Equal(Microsoft.UI.Xaml.Controls.Orientation.Vertical, stack.Orientation);
        Assert.Equal(2, stack.Children.Count);

        Assert.IsType<RichTextBlockElement>(stack.Children[0]!);
        // The list remains its own StackElement (unchanged rendering).
        Assert.IsType<StackElement>(stack.Children[1]!);
    }
}
