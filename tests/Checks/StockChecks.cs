using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FloatHub.Core;
using FloatHub.StockTracker;

internal static partial class Program
{
    static partial void Extra(string[] args)
    {
        EtfRecords();
        string dir = Path.Combine(Path.GetTempPath(), "FloatHub-stocks-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new Store(dir); var log = new Journal(dir);
            var index = TencentParser.Suggestions("v_hint=\"us~ndx~纳斯达克100~nsdk100~ZS\"").Single();
            using (var vm = new StockViewModel(new TencentService(), db, log))
            {
                vm.Move(0, 1); vm.Rename(0, "港"); vm.Remove(2);
                vm.Add(new("usIBM", "IBM", "IBM", "NYSE"));
                vm.Add(index); vm.Add(index);
            }
            using var restored = new StockViewModel(new TencentService(), db, log);
            Check(restored.Rows.Count == 4 && restored.Rows[0].Watch.Security.Key == "hk00700" && restored.Rows[0].Label == "港" && restored.Rows[2].Label == "I", "watchlist order, rename, add/remove and restart");
            Check(restored.Rows[3].Watch.Security == index && index.IsIndex && index.Key == "usNDX", "added index survives restart with its quote key");
            if (args.Contains("--live")) Live().GetAwaiter().GetResult();
        }
        finally { Directory.Delete(dir, true); }
        DisplayChecks();
        DirectoryFallback().GetAwaiter().GetResult();
        CycleStress();
        if (args.Contains("--desktop")) DesktopChecks.Run();
    }
    static partial void DisplayChecks();

    private static void EtfRecords()
    {
        var suggestions = TencentParser.Suggestions("v_hint=\"sh~513330~恒生互联网ETF华夏~hshlwetfhx~QDII-ETF^sz~159915~创业板ETF易方达~cybetfyfd~ETF^jj~013172~ETF联接C~ljc~QDII^sh~000300~沪深300~hs300~ZS\"");
        Check(suggestions.Select(s => s.Key).SequenceEqual(new[] { "sh513330", "sz159915", "sh000300" }) &&
            suggestions.Single(s => s.IsIndex).Key == "sh000300", "mixed ETF and index suggestions exclude OTC funds");

        string[] fields = new string[83];
        fields[1] = "恒生互联网ETF华夏"; fields[2] = "513330";
        fields[3] = "0.275"; fields[4] = "0.281"; fields[30] = "20261124155002";
        fields[35] = "0.275/67437641/1890460621"; fields[61] = "ETF"; fields[82] = "CNY";
        string QuoteRow(string key) => $"v_{key}=\"{string.Join('~', fields)}\";";
        var etf = QuoteRow("sh513330");
        fields[1] = "ETF"; fields[2] = "501018"; fields[61] = "LOF";
        var quote = TencentParser.Quotes(etf + "\n" + QuoteRow("sh501018")).Values.Single();
        Check(quote.Security == suggestions[0] && quote.Change == -0.006m &&
            quote.Turnover == 1890460621m && quote.Currency == "CNY", "ETF quote uses the listed security type and exchange turnover");
    }

    private static async Task Live()
    {
        using var api = new TencentService(); var token = CancellationToken.None;
        var examples = new (string key, string market, string currency)[] { ("sh600519", "上交所", "CNY"), ("sz000001", "深交所", "CNY"), ("bj920002", "北交所", "CNY"), ("hk00700", "港交所", "HKD"), ("usAAPL", "NASDAQ", "USD"), ("usIBM", "NYSE", "USD"), ("usBTG", "NYSE American", "USD"), ("hk80700", "港交所", "CNY"), ("usBRK.B", "NYSE", "USD"), ("sh513330", "上交所", "CNY"), ("sz159915", "深交所", "CNY") };
        var quotes = await api.FetchAsync(examples.Select(e => new Security(e.key, "", "", e.market)).ToArray(), token);
        foreach (var e in examples)
        {
            Check(quotes.TryGetValue(e.key, out var quote) && quote.Security.Market == e.market && quote.Currency == e.currency && quote.Previous > 0 && quote.Turnover > 0, "live " + e.key + " " + e.market + " " + e.currency);
        }
        foreach (var (term, key, market) in new[] { ("腾讯", "hk00700", "港交所"), ("工商银行", "sh601398", "上交所"), ("AAPL", "usAAPL", "NASDAQ"), ("920002", "bj920002", "北交所"), ("万达轴承", "bj920002", "北交所"), ("BTG", "usBTG", "NYSE American") })
            Check((await api.SearchAsync(term, market, token)).Any(s => s.Key == key), "search " + term + " in " + market);
        foreach (string term in new[] { "513330", "sh513330", "恒生互联网ETF华夏", "恒生互联网ETF华夏513330" })
        {
            var hits = await api.SearchAsync(term, "全部市场", token);
            Check(hits.Any(s => s.Key == "sh513330" && s.Name == "恒生互联网ETF华夏" && s.Market == "上交所"), "ETF search " + term);
        }
        Check((await api.SearchAsync("159915", "深交所", token)).Any(s => s.Key == "sz159915"), "Shenzhen ETF search");
        Check(!(await api.SearchAsync("恒生互联网ETF华夏", "深交所", token)).Any(), "ETF search respects issuer and exchange");

        var indices = new System.Collections.Generic.List<Security>();
        foreach (var (term, key, market) in new[] { ("上证指数", "sh000001", "全部市场"), ("恒生指数", "hkHSI", "全部市场"),
            ("纳斯达克100指数", "usNDX", "全部市场"), ("创业板指数", "sz399006", "深交所"), ("标普500指数", "usINX", "指数") })
        {
            var hits = await api.SearchAsync(term, market, token);
            Check(hits.Count > 0 && hits[0].IsIndex && hits[0].Key == key, "index first for " + term);
            indices.Add(hits[0]);
        }
        var indexQuotes = await api.FetchAsync(indices, token);
        foreach (var index in indices)
        {
            Check(indexQuotes.TryGetValue(index.Key, out var q) && q.Security == index && q.Last > 0 && q.Previous > 0 &&
                (index.Region is "us" or "hk" ? q.Turnover == null : q.Turnover > 0), "watchlist index quote " + index.Key);
        }
        var sameCode = await api.SearchAsync("000001", "全部市场", token);
        Check(sameCode.Any(s => s.Key == "sh000001" && s.IsIndex) && sameCode.Any(s => s.Key == "sz000001" && !s.IsIndex),
            "shared numeric code returns both index and stock");
        var hkIndices = await api.SearchAsync("hkHSI", "指数", token);
        Check(hkIndices.Any(s => s.Key == "hkHSI") && hkIndices.All(s => s.IsIndex), "exact HK index key and index filter");
    }
}
