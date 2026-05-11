using System;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Chat.Explorations;

// Enums mirror Kenny's v2 ChatExplorationPreview spec
// (openclaw-windows-node-v2/src/OpenClaw.Tray.WinUI/Controls/ChatExplorations/ChatExplorationPreview.xaml.cs).
// Source of truth for value names — keep in sync if the v2 spec evolves.

public enum ChatVariation
{
    /// <summary>Mica look-alike, large rounded bubbles, generous spacing.</summary>
    Calm,
    /// <summary>Acrylic look-alike, small bubbles, tight spacing.</summary>
    Compact,
    /// <summary>Solid surface, no bubble fill, thin accent left stroke + larger typography.</summary>
    Plain,
}

public enum ChatBackdropMode
{
    Mica,
    MicaAlt,
    Acrylic,
    Solid,
}

public enum ChatPaddingDensity
{
    Cozy,
    Comfortable,
    Compact,
}

public enum ChatPreviewTheme
{
    System,
    Light,
    Dark,
}

public enum ChatAvatarMode
{
    Both,
    AgentOnly,
    None,
}

public enum ChatComposerLayout
{
    /// <summary>Three rows: dropdowns / textbox / actions. Mirrors production OpenClawComposer.</summary>
    ThreeRow,
    /// <summary>Two rows: textbox on top, then [borderless session·model pill] [actions] [Send].
    /// The pill opens a single MenuFlyout grouping Session / Model / Reasoning sections.</summary>
    InlinePill,
    /// <summary>Single row: textbox + Send. Everything else hides under a More menu.</summary>
    Minimal,
}

/// <summary>
/// Process-wide live state for the chat exploration toggles. Mirrors the
/// dependency-property surface of v2's <c>ChatExplorationPreview</c> XAML
/// control, but as plain static properties so the Reactor chat tree
/// (<see cref="OpenClawChatRoot"/>, <see cref="OpenClawChatTimeline"/>,
/// <see cref="OpenClawComposer"/>) can read them at render time and any
/// component can subscribe to <see cref="Changed"/> to invalidate.
///
/// Not persisted — resets every app launch (debug/exploration tool only).
/// Follows the same static + <c>Changed</c> event pattern as
/// <see cref="DebugChatSurfaceOverrides"/>.
/// </summary>
public static class ChatExplorationState
{
    // Defaults mirror v2 PropertyMetadata.

    private static ChatVariation _variation = ChatVariation.Calm;
    private static ChatBackdropMode _backdropMode = ChatBackdropMode.Mica;
    private static ChatPreviewTheme _previewTheme = ChatPreviewTheme.System;
    private static bool _usesHostBackdrop;

    private static double _bubbleCornerRadius = 16d;
    private static double _gutter = 64d;
    private static double _messageGap = 12d;
    private static ChatPaddingDensity _paddingDensity = ChatPaddingDensity.Comfortable;
    private static bool _showTimestamps = true;

    private static bool _showAvatars = true;
    private static ChatAvatarMode _avatarMode = ChatAvatarMode.Both;

    private static ChatComposerLayout _composerLayout = ChatComposerLayout.ThreeRow;
    private static double _composerCornerRadius = 8d;
    private static double _composerIconSize = 14d;
    private static double _sendButtonSize = 32d;

    private static Brush? _accentBrushOverride;
    private static Brush? _userBubbleBrushOverride;
    private static Brush? _assistantBubbleBrushOverride;
    private static Brush? _sendButtonBrushOverride;

    // ── v2 additions: bubble/tool visibility, size, footer details, icon customization ──
    private static bool _showAssistantBubbles = true;
    private static bool _showToolCalls = true;
    private static double _bubbleMaxWidth = 560d;
    private static double _bubbleSideMargin = 8d;

    private static bool _showSenderName = true;
    private static bool _showModelName = true;
    private static bool _showTokens;
    private static bool _showContextPercent;

