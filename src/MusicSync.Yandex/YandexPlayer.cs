using MusicSync.Core;

namespace MusicSync.Yandex;

public sealed class YandexPlayer(Func<long> now) : IPlayer
{
    private TrackId? knownTrack;
    private Snapshot? last;
    private long nextMediaProbe;
    private string? mediaTimeout;
    private nint panelWindow;
    private Task<PlayerReading?>? panelRead;

    public async Task<PlayerReading> ObserveAsync(bool identify, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var client = YandexClient.Find();
        if (!client.Running)
        {
            Forget();
            mediaTimeout = null;
            return new PlayerReading(false);
        }
        if (mediaTimeout is not null && Environment.TickCount64 < nextMediaProbe)
            return await ReadPanelAsync(client, mediaTimeout);

        try
        {
            var reading = await ObserveSessionAsync(client, identify);
            mediaTimeout = null;
            return reading;
        }
        catch (TimeoutException ex)
        {
            mediaTimeout = ex.Message;
            nextMediaProbe = Environment.TickCount64 + 5000;
            return await ReadPanelAsync(client, mediaTimeout);
        }
    }

    private async Task<PlayerReading> ReadPanelAsync(YandexClient client, string timeout)
    {
        if (client.Window != 0)
        {
            try
            {
                if (panelRead is null || panelRead.IsCompleted || panelWindow != client.Window)
                {
                    panelWindow = client.Window;
                    panelRead = TrackPage.ReadPlayerPanelAsync(client.Window, now);
                }
                if (await panelRead.WaitAsync(TimeSpan.FromSeconds(4)) is { } reading)
                    return reading;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                System.Runtime.InteropServices.COMException)
            {
                throw new PlayerException("media-session-timeout", $"{timeout} Player panel: {ex.Message}");
            }
        }
        throw new PlayerException("media-session-timeout", $"{timeout} No verified track and timeline in the player panel.");
    }

    private async Task<PlayerReading> ObserveSessionAsync(YandexClient client, bool identify)
    {
        var media = await MediaSession.ConnectAsync();
        if (media is null)
        {
            Forget();
            return new PlayerReading(true);
        }
        var state = await media.ReadAsync();
        long sampled = now();
        ObserveIdentity(state);
        if (identify && state.Ready && client.Window != 0)
        {
            TrackPage? page = null;
            try { page = await TrackPage.ReadCurrentTrackAsync(client.Window, state.Title); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
            {
                // The current player layout provides no new identity evidence.
            }
            if (page is not null)
            {
                var after = await media.ReadAsync();
                sampled = now();
                ObserveIdentity(after);
                if (after.Ready && after.SameTrack(state) && !Restarted(state, after))
                    knownTrack = new TrackId(page.AlbumId, page.TrackId);
                state = after;
            }
        }
        last = state.Ready ? state : null;
        return Reading(state, sampled);
    }

    public Task<PlayerReading> LoadAsync(TrackId track, CancellationToken token) => WithCommandLock(async () =>
    {
        var client = YandexClient.Find();
        if (!client.Running)
            throw new PlayerException("no-player");
        if (client.Window == 0)
            throw new PlayerException("window-closed");
        Forget();
        try
        {
            var state = await new TrackPage(track.AlbumId, track.Id).PlayAsync(client);
            knownTrack = track;
            last = state;
            return Reading(state, now());
        }
        catch (TimeoutException ex) { throw new PlayerException("track-unavailable", ex.Message); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new PlayerException("track-unconfirmed", ex.Message);
        }
    }, token);

    public Task<PlayerReading> SetPlaybackAsync(Playback playback, CancellationToken token) =>
        ControlAsync(playback == Playback.Playing ? "play" : "pause", 0, token);

    public Task<PlayerReading> PauseForHandoffAsync(CancellationToken token) =>
        ControlAsync("pause", 0, token, requireTrack: false);

    public Task<PlayerReading> SeekAsync(long positionMs, CancellationToken token) =>
        ControlAsync("seek", positionMs, token);

    private Task<PlayerReading> ControlAsync(string command, long position, CancellationToken token,
        bool requireTrack = true) => WithCommandLock(async () =>
    {
        var expected = requireTrack ? last : null;
        if (requireTrack && (knownTrack is null || expected is null))
            throw new PlayerException("track-unconfirmed");
        var media = await MediaSession.ConnectAsync() ?? throw new PlayerException("no-session");
        try
        {
            var state = await media.ControlAsync(command, position, expected);
            if (expected is not null && !state.SameTrack(expected))
            {
                Forget();
                throw new PlayerException("track-unconfirmed");
            }
            if (!requireTrack)
                ObserveIdentity(state);
            last = state;
            return Reading(state, now());
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new PlayerException("command-unconfirmed", ex.Message);
        }
    }, token);

    private static Task<PlayerReading> WithCommandLock(Func<Task<PlayerReading>> action, CancellationToken token) =>
        Task.Run(() =>
        {
            using var mutex = new Mutex(false, @"Local\MusicSync.YandexMusic.Control");
            bool owns;
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)
                throw new PlayerException("player-busy");
            try
            {
                token.ThrowIfCancellationRequested();
                return action().GetAwaiter().GetResult();
            }
            finally { mutex.ReleaseMutex(); }
        }, token);

    private void ObserveIdentity(Snapshot state)
    {
        if (!state.Ready || last is null || !state.SameTrack(last) || Restarted(last, state))
            knownTrack = null;
    }

    private static bool Restarted(Snapshot before, Snapshot after) =>
        before.DurationMs != after.DurationMs ||
        (after.ReportedPositionMs < 1500 && after.LastUpdatedUtc > before.LastUpdatedUtc &&
            before.PositionMs - after.PositionMs > 3500);

    private void Forget()
    {
        knownTrack = null;
        last = null;
    }

    private PlayerReading Reading(Snapshot state, long sampled) => new(true, state.Title, state.Artist, state.Album,
        state.Status switch { "Playing" => Playback.Playing, "Paused" => Playback.Paused, _ => null },
        (long?)state.PositionMs, (long?)state.DurationMs, knownTrack, sampled);
}
