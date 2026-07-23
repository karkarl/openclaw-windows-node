using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

public sealed record OpenClawReactorChatRootProps(
    IChatDataProvider Provider,
    ReactorChatHostCallbacks HostCallbacks,
    string? InitialThreadId = null,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest = null,
    Action? OnAttachClick = null,
    Action? OnSettingsClick = null,
    Action<bool>? OnSpeakerMuteChanged = null,
    Func<string, string?, Task<bool>>? ConfirmResetAsync = null,
    bool InitialMuted = false,
    bool IsCompact = false);

/// <summary>
/// Production Reactor root for the native chat surface. It owns the provider
/// subscription and renders the message timeline and composer in one tree.
/// </summary>
public sealed class OpenClawReactorChatRoot : Component<OpenClawReactorChatRootProps>
{
    private static bool s_showToolCalls = true;
    private static int s_toolCallsCollapseVersion;
    private static event EventHandler? ToolCallsVisibilityChanged;

    private string? _pendingSelectedThreadId;

    public static void SetToolCallsVisible(bool visible)
    {
        if (!visible && s_showToolCalls)
            s_toolCallsCollapseVersion++;

        s_showToolCalls = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var initialSelection = props.InitialThreadId
            ?? (props.Provider as OpenClawChatDataProvider)?.CachedLastChatState?.DefaultThreadId;
        var (selectedId, setSelectedId) = UseState<string?>(initialSelection, threadSafe: true);
        var selectedIdRef = UseRef<string?>(initialSelection);
        selectedIdRef.Current = selectedId;
        var (pendingAttachments, setPendingAttachments) =
            UseState<IReadOnlyList<ChatAttachment>>(Array.Empty<ChatAttachment>(), threadSafe: true);
        var pendingAttachmentsRef = UseRef<IReadOnlyList<ChatAttachment>>(pendingAttachments);
        pendingAttachmentsRef.Current = pendingAttachments;
        var (speakerMuted, setSpeakerMuted) = UseState(props.InitialMuted, threadSafe: true);
        var (voiceTranscript, setVoiceTranscript) = UseState<string?>(null, threadSafe: true);
        var (voiceAudioLevel, setVoiceAudioLevel) = UseState(0f, threadSafe: true);
        var (scrollToBottomToken, setScrollToBottomToken) = UseState(0, threadSafe: true);
        var (showToolCalls, setShowToolCalls) = UseState(s_showToolCalls, threadSafe: true);
        var (toolCallsCollapseVersion, setToolCallsCollapseVersion) =
            UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var (firstSendInFlight, setFirstSendInFlight) = UseState(false, threadSafe: true);

        void UpdatePendingAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            pendingAttachmentsRef.Current = attachments;
            setPendingAttachments(attachments);
        }

        props.HostCallbacks.AttachFiles = attachments =>
        {
            if (attachments.Count > 0)
                UpdatePendingAttachments(pendingAttachmentsRef.Current.Concat(attachments).ToArray());
        };
        props.HostCallbacks.SetVoiceTranscript = setVoiceTranscript;
        props.HostCallbacks.SetVoiceAudioLevel = setVoiceAudioLevel;
        props.HostCallbacks.SetSpeakerMuted = setSpeakerMuted;

        UseEffect((Func<Action>)(() => () => props.HostCallbacks.Clear()), props.HostCallbacks);

        UseEffect((Func<Action>)(() =>
        {
            EventHandler visibilityChanged = (_, _) =>
            {
                setShowToolCalls(s_showToolCalls);
                setToolCallsCollapseVersion(s_toolCallsCollapseVersion);
            };
            ToolCallsVisibilityChanged += visibilityChanged;
            return () => ToolCallsVisibilityChanged -= visibilityChanged;
        }), Array.Empty<object>());

        UseEffect((Func<Action>)(() =>
        {
            var provider = props.Provider;
            EventHandler<ChatDataChangedEventArgs> onChanged = (_, args) =>
            {
                setSnapshot(args.Snapshot);
                if (args.Snapshot.ComposeTarget.SessionKey is { } composeKey
                    && args.Snapshot.Timelines.TryGetValue(composeKey, out var timeline)
                    && timeline.Entries.Any(entry => entry.Kind == ChatTimelineItemKind.User))
                {
                    setFirstSendInFlight(false);
                }

                if (selectedIdRef.Current is null && args.Snapshot.DefaultThreadId is { } defaultThreadId)
                {
                    selectedIdRef.Current = defaultThreadId;
                    setSelectedId(defaultThreadId);
                }
            };

            provider.Changed += onChanged;
            _ = LoadAsync(
                provider,
                setSnapshot,
                () => selectedIdRef.Current,
                next =>
                {
                    selectedIdRef.Current = next;
                    setSelectedId(next);
                });
            return () => provider.Changed -= onChanged;
        }), props.Provider);

        if (snapshot is null)
            return RenderLoading();

