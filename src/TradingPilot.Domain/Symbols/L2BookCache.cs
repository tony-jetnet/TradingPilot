using System.Collections.Concurrent;

namespace TradingPilot.Symbols;

public class L2BookCache
{
    private const int MaxPerTicker = 720;

    private readonly ConcurrentDictionary<long, ConcurrentQueue<SymbolBookSnapshot>> _cache = new();

    public void AddSnapshot(long tickerId, SymbolBookSnapshot snapshot)
    {
        var queue = _cache.GetOrAdd(tickerId, _ => new ConcurrentQueue<SymbolBookSnapshot>());
        queue.Enqueue(snapshot);
        while (queue.Count > MaxPerTicker)
            queue.TryDequeue(out _);
    }

    public SymbolBookSnapshot? GetLatest(long tickerId)
    {
        if (!_cache.TryGetValue(tickerId, out var queue))
            return null;
        return queue.LastOrDefault();
    }

    public List<SymbolBookSnapshot> GetSnapshots(long tickerId, int count)
    {
        if (!_cache.TryGetValue(tickerId, out var queue))
            return [];
        return queue.TakeLast(count).ToList();
    }
}
