using ChatSample.Chat.Model;
using ChatSample.Chat.UI;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;
using static Microsoft.UI.Reactor.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
/// Extension of <see cref="ChatTimelineProps"/> with OpenClaw-specific
/// per-entry metadata (<see cref="ChatEntryMetadata"/>) and sender/model
/// labels used in the per-message footer rendering. Created by
/// <c>OpenClawChatRoot</c>.
/// </summary>
/// <param name="EntryMetadata">
/// Optional per-entry metadata snapshot keyed by <c>ChatTimelineItem.Id</c>.
/// Renderer falls back to defaults when an entry isn't present.
/// </param>
/// <param name="UserSenderLabel">Sender label shown below user bubbles.</param>
/// <param name="AssistantSenderLabel">Sender label shown below assistant cards.</param>
/// <param name="DefaultModel">Fallback model name when an entry's metadata doesn't carry one.</param>
/// <param name="ShowThinkingIndicator">
/// When true, renders an inline "<c>&lt;agent&gt; is thinking…</c>" placeholder
/// at the bottom of the timeline. Used by callers to bridge the gap between
/// turn-start and the first assistant delta arriving.
/// </param>
public record OpenClawChatTimelineProps(
    string? SessionId,
    IReadOnlyList<ChatTimelineItem> Entries,
    bool HasMoreHistory,
    Action? OnLoadMoreHistory,
    IReadOnlyDictionary<string, ChatEntryMetadata>? EntryMetadata = null,
    string UserSenderLabel = "OpenClaw Windows Tray (cli)",
    string AssistantSenderLabel = "Field",
    string? DefaultModel = null,
    bool ShowThinkingIndicator = false);

/// <summary>
/// OpenClaw-skinned variant of <see cref="ChatTimeline"/> from the vendored
/// chat sample. Reuses the same scroll/follow/load-more behavior but renames
/// the per-entry rendering to better match the web Control UI:
///
/// <list type="bullet">
///   <item>User messages: right-aligned pink bubble with avatar glyph and a
///         "<c>&lt;sender&gt; · &lt;time&gt;</c>" footer.</item>
///   <item>Assistant messages: left-aligned subtle card with ★ avatar glyph
///         and a "<c>&lt;agent&gt; · &lt;time&gt; · &lt;model&gt;</c>" footer.</item>
///   <item>Tool calls: prominent compact rounded card matching the web's
///         "Tool call exec" affordance, with a small footer for time.</item>
///   <item>Reasoning / status entries: muted styling as in upstream.</item>
/// </list>
/// </summary>
public class OpenClawChatTimeline : Component<OpenClawChatTimelineProps>
{
    const double FollowThreshold = 60;

    static readonly Microsoft.UI.Reactor.Markdown.MarkdownOptions _markdownOptions = new()
    {
        CodeFontFamily = "Cascadia Code, Cascadia Mono, Consolas",
        CodeBlock = (code, lang) =>
        {
            var header = lang is { Length: > 0 }
                ? (Element)Caption(lang).Foreground(Theme.TertiaryText).Padding(12, 6, 12, 0)
                : Empty();
            return Border(
                VStack(0,
                    header,
                    TextBlock(code)
                        .Set(t =>
                        {
                            t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                            t.FontSize = 13;
                            t.TextWrapping = TextWrapping.Wrap;
                            t.IsTextSelectionEnabled = true;
                        })
                        .Foreground(Theme.PrimaryText)
                        .Padding(12, 8, 12, 12)
                )
            ).Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
             .WithBorder(Theme.DividerStroke, 1)
             .CornerRadius(8).Margin(0, 4, 0, 4);
        },
        Table = (rows, aligns) =>
        {
            // Simple bordered table
            return Border(
                VStack(0, rows)
            ).WithBorder(Theme.DividerStroke, 1)
             .CornerRadius(4).Margin(0, 4, 0, 4);
        },
    };

    static string FormatToolLabel(ChatTimelineItem e)
    {
        var text = e.Text ?? "";
        return e.ToolName switch
        {
            "bash" or "powershell" => $"$ {text}",
            "read" or "view" => text,
            "edit" or "create" => text,
            "grep" => $"🔍 {text}",
            "glob" => $"📂 {text}",
            "web_fetch" => $"🌐 {text}",
            "web_search" => $"🔎 {text}",
            "task" => text,
            "report_intent" => text,
            _ => text == e.ToolName || string.IsNullOrEmpty(text) ? e.ToolName ?? "tool" : $"{e.ToolName}: {text}"
        };
    }

