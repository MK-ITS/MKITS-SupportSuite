// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class FailureTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void RecordFailure_CountsWithinWindow()
    {
        var tracker = new FailureTracker();

        Assert.Equal(1, tracker.RecordFailure("a", Start, Window));
        Assert.Equal(2, tracker.RecordFailure("a", Start.AddMinutes(1), Window));
        Assert.Equal(1, tracker.RecordFailure("b", Start.AddMinutes(1), Window));
    }

    [Fact]
    public void RecordFailure_DropsFailuresOutsideWindow()
    {
        var tracker = new FailureTracker();
        tracker.RecordFailure("a", Start, Window);
        tracker.RecordFailure("a", Start.AddMinutes(5), Window);

        var count = tracker.RecordFailure("a", Start.AddMinutes(10), Window);

        Assert.Equal(2, count); // the failure at Start is exactly one window old and no longer counts
    }

    [Fact]
    public void Reset_ForgetsNetwork()
    {
        var tracker = new FailureTracker();
        tracker.RecordFailure("a", Start, Window);

        tracker.Reset("a");

        Assert.Equal(1, tracker.RecordFailure("a", Start, Window));
    }

    [Fact]
    public void Purge_RemovesStaleNetworks()
    {
        var tracker = new FailureTracker();
        tracker.RecordFailure("old", Start, Window);
        tracker.RecordFailure("fresh", Start.AddMinutes(9), Window);

        tracker.Purge(Start.AddMinutes(11), Window);

        Assert.Equal(1, tracker.TrackedNetworks);
    }

    [Fact]
    public void RecordFailure_AtCapacity_StaysWithinBound()
    {
        var tracker = new FailureTracker(capacity: 3);

        for (var i = 0; i < 50; i++)
        {
            tracker.RecordFailure($"net-{i}", Start.AddSeconds(i), Window);
            Assert.True(tracker.TrackedNetworks <= 3);
        }
    }

    [Fact]
    public void RecordFailure_AtCapacity_PrefersPurgingStaleEntries()
    {
        var tracker = new FailureTracker(capacity: 2);
        tracker.RecordFailure("stale", Start, Window);
        tracker.RecordFailure("active", Start.AddMinutes(15), Window);

        // "stale" is outside the window at this point and is purged instead of evicting "active".
        tracker.RecordFailure("new", Start.AddMinutes(16), Window);

        Assert.Equal(2, tracker.TrackedNetworks);
        Assert.Equal(2, tracker.RecordFailure("active", Start.AddMinutes(17), Window));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FailureTracker(0));
    }
}
