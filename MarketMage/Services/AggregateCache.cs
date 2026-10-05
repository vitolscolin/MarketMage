using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MarketMage.Models;

namespace MarketMage.Services;

// Scope-specific and safe across a canceled scan finishing while its replacement starts.
public sealed class AggregateCache
{
    private sealed record Entry(AggregateSnapshot? Snapshot, DateTimeOffset FetchedAt);
    private readonly ConcurrentDictionary<(string Scope, uint Item), Entry> entries = new();
    public void RemoveExpired(DateTimeOffset now)
    {
        foreach (var pair in entries)
            if (now - pair.Value.FetchedAt > TimeSpan.FromMinutes(10)) entries.TryRemove(pair.Key, out _);
    }
    public void Put(string scope, uint id, AggregateSnapshot? value, DateTimeOffset now) => entries[(scope, id)] = new(value, now);
    public bool TryGet(string scope, uint id, DateTimeOffset now, [MaybeNullWhen(false)] out AggregateSnapshot? value)
    {
        value = null;
        if (!entries.TryGetValue((scope, id), out var entry)) return false;
        var age = now - entry.FetchedAt;
        if (age < TimeSpan.Zero || age > (entry.Snapshot == null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10))) return false;
        value = entry.Snapshot;
        return true;
    }
}
