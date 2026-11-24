using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FloatHub.StockTracker;

public sealed record Security(string Key, string Code, string Name, string Market, bool IsIndex = false)
{
    public string Caption => $"{Name}   ·   {Code}   ·   {Market}" + (IsIndex ? "   ·   指数" : "");
    public string Region => Key[..2];
    public override string ToString() => Caption;
}

public sealed record Watch(Security Security, string Alias = "")
{
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Alias)) return Alias.Trim();
            Rune? firstLetter = null;
            foreach (var rune in Security.Name.EnumerateRunes())
            {
                if (!Rune.IsLetter(rune)) continue;
                // Chinese listings can begin with trading-status prefixes such as *ST.
                if (rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                    or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
                    return rune.ToString();
                firstLetter ??= rune;
            }
            return firstLetter?.ToString() ?? StringInfo.GetNextTextElement(Security.Code);
        }
    }
}

public sealed record Quote(Security Security, decimal Last, decimal Previous, decimal? Turnover, string Currency, DateTimeOffset Time)
{
    public decimal Change => Last - Previous;
    public decimal Percent => Change / Previous * 100;
}

public enum Metric { Percent, Change, Turnover }
public sealed record Field(Metric Metric, bool Visible = true, int Precision = 2)
{
    public string Name => Metric switch { Metric.Percent => "涨跌幅", Metric.Change => "涨跌额", _ => "成交额" };
}
public sealed class StockOptions
{
    public int Seconds { get; set; } = 30;
    public bool ShortUnits { get; set; } = true;
    public List<Field> Fields { get; set; } = [new(Metric.Percent), new(Metric.Change), new(Metric.Turnover, true, 1)];
    public List<Watch> Watchlist { get; set; } =
    [
        new(new("sh600519","600519","贵州茅台","上交所")),
        new(new("hk00700","00700","腾讯控股","港交所")),
        new(new("usAAPL","AAPL","苹果","NASDAQ"))
    ];
}
public interface IQuoteService : IDisposable
{
    Task<IReadOnlyList<Security>> SearchAsync(string query, string market, CancellationToken token);
    Task<IReadOnlyDictionary<string, Quote>> FetchAsync(IReadOnlyList<Security> batch, CancellationToken token);
}
