using OpenClaw.Chat;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Chat.Explorations;
using Windows.ApplicationModel.DataTransfer;
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
    bool ShowThinkingIndicator = false,
    Func<string, Task>? OnReadAloud = null);

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

    // SECURITY (chat-rubber-duck HIGH 1 / MEDIUM 3): chat-bubble Markdown is
    // rendered with a hardened options object that:
    //   1. Renders images as inert ``[Image: <alt>]`` text (no Uri fetch) —
    //      blocks SSRF / tracking-pixel beacons triggered by a compromised
    //      gateway, malicious tool output, or a prompt-injected model.
    //   2. Pre-strips inline link / image / ref-def syntax via
    //      <see cref="ChatMarkdownSanitizer.Sanitize(string?)"/> so explicit
    //      ``[text](url)`` syntax never reaches the parser.
    //   3. Wires the Reactor
    //      <see cref="Microsoft.UI.Reactor.Markdown.MarkdownOptions.LinkBuilder"/>
    //      hook (vendored edit, see ``external/reactor/README.md``) to
    //      collapse any link the parser DOES emit — bare URLs and
    //      ``<https://…>`` autolinks that the sanitizer can't strip
    //      without breaking prose — into an inert ``RichTextRun`` carrying
    //      visible URL text but no NavigateUri.
    //   4. Renders raw HTML blocks as selectable plain text. Reactor's default
    //      behavior is currently also text-only, but keep the chat surface's
    //      trust boundary explicit so a future vendored update cannot turn
    //      ``<img>``, ``<a>``, ``<script>``, SVG, or iframe blocks into active UI.
    //      Net effect: no click-to-navigate hyperlink or network-fetching
    //      image can be manufactured by untrusted Markdown inside a chat bubble.
    internal static readonly Microsoft.UI.Reactor.Markdown.MarkdownOptions _markdownOptions = new()
    {
        CodeFontFamily = "Cascadia Code, Cascadia Mono, Consolas",
        // Inert link rendering: emit the link's display text followed by
        // the visible URL in parentheses, all as a non-clickable
        // RichTextRun. No NavigateUri is constructed anywhere in this
        // path, so even an attacker-controlled bare URL or autolink
        // cannot become a hyperlink.
        LinkBuilder = (inlines, uri) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Microsoft.UI.Reactor.Core.RichTextRun r: sb.Append(r.Text); break;
                    case Microsoft.UI.Reactor.Core.RichTextHyperlink h: sb.Append(h.Text); break;
                    case Microsoft.UI.Reactor.Core.RichTextLineBreak: sb.Append(' '); break;
                }
            }
            var text = ChatMarkdownSanitizer.FlattenLinkToInertText(sb.ToString(), uri?.ToString());
            return new Microsoft.UI.Reactor.Core.RichTextRun(text);
        },
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
        // Raw HTML from the gateway is not trusted markup. Display it as
        // literal selectable text rather than delegating to Reactor defaults
        // or any HTML-capable renderer.
        HtmlBlock = rawHtml =>
            TextBlock(ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(rawHtml))
                .Set(t =>
                {
                    t.TextWrapping = TextWrapping.Wrap;
                    t.IsTextSelectionEnabled = true;
                })
                .Foreground(Theme.TertiaryText),
        // Defense-in-depth for any image syntax that survives sanitization
        // (e.g. reference-style images): render an inert caption-styled
        // placeholder; never instantiate a Uri-bound BitmapImage.
        Image = (alt, _) =>
        {
            var label = string.IsNullOrEmpty(alt) ? "[Image]" : $"[Image: {alt}]";
            return Caption(label)
                .Foreground(Theme.TertiaryText)
                .Set(t => t.IsTextSelectionEnabled = true);
        },
    };

    static string FormatToolLabel(ChatTimelineItem e)    {
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

    /// <summary>
    /// Title-case a single token: <c>"exec"</c> → <c>"Exec"</c>. Used by the
    /// tool-chip inner header to mirror the web's <c>Exec</c>/<c>Process</c>
    /// styling. Returns the empty string for null/empty input.
    /// </summary>
    static string CapitalizeFirst(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : string.Empty);
    }

    /// <summary>
    /// If <paramref name="text"/> looks like a JSON object/array, pretty-print
    /// it with 2-space indentation. Otherwise return the string verbatim.
    /// Used so tool chips render gateway action blobs (<c>{"action":"poll"…}</c>)
    /// the same way the web does, without affecting plain shell output.
    /// </summary>
    static string TryFormatJsonForDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return text;
        var first = trimmed[0];
        if (first != '{' && first != '[') return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return text;
        }
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
        // Subscribe to ChatExplorationState so toggles live-rerender the
        // timeline. Same inline pattern as OpenClawComposer (UseState +
        // UseEffect — extension methods can't access protected hooks).
        var explorationRev = UseState(0, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler h = (_, _) => explorationRev.Set(explorationRev.Value + 1);
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        // Live values from ChatExplorationState (groups C/D/F).
        var bubbleRadius     = ChatVisualResolver.BubbleCornerRadius();
        var bubblePadding    = ChatVisualResolver.BubbleInnerPadding();
        var bubbleMaxWidth   = ChatVisualResolver.BubbleMaxWidth();
        var bubbleSideMargin = ChatVisualResolver.BubbleSideMargin();
        var showAsstBubbles  = ChatVisualResolver.ShowAssistantBubbles();
        var showToolCalls    = ChatVisualResolver.ShowToolCalls();
        var gutter           = ChatExplorationState.Gutter;
        var messageGap       = ChatExplorationState.MessageGap;
        var showUserAvatar   = ChatVisualResolver.ShowUserAvatar();
        var showAssistAvatar = ChatVisualResolver.ShowAssistantAvatar();
        var showTimestamps   = ChatVisualResolver.ShowTimestamps();

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

        // Hover state — set of entry ids currently under the pointer. Used to
        // reveal the trash / speak action icons beside user / assistant
        // bubbles. Re-renders the whole timeline on hover transitions; that's
        // fine for the entry counts we deal with (typically <100 visible).
        var hoveredEntries = UseState<HashSet<string>>(new HashSet<string>(), threadSafe: true);

        // Acknowledged actions — set of "entryId|actionKey" strings briefly
        // marked after a click so the icon can swap to a checkmark for ~1.2s
        // before reverting. Gives the user immediate "done" feedback for
        // Copy / Read aloud / Delete without a toast.
        // UseReducer (not UseState) so the updater always sees the LIVE
        // hook value — UseState's `.Value` is a render-time snapshot, so
        // a delayed continuation that reads it later sees a stale set and
        // bails out, leaving the ack glyph stuck.
        var (ackedActionsValue, ackUpdate) = UseReducer<HashSet<string>>(new HashSet<string>(), threadSafe: true);
        async void AckAction(string entryId, string actionKey)
        {
            var key = entryId + "|" + actionKey;
            ackUpdate(prev =>
            {
                if (prev.Contains(key)) return prev;
                return new HashSet<string>(prev) { key };
            });
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            await Task.Delay(700);
            void Clear() => ackUpdate(prev =>
            {
                if (!prev.Contains(key)) return prev;
                var nxt = new HashSet<string>(prev);
                nxt.Remove(key);
                return nxt;
            });
            if (dq is null) Clear();
            else dq.TryEnqueue(Clear);
        }

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
            ? Button(LocalizationHelper.GetString("Chat_Timeline_LoadEarlier"), () => Props.OnLoadMoreHistory?.Invoke())
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
        // ── Kenny Hong palette (kenehong/native-chat-v2): Microsoft Fluent
        // ``AccentFillColorDefaultBrush`` for the user bubble (white text on
        // accent), ``SubtleFillColorSecondaryBrush`` for the assistant bubble
        // and page background. All looked up from the theme so they react to
        // light/dark mode and high-contrast settings without manual swaps.
        // ─────────────────────────────────────────────────────────────────
        Brush themeBrush(string key) => (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
        // When the host window paints a non-Solid SystemBackdrop (Mica / MicaAlt /
        // Acrylic), let it show through by using a transparent chat-page fill.
        // Otherwise fall back to the subtle layer color so Solid mode still
        // reads as a flat surface.
        var chatPageBg = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("SubtleFillColorSecondaryBrush")
            : (Brush)new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var assistantBubbleBg   = ChatVisualResolver.AssistantBubbleBrush(themeBrush("SubtleFillColorSecondaryBrush"));
        var assistantBubbleBdr  = themeBrush("ControlStrokeColorDefaultBrush");
        var userBubbleBg        = ChatVisualResolver.UserBubbleBrush(themeBrush("AccentFillColorDefaultBrush"));
        var userBubbleBdr       = themeBrush("AccentFillColorDefaultBrush");
        var userBubbleFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        var avatarPanelBg       = themeBrush("SubtleFillColorTertiaryBrush");
        var avatarBorder        = themeBrush("ControlStrokeColorDefaultBrush");
        var assistantAvatarFg   = themeBrush("TextFillColorSecondaryBrush");
        var userAvatarBg        = ChatVisualResolver.AccentBrush(themeBrush("AccentFillColorDefaultBrush"));
        var userAvatarFg        = themeBrush("TextOnAccentFillColorPrimaryBrush");
        // a11y: timestamps and "is thinking" caption sit directly on the
        // window backdrop. On Mica/Acrylic the system tint is translucent,
        // so Tertiary text can fall below WCAG AA. Bump to Secondary when
        // the chat surface is transparent over a host backdrop.
        var chatStampFg         = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
            ? themeBrush("TextFillColorTertiaryBrush")
            : themeBrush("TextFillColorSecondaryBrush");
        var chatTextFg          = themeBrush("TextFillColorPrimaryBrush");
        // Tool chips kept in a slightly cooler/dim shade so they read as
        // secondary content next to the assistant bubble.
        var toolCardBgBrush     = themeBrush("SubtleFillColorTertiaryBrush");
        var toolCardBorderBrush = themeBrush("ControlStrokeColorDefaultBrush");

        // Avatar: 36×36 circle (Kenny uses circular avatars). Same constructor
        // as before but radius defaults to half the size for a perfect circle.
        Element AvatarBox(string glyph, Brush bg, Brush border, Brush fg, double size = 36, double radius = 18) =>
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

        // Assistant avatar: 36×36 circle showing the OpenClaw app icon (the
        // same PNG used by the tray and chat-window title bar) so the agent
        // identity is visually consistent across surfaces.
        Element AssistantAvatar(double size = 36, double radius = 18) =>
            Border(
                Image("ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-unplated.png")
                    .Set(im =>
                    {
                        im.Stretch = Stretch.UniformToFill;
                        im.HorizontalAlignment = HorizontalAlignment.Stretch;
                        im.VerticalAlignment = VerticalAlignment.Stretch;
                    })
            ).Background(avatarPanelBg).Size(size, size).CornerRadius(radius)
             .WithBorder(avatarBorder, 1)
             .Set(b => b.Padding = new Thickness(0))
             .AutomationName($"{assistantSender} avatar");

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

        // Hover-revealed action icon (copy / read aloud / trash). Opacity 0
        // and not hit-testable until the entry is hovered, then fades in
        // and becomes clickable. Soft pill radius + Light weight glyph so
        // it feels friendlier than the standard MDL2 button look. When the
        // matching action is acknowledged (briefly after click) the glyph
        // swaps to a checkmark for instant visual feedback.
        Element HoverIcon(string entryId, string actionKey, string glyph, string ackGlyph,
            string tip, Action onClick)
        {
            var visible = hoveredEntries.Value.Contains(entryId);
            var acked = ackedActionsValue.Contains(entryId + "|" + actionKey);
            var shownGlyph = acked ? ackGlyph : glyph;
            var shownColor = acked ? themeBrush("SystemFillColorSuccessBrush") : chatStampFg;
            return Button(
                TextBlock(shownGlyph)
                    .Set(t =>
                    {
                        t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                        t.FontSize = 14;
                        t.FontWeight = Microsoft.UI.Text.FontWeights.Light;
                        t.Foreground = shownColor;
                    }),
                onClick
            ).Set(b =>
            {
                b.Padding = new Thickness(7, 5, 7, 5);
                b.MinWidth = 30; b.MinHeight = 26;
                b.CornerRadius = new CornerRadius(13);
                // Hide together with hover — once the pointer leaves the
                // bubble, the icon (whether ack'd or not) goes away too.
                b.Opacity = visible ? 1.0 : 0.0;
                b.IsHitTestVisible = visible;
            })
            .Resources(r => r
                .Set("ButtonBackground", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBackgroundPointerOver", themeBrush("SubtleFillColorSecondaryBrush"))
                .Set("ButtonBackgroundPressed", themeBrush("SubtleFillColorTertiaryBrush"))
                .Set("ButtonBorderBrush", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent))
                .Set("ButtonBorderBrushPressed", new SolidColorBrush(Colors.Transparent)))
            .AutomationName(tip);
        }

        // Wrap a row with hover handlers that flip the entry id in
        // hoveredEntries on PointerEntered/Exited. Callers should wrap the
        // row in a Border with a transparent background so the WHOLE
        // bounding box (including the gap between bubble and footer) is
        // hit-testable — otherwise moving the pointer down to a
        // hover-revealed action button briefly exits the hover area and
        // hides the icon before the click lands.
        T WithHoverHandlers<T>(T el, string entryId) where T : Element
        {
            return el
                .OnPointerEntered((_, _) =>
                {
                    var current = hoveredEntries.Value;
                    if (current.Contains(entryId)) return;
                    var next = new HashSet<string>(current) { entryId };
                    hoveredEntries.Set(next);
                })
                .OnPointerExited((_, _) =>
                {
                    var current = hoveredEntries.Value;
                    if (current.Contains(entryId))
                    {
                        var next = new HashSet<string>(current);
                        next.Remove(entryId);
                        hoveredEntries.Set(next);
                    }
                    // Drop any pending ack glyph for this entry so the next
                    // hover starts fresh with the original copy/speak/trash
                    // icon instead of a stale checkmark.
                    var prefix = entryId + "|";
                    ackUpdate(prev =>
                    {
                        if (!prev.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                            return prev;
                        return new HashSet<string>(prev.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal)));
                    });
                });
        }

        // Build the WebView-style multi-pill footer:
        //   "Field   7:54 PM   ↑1475   ↓12   R45.4k   23% ctx   gpt-5.5"
        // Each pill is a small caption; missing pieces (e.g. token counts not
        // reported) are silently skipped so the footer just shows what's
        // known. Not clickable yet — that's deferred until the gateway surfaces
        // the corresponding metadata.
        static string FormatTokenCount(int n) =>
            n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

        // Copy assistant message text to the system clipboard. Strips a
        // light amount of markdown noise (fenced code backticks) so the
        // clipboard payload reads naturally when pasted into prose.
        static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var data = new DataPackage();
                data.SetText(text);
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }
            catch { /* clipboard contention — silently ignore */ }
        }

        async void ReadAloud(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Props.OnReadAloud is not { } onReadAloud) return;

            await onReadAloud(StripMarkdownForSpeech(text));
        }

        // Very light markdown stripper so the synthesizer doesn't read
        // backticks, asterisks, link brackets, etc. Markdown rendering is
        // already done visually; this only cleans the spoken transcript.
        static string StripMarkdownForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var s = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " code block ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"`([^`]+)`", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", " image ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_#>]+", " ");
            return s;
        }

        Element BuildAssistantFooter(string sender, string time, string? model,
            int? inputTokens, int? outputTokens, int? responseTokens, int? contextPct,
            Brush stampFg,
            string entryId, string entryText)
        {
            // Honor per-field toggles from ChatExplorationState.
            var showSender   = ChatExplorationState.ShowSenderName;
            var showModel    = ChatExplorationState.ShowModelName;
            var showTokens   = ChatExplorationState.ShowTokens;
            var showCtxPct   = ChatExplorationState.ShowContextPercent;

            var parts = new List<Element>();
            void AddPill(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                parts.Add(Caption(text).Foreground(stampFg)
                    .Set(t => t.FontSize = 11)
                    .VAlign(VerticalAlignment.Center));
            }

            // Hover actions — Copy + Read aloud. Placed at the END of the
            // footer so the timestamp/sender stay anchored on the left and
            // the empty space (when not hovered) trails off harmlessly to
            // the right instead of leaving an awkward gap before the time.
            if (showSender && !string.IsNullOrEmpty(sender))
                parts.Add(Caption(sender).Foreground(stampFg)
                    .Set(t => { t.FontSize = 11; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                    .VAlign(VerticalAlignment.Center));

            AddPill(time);
            if (showTokens && inputTokens   is int inN)   AddPill($"↑{FormatTokenCount(inN)}");
            if (showTokens && outputTokens  is int outN)  AddPill($"↓{FormatTokenCount(outN)}");
            if (showTokens && responseTokens is int respN) AddPill($"R{FormatTokenCount(respN)}");
            if (showCtxPct && contextPct    is int pct)   AddPill($"{pct}% ctx");
            if (showModel) AddPill(model ?? "");

            parts.Add(HoverIcon(entryId, "copy", "\uE8C8", "\uE73E",
                LocalizationHelper.GetString("Chat_Assistant_Action_Copy"),
                () => { CopyToClipboard(entryText); AckAction(entryId, "copy"); }).VAlign(VerticalAlignment.Center));
            parts.Add(HoverIcon(entryId, "speak", "\uE767", "\uE73E",
                LocalizationHelper.GetString("Chat_Assistant_Action_ReadAloud"),
                () => { ReadAloud(entryText); AckAction(entryId, "speak"); }).VAlign(VerticalAlignment.Center));

            return (FlexRow(parts.ToArray()) with { ColumnGap = 8 })
                .HAlign(HorizontalAlignment.Left);
        }

        // User-bubble footer mirrors the assistant footer UX so the same
        // hover affordance shows up on both sides. Order is reversed for
        // the user side: hover actions sit on the LEFT and the timestamp
        // anchors the FAR RIGHT (closest to the bubble corner) — matches
        // the user's reading direction when the bubble is right-aligned.
        Element BuildUserFooter(string sender, string time, Brush stampFg,
            string entryId, string entryText)
        {
            var showSender = ChatExplorationState.ShowSenderName;
            var parts = new List<Element>
            {
                HoverIcon(entryId, "copy", "\uE8C8", "\uE73E",
                    LocalizationHelper.GetString("Chat_Assistant_Action_Copy"),
                    () => { CopyToClipboard(entryText); AckAction(entryId, "copy"); }).VAlign(VerticalAlignment.Center),
                HoverIcon(entryId, "delete", "\uE74D", "\uE73E",
                    LocalizationHelper.GetString("Chat_User_Action_Delete"),
                    () => { /* TODO: wire to provider */ AckAction(entryId, "delete"); }).VAlign(VerticalAlignment.Center),
            };

            if (showSender && !string.IsNullOrEmpty(sender))
                parts.Add(Caption(sender).Foreground(stampFg)
                    .Set(t => { t.FontSize = 11; t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; })
                    .VAlign(VerticalAlignment.Center));

            if (!string.IsNullOrEmpty(time))
                parts.Add(Caption(time).Foreground(stampFg)
                    .Set(t => t.FontSize = 11)
                    .VAlign(VerticalAlignment.Center));

            return (FlexRow(parts.ToArray()) with { ColumnGap = 8 })
                .HAlign(HorizontalAlignment.Right);
        }

        Element RenderUserEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst)
        {
            // User bubble — Microsoft accent fill with white-on-accent text.
            // Kenny's Calm variation has no border on the user bubble — the
            // accent fill is bold enough to read on its own.
            var bubble = Border(
                TextBlock(entry.Text)
                    .Set(t =>
                    {
                        t.TextWrapping = TextWrapping.Wrap;
                        t.IsTextSelectionEnabled = true;
                        t.FontSize = 14;
                        t.Foreground = userBubbleFg;
                    })
            ).Background(userBubbleBg)
             .Set(b =>
             {
                 b.CornerRadius = bubbleRadius;
                b.MaxWidth = bubbleMaxWidth;
                 b.Padding = bubblePadding;
                 b.VerticalAlignment = VerticalAlignment.Center;
             });

            // Avatar shown only on the LAST entry of a same-sender burst,
            // and only when ChatExplorationState.AvatarMode allows. When
            // avatars are hidden entirely we drop the slot; mid-burst entries
            // still get a spacer so they stay aligned with the first bubble.
            Element rightSlot = !showUserAvatar
               ? Empty()
               : (endsBurst
                   ? AvatarBox("🧑", userAvatarBg, userBubbleBdr, userAvatarFg).VAlign(VerticalAlignment.Center)
                   : Border(Empty()).Size(36, 36));

            var bubbleRow = (FlexRow(
                bubble,
                rightSlot
            ) with { ColumnGap = bubbleSideMargin }).HAlign(HorizontalAlignment.Right);

            Element footer = Empty();
            if (endsBurst && showTimestamps)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var rightInset = showUserAvatar ? (36 + bubbleSideMargin) : 0;
                footer = BuildUserFooter(userSender, timeStr, chatStampFg, entry.Id, entry.Text ?? "")
                    .Margin(0, 2, rightInset, 0);
            }

            var topMargin = startsBurst ? 4.0 : 1.0;
            var bottomMargin = endsBurst ? 4.0 : 1.0;
            return WithHoverHandlers(
                Border(
                    VStack(2, bubbleRow, footer)
                        .HAlign(HorizontalAlignment.Stretch)
                ).Background(new SolidColorBrush(Colors.Transparent))
                 .Margin(gutter, topMargin, 8, bottomMargin),
                entry.Id);
        }

        Element RenderAssistantEntry(ChatTimelineItem entry, bool startsBurst, bool endsBurst)
        {
            if (string.IsNullOrEmpty(entry.Text))
                return Empty();

            // Hidden by user toggle — collapses entire assistant block.
            if (!showAsstBubbles)
                return Empty();

            // Avatar shown only on the FIRST entry of a same-sender burst.
            // Mid-burst entries get a spacer so the bubble starts at the
            // same X as the first bubble in the burst.
            Element leftSlot = !showAssistAvatar
                ? Empty()
                : (startsBurst
                    ? AssistantAvatar().VAlign(VerticalAlignment.Top)
                    : Border(Empty()).Size(36, 36));

            // Assistant bubble — subtle gray with primary text. Radius/Padding
            // come from ChatExplorationState (BubbleCornerRadius + PaddingDensity).
            var card = Border(
                Markdown(ChatMarkdownSanitizer.Sanitize(entry.Text), _markdownOptions)
            ).Background(assistantBubbleBg)
             .Set(b =>
             {
                 b.CornerRadius = bubbleRadius;
                 b.MaxWidth = bubbleMaxWidth;
                 b.Padding = bubblePadding;
             });

            var bubbleRow = (FlexRow(
                leftSlot,
                card
            ) with { ColumnGap = bubbleSideMargin }).HAlign(HorizontalAlignment.Left);

            Element footer = Empty();
            if (endsBurst && showTimestamps)
            {
                var entryMeta = MetaFor(entry.Id);
                var timeStr = FormatTime(entryMeta?.Timestamp);
                var modelStr = entryMeta?.Model ?? defaultModel;
                footer = BuildAssistantFooter(assistantSender, timeStr, modelStr,
                    entryMeta?.InputTokens, entryMeta?.OutputTokens,
                    entryMeta?.ResponseTokens, entryMeta?.ContextPercent,
                    chatStampFg, entry.Id, entry.Text ?? "");
                var leftInset = showAssistAvatar ? (36 + bubbleSideMargin) : 0;
                footer = footer.Margin(leftInset, 2, 0, 0);
            }

            var topMargin = startsBurst ? 4.0 : 1.0;
            var bottomMargin = endsBurst ? 4.0 : 1.0;
            return WithHoverHandlers(
                Border(
                    VStack(2, bubbleRow, footer)
                        .HAlign(HorizontalAlignment.Stretch)
                        .AutomationName(entry.Text ?? "")
                ).Background(new SolidColorBrush(Colors.Transparent))
                 .Margin(8, topMargin, gutter, bottomMargin),
                entry.Id);
        }

        // Tool entry: rendered as TWO compact collapsible chips matching the
        // web Control UI's `chat-tool-card` blocks — a "Tool call" chip with
        // the args, and a "Tool output" chip with the result. Each chip is a
        // clickable button: collapsed shows just `▸ ⚡ Tool call <kind>`;
        // expanded reveals a monospace content panel below the header.
        Element RenderToolEntry(ChatTimelineItem entry)
        {
            var kindLabel = entry.ToolName ?? "tool";

            // Status pill colors — adopted from Kenny's ComponentLibrary
            // Cat04 tool cards. Same palette as the web Control UI.
            //   Running #FFDC781E (orange)
            //   Done    #FF28A050 (green)
            //   Yielded #FF8888AA (gray)        — not yet emitted by us
            //   Error   SystemFillColorCriticalBrush
            string statusText;
            Brush statusBg;
            switch (entry.ToolResult)
            {
                case ChatToolCallStatus.Success:
                    statusText = LocalizationHelper.GetString("Chat_Status_Done");
                    statusBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0xA0, 0x50));
                    break;
                case ChatToolCallStatus.Error:
                    statusText = LocalizationHelper.GetString("Chat_Status_Error");
                    statusBg = themeBrush("SystemFillColorCriticalBrush");
                    break;
                default:
                    statusText = LocalizationHelper.GetString("Chat_Status_Running");
                    statusBg = new SolidColorBrush(Color.FromArgb(0xFF, 0xDC, 0x78, 0x1E));
                    break;
            }

            // Lightning glyph color follows pill status so the header reads
            // at a glance even before the pill is in view.
            Brush statusFg = entry.ToolResult switch
            {
                ChatToolCallStatus.Success => statusBg,
                ChatToolCallStatus.Error => statusBg,
                _ => themeBrush("TextFillColorTertiaryBrush")
            };

            // Brushes used by the expanded body — match the web's
            // `__block-preview` / `__block-content` palette.
            var blockBg            = themeBrush("ControlFillColorTertiaryBrush");
            var blockBorder        = themeBrush("ControlStrokeColorDefaultBrush");
            var blockHeaderBg      = themeBrush("SubtleFillColorSecondaryBrush");

            Element BuildChip(string token, string label, string sectionLabel, string contentText, bool hasContent)
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
                    // Status pill — Kenny's CornerRadius 10 / Padding 6,1
                    // colored capsule with white text. Only on the output
                    // chip (the call chip's status is implicit in "Tool call").
                    When(token.EndsWith(":out"),
                        () => Border(
                            Caption(statusText)
                                .Foreground(new SolidColorBrush(Colors.White))
                                .Set(t => { t.FontSize = 10; })
                                .VAlign(VerticalAlignment.Center)
                        ).Background(statusBg)
                         .CornerRadius(10)
                         .Padding(6, 1, 6, 1)
                         .VAlign(VerticalAlignment.Center))
                ) with { ColumnGap = 6 }).Padding(10, 6, 10, 6);

                Element body;
                if (isExpanded && hasContent)
                {
                    // Pretty-print JSON content — gateway often delivers
                    // `{ "action": "poll", ... }` blobs; matching the web's
                    // syntax-highlighted formatting.
                    var displayText = TryFormatJsonForDisplay(contentText);

                    // Inner header: ⚙ <CapitalizedKind>  — mirrors the
                    // `Exec` / `Process` mini-header inside the web chip.
                    var innerHeader = (FlexRow(
                        Caption("\uD83D\uDD27").Foreground(SecondaryText)  // 🔧 wrench
                            .Set(t => { t.FontSize = 12; })
                            .VAlign(VerticalAlignment.Center),
                        Caption(CapitalizeFirst(kindLabel)).Foreground(SecondaryText)
                            .Set(t =>
                            {
                                t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                t.FontSize = 13;
                                t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                            })
                            .VAlign(VerticalAlignment.Center)
                    ) with { ColumnGap = 6 }).Padding(12, 8, 12, 8);

                    // Section label — uppercase 11px muted, like
                    // `.chat-tool-card__block-label` in the web CSS.
                    var sectionRow = (FlexRow(
                        Caption("⚡").Foreground(TertiaryText)
                            .Set(t => { t.FontSize = 11; })
                            .VAlign(VerticalAlignment.Center),
                        Caption(sectionLabel.ToUpperInvariant())
                            .Foreground(TertiaryText)
                            .Set(t =>
                            {
                                t.FontSize = 11;
                                t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                                t.CharacterSpacing = 60; // ~0.04em letter-spacing
                            })
                            .VAlign(VerticalAlignment.Center)
                    ) with { ColumnGap = 6 }).Padding(12, 0, 12, 6);

                    // Code-styled monospace content panel.
                    var codeBlock = Border(
                        ScrollView(
                            TextBlock(displayText)
                                .Set(t =>
                                {
                                    t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                    t.FontSize = 11;
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.IsTextSelectionEnabled = true;
                                    t.LineHeight = 16;
                                })
                                .Foreground(SecondaryText)
                                .Padding(11, 8, 11, 10)
                        ).Set(sv =>
                        {
                            sv.MaxHeight = 280;
                            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        })
                    ).Background(blockBg)
                     .CornerRadius(6)
                     .WithBorder(blockBorder, 1)
                     .Margin(12, 0, 12, 10);

                    body = Border(
                        VStack(0, innerHeader, sectionRow, codeBlock)
                    ).Background(blockHeaderBg);
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
                label: LocalizationHelper.GetString("Chat_Tool_CallLabel"),
                sectionLabel: LocalizationHelper.GetString("Chat_Tool_InputSection"),
                contentText: callContent,
                hasContent: !string.IsNullOrEmpty(callContent));

            // "Tool output" chip only shown once result/error has arrived.
            var hasOutput = !string.IsNullOrEmpty(entry.ToolOutput);
            Element outputChip = hasOutput
                ? BuildChip(
                    token: $"{entry.Id}:out",
                    label: entry.ToolResult == ChatToolCallStatus.Error
                        ? LocalizationHelper.GetString("Chat_Tool_ErrorLabel")
                        : LocalizationHelper.GetString("Chat_Tool_OutputLabel"),
                    sectionLabel: entry.ToolResult == ChatToolCallStatus.Error
                        ? LocalizationHelper.GetString("Chat_Tool_ErrorLabel")
                        : LocalizationHelper.GetString("Chat_Tool_OutputLabel"),
                    contentText: entry.ToolOutput!,
                    hasContent: true)
                : Empty();

            var entryMeta = MetaFor(entry.Id);
            var timeStr = FormatTime(entryMeta?.Timestamp);
            var footerText = string.IsNullOrEmpty(timeStr)
                ? LocalizationHelper.GetString("Chat_Tool_FooterLabel")
                : string.Format(LocalizationHelper.GetString("Chat_Tool_FooterWithTimeFormat"), timeStr);

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
            ChatTimelineItemKind.ToolCall => showToolCalls ? RenderToolEntry(entry) : Empty(),

            // Reasoning — use a WinUI Expander with a "🧠 Thinking" header,
            // matching Kenny's ComponentLibrary Cat03/NativeChatThread design.
            // Collapsed by default so the model thought trace doesn't crowd
            // the conversation; click to peek.
            ChatTimelineItemKind.Reasoning => entry.Text is { Length: > 0 }
                ? TimelineInset(
                    Border(
                        Expander(
                            LocalizationHelper.GetString("Chat_Reasoning_ThinkingHeader"),
                            TextBlock(entry.Text)
                                .Set(t =>
                                {
                                    t.FontSize = 12;
                                    t.TextWrapping = TextWrapping.Wrap;
                                    t.IsTextSelectionEnabled = true;
                                    t.FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");
                                })
                                .Foreground(TertiaryText)
                                .Padding(0, 4, 0, 4),
                            isExpanded: false)
                        .Set(e =>
                        {
                            e.HorizontalAlignment = HorizontalAlignment.Stretch;
                            e.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        })
                    ).Background(Ref("SubtleFillColorTertiaryBrush"))
                     .CornerRadius(8)
                     .WithBorder(new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x8C, 0xB4)), 1)
                     .Margin(8, 2, 40, 2),
                    top: 4,
                    bottom: 4)
                : TimelineInset(
                    Caption(LocalizationHelper.GetString("Chat_Reasoning_ThinkingEllipsis")).Foreground(TertiaryText)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 12; })),

            // Filtered status — drop transient connection chatter.
            ChatTimelineItemKind.Status when entry.Text.Contains("Restored") || entry.Text.Contains("Connecting to") || entry.Text.Contains("Connected") || entry.Text.Contains("Resuming") => Empty(),

            // Error status — centered red pill (Kenny's Cat10 system-notice
            // pattern: small bordered capsule, tinted background, glyph + text).
            ChatTimelineItemKind.Status when entry.Tone == ChatTone.Error =>
                Border(
                    Border(
                        (FlexRow(
                            Caption("⚠").Foreground(themeBrush("SystemFillColorCriticalBrush"))
                                .Set(t => { t.FontSize = 12; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(entry.Text).Foreground(themeBrush("SystemFillColorCriticalBrush"))
                                .Set(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; })
                                .VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 })
                    ).Background(new SolidColorBrush(Color.FromArgb(0x2E, 0xC8, 0x32, 0x32)))  // crimson @ ~18%
                     .CornerRadius(12)
                     .Padding(10, 4, 10, 4)
                     .HAlign(HorizontalAlignment.Center)
                ).Margin(0, 4, 0, 4),

            // Generic status — small dim centered pill at 18% tint.
            ChatTimelineItemKind.Status =>
                Border(
                    Border(
                        (FlexRow(
                            Caption("ℹ").Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 12; })
                                .VAlign(VerticalAlignment.Center),
                            Caption(entry.Text).Foreground(TertiaryText)
                                .Set(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; })
                                .VAlign(VerticalAlignment.Center)
                        ) with { ColumnGap = 6 })
                    ).Background(themeBrush("SubtleFillColorTertiaryBrush"))
                     .CornerRadius(12)
                     .Padding(10, 4, 10, 4)
                     .HAlign(HorizontalAlignment.Center)
                ).Margin(0, 4, 0, 4),

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
                    AssistantAvatar().VAlign(VerticalAlignment.Center),
                    Caption(string.Format(LocalizationHelper.GetString("Chat_Timeline_AssistantThinkingFormat"), assistantSender))
                        .Foreground(chatStampFg)
                        .Set(t => { t.FontStyle = global::Windows.UI.Text.FontStyle.Italic; t.FontSize = 13; })
                        .VAlign(VerticalAlignment.Center)
                ) with { ColumnGap = 8 })
            ).Margin(12, 4, 60, 4)
             .LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
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
