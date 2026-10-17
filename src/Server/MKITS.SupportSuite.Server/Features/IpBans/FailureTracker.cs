// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// In-memory sliding-window failure counter per client network. Counters are intentionally not persisted:
/// a restart only resets counting, existing bans are stored in the database.
/// </summary>
/// <remarks>
/// Memory is hard-bounded by <see cref="Capacity"/>: when full, stale entries are purged (at most every
/// <see cref="PurgeInterval"/>, so a flood does not cause a full scan per request); if it is still full,
/// an arbitrary existing entry is evicted. Under an address-rotation flood this may forget some counters –
/// acceptable, because each rotated address only had a single failure anyway.
/// </remarks>
public sealed class FailureTracker(int capacity = FailureTracker.DefaultCapacity)
{
    public const int DefaultCapacity = 100_000;

    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private long _lastPurgeUtcTicks;

    /// <summary>Maximum number of tracked networks.</summary>
    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>Number of networks currently tracked (for tests and diagnostics).</summary>
    public int TrackedNetworks => _failures.Count;

    /// <summary>Records a failure and returns the number of failures for <paramref name="networkKey"/> within <paramref name="window"/>.</summary>
    public int RecordFailure(string networkKey, DateTimeOffset now, TimeSpan window)
    {
        if (!_failures.ContainsKey(networkKey) && _failures.Count >= Capacity)
        {
            MakeRoom(now, window);
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
        Interlocked.Exchange(ref _lastPurgeUtcTicks, now.UtcTicks);

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

    private void MakeRoom(DateTimeOffset now, TimeSpan window)
    {
        if (now.UtcTicks - Interlocked.Read(ref _lastPurgeUtcTicks) >= PurgeInterval.Ticks)
        {
            Purge(now, window);
        }

        // Still full (flood of recent, distinct networks): evict arbitrary entries until there is room.
        foreach (var key in _failures.Keys)
        {
            if (_failures.Count < Capacity)
            {
                break;
            }

            _failures.TryRemove(key, out _);
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
