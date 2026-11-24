using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FloatHub.Core;
using FloatHub.StockTracker;

internal static partial class Program
{
    static partial void DisplayChecks()
    {
        var sec = new Security("hk00700", "00700", "腾讯控股", "港交所");
        Check(new Watch(sec with { Name = "*ST 华讯" }).Label == "华" && new Watch(sec with { Name = "3M Company" }).Label == "M", "first Chinese character or English letter");
        var now = new DateTimeOffset(2026, 11, 24, 11, 30, 0, TimeSpan.FromHours(8));
        var quote = new Quote(sec, 103.5m, 100m, 123456789m, "HKD", now.AddMinutes(-15));
        var row = new Capsule(new Watch(sec)) { Quote = quote, Issue = "" };
        var options = new StockOptions();
        row.Render(options, now);
        Check(row.Cells[0].Value == "+3.50%" && row.Cells[1].Value == "+3.50" && row.Cells[2].Value == "1.2亿 HKD", "metric values, precision, currency");
        Check(row.Cells[0].Color == row.Cells[1].Color && row.Cells[0].Color == "#FF999A" && row.State == "延时", "positive HK snapshot");
        row.Quote = quote with { Last = 97 }; row.Render(options, now);
        Check(row.Cells[0].Color == "#70DEB1", "negative color");
        row.Quote = quote with { Last = 100 }; row.Render(options, now.AddMinutes(40));
        Check(row.Cells[0].Color == "#B4C3C0" && row.State == "已过期", "flat and stale");
        options.Fields = [new(Metric.Turnover, true, 0), new(Metric.Percent, false), new(Metric.Change, true, 3)];
        options.ShortUnits = false; row.Render(options, now);
        Check(row.Cells.Count == 2 && row.Cells[0].Value == "123456789 HKD" && row.Cells[1].Value == "0.000", "metric ordering, hiding, raw units");
        row.Issue = "连接异常"; row.Render(options, now);
        Check(row.State == "过期缓存" && row.Detail.Contains("2026-11-24"), "offline cache timestamp");
        Check(MarketClock.Session("us", new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero)) == "节假日休市", "Thanksgiving");
        Check(MarketClock.Session("us", new DateTimeOffset(2026, 11, 27, 18, 30, 0, TimeSpan.Zero)) == "盘后时段", "NYSE early close");
        Check(MarketClock.Session("us", new DateTimeOffset(2026, 7, 6, 13, 30, 0, TimeSpan.Zero)) == "交易时段", "US daylight saving");
        Check(MarketClock.Session("sh", now.AddHours(1)) == "午间休市", "Shanghai lunch break");
        Check(MarketClock.Session("hk", now.AddYears(5)) == "交易日历待更新" || MarketClock.Session("hk", now.AddYears(5)) == "周末休市", "unknown calendar");
        Check(TencentParser.Quotes("v_pv_none_match=\"1\";\nv_sh600001=\"1~broken\";").Count == 0, "partial and missing rows");
        SchedulerChecks();
    }

    private sealed class TestFeed : IQuoteService
    {
        public int Calls;
        public bool Offline;
        public bool Partial;
        public Task<IReadOnlyDictionary<string, Quote>> FetchAsync(IReadOnlyList<Security> batch, CancellationToken token)
        {
            Calls++;
            if (Offline) throw new IOException("test disconnect");
            var subset = Partial ? batch.Take(1) : batch;
            return Task.FromResult<IReadOnlyDictionary<string, Quote>>(subset.ToDictionary(s => s.Key,
                s => new Quote(s, 11, 10, 123, "CNY", DateTimeOffset.Now)));
        }
        public Task<IReadOnlyList<Security>> SearchAsync(string query, string market, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static void SchedulerChecks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FloatHub-schedule-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new Store(dir);
            var feed = new TestFeed();
            using var vm = new StockViewModel(feed, db, new Journal(dir));
            vm.Enable(true); Check(feed.Calls == 0, "no hidden polling");
            vm.Present(true); vm.Present(true); Check(feed.Calls == 1 && vm.Rows.All(r => r.Quote != null), "one shared batch on reveal");
            vm.Present(false); vm.Present(true); Check(feed.Calls == 2, "resume fetch");
            feed.Partial = true; vm.Restart(); Check(vm.Rows[0].Issue == "" && vm.Rows[1].State == "过期缓存", "per-symbol isolation");
            feed.Offline = true; vm.Restart(); Check(vm.Rows.All(r => r.Quote != null && r.State == "过期缓存"), "retains quotes on transport error");
            vm.Enable(false); int calls = feed.Calls; vm.Present(false); vm.Present(true);
            Check(feed.Calls == calls, "module disabled");
        }
        finally { Directory.Delete(dir, true); }
    }
}
