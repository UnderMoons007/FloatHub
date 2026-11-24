using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatHub.Core;
using Ui = FloatHub.Core.Controls;

namespace FloatHub.StockTracker;

internal sealed class WatchlistPage : StackPanel
{
    private readonly StockViewModel vm;
    private readonly Journal log;
    private readonly TextBox query = new() { MaxLength = 80, MinWidth = 120 };
    private readonly ListBox results = new() { MaxHeight = 145, MinHeight = 36 };
    private readonly ListBox watches = new() { MinHeight = 60, MaxHeight = 170 };
    private readonly TextBox alias = new() { MaxLength = 24, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock feedback = Ui.Text("输入名称、代码或拼音，例如 腾讯、AAPL 或 bj920002。", 13);
    private readonly TextBlock watchInfo = Ui.Text("", 13);
    private CancellationTokenSource? searching;
    private string market = TencentService.Markets[0];
    private bool selecting;

    public WatchlistPage(StockViewModel vm, Journal log)
    {
        this.vm = vm; this.log = log;
        var box = Ui.Choice(TencentService.Markets, market, m => { market = m; if (query.Text.Trim() != "") _ = Search(); });
        var bar = new DockPanel();
        var find = Ui.Button("搜索", () => _ = Search(), true); DockPanel.SetDock(find, Dock.Right); bar.Children.Add(find);
        DockPanel.SetDock(box, Dock.Left); box.Margin = new Thickness(0, 2, 8, 2); bar.Children.Add(box); bar.Children.Add(query);
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Search(); } };
        query.TextChanged += (_, _) => { searching?.Cancel(); results.ItemsSource = null; feedback.Text = "按 Enter 搜索"; };
        results.MouseDoubleClick += (_, _) => Add();
        var add = Ui.Button("添加到自选", Add, true);
        results.SelectionChanged += (_, _) => add.IsEnabled = results.SelectedItem != null; add.IsEnabled = false;
        Children.Add(Ui.Card("搜索证券", bar, feedback, results, Ui.Row(add)));
        watches.SelectionChanged += (_, _) => Select();
        alias.TextChanged += (_, _) => { if (!selecting && watches.SelectedIndex >= 0) vm.Rename(watches.SelectedIndex, alias.Text); };
        Children.Add(Ui.Card("我的自选", watches,
            Ui.Row(Ui.Button("上移", () => Move(-1)), Ui.Button("下移", () => Move(1)), Ui.Button("移除", Remove)),
            Ui.Text("显示名称", 13), alias, watchInfo));
        Rebind(0);
        Unloaded += (_, _) => searching?.Cancel();
    }
    private async Task Search()
    {
        searching?.Cancel();
        var term = query.Text.Trim(); if (term == "") return;
        using var owner = new CancellationTokenSource();
        searching = owner;
        try
        {
            feedback.Text = "正在查询腾讯证券目录…"; results.ItemsSource = null;
            var found = await vm.Provider.SearchAsync(term, market, owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            results.ItemsSource = found;
            if (found.Count > 0) results.SelectedIndex = 0;
            feedback.Text = found.Count == 0 ? "未找到匹配证券。可尝试交易所前缀和完整代码。" : $"找到 {found.Count} 只证券";
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex) { log.Record("search.failed", ex); feedback.Text = "搜索连接失败，可稍后重试。"; }
        finally { if (ReferenceEquals(owner, searching)) searching = null; }
    }
    private void Add()
    {
        if (results.SelectedItem is not Security s) return;
        int index = vm.Options.Watchlist.FindIndex(w => w.Security.Key == s.Key);
        if (index >= 0) { feedback.Text = "已在自选列表中"; Rebind(index); return; }
        vm.Add(s); Rebind(vm.Rows.Count - 1); feedback.Text = "已添加 " + s.Name;
    }
    private void Move(int delta)
    {
        var index = watches.SelectedIndex; int target = index + delta;
        if (index < 0 || target < 0 || target >= vm.Rows.Count) return;
        vm.Move(index, target); Rebind(target);
    }
    private void Remove() { int index = watches.SelectedIndex; vm.Remove(index); Rebind(index); }
    private void Rebind(int index)
    {
        watches.ItemsSource = vm.Options.Watchlist.Select(w => w.Security.Caption).ToList();
        watches.SelectedIndex = Math.Min(index, vm.Rows.Count - 1); Select();
    }
    private void Select()
    {
        selecting = true;
        int i = watches.SelectedIndex;
        alias.IsEnabled = i >= 0;
        alias.Text = i >= 0 ? vm.Options.Watchlist[i].Alias : "";
        watchInfo.Text = i >= 0 ? "留空时使用证券简称的首个文字或字母。" : "添加股票后即可设置显示名称。";
        selecting = false;
    }
}
