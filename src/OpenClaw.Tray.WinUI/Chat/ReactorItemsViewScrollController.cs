using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using WinUIAnnotatedScrollBar = Microsoft.UI.Xaml.Controls.AnnotatedScrollBar;
using WinUIItemsView = Microsoft.UI.Xaml.Controls.ItemsView;
using WinUIScrollView = Microsoft.UI.Xaml.Controls.ScrollView;

namespace OpenClawTray.Chat;

file sealed record ItemsViewVerticalScrollControllerElement(
    Element Child,
    ElementRef<WinUIAnnotatedScrollBar> ScrollBarRef,
    int InitialTailIndex,
    string InitialTailRequestKey) : Element
{
    static ItemsViewVerticalScrollControllerElement()
    {
        ControlRegistry.RegisterDecorator<ItemsViewVerticalScrollControllerElement>(
            static () => new ItemsViewVerticalScrollControllerHandler());
    }
}

file sealed class ItemsViewVerticalScrollControllerHandler
    : IDecoratorElementHandler<ItemsViewVerticalScrollControllerElement>
{
    private static readonly ConditionalWeakTable<WinUIItemsView, InitialTailPositioner> InitialTailPositioners = new();

    public UIElement Mount(
        MountContext context,
        ItemsViewVerticalScrollControllerElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView scroll controller binding requires an ItemsView child.");

        context.BindFor(itemsView, element).Reference(
            get: static value => value.ScrollBarRef,
            set: static (control, scrollBar) =>
                ((WinUIItemsView)control).VerticalScrollController = scrollBar?.ScrollController);

        if (InitialTailPositioners.TryGetValue(itemsView, out var existingInitialTailPositioner))
            existingInitialTailPositioner.Dispose();
        InitialTailPositioners.Remove(itemsView);
        var initialTailPositioner = new InitialTailPositioner(itemsView);
        InitialTailPositioners.Add(itemsView, initialTailPositioner);
        initialTailPositioner.Request(element.InitialTailIndex, element.InitialTailRequestKey);

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

        if (!string.Equals(
                oldElement.InitialTailRequestKey,
                newElement.InitialTailRequestKey,
                StringComparison.Ordinal)
            && InitialTailPositioners.TryGetValue(itemsView, out var initialTailPositioner))
        {
            initialTailPositioner.Request(newElement.InitialTailIndex, newElement.InitialTailRequestKey);
        }

        return itemsView;
    }

    public V1UnmountDisposition Unmount(
        UnmountContext context,
        ItemsViewVerticalScrollControllerElement? element,
        UIElement control)
    {
        if (control is WinUIItemsView itemsView
            && InitialTailPositioners.TryGetValue(itemsView, out var initialTailPositioner))
        {
            InitialTailPositioners.Remove(itemsView);
            initialTailPositioner.Dispose();
        }

        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

file sealed class InitialTailPositioner : IDisposable
{
    private readonly WinUIItemsView _itemsView;
    private string? _requestKey;
    private int _tailIndex;
    private int _requestVersion;
    private bool _hasValidTailRequest;
    private bool _awaitingLayout;
    private WinUIScrollView? _awaitingScrollView;
    private bool _disposed;

    public InitialTailPositioner(WinUIItemsView itemsView)
    {
        _itemsView = itemsView;
        _itemsView.Loaded += OnLoaded;
        _itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, string requestKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        _requestKey = requestKey;
        _requestVersion++;
        DetachLayoutUpdated();
        _hasValidTailRequest = tailIndex >= 0;
        if (!_hasValidTailRequest)
            return;

        _tailIndex = tailIndex;
        if (_itemsView.IsLoaded)
            AwaitCompletedLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_hasValidTailRequest)
            AwaitCompletedLayout();
    }

    private void AwaitCompletedLayout()
    {
        if (_disposed || !_hasValidTailRequest || !_itemsView.IsLoaded || _awaitingLayout)
            return;

        if (_itemsView.ScrollView is { IsLoaded: false } scrollView)
        {
            _awaitingScrollView = scrollView;
            scrollView.Loaded += OnScrollViewLoaded;
            return;
        }

        _awaitingLayout = true;
        _itemsView.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView)
            scrollView.Loaded -= OnScrollViewLoaded;
        _awaitingScrollView = null;
        AwaitCompletedLayout();
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        DetachLayoutUpdated();
        if (!HasUsableScrollView())
        {
            AwaitCompletedLayout();
            return;
        }

        var requestVersion = _requestVersion;
        var tailIndex = _tailIndex;
        _itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed
                || !_hasValidTailRequest
                || !_itemsView.IsLoaded
                || !HasUsableScrollView()
                || requestVersion != _requestVersion)
            {
                if (!_disposed && _hasValidTailRequest)
                    AwaitCompletedLayout();
                return;
            }

            _itemsView.StartBringItemIntoView(
                tailIndex,
                new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 1.0,
                });
        });
    }

    private bool HasUsableScrollView() =>
        _itemsView.ScrollView is WinUIScrollView { IsLoaded: true };

    private void OnUnloaded(object sender, RoutedEventArgs args) => Dispose();

    private void DetachLayoutUpdated()
    {
        if (_awaitingScrollView is { } scrollView)
        {
            scrollView.Loaded -= OnScrollViewLoaded;
            _awaitingScrollView = null;
        }

        if (!_awaitingLayout)
            return;

        _itemsView.LayoutUpdated -= OnLayoutUpdated;
        _awaitingLayout = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _requestVersion++;
        DetachLayoutUpdated();
        _itemsView.Loaded -= OnLoaded;
        _itemsView.Unloaded -= OnUnloaded;
    }
}

internal static class ItemsViewScrollControllerExtensions
{
    public static Element BindVerticalScrollController<T>(
        this ItemsViewElement<T> itemsView,
        ElementRef<WinUIAnnotatedScrollBar> scrollBarRef,
        int initialTailIndex,
        string initialTailRequestKey) =>
        new ItemsViewVerticalScrollControllerElement(
            itemsView,
            scrollBarRef,
            initialTailIndex,
            initialTailRequestKey);
}
