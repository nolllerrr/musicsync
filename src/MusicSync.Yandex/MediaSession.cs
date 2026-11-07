using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

internal sealed class MediaSession(GlobalSystemMediaTransportControlsSession session)
{
    private const string AppId = "ru.yandex.desktop.music";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(3);
    private static readonly object ManagerGate = new();
    private static Task<GlobalSystemMediaTransportControlsSessionManager>? managerRequest;

    public static async Task<MediaSession?> ConnectAsync()
    {
        Task<GlobalSystemMediaTransportControlsSessionManager> request;
        lock (ManagerGate)
        {
            if (managerRequest is null || managerRequest.IsFaulted || managerRequest.IsCanceled)
                managerRequest = ReadWindowsAsync(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(),
                    "Windows timed out opening the media session manager.");
            request = managerRequest;
        }
        var manager = await request;
        GlobalSystemMediaTransportControlsSession[] matches;
        try
        {
            matches = manager.GetSessions()
                .Where(s => string.Equals(s.SourceAppUserModelId, AppId, StringComparison.Ordinal))
                .ToArray();
        }
        catch (COMException)
        {
            lock (ManagerGate)
            {
                if (managerRequest == request)
                    managerRequest = null;
            }
            throw;
        }
        return matches.Length switch
        {
            0 => null,
            1 => new MediaSession(matches[0]),
            _ => throw new InvalidOperationException("Windows reported multiple Yandex Music sessions.")
        };
    }

    public async Task<Snapshot> ReadAsync()
    {
        var properties = await ReadWindowsAsync(session.TryGetMediaPropertiesAsync(),
            "Windows timed out reading the Yandex Music media properties.");
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var updated = timeline.LastUpdatedTime.ToUniversalTime();
        var now = DateTimeOffset.UtcNow;
        double start = timeline.StartTime.TotalMilliseconds;
        double duration = Math.Max(0, timeline.EndTime.TotalMilliseconds - start);
        double raw = Math.Max(0, timeline.Position.TotalMilliseconds - start);
        double estimated = raw;
        if (playback.PlaybackStatus == PlaybackStatus.Playing && updated > DateTimeOffset.UnixEpoch)
            estimated += Math.Max(0, (now - updated).TotalMilliseconds) * (playback.PlaybackRate ?? 1);
        var controls = playback.Controls;
        return new Snapshot(
            session.SourceAppUserModelId, properties.Title, properties.Artist, properties.AlbumTitle,
            playback.PlaybackStatus.ToString(), duration > 0 ? Math.Min(estimated, duration) : null,
            duration > 0 ? raw : null, duration > 0 ? duration : null, updated,
            new Capabilities(controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsNextEnabled,
                controls.IsPreviousEnabled, controls.IsPlaybackPositionEnabled),
            timeline.StartTime.Ticks, timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks);
    }

