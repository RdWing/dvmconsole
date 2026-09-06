// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace DvmConsole.Desktop;

// Realizes absolute-positioned channel cards only around the effective
// ScrollViewer viewport. Interaction-owned cards stay alive until ownership
// ends so a layout pass can never interrupt drag, focus, or PTT state.
internal sealed class ViewportAwareAbsolutePanel : VirtualizingPanel
{
    private const double SpatialBucketSize = 320;

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<ViewportAwareAbsolutePanel, double>(
            nameof(ItemHeight),
            160);

    public static readonly StyledProperty<double> OverscanProperty =
        AvaloniaProperty.Register<ViewportAwareAbsolutePanel, double>(
            nameof(Overscan),
            240);

    public static readonly AttachedProperty<bool> IsInteractionPinnedProperty =
        AvaloniaProperty.RegisterAttached<ViewportAwareAbsolutePanel, Control, bool>(
            "IsInteractionPinned");

    private readonly Dictionary<int, Control> realized = [];
    private readonly HashSet<ChannelViewModel> observedItems = [];
    private readonly Dictionary<SpatialBucket, List<int>> spatialBuckets = [];
    private readonly Dictionary<ChannelViewModel, IndexedChannel> indexedChannels = [];
    private readonly HashSet<int> transmittingIndices = [];
    private readonly HashSet<int> requiredScratch = [];
    private readonly List<int> recycleScratch = [];
    private Rect effectiveViewport;
    private SpatialRange lastViewportRange = SpatialRange.Empty;
    private Rect realizedCoverage;
    private double extentWidth = 1;
    private double extentHeight = 1;
    private double maximumCardWidth = 1;

    static ViewportAwareAbsolutePanel()
        => AffectsMeasure<ViewportAwareAbsolutePanel>(ItemHeightProperty, OverscanProperty);

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double Overscan
    {
        get => GetValue(OverscanProperty);
        set => SetValue(OverscanProperty, value);
    }

    internal int RealizedCount => realized.Count;
    internal Control? GetRealizedContainer(int index)
        => realized.GetValueOrDefault(index);

    internal bool Reveal(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return indexedChannels.TryGetValue(channel, out IndexedChannel indexed) &&
               ScrollIntoView(indexed.Index) is not null;
    }

    public static bool GetIsInteractionPinned(Control control)
        => control.GetValue(IsInteractionPinnedProperty);

