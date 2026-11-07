namespace MusicSync.Core;

public sealed class MonotonicClock
{
    private readonly TimeProvider time;
    private readonly long anchor;
    private readonly long utc;

    public MonotonicClock(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        anchor = this.time.GetTimestamp();
        utc = this.time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    public long Now => utc + (long)time.GetElapsedTime(anchor).TotalMilliseconds;
}

public sealed class SyncClock(MonotonicClock clock)
{
    private readonly Queue<(long Rtt, long Offset)> samples = new();
    private readonly object gate = new();

    public long LocalNow => clock.Now;
    public long Now => LocalNow + Best.Offset;
    public long? RttMs => Best.Rtt;

    public void Add(long request, long received, long sent, long response)
    {
        long rtt = response - request - (sent - received);
        if (rtt is < 0 or > 10_000 || sent < received)
            return;
        long offset = ((received - request) + (sent - response)) / 2;
        lock (gate)
        {
            samples.Enqueue((rtt, offset));
            if (samples.Count > 8)
                samples.Dequeue();
        }
    }

    private (long? Rtt, long Offset) Best
    {
        get
        {
            lock (gate)
                return samples.Count == 0 ? (null, 0) : samples.MinBy(sample => sample.Rtt);
        }
    }

    public static long? Difference(RoomView room, long now)
    {
        var leader = room.Leader.Report;
        var listener = room.Listener.Report;
        var state = room.State;
        if (!room.Leader.Connected || !room.Listener.Connected || state.Track is null ||
            !Usable(leader) || !Usable(listener) ||
            Math.Abs(leader!.DurationMs!.Value - listener!.DurationMs!.Value) > 1000)
            return null;
        return Project(listener) - Project(leader);

        bool Usable(PlayerReport? report) => report is { PositionMs: not null, SampleTime: not null,
            DurationMs: not null, RttMs: <= 400 } && report.Revision == state.Revision &&
            report.Playback == state.Playback && report.SampleTime >= now - 5000 && report.SampleTime <= now + 500;

        long Project(PlayerReport report) => Math.Min(report.DurationMs!.Value, report.PositionMs!.Value +
            (report.Playback == Playback.Playing ? Math.Max(0, now - report.SampleTime!.Value) : 0));
    }
}