    static bool ContainsEntryId(IReadOnlyList<ChatTimelineItem> entries, string id)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == id)
                return true;
        }

        return false;
    }

    static double ClampOffset(double offset, double max) =>
        Math.Max(0, Math.Min(offset, max));

    public override Element Render()
    {
        var scrollViewRef = UseRef<Microsoft.UI.Xaml.Controls.ScrollViewer?>(null);
        var isFollowingRef = UseRef(true);
        var contentRef = UseRef<Microsoft.UI.Xaml.Controls.StackPanel?>(null);
        var prevEntryCountRef = UseRef(0);
        var prevSessionIdRef = UseRef<string?>(null);
        var prevFirstEntryIdRef = UseRef<string?>(null);
        var prevLastEntryIdRef = UseRef<string?>(null);
        var lastVerticalOffsetRef = UseRef(0.0);
        var lastScrollableHeightRef = UseRef(0.0);
        var suppressAutoFollowRef = UseRef(false);
        var sessionOffsetsRef = UseRef<Dictionary<string, double>>(new());
        var hasMoreHistoryRef = UseRef(Props.HasMoreHistory);
        var loadMoreHistoryRef = UseRef<Action?>(Props.OnLoadMoreHistory);
        var loadMoreRequestedForCountRef = UseRef(-1);

        // Per-entry expand state for tool chips. Tokens are
        // "{entryId}:call" and "{entryId}:out" so call and output
        // toggle independently. HashSet so the empty default is "all
        // collapsed" — matches the web's default-collapsed look.
        var expandedToolChips = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        hasMoreHistoryRef.Current = Props.HasMoreHistory;
        loadMoreHistoryRef.Current = Props.OnLoadMoreHistory;

        var entryCount = Props.Entries.Count;
        var firstEntryId = entryCount > 0 ? Props.Entries[0].Id : null;
        var lastEntryId = entryCount > 0 ? Props.Entries[entryCount - 1].Id : null;
        var previousSessionId = prevSessionIdRef.Current;
        var previousEntryCount = prevEntryCountRef.Current;
        var previousFirstEntryId = prevFirstEntryIdRef.Current;
        var previousLastEntryId = prevLastEntryIdRef.Current;
        var sessionChanged = Props.SessionId != previousSessionId;
        var initialLoad = !sessionChanged && previousEntryCount == 0 && entryCount > 0;
        var prependedHistory = !sessionChanged
            && previousEntryCount > 0
            && entryCount > previousEntryCount
            && previousFirstEntryId is not null
            && firstEntryId != previousFirstEntryId
            && lastEntryId == previousLastEntryId
            && ContainsEntryId(Props.Entries, previousFirstEntryId);
        var appendedEntries = !sessionChanged
            && entryCount > previousEntryCount
            && !prependedHistory;

        void StoreSessionOffset(string? sessionId, double offset)
        {
            if (sessionId is { Length: > 0 })
                sessionOffsetsRef.Current[sessionId] = offset;
        }

        void UpdateScrollMetrics(Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            lastVerticalOffsetRef.Current = sv.VerticalOffset;
            lastScrollableHeightRef.Current = sv.ScrollableHeight;
            isFollowingRef.Current = sv.ScrollableHeight - sv.VerticalOffset <= FollowThreshold;
            StoreSessionOffset(prevSessionIdRef.Current, sv.VerticalOffset);
        }

        void QueueScrollToOffset(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, double targetOffset, bool disableAnimation, bool suppressAutoFollow)
        {
            suppressAutoFollowRef.Current = suppressAutoFollow;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var target = ClampOffset(targetOffset, sv.ScrollableHeight);
                sv.ChangeView(null, target, null, disableAnimation);
                lastVerticalOffsetRef.Current = target;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = sv.ScrollableHeight - target <= FollowThreshold;
                StoreSessionOffset(sessionId, target);

                if (suppressAutoFollow)
                    sv.DispatcherQueue.TryEnqueue(() => suppressAutoFollowRef.Current = false);
            });
        }

        void QueueScrollToBottom(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, bool disableAnimation)
        {
            isFollowingRef.Current = true;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var bottom = sv.ScrollableHeight;
                sv.ChangeView(null, bottom, null, disableAnimation);
                lastVerticalOffsetRef.Current = bottom;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = true;
                StoreSessionOffset(sessionId, bottom);
            });
        }

        void QueuePreservePrependOffset(Microsoft.UI.Xaml.Controls.ScrollViewer sv, string? sessionId, double oldOffset, double oldScrollableHeight)
        {
            suppressAutoFollowRef.Current = true;
            sv.DispatcherQueue.TryEnqueue(() =>
            {
                var delta = sv.ScrollableHeight - oldScrollableHeight;
                var target = ClampOffset(oldOffset + delta, sv.ScrollableHeight);
                sv.ChangeView(null, target, null, disableAnimation: true);
                lastVerticalOffsetRef.Current = target;
                lastScrollableHeightRef.Current = sv.ScrollableHeight;
                isFollowingRef.Current = sv.ScrollableHeight - target <= FollowThreshold;
                StoreSessionOffset(sessionId, target);
                sv.DispatcherQueue.TryEnqueue(() => suppressAutoFollowRef.Current = false);
            });
        }

        // Load more button — outside the repeated items
        var loadMoreButton = Props.HasMoreHistory
            ? Button("Load earlier messages", () => Props.OnLoadMoreHistory?.Invoke())
                .HAlign(HorizontalAlignment.Center)
                .Set(b => { b.Padding = new Thickness(16, 8, 16, 8); b.CornerRadius = new CornerRadius(4); })
                .Resources(r => r
                    .Set("ButtonBackground", Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Ref("SubtleFillColorSecondaryBrush"))
                    .Set("ButtonBackgroundPressed", Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Ref("SubtleFillColorTransparentBrush")))
                .Margin(0, 8, 0, 8)
            : (Element)Empty();

        static Element TimelineInset(Element child, double top = 2, double bottom = 2) =>
            Border(child).Padding(36, top, 24, bottom);

        // ── OpenClaw skin: bubbled user vs. left-aligned assistant card ──

        var userSender = Props.UserSenderLabel;
        var assistantSender = Props.AssistantSenderLabel;
        var defaultModel = Props.DefaultModel;
        var meta = Props.EntryMetadata;

        // ── Web Control UI palette: "dash-light" theme (verified against the
        // bundled assets/index-*.css — dash-light is what the user runs).
        // Colors here mirror the CSS variables exactly so bubbles/avatars
        // look identical to the web at http://localhost:18789/chat.
        // ──────────────────────────────────────────────────────────────
        var chatPageBg          = new SolidColorBrush(Color.FromArgb(0xFF, 0xF7, 0xF2, 0xEC)); // --bg
        var assistantBubbleBg   = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xDD, 0xD2)); // --bg-muted
        var assistantBubbleBdr  = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xD0, 0xC2)); // --border
        var userBubbleBg        = new SolidColorBrush(Color.FromArgb(0x33, 0x6E, 0x48, 0x28)); // --accent-subtle (#6e4828 @ 20%; CSS uses 12% but 20% reads better in WinUI sRGB)
        var userBubbleBdr       = new SolidColorBrush(Color.FromArgb(0x33, 0x6E, 0x48, 0x28)); // accent @ 20%
        var avatarPanelBg       = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xE8, 0xE0)); // --panel-strong
        var avatarBorder        = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xD0, 0xC2)); // --border
        var assistantAvatarFg   = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x60, 0x50)); // --muted
        var userAvatarBg        = new SolidColorBrush(Color.FromArgb(0x33, 0x6E, 0x48, 0x28));
        var userAvatarFg        = new SolidColorBrush(Color.FromArgb(0xFF, 0x6E, 0x48, 0x28)); // --accent
        var chatStampFg         = new SolidColorBrush(Color.FromArgb(0xFF, 0x75, 0x60, 0x50)); // --muted
        var chatTextFg          = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x38, 0x28)); // --chat-text
        var toolCardBgBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xE8, 0xE0)); // --secondary
        var toolCardBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xD0, 0xC2));

        // Avatar: 36×36 rounded square (radius 10px), matches .chat-avatar in CSS.
        Element AvatarBox(string glyph, Brush bg, Brush border, Brush fg, double size = 36, double radius = 10) =>
            Border(
                TextBlock(glyph)
                    .Set(t =>
                    {
                        t.HorizontalAlignment = HorizontalAlignment.Center;
                        t.VerticalAlignment = VerticalAlignment.Center;
                        t.FontSize = 13;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        t.Foreground = fg;
                    })
            ).Background(bg).Size(size, size).CornerRadius(radius)
             .WithBorder(border, 1);

        // Helper to format a timestamp as the web does: "h:mm tt" in local time.
        static string FormatTime(DateTimeOffset? ts) =>
            ts is { } v ? v.ToLocalTime().ToString("h:mm tt") : "";

        ChatEntryMetadata? MetaFor(string id) =>
            meta is not null && meta.TryGetValue(id, out var m) ? m : null;

        Element FooterCaption(string text, HorizontalAlignment align) =>
            Caption(text)
                .Foreground(chatStampFg)
                .Set(t => t.FontSize = 11)
                .HAlign(align);

        Element RenderUserEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst)
        {
            // Bubble matches .chat-line.user .chat-bubble: accent-subtle bg,
            // accent border @ 20%, padding 10×14, radius-lg (14px),
            // max-width min(700, 82%).
            var bubble = Border(
                TextBlock(entry.Text)
                    .Set(t =>
                    {
                        t.TextWrapping = TextWrapping.Wrap;
                        t.IsTextSelectionEnabled = true;
                        t.FontSize = 14;
                        t.Foreground = chatTextFg;
                    })
                    .Padding(14, 10, 14, 10)
            ).Background(userBubbleBg).CornerRadius(14)
             .WithBorder(userBubbleBdr, 1)
             .Set(b => b.MaxWidth = 700);

            // Avatar shown only on the LAST entry of a same-sender burst.
            // Mid-burst entries get a 36px-wide spacer so bubbles align.
            Element rightSlot = endsBurst
                ? AvatarBox("🧑", userAvatarBg, userBubbleBdr, userAvatarFg).VAlign(VerticalAlignment.Bottom)
                : Border(Empty()).Size(36, 36);

            var bubbleRow = (FlexRow(
                bubble,
                rightSlot
            ) with { ColumnGap = 8 }).HAlign(HorizontalAlignment.Right);

            Element footer = Empty();
            if (endsBurst)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var footerText = string.IsNullOrEmpty(timeStr) ? userSender : $"{userSender} · {timeStr}";
                footer = FooterCaption(footerText, HorizontalAlignment.Right).Margin(0, 2, 44, 0);
            }

            var topMargin = startsBurst ? 8.0 : 1.0;
            var bottomMargin = endsBurst ? 8.0 : 1.0;
            return VStack(2, bubbleRow, footer)
                .HAlign(HorizontalAlignment.Stretch)
                .Margin(60, topMargin, 12, bottomMargin);
        }

        Element RenderAssistantEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst)
        {
            if (string.IsNullOrEmpty(entry.Text))
                return Empty();

            // Avatar matches .chat-avatar.assistant: panel-strong bg, muted fg.
            Element leftSlot = startsBurst
                ? AvatarBox("★", avatarPanelBg, avatarBorder, assistantAvatarFg).VAlign(VerticalAlignment.Top)
                : Border(Empty()).Size(36, 36);

            // Bubble matches .chat-line.assistant .chat-bubble: bg-muted bg,
            // border @ var(--border).
            var card = Border(
                Markdown(entry.Text ?? "", _markdownOptions)
                    .Padding(14, 10, 14, 10)
            ).Background(assistantBubbleBg)
             .CornerRadius(14)
             .WithBorder(assistantBubbleBdr, 1)
             .Set(b => b.MaxWidth = 700);

            var bubbleRow = (FlexRow(
                leftSlot,
                card
            ) with { ColumnGap = 8 }).HAlign(HorizontalAlignment.Left);

            Element footer = Empty();
            if (endsBurst)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var modelStr = entryMeta?.Model ?? defaultModel;
                var footerParts = new List<string>(3) { assistantSender };
                if (!string.IsNullOrEmpty(timeStr)) footerParts.Add(timeStr);
                if (!string.IsNullOrEmpty(modelStr)) footerParts.Add(modelStr!);
                var footerText = string.Join(" · ", footerParts);
                footer = FooterCaption(footerText, HorizontalAlignment.Left).Margin(44, 2, 0, 0);
            }

            var topMargin = startsBurst ? 8.0 : 1.0;
            var bottomMargin = endsBurst ? 8.0 : 1.0;
            return VStack(2, bubbleRow, footer)
                .HAlign(HorizontalAlignment.Stretch)
                .Margin(12, topMargin, 60, bottomMargin)
                .AutomationName(entry.Text ?? "");
        }

        // Tool entry: rendered as TWO compact collapsible chips matching the
        // web Control UI's `chat-tool-card` blocks — a "Tool call" chip with
        // the args, and a "Tool output" chip with the result. Each chip is a
        // clickable button: collapsed shows just `▸ ⚡ Tool call <kind>`;
        // expanded reveals a monospace content panel below the header.
        Element RenderToolEntry(ChatTimelineItem entry)
        {
            var kindLabel = entry.ToolName ?? "tool";

            var statusGlyph = entry.ToolResult switch
            {
                ChatToolCallStatus.Success => "✓",
                ChatToolCallStatus.Error => "✗",
                _ => "⋯"
            };
            var statusFg = entry.ToolResult switch
            {
                ChatToolCallStatus.Success => Ref("SystemFillColorSuccessBrush"),
                ChatToolCallStatus.Error => Ref("SystemFillColorCriticalBrush"),
                _ => TertiaryText
            };

            // Tone matches dash-light --bg-muted/--border for visual parity
            // with the web's `chat-tool-card`.
            Element BuildChip(string token, string label, string contentText, bool hasContent)
            {
                var isExpanded = expandedToolChips.Value.Contains(token);
                var chevron = isExpanded ? "▾" : "▸";

                var headerRow = (FlexRow(
                    Caption(chevron).Foreground(TertiaryText)
                        .Set(t => { t.FontSize = 11; })
                        .VAlign(VerticalAlignment.Center),
                    Caption("⚡").Foreground(statusFg)
                        .Set(t => { t.FontSize = 12; })
                        .VAlign(VerticalAlignment.Center),
                    Caption(label).Foreground(SecondaryText)
                        .Set(t =>
                        {
                            t.FontSize = 12;
                            t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        })
                        .VAlign(VerticalAlignment.Center),
                    Caption(kindLabel).Foreground(TertiaryText)
                        .Set(t =>
                        {
                            t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                            t.FontSize = 12;
                            t.TextTrimming = TextTrimming.CharacterEllipsis;
                            t.MaxLines = 1;
                        })
                        .VAlign(VerticalAlignment.Center).Flex(grow: 1),
                    When(token.EndsWith(":out"),
                        () => Caption(statusGlyph).Foreground(statusFg)
                            .Set(t => { t.FontSize = 12; })
                            .VAlign(VerticalAlignment.Center))
                ) with { ColumnGap = 6 }).Padding(10, 6, 10, 6);

                Element body;
                if (isExpanded && hasContent)
                {
                    body = Border(
                        ScrollView(
                            TextBlock(contentText)
                                .Set(t =>
                                {
                                    t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                    t.FontSize = 11;
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.IsTextSelectionEnabled = true;
                                })
                                .Foreground(SecondaryText)
                                .Padding(10, 6, 10, 8)
                        ).Set(sv =>
                        {
                            sv.MaxHeight = 240;
                            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        })
                    ).Background(toolCardBgBrush);
                }
                else
                {
                    body = Empty();
                }

                // Whole chip is one Button so the entire card surface toggles
                // expansion (matches the web `.chat-tool-card--clickable`).
                Action toggle = () =>
                {
                    var next = new HashSet<string>(expandedToolChips.Value);
                    if (!next.Add(token)) next.Remove(token);
                    expandedToolChips.Set(next);
                };
                return Button(
                    VStack(0, headerRow, body),
                    toggle
                ).Set(b =>
                {
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    b.Padding = new Thickness(0);
                    b.CornerRadius = new CornerRadius(8);
                })
                .Resources(r => r
                    .Set("ButtonBackground", toolCardBgBrush)
                    .Set("ButtonBackgroundPointerOver", new SolidColorBrush(Color.FromArgb(0xFF, 0xE3, 0xD6, 0xC8)))
                    .Set("ButtonBackgroundPressed", new SolidColorBrush(Color.FromArgb(0xFF, 0xDA, 0xCB, 0xBB)))
                    .Set("ButtonBorderBrush", toolCardBorderBrush)
                    .Set("ButtonBorderBrushPointerOver", toolCardBorderBrush)
                    .Set("ButtonBorderBrushPressed", toolCardBorderBrush));
            }

            // "Tool call" chip is always shown; args/intent come from
            // `entry.Text` (built by ExtractToolLabel from data.args).
            var callContent = !string.IsNullOrEmpty(entry.Text) && entry.Text != entry.ToolName
                ? entry.Text!
                : kindLabel;
            var callChip = BuildChip(
                token: $"{entry.Id}:call",
                label: "Tool call",
                contentText: callContent,
                hasContent: !string.IsNullOrEmpty(callContent));

            // "Tool output" chip only shown once result/error has arrived.
            var hasOutput = !string.IsNullOrEmpty(entry.ToolOutput);
            Element outputChip = hasOutput
                ? BuildChip(
                    token: $"{entry.Id}:out",
                    label: entry.ToolResult == ChatToolCallStatus.Error ? "Tool error" : "Tool output",
                    contentText: entry.ToolOutput!,
                    hasContent: true)
                : Empty();

            var entryMeta = MetaFor(entry.Id);
            var timeStr = FormatTime(entryMeta?.Timestamp);
            var footerText = string.IsNullOrEmpty(timeStr) ? "Tool" : $"Tool · {timeStr}";

            return VStack(4,
                callChip,
                outputChip,
                FooterCaption(footerText, HorizontalAlignment.Left).Margin(0, 2, 0, 0)
            ).HAlign(HorizontalAlignment.Stretch)
             .Margin(36, 6, 24, 6);
        }

        Element RenderEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst) => entry.Kind switch
        {
            ChatTimelineItemKind.User => RenderUserEntry(entry, startsBurst, endsBurst),
            ChatTimelineItemKind.Assistant => RenderAssistantEntry(entry, startsBurst, endsBurst),
            ChatTimelineItemKind.ToolCall => RenderToolEntry(entry),

            // Reasoning — show the actual model thought trace in a muted
            // collapsible panel, with a "thinking" caption when empty.
            ChatTimelineItemKind.Reasoning => entry.Text is { Length: > 0 }
                ? TimelineInset(
                    Border(
                        VStack(2,
                            Caption("Reasoning")
                                .Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 11; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; }),
                            TextBlock(entry.Text)
                                .Set(t =>
                                {
                                    t.FontSize = 12;
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.IsTextSelectionEnabled = true;
                                    t.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
                                })
                                .Foreground(TertiaryText)
                        )
                    ).Padding(12, 8, 12, 8)
                     .Background(Ref("SubtleFillColorTertiaryBrush"))
                     .CornerRadius(6)
                     .WithBorder(toolCardBorderBrush, 1),
                    top: 4,
                    bottom: 4)
                : TimelineInset(
                    Caption("thinking…").Foreground(TertiaryText)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 12; })),

            // Filtered status — drop transient connection chatter.
            ChatTimelineItemKind.Status when entry.Text.Contains("Restored") || entry.Text.Contains("Connecting to") || entry.Text.Contains("Connected") || entry.Text.Contains("Resuming") => Empty(),

            ChatTimelineItemKind.Status when entry.Tone == ChatTone.Error =>
                TimelineInset(
                    Caption(entry.Text).Foreground(Ref("SystemFillColorCriticalBrush"))
                        .Set(t => { t.TextWrapping = TextWrapping.Wrap; t.FontSize = 12; }),
                    top: 4,
                    bottom: 4),

            ChatTimelineItemKind.Status => TimelineInset(
                Caption(entry.Text).Foreground(TertiaryText).Set(t => t.FontSize = 12)),

             _ => Empty()
        };

        // Render entries — compute "burst" boundaries so consecutive
        // messages from the same role share a single avatar+footer.
        // A burst is delimited by a Kind change (User↔Assistant, or any
        // Tool/Status/Reasoning entry breaks both).
        static bool SameBurstKind(ChatTimelineItemKind a, ChatTimelineItemKind b) =>
            a == b && (a == ChatTimelineItemKind.User || a == ChatTimelineItemKind.Assistant);

        var renderedEntries = new Element[Props.Entries.Count];
        for (int i = 0; i < Props.Entries.Count; i++)
        {
            var entry = Props.Entries[i];
            var prevKind = i > 0 ? Props.Entries[i - 1].Kind : (ChatTimelineItemKind?)null;
            var nextKind = i < Props.Entries.Count - 1 ? Props.Entries[i + 1].Kind : (ChatTimelineItemKind?)null;
            var startsBurst = prevKind is null || !SameBurstKind(prevKind.Value, entry.Kind);
            var endsBurst = nextKind is null || !SameBurstKind(entry.Kind, nextKind.Value);
            renderedEntries[i] = RenderEntry(entry, startsBurst, endsBurst).WithKey(entry.Id);
        }

        // Inline "thinking" indicator rendered just below the last entry
        // when caller signals we're between turn-start and the first byte.
        Element thinkingIndicator = Empty();
        if (Props.ShowThinkingIndicator)
        {
            thinkingIndicator = Border(
                (FlexRow(
                    AvatarBox("★", avatarPanelBg, avatarBorder, assistantAvatarFg).VAlign(VerticalAlignment.Center),
                    Caption($"{assistantSender} is thinking…")
                        .Foreground(chatStampFg)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 13; })
                        .VAlign(VerticalAlignment.Center)
                ) with { ColumnGap = 8 })
            ).Margin(12, 4, 60, 4);
        }

        return Grid([GridSize.Star()], [GridSize.Star()],
            // Page background matches dash-light --bg so bubbles stand out.
            Border(
                ScrollView(
                    Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto],
                        loadMoreButton.Grid(row: 0, column: 0),
                        VStack(2, renderedEntries).Set(sp =>
                        {
                            if (contentRef.Current != sp)
                            {
                                contentRef.Current = (Microsoft.UI.Xaml.Controls.StackPanel)sp;
                                sp.SizeChanged += (_, _) =>
                                {
                                    if (!suppressAutoFollowRef.Current && isFollowingRef.Current && scrollViewRef.Current is { } sv)
                                        QueueScrollToBottom(sv, prevSessionIdRef.Current, disableAnimation: true);
                                };
                            }
                        }).Grid(row: 1, column: 0),
                        thinkingIndicator.Grid(row: 2, column: 0),
                        Border(Empty()).Height(24).Grid(row: 3, column: 0)
                    )
                ).Set(sv =>
            {
                sv.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled;
                sv.HorizontalScrollMode = Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
                sv.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                if (scrollViewRef.Current != sv)
                {
                    scrollViewRef.Current = sv;
                    sv.ViewChanged += (_, _) =>
                    {
                        UpdateScrollMetrics(sv);

                        if (sv.ScrollableHeight > 0
                            && sv.VerticalOffset <= FollowThreshold
                            && hasMoreHistoryRef.Current
                            && loadMoreRequestedForCountRef.Current != prevEntryCountRef.Current)
                        {
                            loadMoreRequestedForCountRef.Current = prevEntryCountRef.Current;
                            loadMoreHistoryRef.Current?.Invoke();
                        }
                    };
                }

                if (entryCount != previousEntryCount)
                    loadMoreRequestedForCountRef.Current = -1;

                if (sessionChanged)
                {
                    StoreSessionOffset(previousSessionId, lastVerticalOffsetRef.Current);

                    if (entryCount > 0)
                    {
                        if (Props.SessionId is not null && sessionOffsetsRef.Current.TryGetValue(Props.SessionId, out var savedOffset))
                            QueueScrollToOffset(sv, Props.SessionId, savedOffset, disableAnimation: true, suppressAutoFollow: true);
                        else
                            QueueScrollToBottom(sv, Props.SessionId, disableAnimation: true);
                    }
                }
                else if (prependedHistory)
                {
                    QueuePreservePrependOffset(sv, Props.SessionId, lastVerticalOffsetRef.Current, lastScrollableHeightRef.Current);
                }
                else if (initialLoad)
                {
                    if (Props.SessionId is not null && sessionOffsetsRef.Current.TryGetValue(Props.SessionId, out var savedOffset))
                        QueueScrollToOffset(sv, Props.SessionId, savedOffset, disableAnimation: true, suppressAutoFollow: true);
                    else
                        QueueScrollToBottom(sv, Props.SessionId, disableAnimation: true);
                }
                else if (appendedEntries && isFollowingRef.Current)
                {
                    QueueScrollToBottom(sv, Props.SessionId, disableAnimation: false);
                }

                prevSessionIdRef.Current = Props.SessionId;
                prevFirstEntryIdRef.Current = firstEntryId;
                prevLastEntryIdRef.Current = lastEntryId;
                prevEntryCountRef.Current = entryCount;
            })
            ).Background(chatPageBg).Grid(row: 0, column: 0)
        );
    }
}
