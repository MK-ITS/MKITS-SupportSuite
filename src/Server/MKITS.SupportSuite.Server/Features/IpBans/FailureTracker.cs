// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// In-memory sliding-window failure counter per client network. Counters are intentionally not persisted:
/// a restart only resets counting, existing bans are stored in the database.
/// </summary>
public sealed class FailureTracker
{
    /// <summary>Upper bound of tracked networks; protects memory against address-rotation floods.</summary>
    internal const int MaxTrackedNetworks = 100_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    /// <summary>Number of networks currently tracked (for tests and diagnostics).</summary>
    public int TrackedNetworks => _failures.Count;

    /// <summary>Records a failure and returns the number of failures for <paramref name="networkKey"/> within <paramref name="window"/>.</summary>
    public int RecordFailure(string networkKey, DateTimeOffset now, TimeSpan window)
    {
        if (_failures.Count >= MaxTrackedNetworks)
        {
            Purge(now, window);
        }

        var timestamps = _failures.GetOrAdd(networkKey, static _ => new Queue<DateTimeOffset>());
        lock (timestamps)
        {
            timestamps.Enqueue(now);
            DropExpired(timestamps, now, window);
            return timestamps.Count;
        }
    }

    /// <summary>Forgets all failures of a network (after a ban or a successful login).</summary>
    public void Reset(string networkKey) => _failures.TryRemove(networkKey, out _);

    /// <summary>Removes networks without failures inside the window.</summary>
    public void Purge(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, timestamps) in _failures)
        {
            lock (timestamps)
            {
                DropExpired(timestamps, now, window);
                if (timestamps.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }

    private static void DropExpired(Queue<DateTimeOffset> timestamps, DateTimeOffset now, TimeSpan window)
    {
        while (timestamps.Count > 0 && timestamps.Peek() <= now - window)
        {
            timestamps.Dequeue();
        }
    }
}
