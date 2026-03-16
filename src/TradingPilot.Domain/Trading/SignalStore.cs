using System.Collections.Concurrent;

namespace TradingPilot.Trading;

/// <summary>
/// In-memory ring buffer that stores recent trading signals per ticker.
/// Thread-safe for concurrent reads and writes.
/// </summary>
public class SignalStore
{
    private const int MaxSignalsPerTicker = 200;

    private readonly ConcurrentDictionary<long, ConcurrentQueue<TradingSignal>> _signals = new();

    public void AddSignal(TradingSignal signal)
    {
        var queue = _signals.GetOrAdd(signal.TickerId, _ => new ConcurrentQueue<TradingSignal>());
        queue.Enqueue(signal);
        while (queue.Count > MaxSignalsPerTicker)
            queue.TryDequeue(out _);
    }

    public List<TradingSignal> GetRecent(long tickerId, int count = 50)
    {
        if (!_signals.TryGetValue(tickerId, out var queue))
            return [];
        return queue.TakeLast(count).ToList();
    }

    public TradingSignal? GetLatest(long tickerId)
    {
        if (!_signals.TryGetValue(tickerId, out var queue))
            return null;
        return queue.LastOrDefault();
    }

    public List<long> GetActiveTickerIds()
    {
        return _signals.Keys.ToList();
    }
}