    private static async Task<T> ReadWindowsAsync<T>(IAsyncOperation<T> operation, string error)
    {
        using var timeout = new CancellationTokenSource(ApiTimeout);
        try
        {
            return await operation.AsTask(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(error, ex);
        }
    }

    public async Task<Snapshot> ControlAsync(string command, long milliseconds, Snapshot? expected = null)
    {
        var before = await ReadAsync();
        if (expected is not null && (!before.Ready || !before.SameTrack(expected)))
            throw new InvalidOperationException("The track changed before the player command.");
        var elapsed = Stopwatch.StartNew();
        Task<bool> request;
        Func<Snapshot, bool> confirmed;
        int stableForMs = 0;
        switch (command)
        {
            case "play":
                if (before.Status == "Playing") return before;
                Require(before.Controls.Play, command);
                request = session.TryPlayAsync().AsTask();
                confirmed = state => state.Status == "Playing";
                break;
            case "pause":
                if (before.Status == "Paused") return before;
                Require(before.Controls.Pause, command);
                request = session.TryPauseAsync().AsTask();
                confirmed = state => state.Status == "Paused";
                break;
            case "next":
                Require(before.Controls.Next, command);
                request = session.TrySkipNextAsync().AsTask();
                confirmed = state => ChangedTrack(before, state);
                stableForMs = 600;
                break;
            case "previous":
                Require(before.Controls.Previous, command);
                request = session.TrySkipPreviousAsync().AsTask();
                confirmed = state => ChangedTrack(before, state) ||
                    (before.PositionMs > 3000 && state.ReportedPositionMs < 2000 &&
                     state.SameTrack(before) && state.LastUpdatedUtc > before.LastUpdatedUtc);
                stableForMs = 600;
                break;
            case "seek":
                Require(before.Controls.Seek, command);
                long ticks = SeekTicks(before, milliseconds);
                if (before.Status == "Paused" && Math.Abs(before.ReportedPositionMs!.Value - milliseconds) < 1)
                    return before;
                request = session.TryChangePlaybackPositionAsync(ticks).AsTask();
                confirmed = state => state.SameTrack(before) &&
                    state.LastUpdatedUtc > before.LastUpdatedUtc &&
                    state.ReportedPositionMs >= milliseconds - 250 &&
                    state.ReportedPositionMs <= milliseconds + 250 +
                    (state.Status == "Playing" ? elapsed.Elapsed.TotalMilliseconds : 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (!await request.WaitAsync(ApiTimeout))
            throw new InvalidOperationException($"Windows rejected '{command}'.");
        return await WaitForAsync(confirmed, $"Windows accepted '{command}' but the player did not confirm it.", stableForMs);
    }

    public async Task<Snapshot> WaitForAsync(Func<Snapshot, bool> expected, string timeoutMessage, int stableForMs = 0)
    {
        var timer = Stopwatch.StartNew();
        Snapshot? previous = null;
        long stableSince = 0;
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var state = await ReadAsync();
            if (state.Ready && expected(state))
            {
                bool unchanged = previous is not null && state.SameTrack(previous) &&
                    state.Status == previous.Status && state.DurationMs == previous.DurationMs;
                if (!unchanged)
                    stableSince = timer.ElapsedMilliseconds;
                else if (timer.ElapsedMilliseconds - stableSince >= stableForMs)
                    return state;
                previous = state;
            }
            else
            {
                previous = null;
            }
            await Task.Delay(180);
        }
        throw new TimeoutException(timeoutMessage);
    }

    private static bool ChangedTrack(Snapshot before, Snapshot after) =>
        !after.SameTrack(before) && after.LastUpdatedUtc > before.LastUpdatedUtc &&
        (after.ReportedPositionMs < 3000 || after.DurationMs != before.DurationMs);

    private static void Require(bool enabled, string command)
    {
        if (!enabled)
            throw new InvalidOperationException($"Yandex Music does not currently enable '{command}'.");
    }

    private static long SeekTicks(Snapshot state, long milliseconds)
    {
        if (state.DurationMs is not double duration || milliseconds >= duration)
            throw new InvalidOperationException("Seek requires a position before the end of the current track.");
        long ticks = checked(state.StartTicks + milliseconds * TimeSpan.TicksPerMillisecond);
        if (ticks < state.MinSeekTicks || ticks > state.MaxSeekTicks)
            throw new InvalidOperationException("The position is outside the current seekable range.");
        return ticks;
    }
}

internal sealed record Capabilities(bool Play, bool Pause, bool Next, bool Previous, bool Seek);

internal sealed record Snapshot(
    string SourceAppId, string Title, string Artist, string Album, string Status,
    double? PositionMs, double? ReportedPositionMs, double? DurationMs, DateTimeOffset LastUpdatedUtc,
    Capabilities Controls,
    [property: System.Text.Json.Serialization.JsonIgnore] long StartTicks,
    [property: System.Text.Json.Serialization.JsonIgnore] long MinSeekTicks,
    [property: System.Text.Json.Serialization.JsonIgnore] long MaxSeekTicks)
{
    internal bool Ready => !string.IsNullOrWhiteSpace(Title) && Status is "Playing" or "Paused";
    internal bool SameTrack(Snapshot other) =>
        Title == other.Title && Artist == other.Artist && Album == other.Album;
}
