using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatHub;
using FloatHub.Core;
using FloatHub.StockTracker;
using PointPx = System.Drawing.Point;

internal static class DesktopChecks
{
    private static readonly string tempRoot = Path.Combine(Path.GetTempPath(), "FloatHub-desktop-" + Guid.NewGuid().ToString("N"));
    private static readonly string images = Path.GetFullPath(".test-work/screens");
    public static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = new ResourceDictionary { Source = new Uri("/FloatHub.Core;component/Theme.xaml", UriKind.Relative) };
        Exception? error = null;
        app.Startup += async (_, _) =>
        {
            try { await Exercise(); }
            catch (Exception ex) { error = ex; }
            finally { app.Shutdown(); }
        };
        app.Run();
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        if (error != null) throw new Exception("Desktop checks failed", error);
    }

    private static async Task Exercise()
    {
        using var db = new Store(tempRoot);
        var prefs = new Preferences();
        using var stocks = new StockModule();
        stocks.Initialize(db, new Journal(tempRoot), _ => { });
        stocks.LoadConfiguration();
        FloatingWindow? window = null;
        window = new FloatingWindow(prefs, () => db.Write("desktop", prefs), () => { }, () => window!.ToggleDock(), () => window!.Hide());
        ((StackPanel)window.FindName("Modules")).Children.Add(stocks.View);
        GetCursorPos(out var saved);
        try
        {
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            await Task.Delay(250);
            var body = Bounds(hwnd);
            Program.Check(window.Presented && body.Width > 150 && body.Height >= 85, "real HWND initial size");
            var view = (StockViewModel)stocks.View.DataContext;
            stocks.SetEnabled(true); stocks.SetPresented(true);
            for (int i = 0; i < 60 && !view.Rows.All(r => r.Quote != null); i++) await Task.Delay(200);
            Program.Check(view.Rows.All(r => r.Quote != null), "live quotes in WPF view model");
            Directory.CreateDirectory(images); Snapshot(window.Content as FrameworkElement, "widget");
            window.ToggleDock(); await Task.Delay(290);
            var grip = Bounds(hwnd);
            Program.Check(!window.Presented && Math.Min(grip.Width, grip.Height) < body.Height / 2, "fold shrinks actual HWND");
            var center = new PointPx(grip.Left + grip.Width / 2, grip.Top + grip.Height / 2);
            SetCursorPos(center.X, center.Y);
            for (int i = 0; i < 20 && !window.Presented; i++) await Task.Delay(50);
            if (!window.Presented)
            {
                GetCursorPos(out var actual);
                Console.WriteLine($"Handle={grip}, current={Bounds(hwnd)}, pointer={actual}, pointerWindow={WindowFromPoint(actual)}, testWindow={hwnd}, overGrip={((FrameworkElement)window.FindName("Grip")).IsMouseOver}");
                Snapshot(window.Content as FrameworkElement, "handle");
            }
            Program.Check(window.Presented && prefs.Position?.Edge != Edge.None, "native handle hover reveals");
            SetCursorPos(body.Left - 60, body.Top - 60);
            await Task.Delay(1000);
            Program.Check(!window.Presented, "mouse leave delay folds again");

            // Deliver routed button events only to the test window, never click the desktop.
            grip = Bounds(hwnd);
            SetCursorPos(grip.Left + grip.Width / 2, grip.Top + grip.Height / 2);
            await Task.Delay(240);
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            SetCursorPos(body.Left - 180, body.Top - 90);
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.PreviewMouseMoveEvent });
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await Task.Delay(850);
            Program.Check(window.Presented && prefs.Position?.Edge == Edge.None, "drag detaches and stays open");
            foreach (var side in new[] { Edge.Left, Edge.Right, Edge.Top, Edge.Bottom })
            {
                prefs.Position = prefs.Position! with { Edge = side };
                window.ResetBounds(false);
                await Task.Delay(90);
                var bounds = Bounds(hwnd);
                var vertical = side is Edge.Left or Edge.Right;
                Program.Check(!window.Presented && (vertical ? bounds.Width < bounds.Height : bounds.Height < bounds.Width),
                    "restore native handle " + side);
            }
            window.ResetBounds(true);
            window.Close();
            Program.Check(!window.IsVisible, "close hides floating HWND");
            window.Show(); Program.Check(window.Presented, "show restores floating HWND");
            var settings = new SettingsWindow(stocks.Pages.ToList());
            try
            {
                settings.Show();
                foreach (var page in stocks.Pages)
                {
                    settings.Select(page.Title); await Task.Delay(140);
                    Program.Check(settings.Current == page.Title && ((ContentControl)settings.FindName("Page")).Content is FrameworkElement,
                        "settings page " + page.Title);
                    if (page.Title == "行情显示") Snapshot(settings.Content as FrameworkElement, "settings");
                }
            }
            finally { settings.Close(); }
            stocks.SetEnabled(false);
            for (int i = 0; i < 35; i++) view.Add(new Security("test" + i, i.ToString(), "测试证券" + i, "测试"));
            await Task.Delay(200);
            var viewport = (ScrollViewer)window.FindName("Viewport");
            Program.Check(viewport.ScrollableHeight > 0 && viewport.ActualHeight <= 490, "large watchlist scrolls inside viewport");
        }
        finally
        {
            SetCursorPos(saved.X, saved.Y); window.Exiting = true; window.Close();
        }
    }

    private static void Snapshot(FrameworkElement? element, string name)
    {
        if (element == null) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(images, name + ".png")); encoder.Save(file);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public int Left, Top, Right, Bottom; }
    private static Rectangle Bounds(IntPtr hwnd) { GetWindowRect(hwnd, out var b); return Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom); }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Box box);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointPx point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointPx point);
}
