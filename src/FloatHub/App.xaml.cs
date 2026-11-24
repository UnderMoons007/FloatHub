using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using FloatHub.Core;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Ui = FloatHub.Core.Controls;

namespace FloatHub;

public partial class App : Application
{
    private readonly List<IDesktopModule> modules = new();
    private readonly Dictionary<string, Forms.ToolStripMenuItem> moduleItems = new();
    private Store store = null!;
    private Preferences prefs = null!;
    private Journal log = null!;
    private FloatingWindow floating = null!;
    private SettingsWindow? settings;
    private Forms.NotifyIcon? tray;
    private Mutex? mutex;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wakeListener;
    private static string StartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "FloatHub.lnk");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(false, @"Local\FloatHub.Desktop", out bool first);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FloatHub.Show");
        if (!first) { wake.Set(); Shutdown(); return; }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatHub");
        log = new Journal(folder);
        DispatcherUnhandledException += (_, a) =>
        {
            log.Record("desktop.error", a.Exception);
            MessageBox.Show(a.Exception.Message, "FloatHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            a.Handled = true;
        };
        try
        {
            store = new Store(folder);
            prefs = store.Read("desktop", new Preferences());
            floating = new FloatingWindow(prefs, Save, () => OpenSettings("常规设置"), () => floating.ToggleDock(), () => floating.Hide());
            MainWindow = floating;
            RegisterModules();
            foreach (var module in modules)
            {
                module.Initialize(store, log, OpenSettings);
                module.LoadConfiguration();
                floating.Modules.Children.Add(module.View);
                Enable(module, IsEnabled(module), false);
            }
            floating.PresentationChanged += () => { foreach (var module in modules) module.SetPresented(floating.Presented); };
            CreateTray();
            floating.Show();
            wakeListener = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(() => { floating.Show(); OpenSettings("常规设置"); }), null, -1, false);
            log.Record("desktop.started");
        }
        catch (Exception ex)
        {
            log.Record("desktop.start.failed", ex);
            MessageBox.Show(ex.Message, "FloatHub 启动失败"); Shutdown(1);
        }
    }

    private void RegisterModules() => modules.Add(new StockTracker.StockModule());

    private bool IsEnabled(IDesktopModule m) => !prefs.Modules.TryGetValue(m.Id, out bool on) || on;
    private void Save() => store.Write("desktop", prefs);
    private void Enable(IDesktopModule m, bool on, bool persist = true)
    {
        prefs.Modules[m.Id] = on;
        if (persist) Save();
        m.SetEnabled(on);
        m.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (moduleItems.TryGetValue(m.Id, out var item)) item.Checked = on;
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        void Item(string text, Action run) => menu.Items.Add(text, null, (_, _) => run());
        Item("显示 / 隐藏悬浮组件", ToggleVisible);
        Item("打开设置", () => OpenSettings("常规设置"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var m in modules)
        {
            var item = new Forms.ToolStripMenuItem(m.Name) { Checked = IsEnabled(m) };
            item.Click += (_, _) => { Enable(m, !IsEnabled(m)); RefreshSettings("模块管理"); };
            menu.Items.Add(item); moduleItems.Add(m.Id, item);
        }
        var startup = new Forms.ToolStripMenuItem("开机启动");
        menu.Opening += (_, _) => startup.Checked = StartsWithWindows();
        startup.Click += (_, _) => { SetStartup(!StartsWithWindows()); RefreshSettings("常规设置"); };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item("退出 FloatHub", Shutdown);
        tray = new Forms.NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, Text = "FloatHub · 桌面悬浮组件", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ToggleVisible();
    }

    private void ToggleVisible() { if (floating.IsVisible) floating.Hide(); else floating.Show(); }
    private void RefreshSettings(string page) { if (settings?.Current == page) settings.Select(page); }

    private static bool StartsWithWindows() => File.Exists(StartupLink);
    private void SetStartup(bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StartupLink)!);
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
                try
                {
                    dynamic link = shell.CreateShortcut(StartupLink);
                    try { link.TargetPath = Environment.ProcessPath!; link.Description = "FloatHub"; link.Save(); }
                    finally { Marshal.FinalReleaseComObject(link); }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            else File.Delete(StartupLink);
            store.Write("startup", on);
        }
        catch (Exception ex) { log.Record("startup.write", ex); MessageBox.Show(ex.Message, "开机启动设置"); }
    }

    private void OpenSettings(string page)
    {
        if (settings == null)
        {
            var pages = new List<SettingsPage> {
                new("常规设置","工作区","\uE713",General),
                new("模块管理","工作区","\uE74C",ModuleList)
            };
            pages.AddRange(modules.SelectMany(m => m.Pages));
            pages.Add(new("关于", "应用", "\uE946", About));
            settings = new SettingsWindow(pages);
            settings.Closed += (_, _) => settings = null;
        }
        settings.Select(page);
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Show(); settings.Activate();
    }

    private FrameworkElement General()
    {
        var value = Ui.Text($"{prefs.Opacity:P0}", 14);
        var slider = new Slider { Minimum = .35, Maximum = 1, Value = prefs.Opacity, TickFrequency = .05, IsSnapToTickEnabled = true, Width = 200, Margin = new Thickness(0, 6, 14, 6) };
        slider.ValueChanged += (_, _) => { floating.Opacity = prefs.Opacity = slider.Value; value.Text = $"{prefs.Opacity:P0}"; Save(); };
        return Ui.Stack(
            Ui.Card("桌面窗口", Ui.Toggle("窗口始终置顶", prefs.Topmost, on => { floating.Topmost = prefs.Topmost = on; Save(); }),
                Ui.Text("窗口不透明度", 13), Ui.Row(slider, value)),
            Ui.Card("屏幕边缘", Ui.Toggle("拖到边缘时自动收纳", prefs.AutoDock, on => { prefs.AutoDock = on; if (!on) floating.PinOpen(); Save(); }),
                Ui.Text("悬停把手可临时展开。拖离边缘并松开可固定窗口。", 13, "#536B67"),
                Ui.Row(Ui.Button("收纳 / 固定展开", () => { floating.ToggleDock(); RefreshSettings("常规设置"); }), Ui.Button("恢复右下角位置", () => floating.ResetBounds(true)))),
            Ui.Card("启动与本地配置", Ui.Toggle("登录 Windows 后启动", StartsWithWindows(), SetStartup),
                Ui.Row(Ui.Button("备份配置与自选股", Backup), Ui.Button("打开数据文件夹", () => Process.Start(new ProcessStartInfo(store.Folder) { UseShellExecute = true })))));
    }

    private void Backup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQLite 数据库 (*.db)|*.db", FileName = $"FloatHub-{DateTime.Now:yyyyMMdd-HHmm}.db", AddExtension = true };
        if (dialog.ShowDialog(settings) == true) store.Backup(dialog.FileName);
    }

    private FrameworkElement ModuleList()
    {
        var panel = new StackPanel();
        foreach (var m in modules) panel.Children.Add(Ui.Card(m.Name, Ui.Text(m.Description), Ui.Toggle("启用模块", IsEnabled(m), on => Enable(m, on))));
        return panel;
    }

    private FrameworkElement About() => Ui.Stack(Ui.Card("FloatHub 1.0.2", Ui.Text("Windows x64 桌面悬浮组件")),
        Ui.Card("此版本更新", Ui.Text("四边自动收纳、托盘管理、自选股与可配置行情胶囊。")),
        Ui.Card("本地数据", Ui.Text(store.Folder, 13)));

    protected override void OnExit(ExitEventArgs e)
    {
        wakeListener?.Unregister(null);
        if (floating != null) floating.Exiting = true;
        foreach (var m in modules) m.Dispose();
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); }
        store?.Dispose(); wake?.Dispose(); mutex?.Dispose();
        base.OnExit(e);
    }
}