        var selectedMaterializedThread = selectedId is null
            ? null
            : snapshot.Threads.FirstOrDefault(thread => string.Equals(thread.Id, selectedId, StringComparison.Ordinal));
        if (selectedMaterializedThread is null
            && selectedId is not null
            && snapshot.DefaultThreadId is { } fallbackId
            && ChatLifecycleSelectionPolicy.ShouldFallback(
                selectedId,
                _pendingSelectedThreadId,
                fallbackId))
        {
            selectedIdRef.Current = fallbackId;
            setSelectedId(fallbackId);
            selectedMaterializedThread = snapshot.Threads.FirstOrDefault(thread =>
                string.Equals(thread.Id, fallbackId, StringComparison.Ordinal));
        }

        var effectiveThread = selectedMaterializedThread ?? CreateComposeOnlyThread(props.Provider, snapshot);
        if (effectiveThread is { } selected && string.Equals(_pendingSelectedThreadId, selected.Id, StringComparison.Ordinal))
            _pendingSelectedThreadId = null;

        var connectionState = ToConnectionState(snapshot.ConnectionStatus);
        var isGatewayConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        if (isGatewayConnected
            && selectedMaterializedThread is not null
            && props.Provider is OpenClawChatDataProvider nativeProvider)
        {
            RunFireAndForget(ct => nativeProvider.LoadHistoryAsync(selectedMaterializedThread.Id, force: false, ct));
        }

