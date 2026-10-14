namespace QuotaMonitor.Core.Models;

public enum IslandState { Minimal, Collapsed, Expanded }

// Deadlines are supplied by a monotonic clock, independent of rendering and wall-clock changes.
public sealed class IslandInteraction
{
    public static readonly TimeSpan ApproachDelay = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan ExpandedLeaveDelay = TimeSpan.FromMilliseconds(900);
    public IslandState State { get; private set; } = IslandState.Minimal;
    private TimeSpan? approachAt, leaveAt;

    public bool Pointer(bool near, TimeSpan now, bool suspended = false)
    {
        if (suspended) { approachAt = leaveAt = null; return false; }
        if (near)
        {
            leaveAt = null;
            if (State != IslandState.Minimal) return false;
            approachAt ??= now + ApproachDelay;
            if (now < approachAt) return false;
            return Set(IslandState.Collapsed);
        }
        approachAt = null;
        if (State == IslandState.Minimal) { leaveAt = null; return false; }
        leaveAt ??= now + (State == IslandState.Expanded ? ExpandedLeaveDelay : LeaveDelay);
        return now >= leaveAt && Set(IslandState.Minimal);
    }

    public bool Click() => State == IslandState.Collapsed && Set(IslandState.Expanded);
    public void Escape() => Set(IslandState.Minimal);
    public bool Set(IslandState value)
    {
        approachAt = leaveAt = null;
        if (value == State) return false;
        State = value;
        return true;
    }
}