    // Default icon glyphs match production OpenClawComposer.cs.
    private static string _sendIconGlyph   = "\uE724";
    private static bool   _sendIconShow    = true;
    private static string _attachIconGlyph = "\uE723";
    private static bool   _attachIconShow  = true;
    private static string _voiceIconGlyph  = "\uE720";
    private static bool   _voiceIconShow   = true;
    private static string _moreIconGlyph   = "\uE712";
    private static bool   _moreIconShow    = true;
    private static string _stopIconGlyph   = "\uE71A";
    private static bool   _stopIconShow    = true;

    // ---- Surface (A) ----

    public static ChatVariation Variation
    {
        get => _variation;
        set { if (_variation != value) { _variation = value; RaiseChanged(); } }
    }

    public static ChatBackdropMode BackdropMode
    {
        get => _backdropMode;
        set { if (_backdropMode != value) { _backdropMode = value; RaiseChanged(); } }
    }

    public static ChatPreviewTheme PreviewTheme
    {
        get => _previewTheme;
        set { if (_previewTheme != value) { _previewTheme = value; RaiseChanged(); } }
    }

    /// <summary>
    /// When true, the chat is hosted inside a window/page that already paints a
    /// SystemBackdrop, so backdrop changes are no-ops at the chat-surface level
    /// (the host owns the backdrop). Mirrors v2 <c>UsesHostBackdrop</c>.
    /// </summary>
    public static bool UsesHostBackdrop
    {
        get => _usesHostBackdrop;
        set { if (_usesHostBackdrop != value) { _usesHostBackdrop = value; RaiseChanged(); } }
    }

    // ---- Bubble / Layout (C) ----

    public static double BubbleCornerRadius
    {
        get => _bubbleCornerRadius;
        set { if (_bubbleCornerRadius != value) { _bubbleCornerRadius = value; RaiseChanged(); } }
    }

    public static double Gutter
    {
        get => _gutter;
        set { if (_gutter != value) { _gutter = value; RaiseChanged(); } }
    }

    public static double MessageGap
    {
        get => _messageGap;
        set { if (_messageGap != value) { _messageGap = value; RaiseChanged(); } }
    }

    public static ChatPaddingDensity PaddingDensity
    {
        get => _paddingDensity;
        set { if (_paddingDensity != value) { _paddingDensity = value; RaiseChanged(); } }
    }

    public static bool ShowTimestamps
    {
        get => _showTimestamps;
        set { if (_showTimestamps != value) { _showTimestamps = value; RaiseChanged(); } }
    }

    // ---- Avatar (D) ----

    public static bool ShowAvatars
    {
        get => _showAvatars;
        set { if (_showAvatars != value) { _showAvatars = value; RaiseChanged(); } }
    }

    public static ChatAvatarMode AvatarMode
    {
        get => _avatarMode;
        set { if (_avatarMode != value) { _avatarMode = value; RaiseChanged(); } }
    }

    // ---- Composer (E) ----

    public static ChatComposerLayout ComposerLayout
    {
        get => _composerLayout;
        set { if (_composerLayout != value) { _composerLayout = value; RaiseChanged(); } }
    }

    public static double ComposerCornerRadius
    {
        get => _composerCornerRadius;
        set { if (_composerCornerRadius != value) { _composerCornerRadius = value; RaiseChanged(); } }
    }

    public static double ComposerIconSize
    {
        get => _composerIconSize;
        set { if (_composerIconSize != value) { _composerIconSize = value; RaiseChanged(); } }
    }

    public static double SendButtonSize
    {
        get => _sendButtonSize;
        set { if (_sendButtonSize != value) { _sendButtonSize = value; RaiseChanged(); } }
    }

    // ---- Brush overrides (F). null = use theme/accent default. ----