    public static void SetIsInteractionPinned(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        Control target = control;
        for (Control? candidate = control;
             candidate is not null;
             candidate = candidate.GetVisualParent() as Control)
        {
            if (candidate.GetVisualParent() is ViewportAwareAbsolutePanel)
            {
                target = candidate;
                break;
            }
        }
        target.SetValue(IsInteractionPinnedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Rect viewport = GetExtendedViewport(availableSize);
        SpatialRange range = SpatialRange.FromViewport(
            viewport,
            Math.Max(1, maximumCardWidth),
            Math.Max(1, ItemHeight));
        lastViewportRange = range;
        realizedCoverage = viewport;
        requiredScratch.Clear();
        for (int row = range.MinimumRow; row <= range.MaximumRow; row++)
        {
            for (int column = range.MinimumColumn; column <= range.MaximumColumn; column++)
            {
                if (!spatialBuckets.TryGetValue(new SpatialBucket(column, row), out List<int>? indices))
                    continue;
                foreach (int index in indices)
                {
                    if ((uint)index >= (uint)Items.Count || Items[index] is not ChannelViewModel channel)
                        continue;
                    var bounds = new Rect(channel.WidgetX, channel.WidgetY, channel.CardWidth, ItemHeight);
                    if (viewport.Intersects(bounds))
                        requiredScratch.Add(index);
                }
            }
        }

        requiredScratch.UnionWith(transmittingIndices);

        recycleScratch.Clear();
        foreach ((int index, Control control) in realized)
        {
            if (ShouldRetainInteraction(control))
                requiredScratch.Add(index);
            else if (!requiredScratch.Contains(index))
                recycleScratch.Add(index);
        }

        foreach (int index in recycleScratch)
            Recycle(index);

        foreach (int index in requiredScratch)
        {
            Control control = GetOrCreate(index);
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : extentWidth,
            double.IsFinite(availableSize.Height) ? availableSize.Height : extentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach ((int index, Control control) in realized)
        {
            if ((uint)index >= (uint)Items.Count || Items[index] is not ChannelViewModel channel)
                continue;
            double width = control.DesiredSize.Width > 0 ? control.DesiredSize.Width : channel.CardWidth;
            double height = control.DesiredSize.Height > 0 ? control.DesiredSize.Height : ItemHeight;
            control.Arrange(new Rect(channel.WidgetX, channel.WidgetY, width, height));
        }
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += HandleEffectiveViewportChanged;
        ObserveItems(Items);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= HandleEffectiveViewportChanged;
        UnobserveItems();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        ResetRealization();
        ObserveItems(Items);
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items,
        NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        ResetRealization();
        ObserveItems(items);
        InvalidateMeasure();
    }

    protected override IInputElement? GetControl(
        NavigationDirection direction,
        IInputElement? from,
        bool wrap)
    {
        if (Items.Count == 0)
            return null;

        int current = from is Control control ? IndexFromContainer(control) : -1;
        int target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Previous or NavigationDirection.Left or NavigationDirection.Up => current - 1,
            _ => current + 1
        };
        if (wrap)
            target = (target % Items.Count + Items.Count) % Items.Count;
        if ((uint)target >= (uint)Items.Count)
            return null;
        return ScrollIntoView(target);
    }

    protected override Control? ScrollIntoView(int index)
    {
        if ((uint)index >= (uint)Items.Count)
            return null;
        Control control = GetOrCreate(index);
        if (Items[index] is ChannelViewModel channel)
        {
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = control.DesiredSize.Width > 0 ? control.DesiredSize.Width : channel.CardWidth;
            double height = control.DesiredSize.Height > 0 ? control.DesiredSize.Height : ItemHeight;
            control.Arrange(new Rect(channel.WidgetX, channel.WidgetY, width, height));
            control.BringIntoView(new Rect(0, 0, width, height));
        }
        else
        {
            control.BringIntoView();
        }
        return control;
    }

    protected override Control? ContainerFromIndex(int index)
        => realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach ((int index, Control candidate) in realized)
        {
            if (ReferenceEquals(candidate, container))
                return index;
        }
        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers()
        => realized.OrderBy(entry => entry.Key).Select(entry => entry.Value);

    private Control GetOrCreate(int index)
    {
        if (realized.TryGetValue(index, out Control? existing))
            return existing;

        object item = Items[index] ?? throw new InvalidOperationException(
            "Channel card items cannot contain null values.");
        var generator = ItemContainerGenerator ?? throw new InvalidOperationException(
            "The channel-card panel is not attached to an items control.");
        bool needsContainer = generator.NeedsContainer(item, index, out object? recycleKey);
        Control control = needsContainer
            ? generator.CreateContainer(item, index, recycleKey) ??
              throw new InvalidOperationException("The item container generator returned no channel container.")
            : (Control)item;
        generator.PrepareItemContainer(control, item, index);
        control.ZIndex = index;
        AddInternalChild(control);
        generator.ItemContainerPrepared(control, item, index);
        realized.Add(index, control);
        return control;
    }

    private void Recycle(int index)
    {
        if (!realized.Remove(index, out Control? control) || control is null)
            return;
        ItemContainerGenerator?.ClearItemContainer(control);
        RemoveInternalChild(control);
    }

    private void ResetRealization()
    {
        foreach (int index in realized.Keys.ToArray())
            Recycle(index);
        UnobserveItems();
    }

    private void ObserveItems(IReadOnlyList<object?> items)
    {
        ClearSpatialIndex();
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index] is not ChannelViewModel channel)
                continue;
            if (observedItems.Add(channel))
            {
                channel.WidgetPositionChanged += HandleWidgetPositionChanged;
                channel.PropertyChanged += HandleChannelPropertyChanged;
            }
            IndexChannel(channel, index);
        }
        RecalculateExtents();
    }

    private void UnobserveItems()
    {
        foreach (ChannelViewModel channel in observedItems)
        {
            channel.WidgetPositionChanged -= HandleWidgetPositionChanged;
            channel.PropertyChanged -= HandleChannelPropertyChanged;
        }
        observedItems.Clear();
        ClearSpatialIndex();
    }

    private void HandleWidgetPositionChanged(object? sender, WidgetPositionChangedEventArgs e)
    {
        if (sender is not ChannelViewModel channel ||
            !indexedChannels.TryGetValue(channel, out IndexedChannel indexed))
        {
            InvalidateMeasure();
            InvalidateArrange();
            return;
        }

        SpatialBucket nextBucket = SpatialBucket.FromPoint(e.X, e.Y);
        bool bucketChanged = indexed.Bucket != nextBucket;
        if (bucketChanged)
        {
            RemoveFromBucket(indexed.Bucket, indexed.Index);
            AddToBucket(nextBucket, indexed.Index);
            indexedChannels[channel] = indexed with { Bucket = nextBucket };
        }

        bool grewExtent = e.X + channel.CardWidth > extentWidth || e.Y + ItemHeight > extentHeight;
        if (grewExtent)
        {
            extentWidth = Math.Max(extentWidth, e.X + channel.CardWidth);
            extentHeight = Math.Max(extentHeight, e.Y + ItemHeight);
        }

        if (e.IsFinal)
            RecalculateExtents();

        if (bucketChanged || grewExtent || e.IsFinal)
            InvalidateMeasure();
        InvalidateArrange();
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelViewModel.IsTransmitting) ||
            sender is not ChannelViewModel channel ||
            !indexedChannels.TryGetValue(channel, out IndexedChannel indexed))
        {
            return;
        }

        if (channel.IsTransmitting)
            transmittingIndices.Add(indexed.Index);
        else
            transmittingIndices.Remove(indexed.Index);
        InvalidateMeasure();
    }

    private void HandleEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (effectiveViewport == e.EffectiveViewport)
            return;
        effectiveViewport = e.EffectiveViewport;
        Rect viewport = GetExtendedViewport(Bounds.Size);
        SpatialRange nextRange = SpatialRange.FromViewport(
            viewport,
            Math.Max(1, maximumCardWidth),
            Math.Max(1, ItemHeight));
        if (nextRange != lastViewportRange || !realizedCoverage.Contains(effectiveViewport))
            InvalidateMeasure();
    }

    private Rect GetExtendedViewport(Size availableSize)
    {
        Rect viewport = effectiveViewport.Width > 0 && effectiveViewport.Height > 0
            ? effectiveViewport
            : new Rect(
                0,
                0,
                Math.Min(FiniteOr(availableSize.Width, 1200), 1200),
                Math.Min(FiniteOr(availableSize.Height, 800), 800));
        double overscan = Math.Max(0, Overscan);
        return viewport.Inflate(overscan);
    }

    private static bool ShouldRetainInteraction(Control control)
        => GetIsInteractionPinned(control) || control.IsKeyboardFocusWithin ||
           control.DataContext is ChannelViewModel { IsTransmitting: true };

    private void IndexChannel(ChannelViewModel channel, int index)
    {
        SpatialBucket bucket = SpatialBucket.FromPoint(channel.WidgetX, channel.WidgetY);
        indexedChannels[channel] = new IndexedChannel(index, bucket);
        if (channel.IsTransmitting)
            transmittingIndices.Add(index);
        AddToBucket(bucket, index);
        maximumCardWidth = Math.Max(maximumCardWidth, channel.CardWidth);
    }

    private void AddToBucket(SpatialBucket bucket, int index)
    {
        if (!spatialBuckets.TryGetValue(bucket, out List<int>? indices))
        {
            indices = [];
            spatialBuckets.Add(bucket, indices);
        }
        indices.Add(index);
    }

    private void RemoveFromBucket(SpatialBucket bucket, int index)
    {
        if (!spatialBuckets.TryGetValue(bucket, out List<int>? indices))
            return;
        indices.Remove(index);
        if (indices.Count == 0)
            spatialBuckets.Remove(bucket);
    }

    private void RecalculateExtents()
    {
        double width = 1;
        double height = 1;
        double cardWidth = 1;
        foreach (ChannelViewModel channel in observedItems)
        {
            width = Math.Max(width, channel.WidgetX + channel.CardWidth);
            height = Math.Max(height, channel.WidgetY + ItemHeight);
            cardWidth = Math.Max(cardWidth, channel.CardWidth);
        }
        extentWidth = width;
        extentHeight = height;
        maximumCardWidth = cardWidth;
    }

    private void ClearSpatialIndex()
    {
        spatialBuckets.Clear();
        indexedChannels.Clear();
        transmittingIndices.Clear();
        requiredScratch.Clear();
        recycleScratch.Clear();
        maximumCardWidth = 1;
        extentWidth = 1;
        extentHeight = 1;
        lastViewportRange = SpatialRange.Empty;
        realizedCoverage = default;
    }

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;

    private readonly record struct IndexedChannel(int Index, SpatialBucket Bucket);

    private readonly record struct SpatialBucket(int Column, int Row)
    {
        public static SpatialBucket FromPoint(double x, double y)
            => new(ToBucket(x), ToBucket(y));

        private static int ToBucket(double value)
            => (int)Math.Floor(Math.Max(0, value) / SpatialBucketSize);
    }

    private readonly record struct SpatialRange(
        int MinimumColumn,
        int MaximumColumn,
        int MinimumRow,
        int MaximumRow)
    {
        public static readonly SpatialRange Empty = new(0, -1, 0, -1);

        public static SpatialRange FromViewport(
            Rect viewport,
            double maximumItemWidth,
            double maximumItemHeight)
            => new(
                ToBucket(viewport.Left - maximumItemWidth),
                ToBucket(viewport.Right),
                ToBucket(viewport.Top - maximumItemHeight),
                ToBucket(viewport.Bottom));

        private static int ToBucket(double value)
            => (int)Math.Floor(Math.Max(0, value) / SpatialBucketSize);
    }
}
