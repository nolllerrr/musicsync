using System.Diagnostics;

namespace MusicSync.Core;

public sealed record PlayerReading(bool Running, string Title = "", string Artist = "", string Album = "",
    Playback? Playback = null, long? PositionMs = null, long? DurationMs = null, TrackId? Track = null,
    long? SampledAt = null, bool FromPlayerPanel = false)
{
    public bool Ready => Playback is not null && PositionMs is not null && DurationMs is > 0;
}

public interface IPlayer
{
    Task<PlayerReading> ObserveAsync(bool identify, CancellationToken token);
    Task<PlayerReading> LoadAsync(TrackId track, CancellationToken token);
    Task<PlayerReading> SetPlaybackAsync(Playback playback, CancellationToken token);
    Task<PlayerReading> PauseForHandoffAsync(CancellationToken token);
    Task<PlayerReading> SeekAsync(long positionMs, CancellationToken token);
}

public sealed class PlayerException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

public sealed record ApplyResult(PlayerReading Reading, long ElapsedMs, bool WaitingForNextTrack = false);

public sealed class PlaybackApplier(IPlayer player, Func<long> serverNow, long thresholdMs)
{
    public async Task<ApplyResult> ApplyAsync(RoomState state, PlayerReading reading, Func<bool> current,
        CancellationToken token, bool holdAtEnd = false)
    {
        var timer = Stopwatch.StartNew();
        CheckCurrent();
        if (!reading.Running)
            throw new PlayerException("no-player");
        if (state.Track is null)
            throw new ArgumentException("A canonical track is required.", nameof(state));
        if (reading.Track != state.Track || !reading.Ready)
            reading = await player.LoadAsync(state.Track, token);
        VerifyIdentity();

        long duration = reading.DurationMs!.Value;
        long position = Target();
        if (holdAtEnd && state.Playback == Playback.Playing && IsNearEnd(position, duration))
        {
            if (reading.Playback == Playback.Playing)
                reading = await player.PauseForHandoffAsync(token);
            CheckCurrent();
            return new ApplyResult(reading, timer.ElapsedMilliseconds, WaitingForNextTrack: true);
        }
        if (state.Playback == Playback.Playing && position >= duration)
        {
            if (reading.Playback == Playback.Playing)
                await player.SetPlaybackAsync(Playback.Paused, token);
            throw new PlayerException("track-ended");
        }

        if (state.Playback == Playback.Paused && reading.Playback != Playback.Paused)
        {
            reading = await player.SetPlaybackAsync(Playback.Paused, token);
            VerifyIdentity();
        }

        position = Target();
        long tolerance = state.Playback == Playback.Paused ? 300 : Math.Min(500, thresholdMs);
        if (Math.Abs(position - EstimatedPosition(reading)) > tolerance)
        {
            reading = await player.SeekAsync(Math.Clamp(position, 0, duration - 1), token);
            VerifyIdentity();
        }
        if (reading.Playback != state.Playback)
        {
            reading = await player.SetPlaybackAsync(state.Playback, token);
            VerifyIdentity();
        }

        if (reading.Playback != state.Playback ||
            Math.Abs(Target() - EstimatedPosition(reading)) > Math.Max(thresholdMs, 750))
            throw new PlayerException("position-unconfirmed");
        return new ApplyResult(reading, timer.ElapsedMilliseconds);

        long Target() => state.PositionAt(serverNow(), duration);

        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!current())
                throw new OperationCanceledException("The room state changed.");
        }

        void VerifyIdentity()
        {
            CheckCurrent();
            if (!reading.Ready || reading.Track != state.Track)
                throw new PlayerException("track-unconfirmed");
        }
    }

    // Leave enough time for the observation loop and the pause command before the local queue advances.
    internal static bool IsNearEnd(long positionMs, long durationMs) =>
        durationMs - positionMs <= Math.Min(1250, durationMs / 2);

    public long EstimatedPosition(PlayerReading reading) =>
        Math.Min(reading.DurationMs ?? Protocol.MaxPositionMs, (reading.PositionMs ?? 0) +
            (reading.Playback == Playback.Playing && reading.SampledAt is long sampled
                ? Math.Max(0, serverNow() - sampled)
                : 0));

    public bool NeedsCorrection(RoomState state, PlayerReading reading, long now, long lastAttempt)
    {
        if (!reading.Ready || reading.Track is null || reading.Track != state.Track || now - lastAttempt < 8000)
            return false;
        long desired = state.PositionAt(now, reading.DurationMs);
        if (state.Playback == Playback.Playing && desired >= reading.DurationMs)
            return false;
        return reading.Playback != state.Playback || Math.Abs(desired - EstimatedPosition(reading)) > thresholdMs;
    }
}
