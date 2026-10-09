using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using OpenClawTray.Chat;
using OpenClawTray.Chat.Markdown;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.System;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
[Trait("Category", "NativeMarkdownSelectionProof")]
public sealed class ChatMessageTextSelectionProofTests(UIThreadFixture ui)
{
    private const string Message =
        "Opening paragraph with **bold evidence**.\n\n" +
        "- plugin purpose and default activation.\n" +
        "- app\\dist\\extensions\\gateway-isolation: second item.\n\n" +
        "```text\ncode one\ncode two\n```\n\n" +
        "Metric | Value\n--- | ---\nCount | 2\n\n" +
        "Reference <https://example.invalid/evidence> and trailing text.\n\n" +
        "<details><summary>Collapsed evidence</summary>\n\nHidden evidence.\n\n</details>\n\n" +
        "Final paragraph.";

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task VisibleMessage_PointerKeyboardCopyAndTextProviderShareOneRange(string theme)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? resources = null;
        Border? surface = null;
        Element? current = null;
        UIElement? root = null;
        Action? reconcile = null;
        var disposed = false;
        var message = Message;
        string? copied = null;
        bool TryCopy(string text) { copied = text; return true; }
        NativeChatMessageText? owner = null;
        ITextProvider? provider = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                ui.TestWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1600));
                host = new ReactorHostControl { Width = 620, RequestedTheme = resources.ElementTheme };
                Action request = () => ui.Dispatcher.TryEnqueue(() =>
                {
                    if (!disposed)
                        reconcile!();
                });
                reconcile = () =>
                {
                    var next = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                    root = host.Reconciler.Reconcile(current!, next, root!, request);
                    current = next;
                    host.Content = root;
                };
                current = ReactorChatTimeline.BuildSafeMarkdown(message, TryCopy);
                root = host.Reconciler.Mount(current, request);
                host.Content = root;
                surface = new Border
                {
                    Child = host,
                    Width = 652,
                    Padding = new Thickness(16),
                    VerticalAlignment = VerticalAlignment.Top,
                    RequestedTheme = resources.ElementTheme,
                };
                ui.Container.Children.Add(surface);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                owner = Assert.Single(FindLogical<NativeChatMessageText>(host!));
                owner.RefreshDocument();
                provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner)!.GetPattern(PatternInterface.Text));
                var text = provider.DocumentRange.GetText(-1);
                Assert.Contains("Opening paragraph with bold evidence.", text);
                Assert.Contains("\u2022 plugin purpose and default activation.\n\u2022 app\\dist", text);
                Assert.Contains("code one\ncode two", text);
                Assert.Contains("Metric\tValue\nCount\t2", text);
                Assert.Contains("Reference https://example.invalid/evidence and trailing text.", text);
                Assert.DoesNotContain("Hidden evidence.", text);
                Assert.DoesNotContain("Collapsed evidence", text);
                Assert.DoesNotContain("Copy code", text);
                Assert.Equal(SupportedTextSelection.Single, provider.SupportedTextSelection);

                var start = text.IndexOf("plugin purpose", StringComparison.Ordinal);
                var end = text.IndexOf("code two", StringComparison.Ordinal) + "code two".Length;
                Assert.True(owner.BeginSelection(PointForText(owner, provider, "plugin purpose", trailing: false)));
                owner.ExtendSelection(PointForText(owner, provider, "code two", trailing: true));
                owner.EndSelection();
                Assert.Equal(text[start..end], owner.Document.SelectedText);
                Assert.Equal(owner.Document.SelectedText, Assert.Single(provider.GetSelection()).GetText(-1));
                Assert.True(owner.ProcessKey(VirtualKey.C, control: true, shift: false));
                Assert.Equal(text[start..end], copied);
                Assert.All(FindLogical<RichTextBlock>(host!).Where(block =>
                    CollectText(block).Contains("plugin purpose", StringComparison.Ordinal) ||
                    CollectText(block).Contains("code one", StringComparison.Ordinal)),
                    block => Assert.NotEmpty(block.TextHighlighters.SelectMany(highlight => highlight.Ranges)));
            });
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(surface!, $"MessageSelection-{theme}"));
            await ui.RunOnUIAsync(() =>
            {
                var text = owner!.Document.Text;
                owner.SelectRange(0, 0);
                Assert.True(owner.ProcessKey(VirtualKey.End, control: true, shift: true));
                Assert.Equal(text, owner.Document.SelectedText);
                Assert.True(owner.ProcessKey(VirtualKey.Home, control: true, shift: false));
                Assert.False(owner.Document.HasSelection);
                Assert.Equal(0, owner.Document.Focus);
                Assert.True(owner.ProcessKey(VirtualKey.Right, control: true, shift: true));
                Assert.Equal("Opening ", owner.Document.SelectedText);

                var range = provider!.DocumentRange.FindText("code one\ncode two", backward: false, ignoreCase: false);
                Assert.NotNull(range);
                range.Select();
                Assert.Equal("code one\ncode two", owner.Document.SelectedText);
                var clone = range.Clone();
                Assert.True(range.Compare(clone));
                clone.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
                Assert.Equal("code one\ncode tw", clone.GetText(-1));
                Assert.True(owner.ProcessKey(VirtualKey.C, control: true, shift: false));
                Assert.Equal("code one\ncode two", copied);

                var before = range.GetText(-1);
                message = message.Replace("Opening paragraph", "Opening expanded paragraph", StringComparison.Ordinal);
                reconcile!();
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Equal(before, range.GetText(-1));
                Assert.Equal(before, owner.Document.SelectedText);
                host!.Width = 320;
                surface!.Width = 352;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.Equal(before, owner.Document.SelectedText);
                Assert.Equal(before, Assert.Single(provider.GetSelection()).GetText(-1));

                var link = provider.DocumentRange.FindText("https://example.invalid/evidence", false, false);
                link.Select();
                Assert.Equal("https://example.invalid/evidence", owner.Document.SelectedText);
                link.GetBoundingRectangles(out var rectangles);
                Assert.NotEmpty(rectangles);
                Assert.All(rectangles.Chunk(4), rectangle =>
                {
                    Assert.True(rectangle[2] > 0);
                    Assert.True(rectangle[3] > 0);
                });
                Assert.Empty(link.GetChildren());
                Assert.NotNull(link.GetEnclosingElement());
                var nativeText = FindLogical<RichTextBlock>(host!).First(block => CollectText(block).Contains("Opening", StringComparison.Ordinal));
                var nativePeer = FrameworkElementAutomationPeer.CreatePeerForElement(nativeText)!;
                Assert.Contains("Opening", provider.RangeFromChild(
                    new ProviderAccessPeer(nativeText).AsProvider(nativePeer)).GetText(-1));

                var expander = Assert.Single(FindLogical<Expander>(host!));
                Assert.NotNull(FrameworkElementAutomationPeer.CreatePeerForElement(expander)!.GetPattern(PatternInterface.ExpandCollapse));
                expander.IsExpanded = true;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                var hidden = provider.DocumentRange.FindText("Hidden evidence.", false, false);
                Assert.NotNull(hidden);
                hidden.Select();
                expander.IsExpanded = false;
                ui.Container.UpdateLayout();
                owner.RefreshDocument();
                Assert.DoesNotContain("Hidden evidence.", provider.DocumentRange.GetText(-1));
                Assert.DoesNotContain("Hidden evidence.", hidden.GetText(-1));
                Assert.InRange(owner.Document.End, 0, owner.Document.Text.Length);

                var code = provider.DocumentRange.FindText("code one\ncode two", false, false);
                code.Select();
                owner.ProcessKey(VirtualKey.Home, control: false, shift: false);
                owner.ProcessKey(VirtualKey.End, control: false, shift: true);
                Assert.Equal("code two", owner.Document.SelectedText);
                owner.ProcessKey(VirtualKey.Up, control: false, shift: false);
                Assert.InRange(owner.Document.Focus,
                    owner.Document.Text.IndexOf("code one", StringComparison.Ordinal),
                    owner.Document.Text.IndexOf("code two", StringComparison.Ordinal) - 1);

                owner.Update(_ => false);
                owner.SelectRange(0, 7);
                Assert.True(owner.CopySelection());
                Assert.Equal("Copy failed", AutomationProperties.GetItemStatus(owner));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(owner));
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                disposed = true;
                owner?.Dispose();
                if (surface is not null)
                    ui.Container.Children.Remove(surface);
                if (host is not null)
                {
                    surface!.Child = null;
                    host.Content = null;
                    host.Dispose();
                }
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task MessageBoundary_DragScrollAndRealizationReuse(string theme)
    {
        await ui.ResetContainerAsync();
        NativeChatMessageText? first = null;
        NativeChatMessageText? second = null;
        ScrollViewer? scroll = null;
        StackPanel? root = null;
        ChatThemeProofScope? resources = null;
        var scrolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"Line {index:D2} visible message evidence"));
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                first = new NativeChatMessageText { Content = new TextBlock { Text = text, IsTextSelectionEnabled = true }, Width = 500 };
                second = new NativeChatMessageText { Content = new TextBlock { Text = "Other message stays isolated.", IsTextSelectionEnabled = true }, Width = 500 };
                first.Update(_ => true);
                second.Update(_ => true);
                scroll = new ScrollViewer { Content = first, Height = 160, Width = 520 };
                scroll.ViewChanged += (_, _) =>
                {
                    if (scroll.VerticalOffset > 0)
                        scrolled.TrySetResult();
                };
                root = new StackPanel { RequestedTheme = resources.ElementTheme, Width = 540 };
                root.Children.Add(scroll);
                root.Children.Add(second);
                ui.Container.Children.Add(root);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.UpdateLayout();
                first!.RefreshDocument();
                second!.RefreshDocument();
                var provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(first)!.GetPattern(PatternInterface.Text));
                var other = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(second)!.GetPattern(PatternInterface.Text));
                Assert.Equal(text, provider.DocumentRange.GetText(-1));
                Assert.Throws<ArgumentException>(() => provider.DocumentRange.Compare(other.DocumentRange));
                Assert.Throws<ArgumentException>(() => provider.RangeFromChild(other.DocumentRange.GetEnclosingElement()));
                first.SelectRange(0, text.Length);
                Assert.False(second.Document.HasSelection);
                second.SelectRange(0, 5);
                Assert.Equal(text, first.Document.SelectedText);
                provider.DocumentRange.GetBoundingRectangles(out var rectangles);
                Assert.NotEmpty(rectangles);
                var viewportBounds = FrameworkElementAutomationPeer.CreatePeerForElement(scroll!)!.GetBoundingRectangle();
                Assert.All(rectangles.Chunk(4), rectangle =>
                    Assert.True(rectangle[1] + rectangle[3] <= viewportBounds.Bottom + 1));
                Assert.NotEmpty(provider.GetVisibleRanges());
                var start = PointForText(first, provider, "Line 00", false);
                Assert.True(first.BeginSelection(start));
                var bottom = scroll!.TransformToVisual(first).TransformPoint(new Point(20, scroll.ActualHeight + 30));
                first.ExtendSelection(bottom);
            });
            await scrolled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                first!.EndSelection();
                Assert.True(scroll!.VerticalOffset > 0);
                Assert.True(first.Document.End > 0);
                Assert.Equal("Other", second!.Document.SelectedText);
                scroll.Content = null;
                first.Dispose();
                Assert.Empty(first.Document.Text);
                var retired = first;
                var replacement = "Recycled message with new body.";
                retired.Update(_ => true);
                Assert.Empty(retired.Document.Text);
                first = new NativeChatMessageText
                {
                    Content = new TextBlock { Text = replacement, IsTextSelectionEnabled = true },
                    Width = 500,
                };
                first.Update(_ => true);
                first.Loaded += (_, _) => reused.TrySetResult();
                scroll.Content = first;
                scroll.ChangeView(null, 0, null, disableAnimation: true);
            });
            await ui.YieldToRenderAsync();
            await reused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                first!.Update(_ => true);
                ui.Container.UpdateLayout();
                Assert.True(first.IsLoaded);
                first.RefreshDocument();
                Assert.Equal("Recycled message with new body.", first.Document.Text);
                Assert.False(first.Document.HasSelection);
                first.ProcessKey(VirtualKey.A, control: true, shift: false);
                Assert.Equal("Recycled message with new body.", first.Document.SelectedText);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                first?.Dispose();
                second?.Dispose();
                if (root is not null)
                    ui.Container.Children.Remove(root);
                if (scroll is not null)
                    scroll.Content = null;
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task NativeUnits_RespectWrappingParagraphsAndTransientReload(string theme)
    {
        await ui.ResetContainerAsync();
        const string prose = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu";
        const string code = "code one\ncode two";
        NativeChatMessageText? owner = null;
        StackPanel? root = null;
        ChatThemeProofScope? resources = null;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                RichTextBlock Body(string text)
                {
                    var block = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
                    var paragraph = new Paragraph();
                    paragraph.Inlines.Add(new Run { Text = text });
                    block.Blocks.Add(paragraph);
                    return block;
                }
                var body = new StackPanel();
                body.Children.Add(Body(prose));
                body.Children.Add(Body(code));
                owner = new NativeChatMessageText { Content = body, Width = 140 };
                owner.Loaded += (_, _) => loaded.TrySetResult();
                owner.Unloaded += (_, _) => unloaded.TrySetResult();
                root = new StackPanel { RequestedTheme = resources.ElementTheme };
                root.Children.Add(owner);
                ui.Container.Children.Add(root);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var provider = Assert.IsAssignableFrom<ITextProvider>(
                    FrameworkElementAutomationPeer.CreatePeerForElement(owner!)!.GetPattern(PatternInterface.Text));
                Assert.Equal(prose + "\n\n" + code, owner!.Document.Text);
                var line = provider.DocumentRange.FindText("alpha", false, false);
                line.ExpandToEnclosingUnit(TextUnit.Line);
                Assert.StartsWith("alpha", line.GetText(-1));
                Assert.True(line.GetText(-1).Length < prose.Length);
                line.GetBoundingRectangles(out var firstRects);
                Assert.NotEmpty(firstRects);
                Assert.All(firstRects.Chunk(4), rectangle => Assert.Equal(firstRects[1], rectangle[1], precision: 1));
                Assert.Equal(1, line.Move(TextUnit.Line, 1));
                line.GetBoundingRectangles(out var secondRects);
                Assert.True(secondRects[1] > firstRects[1]);
                var paragraph = provider.DocumentRange.FindText("beta", false, false);
                paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                Assert.Equal(prose + "\n\n", paragraph.GetText(-1));
                Assert.Equal(1, paragraph.Move(TextUnit.Paragraph, 1));
                Assert.Equal(code, paragraph.GetText(-1));
                var codeParagraph = provider.DocumentRange.FindText("code two", false, false);
                codeParagraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                Assert.Equal(code, codeParagraph.GetText(-1));
                var format = provider.DocumentRange.FindText("beta", false, false);
                var word = format.Clone();
                format.ExpandToEnclosingUnit(TextUnit.Format);
                word.ExpandToEnclosingUnit(TextUnit.Word);
                Assert.Equal("beta ", format.GetText(-1));
                Assert.True(format.Compare(word));
                Assert.Equal(word.Move(TextUnit.Word, 1), format.Move(TextUnit.Format, 1));
                Assert.True(format.Compare(word));
                owner.SelectRange(0, 5);
                root!.Children.Remove(owner);
            });
            await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(owner!.Document.Text);
                loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                root!.Children.Add(owner);
            });
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.RunOnUIAsync(() =>
            {
                // No Update or Refresh: native Loaded must restore the same retained content.
                Assert.Equal(prose + "\n\n" + code, owner!.Document.Text);
                owner.ProcessKey(VirtualKey.A, control: true, shift: false);
                Assert.Equal(owner.Document.Text, owner.Document.SelectedText);
                owner.Dispose();
                owner.Update(_ => true);
                Assert.Empty(owner.Document.Text);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                owner?.Dispose();
                if (root is not null)
                    ui.Container.Children.Remove(root);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => resources?.Dispose());
        }
    }

    private static Point PointForText(NativeChatMessageText owner, ITextProvider provider, string text, bool trailing)
    {
        var range = provider.DocumentRange.FindText(text, backward: false, ignoreCase: false);
        Assert.NotNull(range);
        range.GetBoundingRectangles(out var rectangles);
        Assert.NotEmpty(rectangles);
        var index = trailing ? rectangles.Length - 4 : 0;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(owner)!;
        var bounds = peer.GetBoundingRectangle();
        var scale = owner.XamlRoot.RasterizationScale;
        return new Point(
            (rectangles[index] - bounds.X + (trailing ? rectangles[index + 2] - 0.1 : 0.1)) / scale,
            (rectangles[index + 1] - bounds.Y + rectangles[index + 3] / 2) / scale);
    }

    private static string CollectText(RichTextBlock text) =>
        string.Concat(text.Blocks.OfType<Paragraph>().SelectMany(paragraph =>
            paragraph.Inlines.OfType<Run>().Select(run => run.Text)));

    private sealed class ProviderAccessPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        internal IRawElementProviderSimple AsProvider(AutomationPeer peer) => ProviderFromPeer(peer);
    }
}