        var timeline = effectiveThread is not null
            && snapshot.Timelines.TryGetValue(effectiveThread.Id, out var currentTimeline)
            ? currentTimeline
            : ChatTimelineState.Initial();
        var timelineGeneration = effectiveThread is not null
            && snapshot.TimelineGenerations?.TryGetValue(effectiveThread.Id, out var generation) == true
                ? generation
                : 0L;
        var entryMetadata = effectiveThread is not null && props.Provider is OpenClawChatDataProvider metadataProvider
            ? metadataProvider.GetEntryMetadata(effectiveThread.Id)
            : null;
        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var queuedMessages = effectiveThread is not null
            && snapshot.QueuedMessagesByThread?.TryGetValue(effectiveThread.Id, out var queued) == true
                ? queued
                : Array.Empty<ChatQueuedMessage>();
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);
        var currentTurnHasAssistant = false;
        for (var index = timeline.Entries.Count - 1; index >= 0; index--)
        {
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.User)
                break;
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.Assistant)
            {
                currentTurnHasAssistant = true;
                break;
            }
        }

        var showThinking = timeline.TurnActive && !currentTurnHasAssistant;
        var isEmptyConversation = entries.Count == 0 && !showThinking && timeline.PendingPermission is null;
        var isComposeOnly = effectiveThread is not null && selectedMaterializedThread is null;
        var mode = effectiveThread is null
                   || (isEmptyConversation && !isComposeOnly && !timeline.HistoryLoaded)
            ? ReactorChatTimelineMode.Loading
            : isEmptyConversation
                ? ReactorChatTimelineMode.Empty
                : ReactorChatTimelineMode.Timeline;

        var timelineProps = new OpenClawChatTimelineProps(
            effectiveThread?.Id,
            entries,
            false,
            null,
            entryMetadata,
            timelineGeneration,
            "OpenClaw Windows Tray",
            "Assistant",
            effectiveThread?.Model,
            showToolCalls
                ? ChatUsageFormatter.Format(entries, entryMetadata) ?? ChatUsageFormatter.Format(effectiveThread)
                : null,
            showThinking,
            showToolCalls,
            toolCallsCollapseVersion,
            props.OnReadAloud,
            props.OnStopSpeaking,
            scrollToBottomToken,
            effectiveThread is { } permissionThread
                ? (requestId, action) => OnPermission(permissionThread.Id, requestId, action)
                : null);

        void SelectThread(string threadId)
        {
            _pendingSelectedThreadId = threadId;
            selectedIdRef.Current = threadId;
            setSelectedId(threadId);
            if (props.Provider is OpenClawChatDataProvider native)
                native.RememberSelectedThread(threadId);
        }

        Action<string>? onSuggestionPicked = null;
        if (mode == ReactorChatTimelineMode.Empty && effectiveThread is { } suggestionThread)
        {
            onSuggestionPicked = suggestion =>
            {
                if (firstSendInFlight)
                    return;

                setFirstSendInFlight(true);
                ObserveFireAndForget(SendAsync(
                    suggestionThread.Id,
                    suggestionThread.Title,
                    suggestion,
                    Array.Empty<ChatAttachment>(),
                    setScrollToBottomToken,
                    scrollToBottomToken,
                    SelectThread));
            };
        }

        var timelineElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode,
            timelineProps,
            onSuggestionPicked,
            firstSendInFlight));
        var composerElement = effectiveThread is null
            ? Empty()
            : Component<ReactorChatComposer, ReactorChatComposerProps>(new(
                connectionState,
                timeline.TurnActive,
                effectiveThread,
                VisibleChannels(snapshot.Threads, effectiveThread),
                snapshot.AvailableModels,
                snapshot.ModelChoices,
                timeline.TurnActive || hasPendingQueuedSend,
                pendingAttachments,
                queuedMessages,
                async (message, attachments) =>
                {
                    var accepted = await SendAsync(
                        effectiveThread.Id,
                        effectiveThread.Title,
                        message,
                        attachments,
                        setScrollToBottomToken,
                        scrollToBottomToken,
                        SelectThread);
                    if (accepted)
                        UpdatePendingAttachments(RemoveSubmittedAttachments(pendingAttachmentsRef.Current, attachments));
                    return accepted;
                },
                () => OnStop(effectiveThread.Id),
                SelectThread,
                model => ObserveFireAndForget(props.Provider.SetModelAsync(effectiveThread.Id, model)),
                () => ObserveFireAndForget(props.Provider.ClearModelAsync(effectiveThread.Id)),
                level => RunFireAndForget(ct => props.Provider.SetThinkingLevelAsync(effectiveThread.Id, level, ct)),
                allowAll => RunFireAndForget(ct => props.Provider.SetPermissionModeAsync(effectiveThread.Id, allowAll, ct)),
                props.OnVoiceRequest,
                props.OnAttachClick,
                speakerMuted,
                () =>
                {
                    var next = !speakerMuted;
                    setSpeakerMuted(next);
                    props.OnSpeakerMuteChanged?.Invoke(next);
                },
                props.OnSettingsClick,
                voiceTranscript,
                voiceAudioLevel,
                starter => props.HostCallbacks.TriggerVoiceRecording = starter,
                attachment => UpdatePendingAttachments(pendingAttachmentsRef.Current.Concat(new[] { attachment }).ToArray()),
                attachment => UpdatePendingAttachments(RemoveAttachment(pendingAttachmentsRef.Current, attachment)),
                queuedMessageId => RunFireAndForget(ct => props.Provider.CancelQueuedMessageAsync(effectiveThread.Id, queuedMessageId, ct)),
                props.IsCompact));

        return Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            timelineElement.Grid(row: 0),
            composerElement.Grid(row: 1))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private static Element RenderLoading() =>
        Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            ReactorChatTimelineMode.Loading,
            new OpenClawChatTimelineProps(null, Array.Empty<ChatTimelineItem>(), false, null),
            null,
            false));

    private ChatThread? CreateComposeOnlyThread(
        IChatDataProvider provider,
        ChatDataSnapshot snapshot)
    {
        var composeKey = _pendingSelectedThreadId
            ?? (snapshot.ComposeTarget.IsReady ? snapshot.ComposeTarget.SessionKey : null);
        if (composeKey is null)
            return null;

        var cached = (provider as OpenClawChatDataProvider)?.CachedLastChatState;
        return new ChatThread
        {
            Id = composeKey,
            AgentId = snapshot.ComposeTarget.AgentId,
            Title = _pendingSelectedThreadId is null
                ? cached?.ThreadTitle ?? "OpenClaw Windows Tray"
                : LocalizationHelper.GetString("Chat_PendingNewSessionTitle"),
            Model = cached?.Model,
            ModelProvider = cached?.ModelProvider,
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
        };
    }

    private static IReadOnlyList<ChatThread> VisibleChannels(ChatThread[] threads, ChatThread effectiveThread)
    {
        var visible = SessionVisibilityFilter.VisibleChatPickerThreads(threads)
            .Where(thread => !string.IsNullOrWhiteSpace(thread.Title)
                && thread.IsVisibleInSessionPicker(effectiveThread.Id))
            .ToList();
        if (!visible.Any(thread => string.Equals(thread.Id, effectiveThread.Id, StringComparison.Ordinal)))
            visible.Insert(0, effectiveThread);
        return visible;
    }

    private async Task<bool> SendAsync(
        string threadId,
        string? displayName,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        Action<int> setScrollToBottomToken,
        int scrollToBottomToken,
        Action<string> onLifecycleSessionCreated)
    {
        setScrollToBottomToken(scrollToBottomToken + 1);
        var provider = Props.Provider;
        if (provider is OpenClawChatDataProvider native
            && ChatLifecycleCommandParser.TryParse(message, attachments.Count > 0, out var command))
        {
            if (ChatLifecycleCommandExecutionPolicy.ShouldQueue(command))
                return await native.EnqueueCompactCommandAsync(threadId);

            if (command == ChatLifecycleCommandKind.Reset
                && Props.ConfirmResetAsync is not null
                && !await Props.ConfirmResetAsync(threadId, displayName))
            {
                return false;
            }

            var result = await native.ExecuteLifecycleCommandAsync(threadId, command);
            if (result.Succeeded && result.NewSessionKey is { } sessionKey)
                onLifecycleSessionCreated(sessionKey);
            return result.Succeeded;
        }

        try
        {
            await provider.SendMessageAsync(threadId, message, CancellationToken.None, attachments);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] send failed: {ex}");
            return false;
        }
    }

    private void OnStop(string threadId) =>
        RunFireAndForget(ct => Props.Provider.StopResponseAsync(threadId, ct));

    private void OnPermission(string threadId, string requestId, string action) =>
        RunFireAndForget(ct => Props.Provider.RespondToPermissionAsync(threadId, requestId, action, ct));

    private static IReadOnlyList<ChatAttachment> RemoveAttachment(
        IReadOnlyList<ChatAttachment> attachments,
        ChatAttachment attachment)
    {
        var next = new List<ChatAttachment>(attachments.Count);
        var removed = false;
        foreach (var current in attachments)
        {
            if (!removed && ReferenceEquals(current, attachment))
            {
                removed = true;
                continue;
            }

            next.Add(current);
        }
        return removed ? next : attachments;
    }

    private static IReadOnlyList<ChatAttachment> RemoveSubmittedAttachments(
        IReadOnlyList<ChatAttachment> attachments,
        IReadOnlyList<ChatAttachment> submitted) =>
        attachments.Where(attachment => !submitted.Contains(attachment)).ToArray();

    private static string ToConnectionState(string? value) =>
        value?.StartsWith("Incompatible", StringComparison.OrdinalIgnoreCase) == true
            ? "incompatible-gateway"
            : value?.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) == true
                ? "connected"
                : value?.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) == true
                    ? "connecting"
                    : "disconnected";

    private static void RunFireAndForget(Func<CancellationToken, Task> operation)
    {
        _ = Task.Run(async () =>
        {
            try { await operation(CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        });
    }

    private static void ObserveFireAndForget(Task task)
    {
        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        }
    }

    private static async Task LoadAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Func<string?> getSelected,
        Action<string?> setSelected)
    {
        try
        {
            var snapshot = await provider.LoadAsync();
            setSnapshot(snapshot);
            if (getSelected() is null && snapshot.DefaultThreadId is { } defaultThreadId)
                setSelected(defaultThreadId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] load failed: {ex}");
        }
    }
}

