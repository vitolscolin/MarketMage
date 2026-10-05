using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarketMage.Models;

namespace MarketMage.Services;

public interface IMarketDataClient
{
    Task<IReadOnlyDictionary<uint, AggregateSnapshot?>> GetAggregatesAsync(string scope, IReadOnlyCollection<uint> itemIds, CancellationToken token);
    Task<IReadOnlyList<MarketPriceSnapshot>> GetSnapshotsAsync(string scope, IReadOnlyCollection<uint> itemIds,
        bool hq, bool listings, CancellationToken cancellationToken, bool includeHistory = false);
}
