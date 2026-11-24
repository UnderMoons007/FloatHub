using System;
using System.Drawing;
using System.Linq;

namespace FloatHub.Core;

public enum Edge { None, Left, Top, Right, Bottom }
public record Placement(string Monitor, int X, int Y, Edge Edge);
public sealed class Preferences
{
    public bool Topmost { get; set; } = true;
    public bool AutoDock { get; set; } = true;
    public double Opacity { get; set; } = .96;
    public Placement? Position { get; set; }
    public System.Collections.Generic.Dictionary<string, bool> Modules { get; set; } = new();
}

public static class Docking
{
    public static Edge Snap(Rectangle bounds, Rectangle area, int tolerance)
    {
        var distances = new[] {
            (Edge.Left, Math.Abs(bounds.Left-area.Left)), (Edge.Right, Math.Abs(bounds.Right-area.Right)),
            (Edge.Top, Math.Abs(bounds.Top-area.Top)), (Edge.Bottom, Math.Abs(bounds.Bottom-area.Bottom))
        };
        var nearest = distances.MinBy(item => item.Item2);
        return nearest.Item2 <= tolerance ? nearest.Item1 : Edge.None;
    }

    public static Rectangle Fit(Rectangle rect, Rectangle area, Edge edge)
    {
        int w = Math.Min(rect.Width, area.Width), h = Math.Min(rect.Height, area.Height);
        int x = Math.Clamp(rect.X, area.Left, area.Right - w), y = Math.Clamp(rect.Y, area.Top, area.Bottom - h);
        if (edge == Edge.Left) x = area.Left;
        if (edge == Edge.Right) x = area.Right - w;
        if (edge == Edge.Top) y = area.Top;
        if (edge == Edge.Bottom) y = area.Bottom - h;
        return new Rectangle(x, y, w, h);
    }

    public static Rectangle Handle(Rectangle body, Edge edge, double scale)
    {
        int thin = (int)Math.Ceiling(15 * scale), wide = (int)Math.Ceiling(48 * scale);
        return edge switch
        {
            Edge.Left => new(body.Left, body.Top + (body.Height - wide) / 2, thin, wide),
            Edge.Right => new(body.Right - thin, body.Top + (body.Height - wide) / 2, thin, wide),
            Edge.Top => new(body.Left + (body.Width - wide) / 2, body.Top, wide, thin),
            Edge.Bottom => new(body.Left + (body.Width - wide) / 2, body.Bottom - thin, wide, thin),
            _ => body
        };
    }
}
