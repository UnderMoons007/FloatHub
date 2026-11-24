using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FloatHub.Core;
using FloatHub.StockTracker;

internal static partial class Program
{
    private sealed class UnavailableDirectory : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private static async Task DirectoryFallback()
    {
        using var service = new TencentService(new UnavailableDirectory());
        var results = await service.SearchAsync("万达轴承", "北交所", CancellationToken.None);
        Check(results.Single().Key == "bj920002", "name index when Tencent suggestions are unavailable");
    }

    private sealed class RepeatingFeed : IQuoteService
    {
        private readonly DateTimeOffset at = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(8));
        public int Count;
        public Task<IReadOnlyDictionary<string, Quote>> FetchAsync(IReadOnlyList<Security> batch, CancellationToken token)
        {
            Count++;
            return Task.FromResult<IReadOnlyDictionary<string, Quote>>(batch.ToDictionary(x => x.Key,
                x => new Quote(x, 11, 10, 120000, "CNY", at)));
        }
        public Task<IReadOnlyList<Security>> SearchAsync(string term, string market, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static void CycleStress()
    {
        string root = Path.Combine(Path.GetTempPath(), "FloatHub-cycles-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new Store(root);
            var feed = new RepeatingFeed();
            using var vm = new StockViewModel(feed, store, new Journal(root));
            vm.Enable(true);
            void Cycle() { vm.Present(true); vm.Present(false); }
            for (int i = 0; i < 100; i++) Cycle();
            var before = GC.GetTotalMemory(true);
            for (int i = 0; i < 3000; i++) Cycle();
            long delta = GC.GetTotalMemory(true) - before;
            Check(feed.Count == 3100 && vm.Rows.Count == 3 && delta < 8 * 1024 * 1024,
                $"3,000 pause/resume cycles, retained managed heap delta {delta:N0} bytes");
        }
        finally { Directory.Delete(root, true); }
    }
}
