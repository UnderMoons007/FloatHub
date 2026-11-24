using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FloatHub.StockTracker;

public sealed partial class TencentService : IQuoteService
{
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    private static readonly Encoding Gbk;
    private static readonly Security[] Beijing;
    public const int BatchSize = 40;
    public static readonly string[] Markets = ["全部市场", "指数", "上交所", "深交所", "北交所", "港交所", "NYSE", "NASDAQ", "NYSE American"];

    static TencentService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
        using var stream = typeof(TencentService).Assembly.GetManifestResourceStream("BseNames")!;
        using var reader = new StreamReader(stream);
        Beijing = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t')).Select(f => new Security("bj" + f[0], f[0], f[1], "北交所")).ToArray();
    }
    public TencentService(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(9);
    }

    public async Task<IReadOnlyDictionary<string, Quote>> FetchAsync(IReadOnlyList<Security> batch, CancellationToken token)
    {
        if (batch.Count == 0) return new Dictionary<string, Quote>();
        if (batch.Count > BatchSize) throw new ArgumentOutOfRangeException(nameof(batch));
        return await FetchKeys(batch.Select(s => s.Key), token).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, Quote>> FetchKeys(IEnumerable<string> keys, CancellationToken token)
    {
        string encoded = string.Join(",", keys.Select(Uri.EscapeDataString));
        var body = await Read("https://qt.gtimg.cn/q=" + encoded, token).ConfigureAwait(false);
        return TencentParser.Quotes(body);
    }

    [GeneratedRegex(@"^(sh|sz|bj|hk|us)?([0-9]{1,6}|[a-z][a-z0-9.\-]{0,14})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Exact();

    [GeneratedRegex(@"^(.+?ETF.*?)(?<![0-9])([0-9]{6})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EtfNameAndCode();

    private async Task<List<Security>> Suggestions(string query, string type, CancellationToken token)
    {
        var hints = await Read($"https://smartbox.gtimg.cn/s3/?v=2&t={type}&q=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        return TencentParser.Suggestions(hints);
    }

    public async Task<IReadOnlyList<Security>> SearchAsync(string query, string market, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is 0 or > 80) return [];
        var namedCode = EtfNameAndCode().Match(query);
        if (namedCode.Success) query = namedCode.Groups[2].Value;
        bool MatchesMarket(Security s) => market == Markets[0] || (market == "指数" ? s.IsIndex : s.Market == market);
        string type = market switch { "港交所" => "hk", "NYSE" or "NASDAQ" or "NYSE American" => "us", _ => "all" };
        var found = new List<Security>();
        Exception? directoryError = null;
        try
        {
            found = await Suggestions(query, type, token).ConfigureAwait(false);
            if (query.Length > 2 && query.EndsWith("指数", StringComparison.Ordinal))
            {
                string stem = query[..^2].TrimEnd();
                bool MatchesIndex(Security s) => s.IsIndex && MatchesMarket(s) &&
                    (s.Name == query || s.Name == stem || s.Name == stem + "指" || s.Code.Equals(stem, StringComparison.OrdinalIgnoreCase));
                if (!found.Any(MatchesIndex))
                {
                    var shorter = await Suggestions(stem, type, token).ConfigureAwait(false);
                    found.AddRange(shorter.Where(MatchesIndex));
                }
                found = found.OrderByDescending(MatchesIndex).ToList();
            }
            // Smartbox can omit an ETF's full name but return it when the issuer suffix is removed.
            int etf = query.IndexOf("ETF", StringComparison.OrdinalIgnoreCase);
            if (etf > 0 && etf + 3 < query.Length && !found.Any(MatchesMarket) && market is "全部市场" or "上交所" or "深交所")
            {
                var candidates = await Suggestions(query[..(etf + 3)], type, token).ConfigureAwait(false);
                found.AddRange(candidates.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            directoryError = ex;
        }
        if (market == Markets[0] || market == "北交所")
            found.AddRange(Beijing.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Code.Equals(query, StringComparison.OrdinalIgnoreCase)));
        // Verify an exact code against the quote feed when Smartbox omits a listing.
        var match = Exact().Match(query);
        if (match.Success && !found.Any(s => (s.Code.Equals(query, StringComparison.OrdinalIgnoreCase) || s.Key.Equals(query, StringComparison.OrdinalIgnoreCase)) && MatchesMarket(s)))
        {
            var prefix = match.Groups[1].Value.ToLowerInvariant();
            var code = match.Groups[2].Value.ToUpperInvariant();
            bool numeric = char.IsDigit(code[0]);
            string[] prefixes = prefix != "" ? [prefix] : numeric ?
                code.Length == 6 ? ["sh", "sz", "bj"] : ["hk"] : ["us", "hk"];
            var quotes = await FetchKeys(prefixes.Select(p => p + (p == "hk" && numeric ? code.PadLeft(5, '0') : code)), token).ConfigureAwait(false);
            found.AddRange(quotes.Values.Select(q => q.Security));
        }
        if (namedCode.Success)
        {
            string name = namedCode.Groups[1].Value.TrimEnd();
            found.RemoveAll(s => s.Code != query || !s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = found.Where(MatchesMarket).DistinctBy(s => s.Key).ToArray();
        if (filtered.Length == 0 && directoryError != null) throw new IOException("腾讯证券名称服务暂不可用。", directoryError);
        return filtered;
    }

    private async Task<string> Read(string url, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var wait = nextRequest - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                    nextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                    if ((int)response.StatusCode is 429 or 503)
                    {
                        var after = response.Headers.RetryAfter;
                        nextRequest = after?.Date ?? DateTimeOffset.UtcNow.Add(after?.Delta ?? TimeSpan.FromSeconds(60));
                    }
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    return Gbk.GetString(bytes);
                }
                finally { gate.Release(); }
            }
            catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested && (ex is TaskCanceledException ||
                ex is HttpRequestException { StatusCode: null or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError }))
            {
                await Task.Delay(1200, token).ConfigureAwait(false);
            }
        }
    }
    public void Dispose() => client.Dispose();
}
