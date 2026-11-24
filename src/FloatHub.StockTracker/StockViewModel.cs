using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FloatHub.Core;

namespace FloatHub.StockTracker;

public sealed class MetricCell : Observable
{
    private string value = "—", color = "#A9C2BE";
    public string Value { get => value; set => Set(ref this.value, value); }
    public string Color { get => color; set => Set(ref color, value); }
    public required Field Field { get; init; }
}

public sealed class Capsule : Observable
{
    private string label = "", state = "等待", detail = "";
    public Watch Watch { get; private set; }
    public Quote? Quote { get; set; }
    public string Issue { get; set; } = "等待刷新";
    public string Label { get => label; private set => Set(ref label, value); }
    public string State { get => state; private set => Set(ref state, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public ObservableCollection<MetricCell> Cells { get; } = [];
    public Capsule(Watch watch) { Watch = watch; Label = watch.Label; }
    public void Rename(Watch watch) { Watch = watch; Label = watch.Label; }

    public void Render(StockOptions options, DateTimeOffset now)
    {
        var visible = options.Fields.Where(f => f.Visible).ToArray();
        if (!Cells.Select(c => c.Field).SequenceEqual(visible))
        {
            Cells.Clear();
            foreach (var field in visible) Cells.Add(new MetricCell { Field = field });
        }
        var q = Quote;
        string region = Watch.Security.Region;
        string session = MarketClock.Session(region, now);
        var delayed = region == "hk";
        bool old = q != null && (MarketClock.ExpectedQuoteTime(region, now) - q.Time > TimeSpan.FromMinutes(delayed ? 22 : 5) ||
            q.Time > now.AddMinutes(5));
        State = Issue != "" ? (q == null ? Issue : "过期缓存") : old ? "已过期" : delayed ? "延时" : "快照";
        string time = q?.Time.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";
        Detail = $"{Watch.Security.Caption}\n{session}（交易所日历）  ·  {State}\n行情时间  {time}\n腾讯财经 · {(delayed ? "港股延时报价" : "延迟时长未获保证")}";
        if (Watch.Security.IsIndex && q != null) Detail += $"\n指数点位  {q.Last:0.####} 点";
        if (Issue != "") Detail += "\n" + Issue;
        foreach (var cell in Cells)
        {
            cell.Value = Format(q, cell.Field, options.ShortUnits);
            cell.Color = q == null || cell.Field.Metric == Metric.Turnover ? "#DBE8E3" : q.Change switch
            {
                > 0 => "#FF999A",
                < 0 => "#70DEB1",
                _ => "#B4C3C0"
            };
        }
    }

    public static string Format(Quote? q, Field field, bool shorten)
    {
        if (q == null) return "—";
        string fmt = "F" + Math.Clamp(field.Precision, 0, 4);
        string Amount(decimal d) => d.ToString(fmt, CultureInfo.InvariantCulture);
        if (field.Metric == Metric.Turnover)
        {
            if (q.Turnover is not decimal total || q.Currency == "") return "—";
            (decimal scale, string unit) = shorten && total >= 100000000 ? (100000000m, "亿") :
                shorten && total >= 10000 ? (10000m, "万") : (1m, "");
            return Amount(total / scale) + unit + " " + q.Currency;
        }
        var v = field.Metric == Metric.Percent ? q.Percent : q.Change;
        return (v > 0 ? "+" : "") + Amount(v) + (field.Metric == Metric.Percent ? "%" : "");
    }
}

public sealed class StockViewModel : Observable, IDisposable
{
    private readonly IQuoteService provider;
    private readonly Store store;
    private readonly Journal log;
    private CancellationTokenSource? run;
    private bool enabled, presented, disposed;
    private string connection = "尚未刷新";
    private DateTimeOffset? lastSuccess;
    public StockOptions Options { get; }
    public ObservableCollection<Capsule> Rows { get; } = [];
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public DateTimeOffset? LastSuccess { get => lastSuccess; private set => Set(ref lastSuccess, value); }
    public bool Empty => Rows.Count == 0;
    public IQuoteService Provider => provider;

    public StockViewModel(IQuoteService provider, Store store, Journal log)
    {
        this.provider = provider; this.store = store; this.log = log;
        Options = store.Read("stocks.options", new StockOptions());
        LastSuccess = store.Read<DateTimeOffset?>("stocks.lastSuccess", null);
        var cache = store.Read("stocks.quotes", new Dictionary<string, Quote>());
        foreach (var watch in Options.Watchlist)
        {
            var row = new Capsule(watch);
            if (cache.TryGetValue(watch.Security.Key, out var quote)) { row.Quote = quote; row.Issue = "本地缓存，等待联网更新"; }
            Rows.Add(row);
        }
        Render();
    }

    public void Add(Security security)
    {
        if (Options.Watchlist.Any(w => w.Security.Key == security.Key)) return;
        var watch = new Watch(security);
        Options.Watchlist.Add(watch); Rows.Add(new Capsule(watch));
        WatchlistChanged();
    }
    public void Remove(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        Options.Watchlist.RemoveAt(index); Rows.RemoveAt(index);
        SaveCache(); WatchlistChanged();
    }
    public void Move(int from, int to)
    {
        if (from < 0 || to < 0 || to >= Rows.Count || from >= Rows.Count) return;
        var watch = Options.Watchlist[from];
        Options.Watchlist.RemoveAt(from); Options.Watchlist.Insert(to, watch);
        Rows.Move(from, to); Save();
    }
    public void Rename(int index, string alias)
    {
        if (index < 0 || index >= Rows.Count) return;
        var watch = Options.Watchlist[index] with { Alias = alias.Trim() };
        Options.Watchlist[index] = watch; Rows[index].Rename(watch); Save();
    }
    private void WatchlistChanged() { Save(); Render(); Notify(nameof(Empty)); Restart(); }
    public void Save() => store.Write("stocks.options", Options);
    private void SaveCache() => store.Write("stocks.quotes", Rows.Where(r => r.Quote != null).ToDictionary(r => r.Watch.Security.Key, r => r.Quote!));
    public void Render() { var now = DateTimeOffset.UtcNow; foreach (var row in Rows) row.Render(Options, now); }
    public void Enable(bool value) { if (enabled != value) { enabled = value; Restart(); } }
    public void Present(bool value) { if (presented != value) { presented = value; Restart(); } }
    public void Restart()
    {
        run?.Cancel(); run = null;
        if (disposed) return;
        Connection = !enabled ? "模块已停用" : !presented ? "窗口已收纳或隐藏，暂停刷新" : Empty ? "等待添加股票" : "正在连接腾讯财经";
        if (!enabled || !presented || Empty) return;
        foreach (var row in Rows)
        {
            if (row.Quote != null) row.Issue = "等待刷新";
            row.Render(Options, DateTimeOffset.UtcNow);
        }
        run = new CancellationTokenSource();
        _ = Schedule(run);
    }
    private async Task Schedule(CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            do
            {
                int successes = 0;
                bool changed = false;
                foreach (var batch in Rows.ToArray().Chunk(TencentService.BatchSize))
                {
                    try
                    {
                        var quotes = await provider.FetchAsync(batch.Select(r => r.Watch.Security).ToArray(), token);
                        token.ThrowIfCancellationRequested();
                        foreach (var row in batch)
                        {
                            if (quotes.TryGetValue(row.Watch.Security.Key, out var fresh) && fresh.Security.Market == row.Watch.Security.Market &&
                                (row.Quote == null || fresh.Time >= row.Quote.Time))
                            {
                                changed |= row.Quote != fresh;
                                row.Quote = fresh; row.Issue = ""; successes++;
                            }
                            else row.Issue = "无有效报价";
                            row.Render(Options, DateTimeOffset.UtcNow);
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        log.Record("quotes.batch.failed", ex);
                        foreach (var row in batch) { row.Issue = "连接异常"; row.Render(Options, DateTimeOffset.UtcNow); }
                    }
                }
                if (successes > 0) { LastSuccess = DateTimeOffset.Now; if (changed) { SaveCache(); store.Write("stocks.lastSuccess", LastSuccess); } }
                Connection = successes == Rows.Count ? "已连接腾讯财经" : successes > 0 ? $"部分可用 · {successes}/{Rows.Count}" : "行情暂不可用";
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, Options.Seconds)), token);
            } while (!token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { log.Record("scheduler.failed", ex); Connection = "刷新失败"; }
        finally { if (ReferenceEquals(owner, run)) run = null; owner.Dispose(); }
    }
    public void Dispose()
    {
        disposed = true; run?.Cancel(); provider.Dispose();
        if (LastSuccess != null) store.Write("stocks.lastSuccess", LastSuccess);
    }
}
