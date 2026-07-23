using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
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
                    .Background(new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)))
                    .CornerRadius(1)
                    .VAlign(VerticalAlignment.Center))
            .ToArray();
        Element voiceFeedback = !isRecording
            ? Empty()
            : Border(
                    HStack(
                        6,
                        Border(Empty())
                            .Width(6)
                            .Height(6)
                            .Background(new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                Color.FromArgb(0xFF, 0xD1, 0x34, 0x38)))
                            .CornerRadius(3),
                        TextBlock(voiceFeedbackText).FontSize(11),
                        HStack(1, waveformBars)))
                .Padding(8, 4)
                .CornerRadius(12)
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

        return Border(
            VStack(
                8,
                voiceFeedback,
                VStack(4, attachmentRows),
                VStack(4, queuedRows),
                TextBox(
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
                        control.MinHeight = props.IsCompact ? 48 : 64;
                        control.MaxHeight = props.IsCompact ? 96 : 160;
                        control.IsEnabled = props.ConnectionState == "connected";
                        control.AcceptsReturn = false;
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
                    }),
                HStack(
                    8,
                    Button(Localized("Chat_Composer_Tooltip_Attach", "Attach"), props.OnAttachClick)
                        .SubtleButton()
                        .Set(button => button.IsEnabled = props.OnAttachClick is not null),
                    MenuFlyout(
                        Button(props.CurrentThread.Title)
                            .SubtleButton()
                            .AutomationName(
                                $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {props.CurrentThread.Title}")
                            .Set(button => button.IsEnabled =
                                !props.MessageOptionsDisabled && props.AvailableChannels.Count > 1),
                        props.AvailableChannels
                            .Select(thread => RadioMenuItem(
                                thread.Title,
                                "chat-sessions",
                                string.Equals(thread.Id, props.CurrentThread.Id, StringComparison.Ordinal),
                                () => props.OnChannelChanged(thread.Id)))
                            .ToArray()),
                    ComboBox(modelNames, modelIndex, index =>
                    {
                        if (index == 0)
                            props.OnModelCleared();
                        else if (index > 0 && index <= selectableModels.Length)
                            props.OnModelChanged(selectableModels[index - 1].SelectionId);
                    })
                    .Header(Localized("Chat_Composer_Accessibility_Model", "Model"))
                    .Set(control => control.IsEnabled = !props.MessageOptionsDisabled),
                    ComboBox(ThinkingLevels, thinkingIndex, index =>
                    {
                        if (index >= 0 && index < ThinkingLevels.Length)
                            props.OnThinkingLevelChanged(ThinkingLevels[index]);
                    })
                    .Header(Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning"))
                    .Set(control => control.IsEnabled = !props.MessageOptionsDisabled),
                    Button(isRecording
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
                        })
                        .SubtleButton()
                        .Set(button => button.IsEnabled = props.OnVoiceRequest is not null),
                    Button(
                        props.IsSpeakerMuted ? "Speaker off" : "Speaker on",
                        props.OnSpeakerToggle)
                        .SubtleButton()
                        .AutomationName(props.IsSpeakerMuted ? "Unmute" : "Mute"),
                    Button(actionLabel, props.TurnActive ? props.OnStop : Send)
                        .AccentButton()
                        .Set(button => button.IsEnabled = props.TurnActive || (
                            props.ConnectionState == "connected"
                            && !isSending
                            && (!string.IsNullOrWhiteSpace(text) || props.PendingAttachments.Count > 0)))))
            .Padding(12))
            .Background(new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)))
            .BorderBrush(new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(8)
            .Margin(12)
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
