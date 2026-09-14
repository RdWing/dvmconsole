// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

public readonly record struct MobileCardPosition(double X, double Y)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X >= 0 && Y >= 0;
    public static MobileCardPosition Snap(double x, double y)
        => new(Math.Max(0, Math.Round(x / 10) * 10), Math.Max(0, Math.Round(y / 10) * 10));
}

public interface IMobileCardLayoutPreferences
{
    IReadOnlyDictionary<string, MobileCardPosition> ReadCardPositions(string configurationId);
    void WriteCardPositions(string configurationId, IReadOnlyDictionary<string, MobileCardPosition> positions);
    IReadOnlyList<string> ReadListOrder(string configurationId);
    void WriteListOrder(string configurationId, IReadOnlyList<string> order);
}

/// <summary>Independent, persistent card coordinates. Hidden zones retain their own layout.</summary>
internal sealed class MobileCardGrid(ConsoleTopologySnapshot topology,
    IReadOnlyDictionary<ChannelId, Control> cards) : Panel
{
    private readonly Dictionary<string, MobileCardPosition> positions = [];
    private Size viewport;
    private IReadOnlyDictionary<string, int> initialRanks = new Dictionary<string, int>();

    public void RestoreInitialOrder(IReadOnlyList<string> order)
        => initialRanks = order.Select((id, index) => (id, index)).GroupBy(pair => pair.id)
            .ToDictionary(group => group.Key, group => group.First().index);

    public void SetViewport(Size value)
    {
        if (viewport == value) return;
        viewport = value;
        InvalidateMeasure();
    }

    public void Restore(IReadOnlyDictionary<string, MobileCardPosition> saved)
    {
        foreach (var pair in saved.Where(pair => pair.Value.IsValid)) positions[pair.Key] = pair.Value;
        InvalidateMeasure();
    }

    public MobileCardPosition Position(ChannelId id) => positions.GetValueOrDefault(id.ToString());
    public IReadOnlyDictionary<string, MobileCardPosition> Snapshot() => new Dictionary<string, MobileCardPosition>(positions);
    public void Move(ChannelId id, MobileCardPosition position)
    {
        if (!position.IsValid) return;
        positions[id.ToString()] = position;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children.Where(child => child.IsVisible)) child.Measure(Size.Infinity);
        // ScrollViewer measures with infinity. Seed only once its actual viewport is known.
        if (viewport.Width > 0)
        {
            foreach (var zone in topology.Channels.GroupBy(channel => (channel.SystemId, channel.ZoneId)))
            {
                var visible = zone.OrderBy(channel => initialRanks.GetValueOrDefault(channel.Id.ToString(), int.MaxValue)).Where(channel => cards.TryGetValue(channel.Id, out var card) && card.IsVisible).ToArray();
                var occupied = visible.Where(channel => positions.ContainsKey(channel.Id.ToString()))
                    .Select(channel => new Rect(new Point(Position(channel.Id).X, Position(channel.Id).Y), cards[channel.Id].DesiredSize)).ToList();
                foreach (var channel in visible.Where(channel => !positions.ContainsKey(channel.Id.ToString())))
                {
                    var size = cards[channel.Id].DesiredSize;
                    double stepX = Math.Ceiling((size.Width + 10) / 10) * 10;
                    double stepY = Math.Ceiling((size.Height + 10) / 10) * 10;
                    int columns = Math.Max(1, (int)(viewport.Width / stepX));
                    int slot = 0;
                    Rect candidate;
                    do { candidate = new Rect(new Point(slot % columns * stepX, slot / columns * stepY), size); slot++; }
                    while (occupied.Any(rect => rect.Intersects(candidate)));
                    positions[channel.Id.ToString()] = new(candidate.X, candidate.Y);
                    occupied.Add(candidate);
                }
            }
        }
        double width = viewport.Width, height = viewport.Height;
        foreach (var pair in cards.Where(pair => pair.Value.IsVisible))
        {
            var position = Position(pair.Key);
            // Leave a blank row and column available for an empty-space drop.
            width = Math.Max(width, position.X + pair.Value.DesiredSize.Width * 2 + 20);
            height = Math.Max(height, position.Y + pair.Value.DesiredSize.Height * 2 + 20);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var pair in cards)
        {
            var position = Position(pair.Key);
            pair.Value.Arrange(new Rect(new Point(position.X, position.Y), pair.Value.DesiredSize));
        }
        return finalSize;
    }
}
