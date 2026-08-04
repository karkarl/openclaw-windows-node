using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using WinUIAnnotatedScrollBar = Microsoft.UI.Xaml.Controls.AnnotatedScrollBar;
using WinUIItemsView = Microsoft.UI.Xaml.Controls.ItemsView;
using WinUIScrollingInteractionState = Microsoft.UI.Xaml.Controls.ScrollingInteractionState;
using WinUIScrollView = Microsoft.UI.Xaml.Controls.ScrollView;

namespace OpenClawTray.Chat;

file sealed record ItemsViewVerticalScrollControllerElement(
    Element Child,
    ElementRef<WinUIAnnotatedScrollBar> ScrollBarRef,
    int InitialTailIndex,
    string InitialTailRequestKey,
    ReactorStreamingTailState? StreamingTailState) : Element
{
    static ItemsViewVerticalScrollControllerElement() =>
        ControlRegistry.RegisterDecorator<ItemsViewVerticalScrollControllerElement>(
            static () => new ItemsViewVerticalScrollControllerHandler());
}

file sealed class ItemsViewVerticalScrollControllerHandler
    : IDecoratorElementHandler<ItemsViewVerticalScrollControllerElement>
{
    private static readonly ConditionalWeakTable<WinUIItemsView, InitialTailPositioner> Positioners = new();

    public UIElement Mount(MountContext context, ItemsViewVerticalScrollControllerElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");

        context.BindFor(itemsView, element).Reference(
            get: static value => value.ScrollBarRef,
            set: static (value, scrollBar) =>
                ((WinUIItemsView)value).VerticalScrollController = scrollBar?.ScrollController);
        var positioner = new InitialTailPositioner(itemsView);
        Positioners.Add(itemsView, positioner);
        positioner.Request(element.InitialTailIndex, element.InitialTailRequestKey);
        return itemsView;
    }

    public UIElement Update(
        UpdateContext context,
        ItemsViewVerticalScrollControllerElement oldElement,
        ItemsViewVerticalScrollControllerElement newElement,
        UIElement control)
    {
        var updated = context.ReconcileChild(oldElement.Child, newElement.Child, control);
        if (updated is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");
        if (!string.Equals(oldElement.InitialTailRequestKey, newElement.InitialTailRequestKey, StringComparison.Ordinal)
            && Positioners.TryGetValue(itemsView, out var positioner))
            positioner.Request(newElement.InitialTailIndex, newElement.InitialTailRequestKey);
        else if (Positioners.TryGetValue(itemsView, out var existingPositioner))
            existingPositioner.UpdateTailIndex(
                newElement.InitialTailIndex,
                oldElement.StreamingTailState,
                newElement.StreamingTailState);
        return itemsView;
    }

    public V1UnmountDisposition Unmount(UnmountContext context, ItemsViewVerticalScrollControllerElement? element, UIElement control)
    {
        if (control is WinUIItemsView itemsView && Positioners.TryGetValue(itemsView, out var positioner))
        {
            Positioners.Remove(itemsView);
            positioner.Dispose();
        }
        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

file sealed class InitialTailPositioner : IDisposable
{
    private const double FollowThreshold = 60;
    private const int StreamingTailFollowIntervalMs = 125;

    private readonly WinUIItemsView itemsView;
    private string? _requestKey;
    private int _tailIndex;
    private int _version;
    private bool _valid;
    private bool _awaitingLayout;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private DispatcherTimer? _streamingTailFollowTimer;
    private ReactorStreamingTailState? _pendingStreamingTail;
    private bool _following;
    private bool _tailRequestQueued;
    private bool _streamingTailFollowPending;
    private bool _userInteractionObserved;
    private bool _disposed;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, string requestKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;

        _requestKey = requestKey;
        _version++;
        DetachLayout();
        StopStreamingTailFollow();
        _valid = tailIndex >= 0;
        if (!_valid)
            return;

        _tailIndex = tailIndex;
        _following = true;
        if (itemsView.IsLoaded)
            AwaitLayout();
    }

    public void UpdateTailIndex(
        int tailIndex,
        ReactorStreamingTailState? previousStreamingTail,
        ReactorStreamingTailState? currentStreamingTail)
    {
        var changed = _tailIndex != tailIndex;
        _tailIndex = tailIndex;
        if (changed && _following && tailIndex >= 0 && itemsView.IsLoaded)
        {
            StopStreamingTailFollow();
            QueueTailRequest(_version);
            return;
        }

        if (_following && IsStreamingTailUpdate(previousStreamingTail, currentStreamingTail))
            RequestStreamingTailFollow(currentStreamingTail!);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_valid)
            AwaitLayout();
    }

    private void AwaitLayout()
    {
        if (_disposed || !_valid || !itemsView.IsLoaded || _awaitingLayout)
            return;

        if (itemsView.ScrollView is { IsLoaded: false } scrollView)
        {
            _awaitingScrollView = scrollView;
            scrollView.Loaded += OnScrollViewLoaded;
            return;
        }

        _awaitingLayout = true;
        itemsView.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView)
            scrollView.Loaded -= OnScrollViewLoaded;

        _awaitingScrollView = null;
        AwaitLayout();
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout();
            return;
        }

        var version = _version;
        var index = _tailIndex;
        itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || itemsView.ScrollView is not { IsLoaded: true })
            {
                if (!_disposed && _valid)
                    AwaitLayout();
                return;
            }

            AttachScrollView();
            StartTailRequest(index);
        });
    }

    private void AttachScrollView()
    {
        var nextScrollView = itemsView.ScrollView;
        if (ReferenceEquals(_scrollView, nextScrollView))
            return;

        DetachScrollView();
        _scrollView = nextScrollView;
        if (_scrollView is not null)
            _scrollView.ViewChanged += OnViewChanged;
    }

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        if (sender.State == WinUIScrollingInteractionState.Interaction)
        {
            _following = false;
            _userInteractionObserved = true;
            StopStreamingTailFollow();
            return;
        }

        if (_userInteractionObserved)
        {
            _userInteractionObserved = false;
            _following = IsNearBottom(sender);
        }
    }

    private void QueueTailRequest(int version)
    {
        if (_tailRequestQueued)
            return;

        _tailRequestQueued = true;
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            _tailRequestQueued = false;
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version || !_following)
            {
                return;
            }

            StartTailRequest(_tailIndex);
        }))
        {
            _tailRequestQueued = false;
            _following = false;
        }
    }

    private void StartTailRequest(int index)
    {
        if (itemsView.ScrollView is not { IsLoaded: true })
            return;

        _following = true;
        itemsView.StartBringItemIntoView(index, new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 1.0,
        });
    }

    private void RequestStreamingTailFollow(ReactorStreamingTailState streamingTail)
    {
        _pendingStreamingTail = streamingTail;
        _streamingTailFollowPending = true;
        _streamingTailFollowTimer ??= CreateStreamingTailFollowTimer();
        if (!_streamingTailFollowTimer.IsEnabled)
            _streamingTailFollowTimer.Start();
    }

    private DispatcherTimer CreateStreamingTailFollowTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(StreamingTailFollowIntervalMs),
        };
        timer.Tick += OnStreamingTailFollowTick;
        return timer;
    }

    private void OnStreamingTailFollowTick(object? sender, object args)
    {
        _streamingTailFollowTimer?.Stop();
        if (_disposed
            || !_valid
            || !_following
            || !_streamingTailFollowPending
            || !itemsView.IsLoaded
            || _scrollView is not { IsLoaded: true, State: not WinUIScrollingInteractionState.Interaction })
        {
            StopStreamingTailFollow();
            return;
        }

        _streamingTailFollowPending = false;
        var streamingTail = _pendingStreamingTail;
        _pendingStreamingTail = null;
        StartTailRequest(_tailIndex);
        if (streamingTail is { IsStreaming: true } && _following)
            return;

        StopStreamingTailFollow();
    }

    private static bool IsNearBottom(WinUIScrollView scrollView) =>
        scrollView.ScrollableHeight - scrollView.VerticalOffset <= FollowThreshold;

    private static bool IsStreamingTailUpdate(
        ReactorStreamingTailState? previous,
        ReactorStreamingTailState? current) =>
        previous is not null
        && current is not null
        && string.Equals(previous.EntryId, current.EntryId, StringComparison.Ordinal)
        && ((current.IsStreaming
             && (!previous.IsStreaming || previous.TextLength != current.TextLength))
            || (previous.IsStreaming && !current.IsStreaming));

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        DetachLayout();
        StopStreamingTailFollow();
        DetachScrollView();
    }

    private void DetachLayout()
    {
        if (_awaitingScrollView is { } scrollView)
        {
            scrollView.Loaded -= OnScrollViewLoaded;
            _awaitingScrollView = null;
        }

        if (_awaitingLayout)
        {
            itemsView.LayoutUpdated -= OnLayoutUpdated;
            _awaitingLayout = false;
        }
    }

    private void DetachScrollView()
    {
        if (_scrollView is not null)
            _scrollView.ViewChanged -= OnViewChanged;

        _scrollView = null;
    }

    private void StopStreamingTailFollow()
    {
        _streamingTailFollowTimer?.Stop();
        _pendingStreamingTail = null;
        _streamingTailFollowPending = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _version++;
        DetachLayout();
        StopStreamingTailFollow();
        if (_streamingTailFollowTimer is not null)
        {
            _streamingTailFollowTimer.Tick -= OnStreamingTailFollowTick;
            _streamingTailFollowTimer = null;
        }
        DetachScrollView();
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
    }
}

internal static class ItemsViewScrollControllerExtensions
{
    public static Element BindVerticalScrollController<T>(
        this ItemsViewElement<T> itemsView,
        ElementRef<WinUIAnnotatedScrollBar> scrollBarRef,
        int initialTailIndex,
        string initialTailRequestKey,
        ReactorStreamingTailState? streamingTailState) =>
        new ItemsViewVerticalScrollControllerElement(
            itemsView,
            scrollBarRef,
            initialTailIndex,
            initialTailRequestKey,
            streamingTailState);
}
