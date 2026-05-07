using ChatSample.Chat.Model;
using ChatSample.Chat.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public OpenClawChatRoot(IChatDataProvider provider, string? initialThreadId = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _initialThreadId = initialThreadId;
    }

    public override Element Render()
    {
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
        if (snapshot is null)
        {
            return Border(
                VStack(8,
                    ProgressRing().Size(28, 28).HAlign(HorizontalAlignment.Center),
                    Caption("Connecting to gateway…").Foreground(SecondaryText).HAlign(HorizontalAlignment.Center)
                ).VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Center)
            ).Background(LayerFill);
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

        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries.AsReadOnly();
        var connectedRaw = snapshot.ConnectionStatus;
        var hostConnected = connectedRaw is not null
            && connectedRaw.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
        var connState = hostConnected ? "connected"
            : (connectedRaw is not null && connectedRaw.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase))
                ? "connecting"
                : "disconnected";

        Element header = Component<SessionHeader, SessionHeaderProps>(new(selectedThread, timeline));

        // Per-entry metadata for the OpenClaw timeline footer (sender · time · model).
        IReadOnlyDictionary<string, ChatEntryMetadata>? entryMeta = null;
        if (selectedThread is not null && _provider is OpenClawChatDataProvider nativeForMeta)
            entryMeta = nativeForMeta.GetEntryMetadata(selectedThread.Id);

        var assistantSenderLabel = !string.IsNullOrWhiteSpace(selectedThread?.Title)
            ? selectedThread!.Title
            : "Field";

        // Show inline "thinking" indicator when the turn is active but the
        // last visible entry is NOT an assistant block yet — i.e. we're between
        // the user's send and the first assistant delta arriving.
        var showThinking = timeline.TurnActive
            && (timeline.Entries.Count == 0
                || timeline.Entries[timeline.Entries.Count - 1].Kind != ChatTimelineItemKind.Assistant);

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
                ShowThinkingIndicator: showThinking));

        Element inputBar = selectedThread is not null
            ? Component<InputBar, InputBarProps>(new(
                connState,
                timeline.TurnActive,
                timeline.PendingPermission,
                msg => OnSend(selectedThread.Id, msg),
                () => OnStop(selectedThread.Id),
                (rid, allow) => OnPermission(selectedThread.Id, rid, allow)))
            : Empty();

        Element statusBar = selectedThread is not null
            ? Component<StatusBar, StatusBarProps>(new(
                selectedThread,
                snapshot.AvailableModels,
                model => RunFireAndForget(ct => _provider.SetModelAsync(selectedThread.Id, model, ct)),
                allowAll => RunFireAndForget(ct => _provider.SetPermissionModeAsync(selectedThread.Id, allowAll, ct))))
            : Empty();

        var divider = Border(Empty()).Height(1).Background(DividerStroke);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
            header.Grid(row: 0, column: 0),
            divider.Grid(row: 1, column: 0),
            body.Grid(row: 2, column: 0),
            inputBar.Grid(row: 3, column: 0),
            statusBar.Grid(row: 4, column: 0)
        );
    }

    private static Element PlaceholderEmptyState(string? connectionStatus)
    {
        var msg = connectionStatus is { } s && s.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)
            ? "Select a session from the sidebar to start chatting."
            : (connectionStatus ?? "Connecting…");
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