public sealed record ReactorChatComposerProps(
    string ConnectionState,
    bool TurnActive,
    ChatThread CurrentThread,
    IReadOnlyList<ChatThread> AvailableChannels,
    string[] AvailableModels,
    IReadOnlyList<ChatModelChoice>? ModelChoices,
    bool MessageOptionsDisabled,
    IReadOnlyList<ChatAttachment> PendingAttachments,
    IReadOnlyList<ChatQueuedMessage> QueuedMessages,
    Func<string, IReadOnlyList<ChatAttachment>, Task<bool>> OnSend,
    Action OnStop,
    Action<string> OnChannelChanged,
    Action<string> OnModelChanged,
    Action OnModelCleared,
    Action<string> OnThinkingLevelChanged,
    Action<bool> OnPermissionsChanged,
    Func<CancellationToken, Action?, Task<string?>>? OnVoiceRequest,
    Action? OnAttachClick,
    bool IsSpeakerMuted,
    Action OnSpeakerToggle,
    Action? OnSettingsClick,
    string? VoiceTranscript,
    float VoiceAudioLevel,
    Action<Action> RegisterVoiceStarter,
    Action<ChatAttachment> OnAttachmentPasted,
    Action<ChatAttachment> OnAttachmentRemoved,
    Action<string> OnQueuedMessageCancel,
    bool IsCompact);

public sealed class ReactorChatComposer : Component<ReactorChatComposerProps>
{
    private static readonly string[] ThinkingLevels = ["off", "minimal", "low", "medium", "high"];
    private static readonly ConditionalWeakTable<FrameworkElement, ThemeCallbackState> ThemeCallbacks = new();
    private static readonly global::Windows.UI.ViewManagement.AccessibilitySettings? AccessibilitySettings =
        CreateAccessibilitySettings();

    private sealed class ThemeCallbackState(Action apply)
    {
        public Action Apply { get; set; } = apply;
        public global::Windows.Foundation.TypedEventHandler<
            global::Windows.UI.ViewManagement.AccessibilitySettings,
            object>? HighContrastChanged { get; set; }
        public bool HighContrastEventUnavailable { get; set; }
    }

    private static void ApplyTheme(FrameworkElement control, Action apply)
    {
        apply();
        if (ThemeCallbacks.TryGetValue(control, out var state))
        {
            state.Apply = apply;
            EnsureHighContrastCallback(control, state);
            return;
        }

        state = new ThemeCallbackState(apply);
        ThemeCallbacks.Add(control, state);
        control.ActualThemeChanged += static (sender, _) =>
        {
            if (sender is FrameworkElement element
                && ThemeCallbacks.TryGetValue(element, out var callback))
                callback.Apply();
        };
        control.Loaded += static (sender, _) =>
        {
            if (sender is FrameworkElement element
                && ThemeCallbacks.TryGetValue(element, out var callback))
            {
                callback.Apply();
                EnsureHighContrastCallback(element, callback);
            }
        };
        control.Unloaded += static (sender, _) =>
        {
            if (sender is FrameworkElement element
                && ThemeCallbacks.TryGetValue(element, out var callback)
                && callback.HighContrastChanged is { } handler
                && AccessibilitySettings is { } accessibilitySettings)
            {
                try
                {
                    accessibilitySettings.HighContrastChanged -= handler;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // The optional WinRT event source can be unavailable while a view tears down.
                }
                callback.HighContrastChanged = null;
            }
        };
        EnsureHighContrastCallback(control, state);
    }

