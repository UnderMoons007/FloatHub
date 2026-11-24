using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatHub.Core;
using Screen = System.Windows.Forms.Screen;
using RectPx = System.Drawing.Rectangle;
using PointPx = System.Drawing.Point;
using SizePx = System.Drawing.Size;

namespace FloatHub;

public partial class FloatingWindow : Window
{
    private readonly Preferences prefs;
    private readonly Action save;
    private readonly DispatcherTimer leave = new() { Interval = TimeSpan.FromMilliseconds(560) };
    private Screen screen = Screen.PrimaryScreen!;
    private RectPx expanded;
    private Edge edge;
    private bool collapsed, animating, dragging, startingDrag, layoutPending, ready;
    private PointPx press, offset;
    private IntPtr hwnd;
    private int transition;
    private double scale = 1;
    public bool Presented => ready && IsVisible && !collapsed;
    public event Action? PresentationChanged;
    public bool Exiting { get; set; }

    public FloatingWindow(Preferences preferences, Action save, Action settings, Action toggle, Action hide)
    {
        InitializeComponent();
        prefs = preferences;
        this.save = save;
        Topmost = prefs.Topmost;
        Opacity = prefs.Opacity;
        Options.Click += (_, _) => settings();
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (title, action) in new (string, Action)[] { ("打开设置", settings), ("收纳 / 固定展开", toggle), ("隐藏到托盘", hide) })
        {
            var item = new System.Windows.Controls.MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        ContextMenu = menu;
        menu.Opened += (_, _) => leave.Stop();
        menu.Closed += (_, _) => ArmLeave();
        leave.Tick += (_, _) => { leave.Stop(); if (!IsMouseOver && !dragging && !startingDrag && !menu.IsOpen) Fold(); };
        Grip.MouseEnter += (_, _) => { if (collapsed && !animating && !startingDrag) Reveal(); };
        MouseEnter += (_, _) => leave.Stop();
        MouseLeave += (_, _) => ArmLeave();
        PreviewMouseLeftButtonDown += Press;
        PreviewMouseMove += Move;
        PreviewMouseLeftButtonUp += Release;
        LostMouseCapture += (_, _) => { if (dragging || startingDrag) FinishDrag(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) leave.Stop(); PresentationChanged?.Invoke(); };
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)!.AddHook(Hook);
            ResetBounds(false);
            ready = true;
        };
        Body.SizeChanged += (_, _) => { if (ready) QueueLayout(); };
        ContentRendered += (_, _) => PresentationChanged?.Invoke();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Exiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    private IntPtr Hook(IntPtr handle, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message is 0x007E or 0x001A) Dispatcher.BeginInvoke(() => { if (ready && !dragging) ResetBounds(false); });
        if (message == 0x02E0) QueueLayout();
        return IntPtr.Zero;
    }

    private static double ScaleFor(Screen target)
    {
        var m = MonitorFromPoint(new PointPx(target.Bounds.Left + 1, target.Bounds.Top + 1), 2);
        return GetDpiForMonitor(m, 0, out var x, out _) == 0 ? x / 96d : 1;
    }

    private SizePx MeasureBody()
    {
        var area = screen.WorkingArea;
        Body.MaxWidth = Math.Max(1, area.Width / scale);
        Viewport.MaxHeight = Math.Max(36, Math.Min(490, area.Height / scale - 60));
        Body.Measure(new System.Windows.Size(Body.MaxWidth, area.Height / scale));
        return new SizePx(Math.Min(area.Width, (int)Math.Ceiling(Body.DesiredSize.Width * scale)),
            Math.Min(area.Height, (int)Math.Ceiling(Body.DesiredSize.Height * scale)));
    }

    private void QueueLayout()
    {
        if (layoutPending) return;
        layoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            layoutPending = false;
            if (!ready || dragging || startingDrag || animating) return;
            scale = ScaleFor(screen);
            var size = MeasureBody();
            if (size == expanded.Size) return;
            var origin = expanded.Location;
            // Keep the bottom-right launch corner stable as the first rows arrive.
            if (prefs.Position == null) origin = DefaultOrigin(size);
            expanded = Docking.Fit(new RectPx(origin, size), screen.WorkingArea, edge);
            Place(collapsed ? Docking.Handle(expanded, edge, scale) : expanded);
        }));
    }

    private PointPx DefaultOrigin(SizePx size)
    {
        int gap = (int)Math.Round(16 * scale);
        return new(screen.WorkingArea.Right - size.Width - gap, screen.WorkingArea.Bottom - size.Height - gap);
    }

    public void ResetBounds(bool forget)
    {
        if (forget) { prefs.Position = null; save(); }
        StopAnimation();
        var pos = prefs.Position;
        screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == pos?.Monitor) ?? Screen.PrimaryScreen!;
        if (screen.DeviceName != pos?.Monitor) pos = null;
        scale = ScaleFor(screen);
        var size = MeasureBody();
        edge = prefs.AutoDock ? pos?.Edge ?? Edge.None : Edge.None;
        var area = screen.WorkingArea;
        var origin = pos == null ? DefaultOrigin(size) : new PointPx(area.Left + pos.X, area.Top + pos.Y);
        expanded = Docking.Fit(new RectPx(origin, size), area, edge);
        collapsed = edge != Edge.None;
        Settle();
    }

    private void Place(RectPx r)
    {
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0014);
    }

    private void Remember()
    {
        prefs.Position = new Placement(screen.DeviceName, expanded.X - screen.WorkingArea.Left,
            expanded.Y - screen.WorkingArea.Top, edge);
        save();
    }

    public void ToggleDock()
    {
        if (edge != Edge.None) { PinOpen(); return; }
        prefs.AutoDock = true;
        edge = Docking.Snap(expanded, screen.WorkingArea, int.MaxValue);
        expanded = Docking.Fit(expanded, screen.WorkingArea, edge);
        Place(expanded);
        Remember();
        Fold();
    }

    public void PinOpen()
    {
        StopAnimation();
        collapsed = false;
        edge = Edge.None;
        Remember();
        Settle();
    }

    private void ArmLeave()
    {
        if (edge == Edge.None || collapsed || startingDrag || dragging || ContextMenu.IsOpen) return;
        leave.Stop();
        leave.Start();
    }

    private void StopAnimation()
    {
        transition++;
        animating = false;
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void Settle()
    {
        bool vertical = edge is Edge.Left or Edge.Right;
        GripLine.Width = vertical ? 3 : 22;
        GripLine.Height = vertical ? 22 : 3;
        Grip.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = collapsed ? Visibility.Hidden : Visibility.Visible;
        Place(collapsed ? Docking.Handle(expanded, edge, scale) : expanded);
        PresentationChanged?.Invoke();
        // A quote or alias can change the body width while a slide is in progress.
        if (ready) QueueLayout();
    }

    private void Fold()
    {
        if (edge == Edge.None || collapsed) return;
        Animate(true);
    }

    private void Reveal()
    {
        if (collapsed) Animate(false);
    }

    private void Animate(bool fold)
    {
        StopAnimation();
        int generation = transition;
        animating = true;
        collapsed = fold;
        Place(expanded);
        Grip.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        bool horizontal = edge is Edge.Left or Edge.Right;
        double extent = (horizontal ? expanded.Width : expanded.Height) / scale * (edge is Edge.Left or Edge.Top ? -1 : 1);
        var slide = new DoubleAnimation(fold ? 0 : extent, fold ? extent : 0, TimeSpan.FromMilliseconds(155))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        slide.Completed += (_, _) =>
        {
            if (transition != generation) return;
            StopAnimation();
            Settle();
            if (!collapsed && !IsMouseOver) ArmLeave();
        };
        Slide.BeginAnimation(horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty, slide);
        PresentationChanged?.Invoke();
    }

    private void Press(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is Visual; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or ScrollBar) return;
        leave.Stop();
        GetCursorPos(out press);
        offset = new PointPx(press.X - expanded.X, press.Y - expanded.Y);
        startingDrag = true;
        CaptureMouse();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (!startingDrag && !dragging) return;
        GetCursorPos(out var p);
        if (!dragging && Math.Abs(p.X - press.X) < SystemParameters.MinimumHorizontalDragDistance * scale &&
            Math.Abs(p.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance * scale) return;
        if (!dragging)
        {
            StopAnimation();
            startingDrag = false;
            dragging = true;
            collapsed = false;
            Grip.Visibility = Visibility.Collapsed;
            Body.Visibility = Visibility.Visible;
            PresentationChanged?.Invoke();
        }
        var next = Screen.FromPoint(p);
        if (next.DeviceName != screen.DeviceName)
        {
            var before = scale;
            screen = next;
            scale = ScaleFor(screen);
            offset = new PointPx((int)(offset.X * scale / before), (int)(offset.Y * scale / before));
            expanded.Size = MeasureBody();
        }
        expanded.Location = new PointPx(p.X - offset.X, p.Y - offset.Y);
        Place(expanded);
    }

    private void Release(object sender, MouseButtonEventArgs e)
    {
        if (!startingDrag && !dragging) return;
        FinishDrag();
        ReleaseMouseCapture();
    }

    private void FinishDrag()
    {
        startingDrag = false;
        if (!dragging) { if (!IsMouseOver) ArmLeave(); return; }
        dragging = false;
        var area = screen.WorkingArea;
        expanded = Docking.Fit(expanded, area, Edge.None);
        edge = prefs.AutoDock ? Docking.Snap(expanded, area, (int)(12 * scale)) : Edge.None;
        expanded = Docking.Fit(expanded, area, edge);
        Place(expanded);
        Remember();
        if (edge != Edge.None) Fold();
        else Settle();
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointPx point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointPx point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
