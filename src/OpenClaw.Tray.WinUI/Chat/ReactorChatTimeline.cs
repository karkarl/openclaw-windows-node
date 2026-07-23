using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

public enum ReactorChatTimelineMode
{
    Timeline,
    Loading,
    Empty,
}

public sealed record ReactorChatTimelineProps(
    ReactorChatTimelineMode Mode,
    OpenClawChatTimelineProps Timeline,
    Action<string>? OnSuggestionPicked = null,
    bool SuggestionsDisabled = false);

/// <summary>
/// Reactor-owned production timeline. Reactor's keyed ItemsView handles row
/// reconciliation, container realization, scrolling, and virtualization.
/// </summary>
public sealed class ReactorChatTimeline : Component<ReactorChatTimelineProps>
{
    public override Element Render()
    {
        var props = Props;
        var (speakingEntryId, setSpeakingEntryId) = UseState<string?>(null, threadSafe: true);
        var speechOperation = UseRef(0);
        var mounted = UseRef(true);
        var annotatedScrollBarRef = UseRef(new ElementRef()).Current;

        UseEffect((Func<Action>)(() =>
        {
            mounted.Current = true;
            return () =>
            {
                mounted.Current = false;
                speechOperation.Current++;
            };
        }), Array.Empty<object>());

        async Task ToggleSpeechAsync(ChatTimelineItem entry)
        {
            var text = entry.Text ?? string.Empty;
            if (text.Length == 0)
                return;

            if (string.Equals(speakingEntryId, entry.Id, StringComparison.Ordinal))
            {
                speechOperation.Current++;
                setSpeakingEntryId(null);
                props.Timeline.OnStopSpeaking?.Invoke();
                return;
            }

            if (props.Timeline.OnReadAloud is not { } readAloud)
                return;

            var operation = ++speechOperation.Current;
            setSpeakingEntryId(entry.Id);
            try
            {
                await readAloud(StripMarkdownForSpeech(text));
            }
            catch (Exception ex)
            {
                OpenClawTray.Services.Logger.Debug($"Reactor chat timeline: read aloud failed: {ex.Message}");
            }
            finally
            {
                if (mounted.Current && speechOperation.Current == operation)
                    setSpeakingEntryId(null);
            }
        }

        var rows = BuildRows(props);

        var itemsView = ItemsView(
            rows,
            static row => row.Key,
            (row, _) => ItemContainer(BuildRow(row, speakingEntryId, ToggleSpeechAsync))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .HAlign(HorizontalAlignment.Stretch)
                .IsTabStop(false)
                .WithKey(row.Key)) with
        {
            LayoutKind = ItemsViewLayoutKind.StackLayout,
            SelectionMode = ItemsViewSelectionMode.None,
            IsItemInvokedEnabled = false,
        };
        // AnnotatedScrollBar labels require absolute content offsets. ItemsView
        // virtualizes variable-height chat rows without exposing those offsets.
        // Keep landmark labels unset rather than estimating unrealized rows.
        return Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Star()],
            AnnotatedScrollBar()
                .Ref(annotatedScrollBarRef)
                .Width(32)
                .Grid(column: 1)
                .AutomationName("Chat message navigation"),
            itemsView
                .Grid(column: 0)
                .AutomationName("Chat messages")
                .Set(nativeItemsView =>
                {
                    if (annotatedScrollBarRef.Current is AnnotatedScrollBar scrollBar
                        && !ReferenceEquals(
                            nativeItemsView.VerticalScrollController,
                            scrollBar.ScrollController))
                    {
                        nativeItemsView.VerticalScrollController = scrollBar.ScrollController;
                    }
                }))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    public static string RowKey(OpenClawChatTimelineProps props, ChatTimelineItem entry) =>
        $"thread:{props.SessionId ?? "none"}|generation:{props.TimelineGeneration}|kind:{entry.Kind}|id:{entry.Id}";

    public static string SyntheticRowKey(OpenClawChatTimelineProps props, string id, ChatTimelineItemKind kind) =>
        $"thread:{props.SessionId ?? "none"}|generation:{props.TimelineGeneration}|kind:{kind}|synthetic:{id}";

    private static IReadOnlyList<ReactorTimelineRow> BuildRows(ReactorChatTimelineProps props)
    {
        if (props.Mode == ReactorChatTimelineMode.Loading)
            return [ReactorTimelineRow.Loading(props)];

        if (props.Mode == ReactorChatTimelineMode.Empty)
            return [ReactorTimelineRow.Empty(props)];

        var rows = new List<ReactorTimelineRow>(props.Timeline.Entries.Count + 2);
        if (props.Timeline.HasMoreHistory)
            rows.Add(ReactorTimelineRow.LoadEarlier(props));

        var orderedEntries = OrderEntriesForPresentation(props.Timeline.Entries);
        var latestAssistantEntryId = orderedEntries
            .LastOrDefault(static entry => entry.Kind == ChatTimelineItemKind.Assistant)
            ?.Id;

        foreach (var entry in orderedEntries)
        {
            if (entry.Kind == ChatTimelineItemKind.ToolCall && !props.Timeline.ShowToolCalls)
                continue;

            rows.Add(ReactorTimelineRow.FromEntry(
                props,
                entry,
                string.Equals(entry.Id, latestAssistantEntryId, StringComparison.Ordinal)));
        }

        if (props.Timeline.ShowThinkingIndicator)
            rows.Add(ReactorTimelineRow.Thinking(props));

        return rows;
    }

    private static IReadOnlyList<ChatTimelineItem> OrderEntriesForPresentation(
        IReadOnlyList<ChatTimelineItem> entries)
    {
        var ordered = new List<ChatTimelineItem>(entries.Count);
        var turnStart = 0;

        static bool IsDeniedPermission(ChatTimelineItem entry) =>
            entry.Kind == ChatTimelineItemKind.PermissionRequest
            && entry.PermissionDecision == ChatPermissionDecision.Denied;

        void AppendTurn(int endExclusive)
        {
            var hasToolFailure = false;
            for (var index = turnStart; index < endExclusive; index++)
            {
                if (entries[index].Kind == ChatTimelineItemKind.ToolCall
                    && entries[index].ToolResult == ChatToolCallStatus.Error)
                {
                    hasToolFailure = true;
                    break;
                }
            }

            if (hasToolFailure)
            {
                for (var index = turnStart; index < endExclusive; index++)
                    ordered.Add(entries[index]);
                return;
            }

            for (var index = turnStart; index < endExclusive; index++)
            {
                if (entries[index].Kind != ChatTimelineItemKind.ToolCall
                    && !IsDeniedPermission(entries[index]))
                {
                    ordered.Add(entries[index]);
                }
            }

            for (var index = turnStart; index < endExclusive; index++)
            {
                if (entries[index].Kind == ChatTimelineItemKind.ToolCall)
                    ordered.Add(entries[index]);
            }

            for (var index = turnStart; index < endExclusive; index++)
            {
                if (IsDeniedPermission(entries[index]))
                    ordered.Add(entries[index]);
            }
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Kind == ChatTimelineItemKind.User && index > turnStart)
            {
                AppendTurn(index);
                turnStart = index;
            }
        }

        AppendTurn(entries.Count);
        return ordered;
    }

    private static Element BuildRow(
        ReactorTimelineRow row,
        string? speakingEntryId,
        Func<ChatTimelineItem, Task> toggleSpeechAsync) => row.Kind switch
    {
        ReactorTimelineRowKind.Loading => BuildLoading(),
        ReactorTimelineRowKind.Empty => BuildEmpty(row),
        ReactorTimelineRowKind.LoadEarlier => BuildLoadEarlier(row),
        ReactorTimelineRowKind.Thinking => BuildThinking(row),
        _ when row.Entry is { } entry => BuildEntry(row, entry, speakingEntryId, toggleSpeechAsync),
        _ => Empty(),
    };

    private static Element BuildLoading()
    {
        var placeholders = new[] { 260d, 180d, 320d, 140d }
            .Select(width => Border(Empty())
                .Width(width)
                .Height(32)
                .CornerRadius(12)
                .Background(BrushFor(
                    "SubtleFillColorSecondaryBrush",
                    Color.FromArgb(0x38, 0x80, 0x80, 0x80)))
                .HAlign(width is 180d or 140d
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left))
            .Cast<Element>()
            .ToArray();

        return VStack(12, placeholders)
            .Margin(52, 24, 52, 24)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static Element BuildEmpty(ReactorTimelineRow row)
    {
        var children = new List<Element>
        {
            Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                .Size(64, 64)
                .HAlign(HorizontalAlignment.Center),
            Text(
                    LocalizedOrDefault("Chat_ZeroState_WelcomeTitle", "Welcome to OpenClaw"),
                    24,
                    FontWeights.SemiBold)
                .HAlign(HorizontalAlignment.Center),
            Text(
                    LocalizedOrDefault("Chat_ZeroState_WelcomeSubtitle", "How can I help you today?"),
                    14,
                    FontWeights.Normal,
                    "TextFillColorSecondaryBrush")
                .HAlign(HorizontalAlignment.Center),
        };

        foreach (var suggestion in new[]
        {
            "Say hi 👋",
            "What can you do?",
            "Give me a quick tour of OpenClaw",
        })
        {
            children.Add(Button(suggestion, () => row.Props.OnSuggestionPicked?.Invoke(suggestion))
                .IsEnabled(!row.Props.SuggestionsDisabled)
                .HAlign(HorizontalAlignment.Stretch)
                .AutomationName(suggestion));
        }

        return VStack(12, children.ToArray())
            .Margin(24, 52, 24, 24)
            .MaxWidth(520)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
    }

    private static Element BuildLoadEarlier(ReactorTimelineRow row)
    {
        var label = LocalizedOrDefault("Chat_Timeline_LoadEarlier", "Load earlier messages");
        return Button(label, () => row.Props.Timeline.OnLoadMoreHistory?.Invoke())
            .Margin(0, 8)
            .HAlign(HorizontalAlignment.Center)
            .AutomationName(label);
    }

    private static Element BuildThinking(ReactorTimelineRow row)
    {
        var format = LocalizedOrDefault("Chat_Timeline_AssistantThinkingFormat", "{0} is thinking…");
        return Text(
                string.Format(format, row.Props.Timeline.AssistantSenderLabel),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .Set(text => text.FontStyle = global::Windows.UI.Text.FontStyle.Italic)
            .Margin(64, 8, 24, 8);
    }

    private static Element BuildEntry(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        string? speakingEntryId,
        Func<ChatTimelineItem, Task> toggleSpeechAsync) => entry.Kind switch
    {
        ChatTimelineItemKind.User => BuildUser(row, entry),
        ChatTimelineItemKind.Assistant => BuildAssistant(
            row,
            entry,
            string.Equals(speakingEntryId, entry.Id, StringComparison.Ordinal),
            toggleSpeechAsync),
        ChatTimelineItemKind.ToolCall => BuildTool(row, entry),
        ChatTimelineItemKind.Reasoning => BuildReasoning(entry),
        ChatTimelineItemKind.PermissionRequest => BuildPermission(row, entry),
        ChatTimelineItemKind.Status => BuildStatus(entry),
        _ => BuildStatus(entry),
    };

    private static Element BuildUser(ReactorTimelineRow row, ChatTimelineItem entry)
    {
        var (messageText, attachments) = ParseAttachments(entry.Text);
        var content = attachments.Select(BuildAttachment).ToList();
        if (messageText.Length > 0)
        {
            content.Add(Text(
                    messageText,
                    14,
                    FontWeights.Normal,
                    "TextOnAccentFillColorPrimaryBrush")
                .Set(text => text.IsTextSelectionEnabled = true));
        }

        var bubble = Border(VStack(8, content.ToArray()))
            .Background(BrushFor(
                "AccentFillColorSecondaryBrush",
                Color.FromArgb(0xFF, 0x4C, 0x66, 0xCC)))
            .CornerRadius(16)
            .Padding(16, 12)
            .MaxWidth(720)
            .HAlign(HorizontalAlignment.Right);

        return VStack(
                bubble,
                Footer(row, entry, HorizontalAlignment.Right),
                CopyAction(entry.Text))
            .Margin(72, 4, 20, 4)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(entry.Text ?? string.Empty);
    }

    private static Element BuildAssistant(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        Func<ChatTimelineItem, Task> toggleSpeechAsync)
    {
        var message = BuildSafeMarkdown(entry.Text);

        var bubble = Border(message)
            .Background(BrushFor(
                "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(16)
            .Padding(16, 12)
            .MaxWidth(720)
            .HAlign(HorizontalAlignment.Left);

        return VStack(
                bubble,
                BuildAssistantFooter(row, entry, isSpeaking, toggleSpeechAsync))
            .Margin(52, 4, 72, 4)
            .HAlign(HorizontalAlignment.Stretch)
            .AutomationName(entry.Text ?? string.Empty);
    }

    private static Element BuildSafeMarkdown(string? text)
    {
        var options = new MarkdownOptions
        {
            ParserFlags = MarkdownParserFlags.DialectCommonMark | MarkdownParserFlags.NoHtml,
            Image = (alt, _) => Text(
                    string.IsNullOrWhiteSpace(alt) ? "[Image]" : $"[Image: {alt}]",
                    14,
                    FontWeights.Normal,
                    "TextFillColorPrimaryBrush")
                .Set(value => value.IsTextSelectionEnabled = true),
            LinkBuilder = (children, _) => HStack(children),
            HtmlBlock = raw => Text(
                    ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(raw),
                    14,
                    FontWeights.Normal,
                    "TextFillColorPrimaryBrush")
                .Set(value => value.IsTextSelectionEnabled = true),
        };

        return Factories.Markdown(ChatMarkdownSanitizer.Sanitize(text), options);
    }

    private static Element BuildAssistantFooter(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        bool isSpeaking,
        Func<ChatTimelineItem, Task> toggleSpeechAsync)
    {
        var children = new List<Element>
        {
            Footer(row, entry, HorizontalAlignment.Left),
            CopyAction(entry.Text),
        };

        if (row.Props.Timeline.OnReadAloud is not null || row.Props.Timeline.OnStopSpeaking is not null)
        {
            var label = isSpeaking
                ? LocalizedOrDefault("Chat_Assistant_Action_Stop", "Stop")
                : LocalizedOrDefault("Chat_Assistant_Action_ReadAloud", "Read aloud");
            children.Add(Button(label, () => _ = toggleSpeechAsync(entry))
                .Padding(6, 2)
                .MinWidth(0)
                .AutomationName(label)
                .ToolTip(label));
        }

        return HStack(8, children.ToArray())
            .Margin(16, 2, 16, 0)
            .HAlign(HorizontalAlignment.Left);
    }

    private static Element CopyAction(string? text)
    {
        var label = LocalizedOrDefault("Chat_Assistant_Action_Copy", "Copy");
        return Button(label, () => ClipboardHelper.CopyText(text ?? string.Empty, flush: true))
            .Padding(6, 2)
            .MinWidth(0)
            .AutomationName(label)
            .ToolTip(label);
    }

    private static (string Message, IReadOnlyList<ChatAttachmentPreview> Attachments) ParseAttachments(string? text)
    {
        const string imagePrefix = "\u200B🖼️ ";
        const string filePrefix = "\u200B📎 ";
        var messageLines = new List<string>();
        var attachments = new List<ChatAttachmentPreview>();

        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(imagePrefix, StringComparison.Ordinal))
            {
                var name = trimmed[imagePrefix.Length..].Trim();
                if (name.Length > 0)
                    attachments.Add(new ChatAttachmentPreview(name, true));
            }
            else if (trimmed.StartsWith(filePrefix, StringComparison.Ordinal))
            {
                var name = trimmed[filePrefix.Length..].Trim();
                if (name.Length > 0)
                    attachments.Add(new ChatAttachmentPreview(name, false));
            }
            else
            {
                messageLines.Add(line);
            }
        }

        return (string.Join('\n', messageLines).Trim(), attachments);
    }

    private static Element BuildAttachment(ChatAttachmentPreview attachment)
    {
        if (attachment.IsImage
            && OpenClawChatDataProvider.ImagePreviewCache.TryGetValue(attachment.Name, out var bytes)
            && TryDecodeAttachmentBitmap(bytes) is { } bitmap)
        {
            const double maxWidth = 280;
            const double maxHeight = 200;
            var pixelWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : (int)maxWidth;
            var pixelHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : (int)maxHeight;
            var scale = Math.Min(Math.Min(maxWidth / pixelWidth, maxHeight / pixelHeight), 1.0);
            return Border(Empty())
                .Set(border => border.Background = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.UniformToFill,
                })
                .Size(pixelWidth * scale, pixelHeight * scale)
                .CornerRadius(8)
                .HAlign(HorizontalAlignment.Right)
                .AutomationName(attachment.Name);
        }

        var glyph = Text(
                attachment.IsImage ? "\uEB9F" : "\uE8A5",
                16,
                FontWeights.Normal,
                "TextOnAccentFillColorPrimaryBrush")
            .Set(text => text.FontFamily = FluentIconCatalog.SymbolThemeFontFamily)
            .Center();
        var glyphBackground = Border(glyph)
            .Size(32, 32)
            .CornerRadius(6)
            .Background(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        var name = Text(
                attachment.Name,
                13,
                FontWeights.Normal,
                "TextOnAccentFillColorPrimaryBrush")
            .Set(text =>
            {
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
            })
            .MaxWidth(240)
            .VAlign(VerticalAlignment.Center);

        return Border(HStack(8, glyphBackground, name))
            .Padding(8, 6, 12, 6)
            .CornerRadius(6)
            .BorderThickness(1)
            .BorderBrush(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)))
            .Background(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)))
            .AutomationName(attachment.Name);
    }

    private static BitmapImage? TryDecodeAttachmentBitmap(byte[] bytes)
    {
        if (s_attachmentBitmaps.TryGetValue(bytes, out var existing))
            return existing;

        try
        {
            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            bitmap.SetSource(stream);
            s_attachmentBitmaps.Add(bytes, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Element BuildTool(ReactorTimelineRow row, ChatTimelineItem entry)
    {
        var details = new List<Element>();
        if (!string.IsNullOrWhiteSpace(entry.Text))
            details.Add(Text(entry.Text, 12, FontWeights.Normal, "TextFillColorSecondaryBrush"));

        if (!string.IsNullOrWhiteSpace(entry.ToolOutput))
        {
            var output = Text(
                    entry.ToolOutput,
                    12,
                    FontWeights.Normal,
                    "TextFillColorSecondaryBrush")
                .Set(text =>
                {
                    text.FontFamily = new FontFamily("Cascadia Code, Consolas");
                    text.IsTextSelectionEnabled = true;
                });
            details.Add(ScrollViewer(output)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                })
                .MaxHeight(240));
        }

        var expander = Expander(
                $"{entry.ToolName ?? "Tool"} · {entry.ToolResult}",
                VStack(6, details.ToArray()))
            .Set(control =>
            {
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            })
            .AutomationName($"Tool call {entry.ToolName ?? "tool"}")
            .WithKey($"tool-expander:{entry.Id}:collapse:{row.Props.Timeline.ToolCallsCollapseVersion}");

        return Border(expander)
            .Margin(68, 4, 40, 4)
            .Padding(12, 8)
            .Background(BrushFor(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(12);
    }

    private static Element BuildReasoning(ChatTimelineItem entry)
    {
        var content = Text(
                entry.Text ?? string.Empty,
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .Set(text => text.IsTextSelectionEnabled = true);
        return Expander(
                LocalizedOrDefault("Chat_Reasoning_ThinkingHeader", "Thinking"),
                content)
            .Set(control =>
            {
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            })
            .Margin(52, 4);
    }

    private static Element BuildPermission(ReactorTimelineRow row, ChatTimelineItem entry)
    {
        var children = new List<Element>
        {
            Text(
                string.IsNullOrWhiteSpace(entry.IntentSummary)
                    ? LocalizedOrDefault("Chat_Permission_Title", "Permission requested")
                    : entry.IntentSummary,
                14,
                FontWeights.SemiBold),
        };
        var detail = entry.Text ?? string.Empty;

        if (entry.PermissionDecision == ChatPermissionDecision.Pending)
        {
            children.Add(Text(
                LocalizedOrDefault(
                   "Chat_Permission_Subtitle",
                   "Review the requested operation before allowing it."),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
            AddPermissionDetails(children, detail);

            var requestId = entry.PermissionRequestId ?? string.Empty;
            var actions = ChatPermissionActionKeys.NormalizeActions(entry.PermissionActions)
                .Select(actionKey =>
                {
                    var label = PermissionActionLabel(actionKey);
                    return (Element)Button(
                            label,
                            () => row.Props.Timeline.OnPermissionResponse?.Invoke(requestId, actionKey))
                        .IsEnabled(row.Props.Timeline.OnPermissionResponse is not null && requestId.Length > 0)
                        .AutomationName(label);
                })
                .ToArray();
            children.Add(HStack(8, actions));
            children.Add(Text(
                LocalizedOrDefault(
                    "Chat_Permission_Caption",
                    "Only allow operations you trust."),
                11,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
        }
        else
        {
            children.Add(Text(
                PermissionDecisionLabel(entry.PermissionDecision),
                12,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush"));
            AddPermissionDetails(children, detail);
        }

        return Border(VStack(8, children.ToArray()))
            .Margin(52, 8)
            .Padding(16)
            .Background(BrushFor(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(12);
    }

    private static void AddPermissionDetails(List<Element> children, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return;

        children.Add(Border(Text(
                detail,
                12,
                FontWeights.Normal,
                "TextFillColorPrimaryBrush")
            .Set(text =>
            {
                text.FontFamily = new FontFamily("Cascadia Code, Consolas");
                text.IsTextSelectionEnabled = true;
            }))
            .Padding(10, 8)
            .CornerRadius(6)
            .Background(BrushFor(
                "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1));
    }

    private static Element BuildStatus(ChatTimelineItem entry)
    {
        var isError = entry.Tone == ChatTone.Error;
        return Border(Text(
                entry.Text ?? string.Empty,
                12,
                FontWeights.Normal,
                isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush")
            .Set(text => text.TextAlignment = TextAlignment.Center))
            .Margin(40, 4)
            .Padding(10, 4)
            .HAlign(HorizontalAlignment.Center)
            .CornerRadius(12)
            .Background(BrushFor(
                isError
                    ? "SystemFillColorCriticalBackgroundBrush"
                    : "SubtleFillColorTertiaryBrush",
                Color.FromArgb(
                    isError ? (byte)0x2E : (byte)0x24,
                    isError ? (byte)0xC8 : (byte)0x80,
                    isError ? (byte)0x32 : (byte)0x80,
                    isError ? (byte)0x32 : (byte)0x80)));
    }

    private static Element Footer(
        ReactorTimelineRow row,
        ChatTimelineItem entry,
        HorizontalAlignment horizontalAlignment)
    {
        ChatEntryMetadata? metadata = null;
        if (row.Props.Timeline.EntryMetadata?.TryGetValue(entry.Id, out var resolvedMetadata) == true)
            metadata = resolvedMetadata;

        var time = metadata?.Timestamp?.ToLocalTime().ToString("h:mm tt");
        var model = metadata?.Model ?? row.Props.Timeline.DefaultModel;
        var usageSummary = row.IsLatestAssistant
            && row.Props.Timeline.ShowToolCalls
            ? row.Props.Timeline.DefaultUsageSummary
            : null;
        return Text(
                string.Join(
                    " · ",
                    new[] { time, model, usageSummary }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                11,
                FontWeights.Normal,
                "TextFillColorSecondaryBrush")
            .Margin(16, 2, 16, 0)
            .HAlign(horizontalAlignment);
    }

    private static TextBlockElement Text(
        string text,
        double fontSize = 14,
        global::Windows.UI.Text.FontWeight? weight = null,
        string foregroundResource = "TextFillColorPrimaryBrush") =>
        TextBlock(text)
            .Set(control => control.TextWrapping = TextWrapping.Wrap)
            .FontSize(fontSize)
            .FontWeight(weight ?? FontWeights.Normal)
            .Foreground(BrushFor(foregroundResource, Microsoft.UI.Colors.Black));

    private static string PermissionActionLabel(string action) =>
        string.Equals(action, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase)
            ? LocalizedOrDefault("Chat_Permission_Allow", "Allow")
            : string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase)
                ? LocalizedOrDefault("Chat_Permission_AllowAlways", "Always allow")
                : string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
                    ? LocalizedOrDefault("Chat_Permission_Deny", "Deny")
                    : action;

    private static string PermissionDecisionLabel(ChatPermissionDecision decision) => decision switch
    {
        ChatPermissionDecision.Allowed => LocalizedOrDefault("Chat_Permission_DecisionAllowed", "Allowed"),
        ChatPermissionDecision.AllowedAlways => LocalizedOrDefault("Chat_Permission_DecisionAlwaysAllowed", "Always allowed"),
        ChatPermissionDecision.Denied => LocalizedOrDefault("Chat_Permission_DecisionDenied", "Denied"),
        _ => LocalizedOrDefault("Chat_Permission_DecisionExpired", "Expired"),
    };

    private static string LocalizedOrDefault(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static Brush BrushFor(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private static string StripMarkdownForSpeech(string text)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " code block ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"`([^`]+)`", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"!\[[^\]]*\]\([^)]*\)", " image ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\[([^\]]+)\]\([^)]*\)", "$1");
        return System.Text.RegularExpressions.Regex.Replace(result, @"[*_#>]+", " ");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], BitmapImage>
        s_attachmentBitmaps = new();

    private sealed record ChatAttachmentPreview(string Name, bool IsImage);
}

internal sealed record ReactorTimelineRow(
    string Key,
    ReactorTimelineRowKind Kind,
    ReactorChatTimelineProps Props,
    ChatTimelineItem? Entry,
    bool IsLatestAssistant = false)
{
    public static ReactorTimelineRow FromEntry(
        ReactorChatTimelineProps props,
        ChatTimelineItem entry,
        bool isLatestAssistant) =>
        new(
            ReactorChatTimeline.RowKey(props.Timeline, entry),
            ReactorTimelineRowKind.Entry,
            props,
            entry,
            isLatestAssistant);

    public static ReactorTimelineRow Thinking(ReactorChatTimelineProps props) =>
        new(
            ReactorChatTimeline.SyntheticRowKey(
                props.Timeline,
                "__thinking__",
                ChatTimelineItemKind.Assistant),
            ReactorTimelineRowKind.Thinking,
            props,
            null);

    public static ReactorTimelineRow LoadEarlier(ReactorChatTimelineProps props) =>
        new(
            ReactorChatTimeline.SyntheticRowKey(
                props.Timeline,
                "__load-earlier__",
                ChatTimelineItemKind.Status),
            ReactorTimelineRowKind.LoadEarlier,
            props,
            null);

    public static ReactorTimelineRow Loading(ReactorChatTimelineProps props) =>
        new("timeline:loading", ReactorTimelineRowKind.Loading, props, null);

    public static ReactorTimelineRow Empty(ReactorChatTimelineProps props) =>
        new("timeline:empty", ReactorTimelineRowKind.Empty, props, null);
}

internal enum ReactorTimelineRowKind
{
    Entry,
    Thinking,
    LoadEarlier,
    Loading,
    Empty,
}