    public static Brush? AccentBrushOverride
    {
        get => _accentBrushOverride;
        set { if (!ReferenceEquals(_accentBrushOverride, value)) { _accentBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? UserBubbleBrushOverride
    {
        get => _userBubbleBrushOverride;
        set { if (!ReferenceEquals(_userBubbleBrushOverride, value)) { _userBubbleBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? AssistantBubbleBrushOverride
    {
        get => _assistantBubbleBrushOverride;
        set { if (!ReferenceEquals(_assistantBubbleBrushOverride, value)) { _assistantBubbleBrushOverride = value; RaiseChanged(); } }
    }

    public static Brush? SendButtonBrushOverride
    {
        get => _sendButtonBrushOverride;
        set { if (!ReferenceEquals(_sendButtonBrushOverride, value)) { _sendButtonBrushOverride = value; RaiseChanged(); } }
    }

    /// <summary>
    /// Fires whenever any toggle or override changes. Subscribers should
    /// invalidate any cached visuals derived from these values.
    /// </summary>
    public static event EventHandler? Changed;

    private static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // ──────────────────────────────────────────────────────────────────
    // v2 additions
    // ──────────────────────────────────────────────────────────────────

    // ---- Bubble / tool visibility + sizing (extends C) ----

    public static bool ShowAssistantBubbles
    {
        get => _showAssistantBubbles;
        set { if (_showAssistantBubbles != value) { _showAssistantBubbles = value; RaiseChanged(); } }
    }

    public static bool ShowToolCalls
    {
        get => _showToolCalls;
        set { if (_showToolCalls != value) { _showToolCalls = value; RaiseChanged(); } }
    }

    public static double BubbleMaxWidth
    {
        get => _bubbleMaxWidth;
        set { if (_bubbleMaxWidth != value) { _bubbleMaxWidth = value; RaiseChanged(); } }
    }

    /// <summary>Distance between the bubble and the avatar (or the wall when avatar hidden).</summary>
    public static double BubbleSideMargin
    {
        get => _bubbleSideMargin;
        set { if (_bubbleSideMargin != value) { _bubbleSideMargin = value; RaiseChanged(); } }
    }

    // ---- Footer detail toggles (extends ShowTimestamps) ----

    public static bool ShowSenderName
    {
        get => _showSenderName;
        set { if (_showSenderName != value) { _showSenderName = value; RaiseChanged(); } }
    }

    public static bool ShowModelName
    {
        get => _showModelName;
        set { if (_showModelName != value) { _showModelName = value; RaiseChanged(); } }
    }

    public static bool ShowTokens
    {
        get => _showTokens;
        set { if (_showTokens != value) { _showTokens = value; RaiseChanged(); } }
    }

    public static bool ShowContextPercent
    {
        get => _showContextPercent;
        set { if (_showContextPercent != value) { _showContextPercent = value; RaiseChanged(); } }
    }

    // ---- Composer icon customization (extends E) ----

    public static string SendIconGlyph   { get => _sendIconGlyph;   set { if (_sendIconGlyph   != value) { _sendIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   SendIconShow    { get => _sendIconShow;    set { if (_sendIconShow    != value) { _sendIconShow    = value;     RaiseChanged(); } } }
    public static string AttachIconGlyph { get => _attachIconGlyph; set { if (_attachIconGlyph != value) { _attachIconGlyph = value ?? ""; RaiseChanged(); } } }
    public static bool   AttachIconShow  { get => _attachIconShow;  set { if (_attachIconShow  != value) { _attachIconShow  = value;     RaiseChanged(); } } }
    public static string VoiceIconGlyph  { get => _voiceIconGlyph;  set { if (_voiceIconGlyph  != value) { _voiceIconGlyph  = value ?? ""; RaiseChanged(); } } }
    public static bool   VoiceIconShow   { get => _voiceIconShow;   set { if (_voiceIconShow   != value) { _voiceIconShow   = value;     RaiseChanged(); } } }
    public static string MoreIconGlyph   { get => _moreIconGlyph;   set { if (_moreIconGlyph   != value) { _moreIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   MoreIconShow    { get => _moreIconShow;    set { if (_moreIconShow    != value) { _moreIconShow    = value;     RaiseChanged(); } } }
    public static string StopIconGlyph   { get => _stopIconGlyph;   set { if (_stopIconGlyph   != value) { _stopIconGlyph   = value ?? ""; RaiseChanged(); } } }
    public static bool   StopIconShow    { get => _stopIconShow;    set { if (_stopIconShow    != value) { _stopIconShow    = value;     RaiseChanged(); } } }
}
