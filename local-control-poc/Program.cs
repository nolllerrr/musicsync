using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Globalization;

Console.OutputEncoding = Encoding.UTF8;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    WriteIndented = true
};

int Error(int code, string message)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = message }, json));
    return code;
}

const string Usage = "musicsync status [--track-id | --player-panel] | play | pause | next | previous | seek <milliseconds> | play-url <track-url>";
if (args is ["--help" or "-h"])
{
    Console.WriteLine(Usage);
    return 0;
}

long milliseconds = 0;
TrackPage? track = args is ["play-url", var url] ? TrackPage.Parse(url) : null;
bool readTrackId = args is ["status", "--track-id"];
bool readPlayerPanel = args is ["status", "--player-panel"];
bool valid = args is ["status" or "play" or "pause" or "next" or "previous"] ||
    args is ["seek", var position] &&
    long.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds) ||
    track is not null || readTrackId || readPlayerPanel;
if (!valid)
    return Error(2, args is ["play-url", _] ? "Use a canonical Yandex Music album/track URL." : Usage);

using var commandLock = new Mutex(false, @"Local\MusicSync.YandexMusic.Control");
bool ownsLock = false;
try
{
    if (args[0] != "status")
    {
        try { ownsLock = commandLock.WaitOne(0); }
        catch (AbandonedMutexException) { ownsLock = true; }
        if (!ownsLock)
            return Error(4, "Another MusicSync command is in progress.");
    }

    var outcome = Task.Run(async () =>
    {
        var client = YandexClient.Find();
        if (!client.Running)
            return (3, (object)new { clientRunning = false, hasSession = false });

        if (readPlayerPanel)
        {
            var panel = client.Window == 0 ? null :
                await TrackPage.ReadPlayerPanelAsync(client.Window, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return (panel is null ? 3 : 0, (object)new { clientRunning = true, panel });
        }

        if (track is not null)
        {
            var played = await track.PlayAsync(client);
            return (0, (object)new { clientRunning = true, hasSession = true, track.AlbumId, track.TrackId, state = played });
        }

        var media = await MediaSession.ConnectAsync();
        if (media is null)
            return (3, (object)new { clientRunning = true, hasSession = false });

        var state = args[0] == "status"
            ? await media.ReadAsync()
            : await media.ControlAsync(args[0], milliseconds);
        if (readTrackId)
        {
            var currentTrack = await TrackPage.ReadCurrentTrackAsync(client.Window, state.Title);
            var after = await media.ReadAsync();
            if (!after.Ready || !after.SameTrack(state))
                throw new InvalidOperationException("The media session changed while reading the track ID.");
            return (0, (object)new { clientRunning = true, hasSession = true, currentTrack.AlbumId, currentTrack.TrackId, state = after });
        }
        return (0, (object)new { clientRunning = true, hasSession = true, state });
    }).WaitAsync(TimeSpan.FromSeconds(22)).GetAwaiter().GetResult();

    Console.WriteLine(JsonSerializer.Serialize(outcome.Item2, json));
    return outcome.Item1;
}
catch (TimeoutException ex)
{
    return Error(5, ex.Message);
}
catch (InvalidOperationException ex)
{
    return Error(4, ex.Message);
}
catch (Exception ex)
{
    return Error(4, $"Windows media control failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
}
finally
{
    if (ownsLock)
        commandLock.ReleaseMutex();
}
