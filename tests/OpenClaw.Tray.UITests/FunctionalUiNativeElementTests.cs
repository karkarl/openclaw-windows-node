using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class FunctionalUiNativeElementTests
{
    private readonly UIThreadFixture _ui;

    public FunctionalUiNativeElementTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task BorderNativeChild_DoesNotUnloadWrappedControlAcrossRenders()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        ComboBox? combo = null;
        var unloadedCount = 0;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            combo = new ComboBox
            {
                Width = 160,
                Height = 32,
            };
            combo.Items.Add(new ComboBoxItem { Content = "One" });
            combo.Items.Add(new ComboBoxItem { Content = "Two" });
            combo.Unloaded += (_, _) => unloadedCount++;

            host = new FunctionalHostControl
            {
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Border(Native(() => combo!)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var border = Assert.IsType<Border>(host!.Content);
            Assert.Same(combo, border.Child);
            Assert.Equal(0, unloadedCount);
        });

        await _ui.RunOnUIAsync(() =>
        {
            host!.Mount(_ => Border(Native(() => combo!)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var border = Assert.IsType<Border>(host!.Content);
            Assert.Same(combo, border.Child);
            Assert.Equal(0, unloadedCount);
            Assert.Equal(160, combo!.Width);
            Assert.Equal(32, combo.Height);
            host.Dispose();
        });
    }

    [Fact]
    public async Task NativeElement_AppliesExplicitModifiersWithoutClearingPreconfiguredState()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        ComboBox? combo = null;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            combo = new ComboBox
            {
                Width = 160,
                Height = 32,
                Padding = new Thickness(8, 0, 4, 0),
                Tag = "caller-tag",
            };

            host = new FunctionalHostControl
            {
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Border(Native(() => combo!)
                .AutomationName("Session picker")
                .Disabled()
                .OnGotFocus((_, _) => { })));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal("Session picker", AutomationProperties.GetName(combo));
            Assert.False(combo!.IsEnabled);
            Assert.Equal(160, combo.Width);
            Assert.Equal(32, combo.Height);
            Assert.Equal(new Thickness(8, 0, 4, 0), combo.Padding);
            Assert.Equal("caller-tag", combo.Tag);
        });

        await _ui.RunOnUIAsync(() =>
        {
            host!.Mount(_ => Border(Native(() => combo!)
                .Disabled(false)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.True(combo!.IsEnabled);
            Assert.Equal("Session picker", AutomationProperties.GetName(combo));
            Assert.Equal(160, combo.Width);
            Assert.Equal(32, combo.Height);
            Assert.Equal(new Thickness(8, 0, 4, 0), combo.Padding);
            Assert.Equal("caller-tag", combo.Tag);
            host!.Dispose();
        });
    }

    [Fact]
    public async Task NativeElement_RemainsAttachedWhenItsRenderPathChanges()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        TextBlock? nativeText = null;
        var showPrefix = false;

        await _ui.RunOnUIAsync(() =>
        {
            nativeText = new TextBlock { Text = "native" };
            host = new FunctionalHostControl { SuppressAutoDispose = true };
            _ui.Container.Children.Add(host);
            host.Mount(_ => showPrefix
                ? VStack(0, TextBlock("prefix"), Native(() => nativeText!))
                : VStack(0, Native(() => nativeText!)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            showPrefix = true;
            host!.Mount(_ => VStack(0, TextBlock("prefix"), Native(() => nativeText!)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var wrapper = Assert.IsType<Border>(host!.Content);
            var panel = Assert.IsType<StackPanel>(wrapper.Child);
            Assert.Equal(2, panel.Children.Count);
            Assert.Same(nativeText, panel.Children[1]);
            host.Dispose();
        });
    }

    [Fact]
    public async Task VirtualStack_RecycleDetachesNativeElement()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        TextBlock? nativeText = null;

        await _ui.RunOnUIAsync(() =>
        {
            nativeText = new TextBlock { Text = "native" };
            host = new FunctionalHostControl
            {
                Width = 400,
                Height = 300,
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => VirtualVStack(0, Native(() => nativeText!)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            var wrapper = Assert.IsType<Border>(host!.Content);
            var repeater = Assert.IsType<ItemsRepeater>(wrapper.Child);
            var container = Assert.IsType<Border>(repeater.TryGetElement(0));
            Assert.Same(nativeText, container.Child);
            host.Mount(_ => VirtualVStack(0));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Null(VisualTreeHelper.GetParent(nativeText));
            host!.Dispose();
        });
    }

    [Fact]
    public async Task NativeElement_OnMountRunsAcrossSamePathOwnershipChanges()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        TextBlock? nativeText = null;
        var showNative = true;
        var nativeMountCount = 0;
        var placeholderMountCount = 0;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            nativeText = new TextBlock { Text = "native" };
            host = new FunctionalHostControl
            {
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => showNative
                ? Border(Native(() => nativeText!).OnMount(_ => nativeMountCount++))
                : Border(TextBlock("placeholder").OnMount(_ => placeholderMountCount++)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal(1, nativeMountCount);
            Assert.Equal(0, placeholderMountCount);
            showNative = false;
            host!.Mount(_ => showNative
                ? Border(Native(() => nativeText!).OnMount(_ => nativeMountCount++))
                : Border(TextBlock("placeholder").OnMount(_ => placeholderMountCount++)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal(1, nativeMountCount);
            Assert.Equal(1, placeholderMountCount);
        });

        await _ui.RunOnUIAsync(() =>
        {
            showNative = true;
            host!.Mount(_ => showNative
                ? Border(Native(() => nativeText!).OnMount(_ => nativeMountCount++))
                : Border(TextBlock("placeholder").OnMount(_ => placeholderMountCount++)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal(2, nativeMountCount);
            Assert.Equal(1, placeholderMountCount);
            var border = Assert.IsType<Border>(host!.Content);
            Assert.Same(nativeText, border.Child);
            host.Dispose();
        });
    }

    [Fact]
    public async Task NativeElement_OnMountRunsWhenStablePathGetsNewNativeInstance()
    {
        await _ui.ResetContainerAsync();

        FunctionalHostControl? host = null;
        TextBlock? firstNative = null;
        TextBlock? secondNative = null;
        TextBlock? currentNative = null;
        var mountCount = 0;

        await _ui.RunOnUIAsync(() =>
        {
            TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
            firstNative = new TextBlock { Text = "first" };
            secondNative = new TextBlock { Text = "second" };
            currentNative = firstNative;
            host = new FunctionalHostControl
            {
                SuppressAutoDispose = true,
            };
            _ui.Container.Children.Add(host);
            host.Mount(_ => Border(Native(() => currentNative!).OnMount(_ => mountCount++)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal(1, mountCount);
            currentNative = secondNative;
            host!.Mount(_ => Border(Native(() => currentNative!).OnMount(_ => mountCount++)));
        });

        await DrainRenderQueueAsync();

        await _ui.RunOnUIAsync(() =>
        {
            Assert.Equal(2, mountCount);
            var border = Assert.IsType<Border>(host!.Content);
            Assert.Same(secondNative, border.Child);
            host.Dispose();
        });
    }

    private async Task DrainRenderQueueAsync()
    {
        await _ui.RunOnUIAsync(() => { });
        await Task.Delay(50);
        await _ui.RunOnUIAsync(() => _ui.Container.UpdateLayout());
        await _ui.RunOnUIAsync(() => { });
    }
}
