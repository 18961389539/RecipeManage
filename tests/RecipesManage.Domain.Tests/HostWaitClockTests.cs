using RecipesManage.Domain.Recipes;
using Xunit;

namespace RecipesManage.Domain.Tests;

public sealed class HostWaitClockTests
{
    [Fact]
    public void Resolve_FreshWait_UsesPlannedDuration()
    {
        var planned = TimeSpan.FromSeconds(8);
        Assert.Equal(planned, HostWaitClock.Resolve(planned, resumeHeld: false, leftoverSeconds: 3));
    }

    [Fact]
    public void Resolve_Resume_ContinuesLeftoverNotFullDuration()
    {
        var leftover = HostWaitClock.Resolve(TimeSpan.FromSeconds(8), resumeHeld: true, leftoverSeconds: 2.5);
        Assert.Equal(2.5, leftover.TotalSeconds, 3);
    }

    [Fact]
    public void Resolve_ResumeWithZeroLeftover_CompletesImmediately()
    {
        Assert.Equal(TimeSpan.Zero, HostWaitClock.Resolve(TimeSpan.FromSeconds(8), true, 0));
    }

    [Fact]
    public void LeftoverAfterInterrupt_SubtractsElapsedFromRecordedRemaining()
    {
        var started = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var leftover = HostWaitClock.LeftoverAfterInterrupt(
            TimeSpan.FromSeconds(12), started, recordedRemainingSeconds: 12, started.AddSeconds(3));
        Assert.Equal(9, leftover.TotalSeconds, 3);
    }

    [Fact]
    public void LeftoverAfterInterrupt_ClampsAtZero_DoesNotRewind()
    {
        var started = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var leftover = HostWaitClock.LeftoverAfterInterrupt(
            TimeSpan.FromSeconds(8), started, recordedRemainingSeconds: 8, started.AddSeconds(30));
        Assert.Equal(TimeSpan.Zero, leftover);
    }
}