    private static global::Windows.UI.ViewManagement.AccessibilitySettings? CreateAccessibilitySettings()
    {
        try
        {
            return new global::Windows.UI.ViewManagement.AccessibilitySettings();
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureHighContrastCallback(
        FrameworkElement control,
        ThemeCallbackState state)
    {
        if (AccessibilitySettings is null
            || state.HighContrastChanged is not null
            || state.HighContrastEventUnavailable)
            return;

        global::Windows.Foundation.TypedEventHandler<
            global::Windows.UI.ViewManagement.AccessibilitySettings,
            object> handler = (_, _) =>
        {
            control.DispatcherQueue?.TryEnqueue(() =>
            {
                if (ThemeCallbacks.TryGetValue(control, out var callback))
                    callback.Apply();
            });
        };
        try
        {
            AccessibilitySettings.HighContrastChanged += handler;
            state.HighContrastChanged = handler;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            state.HighContrastEventUnavailable = true;
            OpenClawTray.Services.Logger.Warn(
                $"[ReactorChatComposer] High Contrast change notifications are unavailable: {ex.Message}");
        }
    }

    private static Brush ResolveThemeBrush(string resourceKey, ElementTheme theme)
    {
        if (FindThemedResource(resourceKey, theme) is Brush themed)
            return themed;
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true
            && value is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static object? FindThemedResource(string resourceKey, ElementTheme theme)
    {
        if (Application.Current?.Resources is not { } root)
            return null;

        var themeNames = IsHighContrast()
            ? new[] { "HighContrast" }
            : theme switch
            {
                ElementTheme.Dark => ["Dark", "Default"],
                ElementTheme.Light => ["Light"],
                _ => Array.Empty<string>(),
            };
        return themeNames
            .Select(themeName => SearchThemeDictionaries(root, resourceKey, themeName, 0))
            .FirstOrDefault(value => value is not null);
    }

    private static bool IsHighContrast()
    {
        return AccessibilitySettings?.HighContrast ?? false;
    }

    private static object? SearchThemeDictionaries(
        ResourceDictionary dictionary,
        string resourceKey,
        string themeName,
        int depth)
    {
        if (depth > 6)
            return null;

        if (dictionary.ThemeDictionaries.TryGetValue(themeName, out var entry)
            && entry is ResourceDictionary themed
            && LookupResource(themed, resourceKey) is { } value)
            return value;

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (SearchThemeDictionaries(merged, resourceKey, themeName, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    private static object? LookupResource(ResourceDictionary dictionary, string resourceKey)
    {
        if (dictionary.TryGetValue(resourceKey, out var value))
            return value;

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (LookupResource(merged, resourceKey) is { } found)
                return found;
        }

        return null;
    }

    public override Element Render()
    {
        var props = Props;
        var (text, setText) = UseState(string.Empty, threadSafe: true);
        var (isSending, setIsSending) = UseState(false, threadSafe: true);
        var (isRecording, setIsRecording) = UseState(false, threadSafe: true);
        var inputRevision = UseRef(0);
        var sendInFlight = UseRef(false);
        var voiceCancellation = UseRef<CancellationTokenSource?>(null);
        var voiceOperation = UseRef(0);
        var voiceStopOperation = UseRef(0);
        var pasteHooked = UseRef(false);
        var inputText = UseRef(text);
        var mounted = UseRef(true);
        inputText.Current = text;
        UseEffect((Func<Action>)(() => () =>
        {
            mounted.Current = false;
            voiceCancellation.Current?.Cancel();
            voiceCancellation.Current?.Dispose();
            voiceCancellation.Current = null;
            voiceOperation.Current++;
        }), Array.Empty<object>());

        void StartVoiceRecording()
        {
            if (props.OnVoiceRequest is null || isRecording)
                return;

            var cancellation = new CancellationTokenSource();
            voiceCancellation.Current?.Cancel();
            voiceCancellation.Current?.Dispose();
            voiceCancellation.Current = cancellation;
            var operation = ++voiceOperation.Current;
            voiceStopOperation.Current = 0;
            setIsRecording(true);
            _ = ReceiveVoiceAsync(
                props.OnVoiceRequest,
                cancellation,
                operation,
                voiceOperation,
                voiceStopOperation,
                voiceCancellation,
                mounted,
                AppendVoiceTranscript,
                setIsRecording);
        }

        props.RegisterVoiceStarter(StartVoiceRecording);

        void SetText(string value)
        {
            inputRevision.Current++;
            inputText.Current = value;
            setText(value);
        }

        void AppendVoiceTranscript(string transcript)
        {
            var draft = inputText.Current.TrimEnd();
            SetText(draft.Length == 0 ? transcript : $"{draft} {transcript}");
        }

        void Send()
        {
            var message = text.Trim();
            if ((message.Length == 0 && props.PendingAttachments.Count == 0)
                || sendInFlight.Current
                || props.ConnectionState != "connected")
                return;

            sendInFlight.Current = true;
            setIsSending(true);
            _ = SendAsync(
                props.OnSend,
                message,
                props.PendingAttachments,
                inputRevision.Current,
                inputRevision,
                sendInFlight,
                SetText,
                setIsSending);
        }

        var modelChoices = props.ModelChoices is { Count: > 0 }
            ? props.ModelChoices
            : props.AvailableModels
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => new ChatModelChoice(model, model))
                .ToArray();
        var selectableModels = modelChoices.Where(model => model.IsSelectable).ToArray();
        var modelNames = new[] { Localized("Chat_Composer_Reasoning_Default", "Default") }
            .Concat(selectableModels.Select(ChatModelLabels.BuildMenuLabel))
            .ToArray();
        var modelIndex = string.IsNullOrWhiteSpace(props.CurrentThread.Model)
            ? 0
            : Math.Max(0, Array.FindIndex(
                selectableModels,
                model => model.MatchesModel(props.CurrentThread.Model, props.CurrentThread.ModelProvider)) + 1);
        var thinkingIndex = Math.Max(0, Array.IndexOf(
            ThinkingLevels,
            props.CurrentThread.ThinkingLevel ?? "medium"));
        var actionLabel = props.TurnActive
            ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
            : Localized("Chat_Composer_Tooltip_Send", "Send");
        var controlCornerRadius = new CornerRadius(4);

        void ApplySubtleButtonStyle(Button button)
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Background = transparent;
            button.BorderBrush = transparent;
            button.BorderThickness = new Thickness(0);
            button.Resources["ButtonBackground"] = transparent;
            button.Resources["ButtonBorderBrush"] = transparent;
            button.Resources["ButtonBorderBrushPointerOver"] = transparent;
            button.Resources["ButtonBorderBrushPressed"] = transparent;
            ApplyTheme(button, () =>
            {
                button.Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush", button.ActualTheme);
                button.Resources["ButtonBackgroundPointerOver"] =
                    ResolveThemeBrush("SubtleFillColorSecondaryBrush", button.ActualTheme);
                button.Resources["ButtonBackgroundPressed"] =
                    ResolveThemeBrush("SubtleFillColorTertiaryBrush", button.ActualTheme);
            });
        }

        Element IconButton(string glyph, string automationName, Action onClick, bool enabled = true)
        {
            return Button(
                    TextBlock(glyph).Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                    }),
                    onClick)
                .AutomationName(automationName)
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    ApplySubtleButtonStyle(button);
                    ToolTipService.SetToolTip(button, automationName);
                });
        }

