using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarketMage.Models;

namespace MarketMage.Services;

public sealed class UniversalisService : IMarketDataClient, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly TimeSpan requestInterval;
    private DateTimeOffset nextRequest;

    public UniversalisService(HttpClient? client = null, TimeSpan? minimumInterval = null)
    {
        httpClient = client ?? new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MarketMage/0.7");
        requestInterval = minimumInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task<IReadOnlyDictionary<uint, AggregateSnapshot?>> GetAggregatesAsync(
        string scope, IReadOnlyCollection<uint> itemIds, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Market scope cannot be empty.", nameof(scope));
        var result = new Dictionary<uint, AggregateSnapshot?>();
        foreach (var batch in itemIds.Distinct().Chunk(100))
        {
            var url = $"https://universalis.app/api/v2/aggregated/{Uri.EscapeDataString(scope)}/{string.Join(',', batch)}";
            try
            {
                var json = await GetJsonAsync(url, token).ConfigureAwait(false);
                foreach (var pair in AggregateParser.Parse(json, batch)) result[pair.Key] = pair.Value;
            }
            catch (HttpRequestException ex) when (batch.Length == 1 && ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Unlike multi-item requests, an unavailable single item may return 404.
                result[batch[0]] = null;
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<uint>> GetRecentItemsAsync(string dataCenter, CancellationToken token)
    {
        var url = $"https://universalis.app/api/v2/extra/stats/most-recently-updated?dcName={Uri.EscapeDataString(dataCenter)}&entries=200";
        var json = await GetJsonAsync(url, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing recent-item list.");
        return items.EnumerateArray()
            .Where(i => i.TryGetProperty("itemID", out var id) && id.TryGetUInt32(out _))
            .Select(i => i.GetProperty("itemID").GetUInt32()).Distinct().ToArray();
    }

    public async Task<IReadOnlyList<MarketPriceSnapshot>> GetSnapshotsAsync(
        string scope, IReadOnlyCollection<uint> itemIds, bool hq, bool listings, CancellationToken cancellationToken, bool includeHistory = false)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Select a world or data center.", nameof(scope));
        var result = new List<MarketPriceSnapshot>();
        foreach (var batch in itemIds.Distinct().Chunk(50))
        {
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(scope)}/{string.Join(',', batch)}?listings={(listings ? 100 : 0)}&entries={(!listings || includeHistory ? 20 : 0)}&hq={hq.ToString().ToLowerInvariant()}";
            var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            result.AddRange(MarketParser.Parse(json, batch, scope, hq));
        }
        return result;
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken token)
    {
        await requestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = nextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                using var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
                nextRequest = DateTimeOffset.UtcNow + requestInterval;
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(attempt + 1);
                    nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(delay.TotalSeconds, 1));
                    if (attempt < 2) continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
        }
        finally { requestGate.Release(); }
    }

    public void Dispose() => httpClient.Dispose();
}
