using System;
using System.Drawing;
using System.IO;
using FloatHub.Core;

internal static partial class Program
{
    private static int passed;
    internal static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS " + name); passed++;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        var area = new Rectangle(-1920, -180, 1920, 1080);
        foreach (var edge in new[] { Edge.Left, Edge.Top, Edge.Right, Edge.Bottom })
        {
            var body = Docking.Fit(new Rectangle(-1300, 120, 310, 180), area, edge);
            Check(Docking.Snap(body, area, 12) == edge && area.Contains(Docking.Handle(body, edge, 1.5)), "dock/handle " + edge);
        }
        Check(Docking.Snap(new Rectangle(-1000, 200, 240, 120), area, 12) == Edge.None, "drag off edge");
        var dir = Path.Combine(Path.GetTempPath(), "FloatHub-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var db = new Store(dir)) db.Write("desktop", new Preferences { Opacity = .72, Position = new("test", 24, 32, Edge.Top) });
            using var reopened = new Store(dir);
            var restored = reopened.Read("desktop", new Preferences());
            Check(restored.Opacity == .72 && restored.Position?.Edge == Edge.Top, "SQLite reopen");
            var backup = Directory.CreateDirectory(Path.Combine(dir, "backup")).FullName;
            reopened.Backup(Path.Combine(backup, "floathub.db"));
            using var copy = new Store(backup);
            Check(copy.Read("desktop", new Preferences()).Position?.Edge == Edge.Top, "consistent SQLite backup");
        }
        finally { Directory.Delete(dir, true); }
        Extra(args);
        Console.WriteLine($"{passed} checks passed");
    }
    static partial void Extra(string[] args);
}