        Element PickerButton(string label, string automationName, bool enabled, double maxLabelWidth)
        {
            return Button(
                    HStack(
                        4,
                        TextBlock(label).Set(textBlock =>
                        {
                            textBlock.FontSize = 13;
                            textBlock.MaxWidth = maxLabelWidth;
                            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                            textBlock.TextWrapping = TextWrapping.NoWrap;
                        }),
                        TextBlock("\uE70D").Set(textBlock =>
                        {
                            textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                            textBlock.FontSize = 10;
                        })),
                    () => { })
                .AutomationName(automationName)
                .Set(button =>
                {
                    button.Height = 32;
                    button.MinHeight = 32;
                    button.MinWidth = 0;
                    button.Padding = new Thickness(8, 0, 8, 0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    ApplySubtleButtonStyle(button);
                });
        }

        var attachmentRows = props.PendingAttachments
            .Select(attachment =>
                (Element)HStack(
                    6,
                    TextBlock(attachment.FileName).FontSize(12),
                    Button("×", () => props.OnAttachmentRemoved(attachment))
                        .SubtleButton()
                        .AutomationName("Remove attachment")))
            .ToArray();
        var audioLevel = Math.Clamp(props.VoiceAudioLevel, 0f, 1f);
        var voiceFeedbackText = string.IsNullOrWhiteSpace(props.VoiceTranscript)
            ? Localized("Chat_Voice_ListeningPrompt", "Listening…")
            : props.VoiceTranscript;
        var waveformBars = Enumerable.Range(0, 8)
            .Select(index =>
                (Element)Border(Empty())
                    .Width(2)
                    .Height(2 + (audioLevel * (index % 3 == 1 ? 10 : 7)))
                    .CornerRadius(1)
                    .VAlign(VerticalAlignment.Center)
                    .Set(border => ApplyTheme(border, () => border.Background =
                        ResolveThemeBrush("TextFillColorSecondaryBrush", border.ActualTheme))))
            .ToArray();
        Element voiceFeedback = !isRecording
            ? Empty()
            : Border(
                    HStack(
                        6,
                        Border(Empty())
                            .Width(6)
                            .Height(6)
                            .CornerRadius(3)
                            .Set(border => ApplyTheme(border, () => border.Background =
                                ResolveThemeBrush("TextFillColorSecondaryBrush", border.ActualTheme))),
                        TextBlock(voiceFeedbackText)
                            .FontSize(11)
                            .Set(textBlock => ApplyTheme(textBlock, () => textBlock.Foreground =
                                ResolveThemeBrush("TextFillColorSecondaryBrush", textBlock.ActualTheme))),
                        HStack(1, waveformBars)))
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Left);
        var queuedRows = props.QueuedMessages
            .Select((message, index) =>
            {
                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var actionKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailed"
                    : "Chat_Composer_QueuedMessageCancel";
                var actionAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageCancelAutomationFormat";
                var rowAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageAutomationFormat";
                var action = message.SendState == ChatQueuedMessageSendState.Sending
                    ? Empty()
                    : Button(Localized(actionKey, failed ? "Remove failed message" : "Cancel"),
                            () => props.OnQueuedMessageCancel(message.Id))
                        .SubtleButton()
                        .AutomationId($"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}")
                        .AutomationName(string.Format(
                            CultureInfo.CurrentCulture,
                            Localized(actionAutomationKey, "{0}: {1}"),
                            index + 1,
                            message.Text));
                var state = failed
                    ? (Element)TextBlock(Localized("Chat_Composer_QueuedMessageFailed", "Failed"))
                        .FontSize(12)
                    : Empty();
                var error = failed && !string.IsNullOrWhiteSpace(message.ErrorText)
                    ? (Element)TextBlock(message.ErrorText!).FontSize(12)
                    : Empty();
                return (Element)HStack(
                        6,
                        VStack(
                                4,
                                state,
                                TextBlock(message.Text).FontSize(12).MaxWidth(260),
                                error)
                            .HAlign(HorizontalAlignment.Left),
                        action)
                    .AutomationName(string.Format(
                        CultureInfo.CurrentCulture,
                        Localized(rowAutomationKey, "{0}"),
                        message.Text));
            })
            .ToArray();

