using OpenClaw.Chat;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Chat.Explorations;
using static Microsoft.UI.Reactor.Factories;
using static Microsoft.UI.Reactor.Core.Theme;

namespace OpenClawTray.Chat;

/// <summary>
/// Reactor root component used to render the OpenClaw chat surface (Header
/// + Timeline + InputBar + StatusBar). The surrounding XAML window/page owns
/// session navigation (via the existing NavigationView/ConversationsPage) so
/// no Sidebar is rendered here.
/// </summary>
public sealed class OpenClawChatRoot : Component
{
    private readonly IChatDataProvider _provider;
    private readonly string? _initialThreadId;
    private readonly Func<string, Task>? _onReadAloud;

    public OpenClawChatRoot(
        IChatDataProvider provider,
        string? initialThreadId = null,
        Func<string, Task>? onReadAloud = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _initialThreadId = initialThreadId;
        _onReadAloud = onReadAloud;
    }

    public override Element Render()
    {
        // Subscribe to ChatExplorationState — without this, Reactor may skip
        // re-rendering child Components (Composer/Timeline) because the props
        // they receive don't change. Bumping this Root's state invalidates
        // the whole tree so toggles always show in the live preview.
        var explorationRev = UseState(0, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            EventHandler h = (_, _) => explorationRev.Set(explorationRev.Value + 1);
            ChatExplorationState.Changed += h;
            return () => ChatExplorationState.Changed -= h;
        }));

        var snapshotState = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var selectedIdState = UseState<string?>(_initialThreadId, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            var setSnapshot = snapshotState.Set;
            var setSelected = selectedIdState.Set;
            var getSelected = (Func<string?>)(() => selectedIdState.Value);

            EventHandler<ChatDataChangedEventArgs> onChanged = (_, e) =>
            {
                setSnapshot(e.Snapshot);
                if (getSelected() is null && e.Snapshot.DefaultThreadId is { } d)
                    setSelected(d);
            };
            _provider.Changed += onChanged;
            _ = LoadAsync(_provider, setSnapshot, getSelected, setSelected);
            return () => _provider.Changed -= onChanged;
        }));

        var snapshot = snapshotState.Value;

        // Preview override (G) — let the explorations panel force a specific
        // lifecycle state without waiting for matching real backend conditions.
        // `Loading` short-circuits ahead of the real snapshot==null branch so
        // it stays previewable even after data has loaded.
        var previewState = ChatExplorationState.PreviewState;

        Element BuildLoadingElement()
        {
            var loadingBg = ChatExplorationState.BackdropMode == ChatBackdropMode.Solid
                ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["LayerFillColorDefaultBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return Border(
                VStack(8,
                    ProgressRing().Size(28, 28).HAlign(HorizontalAlignment.Center),
                    Caption(LocalizationHelper.GetString("Chat_Root_ConnectingToGateway")).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
                ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
            ).Background(loadingBg);
        }

        if (previewState == ChatPreviewState.Loading)
            return BuildLoadingElement();

        if (snapshot is null)
        {
            return BuildLoadingElement();
        }

        var selectedId = selectedIdState.Value ?? snapshot.DefaultThreadId;
        var selectedThread = selectedId is { } id
            ? Array.Find(snapshot.Threads, t => t.Id == id)
            : null;

        // Lazy-load history the first time a thread is selected.
        if (selectedThread is not null && _provider is OpenClawChatDataProvider native)
        {
            var threadId = selectedThread.Id;
            RunFireAndForget(ct => native.LoadHistoryAsync(threadId, force: false, ct));
        }

        var timeline = selectedThread is not null && snapshot.Timelines.TryGetValue(selectedThread.Id, out var tl)
            ? tl
            : ChatTimelineState.Initial();

        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var connectedRaw = snapshot.ConnectionStatus;
        var hostConnected = connectedRaw is not null
            && connectedRaw.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
        var connState = hostConnected ? "connected"
            : (connectedRaw is not null && connectedRaw.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase))
                ? "connecting"
                : "disconnected";

        // Header & divider intentionally hidden — the surrounding chrome
        // (NavigationView page or tray popup TitleBar) already shows the
        // session title; the in-chat header just duplicates it.
        Element header = Empty();

        // Per-entry metadata for the OpenClaw timeline footer (sender · time · model).
        IReadOnlyDictionary<string, ChatEntryMetadata>? entryMeta = null;
        if (selectedThread is not null && _provider is OpenClawChatDataProvider nativeForMeta)
            entryMeta = nativeForMeta.GetEntryMetadata(selectedThread.Id);

        // The agent name visible in the web UI footer is "Field" — that's the
        // gateway's default agent identity, not the chat thread title (which
        // is typically the operator client name like "OpenClaw Windows Tray").
        // TODO: wire to a real agent-name source (agents.list response or
        // sessionDefaults.defaultAgentId from hello-ok) once available.
        const string assistantSenderLabel = "Field";

        // Show inline "thinking" indicator when the turn is active but the
        // last visible entry is NOT an assistant block yet — i.e. we're between
        // the user's send and the first assistant delta arriving.
        var showThinking = timeline.TurnActive
            && (timeline.Entries.Count == 0
                || timeline.Entries[timeline.Entries.Count - 1].Kind != ChatTimelineItemKind.Assistant);

        // Apply preview-state overrides for the four data-dependent states.
        // These mutate locals only — real provider data on disk is untouched.
        var pendingPermissionOverride = timeline.PendingPermission;
        var turnActiveOverride = timeline.TurnActive;
        switch (previewState)
        {
            case ChatPreviewState.EmptyZero:
                selectedThread = null;
                break;

            case ChatPreviewState.EmptyThread:
                selectedThread ??= SynthesizePreviewThread(snapshot);
                entries = Array.Empty<ChatTimelineItem>();
                showThinking = false;
                pendingPermissionOverride = null;
                turnActiveOverride = false;
                break;

            case ChatPreviewState.Thinking:
                selectedThread ??= SynthesizePreviewThread(snapshot);
                if (entries.Count == 0
                    || entries[entries.Count - 1].Kind == ChatTimelineItemKind.Assistant)
                {
                    entries = new[]
                    {
                        new ChatTimelineItem("preview-u1", ChatTimelineItemKind.User,
                            "What's the weather like in Seattle today?")
                    };
                }
                showThinking = true;
                turnActiveOverride = true;
                pendingPermissionOverride = null;
                break;

            case ChatPreviewState.PendingPermission:
                selectedThread ??= SynthesizePreviewThread(snapshot);
                pendingPermissionOverride = new ChatPermissionRequest(
                    RequestId: "preview-perm",
                    PermissionKind: "execute",
                    ToolName: "shell",
                    Detail: "Run `git status` in the current repo.");
                break;
        }

        Element body = selectedThread is null
            ? PlaceholderEmptyState(connectedRaw)
            : Component<OpenClawChatTimeline, OpenClawChatTimelineProps>(new(
                SessionId: selectedThread.Id,
                Entries: entries,
                HasMoreHistory: false,
                OnLoadMoreHistory: null,
                EntryMetadata: entryMeta,
                UserSenderLabel: "OpenClaw Windows Tray (cli)",
                AssistantSenderLabel: assistantSenderLabel,
                DefaultModel: selectedThread.Model,
                ShowThinkingIndicator: showThinking,
                OnReadAloud: _onReadAloud));

        // Distinct list of channel labels (= thread titles) — feeds the
        // composer's first ComboBox so the user can switch chats from the
        // composer, not just the side rail.
        var channelTitles = snapshot.Threads
            .Select(t => t.Title)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Element composer = selectedThread is not null
            ? Component<OpenClawComposer, OpenClawComposerProps>(new(
                ConnectionState: connState,
                TurnActive: turnActiveOverride,
                PendingPermission: pendingPermissionOverride,
                ChannelLabel: selectedThread.Title ?? "main",
                AvailableChannels: channelTitles,
                AvailableModels: snapshot.AvailableModels,
                CurrentModel: selectedThread.Model,
                OnSend: msg => OnSend(selectedThread.Id, msg),
                OnStop: () => OnStop(selectedThread.Id),
                OnPermissionResponse: (rid, allow) => OnPermission(selectedThread.Id, rid, allow),
                OnChannelChanged: title =>
                {
                    var match = Array.Find(snapshot.Threads, t => t.Title == title);
                    if (match is not null) selectedIdState.Set(match.Id);
                },
                OnModelChanged: model => RunFireAndForget(ct => _provider.SetModelAsync(selectedThread.Id, model, ct)),
                OnPermissionsChanged: allowAll => RunFireAndForget(ct => _provider.SetPermissionModeAsync(selectedThread.Id, allowAll, ct))))
            : Empty();

        var divider = Empty();

        // Three rows now (composer absorbs the old StatusBar).
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0, column: 0),
            divider.Grid(row: 1, column: 0),
            body.Grid(row: 2, column: 0),
            composer.Grid(row: 3, column: 0)
        );
    }

    private static ChatThread SynthesizePreviewThread(ChatDataSnapshot snapshot)
    {
        return new ChatThread
        {
            Id = "preview-thread",
            Title = "Preview thread",
            Status = ChatThreadStatus.Running,
            Model = snapshot.AvailableModels is { Length: > 0 } m ? m[0] : null,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static Element PlaceholderEmptyState(string? connectionStatus)
    {
        var msg = connectionStatus is { } s && s.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)
            ? LocalizationHelper.GetString("Chat_Root_EmptyState_SelectSession")
            : (connectionStatus ?? LocalizationHelper.GetString("Chat_Composer_Placeholder_Connecting"));
        return Border(
            VStack(8,
                TextBlock("💬").FontSize(48).HAlign(HorizontalAlignment.Center),
                Caption(msg).Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
            ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
        );
    }

    private void OnSend(string threadId, string message)
    {
        RunFireAndForget(ct => _provider.SendMessageAsync(threadId, message, ct));
    }

    private void OnStop(string threadId)
    {
        RunFireAndForget(ct => _provider.StopResponseAsync(threadId, ct));
    }

    private void OnPermission(string threadId, string requestId, bool allow)
    {
        RunFireAndForget(ct => _provider.RespondToPermissionAsync(threadId, requestId, allow, ct));
    }

    private static void RunFireAndForget(Func<CancellationToken, Task> op)
    {
        _ = Task.Run(async () =>
        {
            try { await op(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] op failed: {ex}"); }
        });
    }

    private static async Task LoadAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Func<string?> getSelected,
        Action<string?> setSelected)
    {
        try
        {
            var snap = await provider.LoadAsync();
            setSnapshot(snap);
            if (getSelected() is null && snap.DefaultThreadId is { } d)
                setSelected(d);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] LoadAsync failed: {ex}");
        }
    }
}
