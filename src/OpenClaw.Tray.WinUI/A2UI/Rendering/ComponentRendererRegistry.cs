using System;
using System.Collections.Generic;
using OpenClawTray.A2UI.Rendering.Renderers;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Catalog-strict registry of component renderers. Built once at process
/// start. Unknown component types resolve to <see cref="UnknownRenderer"/>.
/// To add a new component: implement <see cref="IComponentRenderer"/>,
/// then register it with <see cref="Set"/>.
/// </summary>
public sealed class ComponentRendererRegistry
{
    private readonly Dictionary<string, IComponentRenderer> _byName;
    private readonly IComponentRenderer _unknown;

    /// <summary>
    /// Creates an in-memory renderer registry. Registrations are per process
    /// and are not persisted.
    /// </summary>
    public ComponentRendererRegistry(IEnumerable<IComponentRenderer> renderers)
        : this(renderers, new UnknownRenderer())
    {
    }

    internal ComponentRendererRegistry(IEnumerable<IComponentRenderer> renderers, UnknownRenderer unknown)
    {
        _byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in renderers) Set(r.ComponentName, r);
        _unknown = unknown;
    }

    public IComponentRenderer GetOrUnknown(string componentName) =>
        _byName.TryGetValue(componentName, out var r) ? r : _unknown;

    public IEnumerable<string> KnownNames => _byName.Keys;

    /// <summary>
    /// Registers or replaces the renderer for <paramref name="componentName"/>.
    /// When a renderer is registered for a component name, it is used instead
    /// of any built-in renderer for that name. The registration is in-memory
    /// for the lifetime of this registry and is not persisted.
    /// </summary>
    public void Set(string componentName, IComponentRenderer renderer)
    {
        if (string.IsNullOrEmpty(componentName))
            throw new ArgumentException("Component name must not be empty.", nameof(componentName));
        ArgumentNullException.ThrowIfNull(renderer);

        _byName[componentName] = renderer;
    }

    public static ComponentRendererRegistry BuildDefault(MediaResolver media)
    {
        var renderers = new IComponentRenderer[]
        {
            // Containers
            new RowRenderer(),
            new ColumnRenderer(),
            new ListRenderer(),
            new CardRenderer(),
            new TabsRenderer(),
            new ModalRenderer(),
            // Display
            new TextRenderer(),
            new ImageRenderer(media),
            new IconRenderer(),
            new VideoRenderer(media),
            new AudioPlayerRenderer(media),
            new DividerRenderer(),
            // Interactive
            new ButtonRenderer(),
            new CheckBoxRenderer(),
            new TextFieldRenderer(),
            new DateTimeInputRenderer(),
            new MultipleChoiceRenderer(),
            new SliderRenderer(),
        };
        return new ComponentRendererRegistry(renderers, new UnknownRenderer());
    }
}