        var input = TextBox(
                text,
                SetText,
                PlaceholderFor(props.ConnectionState))
            .AutomationId("ChatComposerInput")
            .AutomationName(PlaceholderFor(props.ConnectionState))
            .OnKeyDown((sender, args) =>
            {
                if (args.Key != global::Windows.System.VirtualKey.Enter)
                    return;

                args.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    global::Windows.System.VirtualKey.Shift);
                if (shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)
                    && sender is Microsoft.UI.Xaml.Controls.TextBox textBox)
                {
                    var current = textBox.Text ?? string.Empty;
                    var start = Math.Clamp(textBox.SelectionStart, 0, current.Length);
                    var end = Math.Clamp(start + textBox.SelectionLength, start, current.Length);
                    SetText(current[..start] + "\n" + current[end..]);
                    textBox.SelectionStart = start + 1;
                    textBox.SelectionLength = 0;
                }
                else
                {
                    Send();
                }
            })
            .TextWrapping(TextWrapping.Wrap)
            .Set(control =>
            {
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                control.MinHeight = 56;
                control.MaxHeight = 200;
                control.FontSize = 14;
                control.Padding = new Thickness(8);
                control.IsEnabled = props.ConnectionState == "connected";
                control.AcceptsReturn = false;
                control.BorderThickness = new Thickness(0);
                control.BorderBrush = transparent;
                control.Background = transparent;
                control.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
                control.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
                control.Resources["TextControlBackground"] = transparent;
                control.Resources["TextControlBackgroundFocused"] = transparent;
                control.Resources["TextControlBackgroundPointerOver"] = transparent;
                control.Resources["TextControlBorderBrush"] = transparent;
                control.Resources["TextControlBorderBrushFocused"] = transparent;
                control.Resources["TextControlBorderBrushPointerOver"] = transparent;
                if (!pasteHooked.Current)
                {
                    pasteHooked.Current = true;
                    control.Paste += async (_, args) =>
                    {
                        try
                        {
                            var attachment = await TryReadImageFromClipboardAsync();
                            if (attachment is null)
                                return;

                            args.Handled = true;
                            props.OnAttachmentPasted(attachment);
                        }
                        catch (Exception ex)
                        {
                            OpenClawTray.Services.Logger.Debug(
                                $"Reactor chat composer: clipboard image paste failed: {ex.Message}");
                        }
                    };
                }
            });

        var sessionPicker = MenuFlyout(
            PickerButton(
                props.CurrentThread.Title,
                $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {props.CurrentThread.Title}",
                !props.MessageOptionsDisabled && props.AvailableChannels.Count > 1,
                props.IsCompact ? 56 : 160),
            props.AvailableChannels
                .Select(thread => RadioMenuItem(
                    thread.Title,
                    "chat-sessions",
                    string.Equals(thread.Id, props.CurrentThread.Id, StringComparison.Ordinal),
                    () => props.OnChannelChanged(thread.Id)))
                .ToArray());

        var modelPickerLabel = modelIndex == 0
            ? Localized("Chat_Composer_Reasoning_Default", "Default")
            : selectableModels[modelIndex - 1].DisplayName;
        var modelPicker = MenuFlyout(
            PickerButton(
                modelPickerLabel,
                $"{Localized("Chat_Composer_Accessibility_Model", "Model")}: {modelPickerLabel}",
                !props.MessageOptionsDisabled,
                props.IsCompact ? 68 : 180),
            modelNames
                .Select((modelName, index) => RadioMenuItem(
                    modelName,
                    "chat-models",
                    index == modelIndex,
                    () =>
                    {
                        if (index == 0)
                            props.OnModelCleared();
                        else if (index <= selectableModels.Length)
                            props.OnModelChanged(selectableModels[index - 1].SelectionId);
                    }))
                .ToArray());

        var reasoningPicker = MenuFlyout(
            PickerButton(
                ThinkingLevels[thinkingIndex],
                $"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {ThinkingLevels[thinkingIndex]}",
                !props.MessageOptionsDisabled,
                props.IsCompact ? 54 : 96),
            ThinkingLevels
                .Select((level, index) => RadioMenuItem(
                    level,
                    "chat-thinking-level",
                    index == thinkingIndex,
                    () => props.OnThinkingLevelChanged(level)))
                .ToArray());

        var attachButton = IconButton(
            "\uE723",
            Localized("Chat_Composer_Tooltip_Attach", "Attach"),
            () => props.OnAttachClick?.Invoke(),
            props.OnAttachClick is not null);
        var voiceButton = IconButton(
            isRecording
                ? "\uE15B"
                : "\uE720",
            isRecording
                ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
                : Localized("Chat_Composer_Tooltip_Voice", "Voice"),
            () =>
            {
                if (isRecording)
                {
                    voiceStopOperation.Current = voiceOperation.Current;
                    voiceCancellation.Current?.Cancel();
                }
                else
                    StartVoiceRecording();
            },
            props.OnVoiceRequest is not null);
        var speakerButton = IconButton(
            props.IsSpeakerMuted ? "\uE74F" : "\uE767",
            props.IsSpeakerMuted ? "Unmute" : "Mute",
            props.OnSpeakerToggle);
        Element settingsButton = props.IsCompact || props.OnSettingsClick is null
            ? Empty()
            : IconButton(
                "\uE713",
                Localized("Chat_Composer_Tooltip_Settings", "Settings"),
                props.OnSettingsClick);

        Element primaryAction = props.TurnActive
            ? IconButton("\uE71A", actionLabel, props.OnStop)
            : Button(
                    TextBlock("\uE724").Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                    }),
                    Send)
                .AccentButton()
                .AutomationName(actionLabel)
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = props.ConnectionState == "connected"
                        && !isSending
                        && (!string.IsNullOrWhiteSpace(text) || props.PendingAttachments.Count > 0);
                    ToolTipService.SetToolTip(button, actionLabel);
                });

        var leftToolbar = HStack(8, attachButton, sessionPicker, modelPicker, reasoningPicker)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);
        var rightToolbar = HStack(8, voiceButton, speakerButton, settingsButton, primaryAction)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);
        var toolbar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            leftToolbar.Grid(row: 0, column: 0),
            rightToolbar.Grid(row: 0, column: 1));

        return Border(
            VStack(
                8,
                voiceFeedback,
                VStack(4, attachmentRows),
                VStack(4, queuedRows),
                input,
                toolbar)
            .Padding(8))
            .BorderThickness(1)
            .CornerRadius(8)
            .Margin(12)
            .Set(border => ApplyTheme(border, () =>
            {
                border.Background = ResolveThemeBrush("ControlFillColorDefaultBrush", border.ActualTheme);
                border.BorderBrush = ResolveThemeBrush("ControlStrokeColorDefaultBrush", border.ActualTheme);
            }))
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static async Task SendAsync(
        Func<string, IReadOnlyList<ChatAttachment>, Task<bool>> send,
        string message,
        IReadOnlyList<ChatAttachment> attachments,
        int submittedRevision,
        Ref<int> inputRevision,
        Ref<bool> sendInFlight,
        Action<string> setText,
        Action<bool> setIsSending)
    {
        try
        {
            if (await send(message, attachments)
                && ChatComposerSubmissionPolicy.ShouldClearInput(
                    submittedRevision,
                    inputRevision.Current))
                setText(string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] composer send failed: {ex}");
        }
        finally
        {
            sendInFlight.Current = false;
            setIsSending(false);
        }
    }

    private static async Task ReceiveVoiceAsync(
        Func<CancellationToken, Action?, Task<string?>> request,
        CancellationTokenSource cancellation,
        int operation,
        Ref<int> voiceOperation,
        Ref<int> voiceStopOperation,
        Ref<CancellationTokenSource?> voiceCancellation,
        Ref<bool> mounted,
        Action<string> setText,
        Action<bool> setIsRecording)
    {
        try
        {
            var transcript = await request(cancellation.Token, () => setIsRecording(true));
            var stoppedByUser = voiceStopOperation.Current == operation;
            if (mounted.Current
                && (!cancellation.IsCancellationRequested || stoppedByUser)
                && !string.IsNullOrWhiteSpace(transcript))
                setText(transcript);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Debug($"Reactor chat composer voice request failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(voiceCancellation.Current, cancellation))
                voiceCancellation.Current = null;
            cancellation.Dispose();
            if (voiceOperation.Current == operation)
                setIsRecording(false);
        }
    }

    private static async Task<ChatAttachment?> TryReadImageFromClipboardAsync()
    {
        var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (content is null
            || !content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
        {
            return null;
        }

        var streamRef = await content.GetBitmapAsync();
        using var input = await streamRef.OpenReadAsync();
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
        var bitmap = await decoder.GetSoftwareBitmapAsync();
        var output = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        var size = (long)output.Size;
        if (size > ChatAttachment.MaxSizeBytes)
            return null;

        output.Seek(0);
        var bytes = new byte[size];
        using (var reader = new global::Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);
        }

        return new ChatAttachment
        {
            Type = "image",
            MimeType = "image/png",
            FileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Content = Convert.ToBase64String(bytes),
            SizeBytes = size,
        };
    }

    private static string PlaceholderFor(string connectionState) => connectionState switch
    {
        "connected" => Localized("Chat_Composer_Placeholder_Connected", "Message Assistant (Enter to send)"),
        "connecting" => Localized("Chat_Composer_Placeholder_Connecting", "Connecting…"),
        "incompatible-gateway" => Localized(
            "Chat_Composer_Placeholder_IncompatibleGateway",
            "Gateway update required: incompatible version"),
        _ => Localized("Chat_Composer_Placeholder_NotConnected", "Not connected"),
    };

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}
