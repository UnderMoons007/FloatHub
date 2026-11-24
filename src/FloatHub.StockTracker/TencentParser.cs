using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FloatHub.StockTracker;

public static partial class TencentParser
{
    [GeneratedRegex(@"^v_([A-Za-z0-9.\-]+)=""([^""]*)"";?$", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();
    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex EscapedUnicode();
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9.\-]{0,18}$")]
    private static partial Regex SymbolCode();

    private static bool Number(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    public static Security? Identify(string region, string rawCode, string name, bool isIndex = false)
    {
        var code = rawCode.Trim().ToUpperInvariant();
        string market;
        if (region == "us" && isIndex)
        {
            // Smartbox uses ndx while the quote record uses .NDX for the same key.
            if (code.StartsWith('.')) code = code[1..];
            market = code is "NDX" or "IXIC" ? "NASDAQ" : "美国市场";
        }
        else if (region == "us")
        {
            var split = code.LastIndexOf('.');
            if (split < 1) return null;
            market = code[(split + 1)..] switch { "OQ" or "O" => "NASDAQ", "N" => "NYSE", "AM" or "A" => "NYSE American", _ => "" };
            code = code[..split];
        }
        else market = region switch { "sh" => "上交所", "sz" => "深交所", "bj" => "北交所", "hk" => "港交所", _ => "" };
        if (market == "" || !SymbolCode().IsMatch(code) || string.IsNullOrWhiteSpace(name)) return null;
        return new(region + code, code, name.Trim(), market, isIndex);
    }

    private static bool IsListedEtf(string region, string type) =>
        (region is "sh" or "sz") && (type is "ETF" or "QDII-ETF");

    public static List<Security> Suggestions(string text)
    {
        var result = new List<Security>();
        int start = text.IndexOf('"'), end = text.LastIndexOf('"');
        if (start < 0 || end <= start) throw new FormatException("搜索服务返回了无法识别的格式。");
        foreach (var row in text[(start + 1)..end].Split('^'))
        {
            var f = row.Split('~');
            if (f.Length < 5) continue;
            bool isIndex = f[4] == "ZS";
            if (!isIndex && !f[4].StartsWith("GP", StringComparison.Ordinal) && !IsListedEtf(f[0], f[4])) continue;
            var name = EscapedUnicode().Replace(f[2], m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            var sec = Identify(f[0], f[1], name, isIndex);
            if (sec != null) result.Add(sec);
        }
        return result;
    }

    public static Dictionary<string, Quote> Quotes(string text)
    {
        var result = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Assignment().Match(line);
            if (!m.Success) continue;
            string key = m.Groups[1].Value;
            var f = m.Groups[2].Value.Split('~');
            if (key.Length < 3 || f.Length < 38 || !Number(f[3], out var last) || last <= 0 ||
                !Number(f[4], out var prev) || prev <= 0) continue;
            var region = key[..2];
            int typeField = region switch { "hk" => 63, "us" => 56, _ => 61 };
            string type = f.Length > typeField ? f[typeField] : "";
            bool isIndex = type == "ZS";
            if (!isIndex && !IsListedEtf(region, type) &&
                !f.Any(p => p == "GP" || p.StartsWith("GP-", StringComparison.Ordinal))) continue;
            var security = Identify(region, f[2], f[1], isIndex);
            if (security == null || !security.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            string[] formats = ["yyyyMMddHHmmss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss"];
            if (!DateTime.TryParseExact(f[30], formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var zone = MarketClock.Zone(region);
            if (zone.IsInvalidTime(date)) continue;
            decimal? turnover = null;
            if (region is "bj" or "sh" or "sz")
            {
                var trade = f[35].Split('/');
                if (trade.Length == 3 && Number(trade[2], out var raw)) turnover = raw;
                else if (Number(f[37], out var wan) && wan <= decimal.MaxValue / 10000 && wan >= 0) turnover = wan * 10000;
            }
            else if (!isIndex && Number(f[37], out var value)) turnover = value;
            var currency = f.FirstOrDefault(t => t is "CNY" or "HKD" or "USD" or "CNH") ?? "";
            result[key] = new(security, last, prev, turnover >= 0 ? turnover : null, currency, new DateTimeOffset(date, zone.GetUtcOffset(date)));
        }
        return result;
    }
}
