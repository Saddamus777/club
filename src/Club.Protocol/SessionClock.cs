namespace Club.Protocol;

// Only elapsed monotonic time is charged. Reopening a process never restores a running grant.
public sealed class SessionClock(TimeProvider time)
{
    private SessionGrant grant = new(null, 0, 0, "Idle");
    private long stamp = time.GetTimestamp();
    public SessionGrant Current
    {
        get
        {
            if (grant.State != "Running") return grant;
            var remaining = Math.Max(0, grant.RemainingMs - (long)time.GetElapsedTime(stamp).TotalMilliseconds);
            return grant with { RemainingMs = remaining, State = remaining == 0 ? "Completed" : "Running" };
        }
    }
    public void Apply(SessionGrant next)
    {
        if (next.RemainingMs < 0 || next.Revision < 0 || next.State is not ("Idle" or "Running" or "Paused" or "Completed" or "Ended"))
            throw new InvalidDataException("Недопустимое состояние сервера.");
        var current = Current;
        if (next.SessionId == current.SessionId)
        {
            if (next.Revision < current.Revision) throw new InvalidDataException("Устаревшая команда сервера.");
            next = next with { RemainingMs = Math.Min(current.RemainingMs, next.RemainingMs) };
        }
        grant = next;
        stamp = time.GetTimestamp();
    }
    public void Recover(SessionGrant saved)
    {
        grant = saved with { State = saved.SessionId is null ? "Idle" : "Recovery" };
        stamp = time.GetTimestamp();
    }
}
