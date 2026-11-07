using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using MusicSync.Core;

internal sealed record TrackPage(string AlbumId, string TrackId)
{
    private const int ElementUnavailable = unchecked((int)0x80040201);

    public static TrackPage? Parse(string value) => FromId(MusicSync.Core.TrackId.Parse(value, allowNative: true));

    private static TrackPage? FromId(TrackId? id) => id is null ? null : new TrackPage(id.AlbumId, id.Id);

    public async Task<Snapshot> PlayAsync(YandexClient client)
    {
        string activeTitle = await RunUiAutomationAsync(client.Window,
            (automation, window) => InvokeOnTrackPage(client, automation, window));
        var media = await WaitForSessionAsync();
        return await media.WaitForAsync(state => state.Status == "Playing" &&
            string.Equals(state.Title, activeTitle, StringComparison.OrdinalIgnoreCase),
            "The Yandex Music media session did not confirm playback of the requested track.");
    }

    public static Task<TrackPage> ReadCurrentTrackAsync(nint handle, string title) =>
        RunUiAutomationAsync(handle, (automation, window) =>
        {
            var document = FindDocument(window);
            if (document is null)
                throw new InvalidOperationException("The Yandex Music document is not accessible.");

            // Only the named player region describes the loaded track, unlike links in the rest of the page.
            var playerLinks = PlayerRegions(document)
                .SelectMany(region => region.FindAllDescendants(c => c.ByControlType(ControlType.Hyperlink)))
                .Where(link => LinkMatchesTitle(link, title))
                .Select(ReadTrackLink).OfType<TrackPage>().Distinct().ToArray();
            if (playerLinks.Length == 1)
                return playerLinks[0];

            var queued = ReadQueueTrack(automation, document, title);
            if (queued is not null)
                return queued;

            var page = FromDesktopUri(document.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
            if (page?.FindAction(automation, window) is { Playing: true } action &&
                string.Equals(action.Title, title, StringComparison.OrdinalIgnoreCase))
                return page;
            throw new InvalidOperationException("Yandex Music did not expose a verified link to the current track.");
        });

    public static Task<PlayerReading?> ReadPlayerPanelAsync(nint handle, Func<long> now) =>
        RunUiAutomationAsync<PlayerReading?>(handle, (_, window) =>
        {
            var document = FindDocument(window);
            if (document is null)
                return null;
            var readings = PlayerRegions(document).Select(panel => ReadPlayerPanel(panel, now))
                .OfType<PlayerReading>().ToArray();
            return readings.Length == 1 ? readings[0] : null;
        });

    private static IEnumerable<AutomationElement> PlayerRegions(AutomationElement document) =>
        document.FindAllDescendants(c => c.ByName("Плеер").Or(c.ByName("Player")))
            .Where(region => region.Properties.AriaRole.ValueOrDefault == "region");

    private static PlayerReading? ReadPlayerPanel(AutomationElement panel, Func<long> now)
    {
        var tracks = panel.FindAllDescendants(c => c.ByControlType(ControlType.Hyperlink))
            .Select(link => (Link: link, Title: TrackLabel(link), Track: ReadTrackLink(link)))
            .Where(item => item is { Title: not null, Track: not null })
            .DistinctBy(item => (item.Title, item.Track)).ToArray();
        if (tracks.Length != 1)
            return null;
        var track = tracks[0];

        var sliders = panel.FindAllDescendants(c => c.ByControlType(ControlType.Slider)
            .And(c.ByName("Управление таймкодом").Or(c.ByName("Timecode control"))));
        var buttons = panel.FindAllDescendants(c => c.ByControlType(ControlType.Button))
            .Where(button => button.Properties.IsEnabled.ValueOrDefault)
            .Select(button => button.Properties.Name.ValueOrDefault)
            .Where(name => name is "Воспроизведение" or "Play" or "Пауза" or "Pause").ToArray();
        if (sliders.Length != 1 || buttons.Length != 1 ||
            sliders[0].Patterns.RangeValue.PatternOrDefault is not { } range)
            return null;

        // Yandex Music's timecode slider measures seconds. Require a bounded track timeline.
        double duration = range.Maximum.ValueOrDefault;
        double position = range.Value.ValueOrDefault;
        if (range.Minimum.ValueOrDefault != 0 || !double.IsFinite(duration) || !double.IsFinite(position) ||
            duration < 1 || duration > Protocol.MaxPositionMs / 1000 || position < 0 || position > duration ||
            ReadTrackLink(track.Link) != track.Track || TrackLabel(track.Link) != track.Title)
            return null;

        var playback = buttons[0] is "Пауза" or "Pause" ? Playback.Playing : Playback.Paused;
        return new PlayerReading(true, track.Title!, Playback: playback, PositionMs: (long)(position * 1000),
            DurationMs: (long)(duration * 1000), Track: new TrackId(track.Track!.AlbumId, track.Track.TrackId),
            SampledAt: now(), FromPlayerPanel: true);
    }

    private static string? TrackLabel(AutomationElement link)
    {
        string? name = link.Properties.Name.ValueOrDefault?.Trim();
        foreach (string prefix in new[] { "Трек ", "Track " })
        {
            if (name?.StartsWith(prefix, StringComparison.Ordinal) == true && name.Length > prefix.Length)
                return name[prefix.Length..];
        }
        return null;
    }

    private static TrackPage? ReadQueueTrack(UIA3Automation automation, AutomationElement document, string title)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var matches = new HashSet<TrackPage>();
        foreach (var dialog in document.FindAllDescendants(
                     new PropertyCondition(automation.PropertyLibrary.Element.AriaRole, "dialog")))
        {
            foreach (var marker in dialog.FindAllChildren(c => c.ByControlType(ControlType.Text)))
            {
                string? name = marker.Properties.Name.ValueOrDefault;
                if (name is null || !(name.StartsWith("Сейчас играет", StringComparison.Ordinal) ||
                    name.StartsWith("Now playing", StringComparison.OrdinalIgnoreCase)))
                    continue;
                for (var item = walker.GetNextSibling(marker); item is not null; item = walker.GetNextSibling(item))
                {
                    var type = item.Properties.ControlType.ValueOrDefault;
                    if (type == ControlType.Text)
                        break;
                    if (type != ControlType.Hyperlink || ReadTrackLink(item) is not { } track)
                        continue;
                    if (LinkMatchesTitle(item, title))
                        matches.Add(track);
                    break;
                }
            }
        }
        return matches.Count == 1 ? matches.Single() : null;
    }

    private static bool LinkMatchesTitle(AutomationElement link, string title)
    {
        string? name = link.Properties.Name.ValueOrDefault?.Trim();
        return name == "Трек " + title || name == "Track " + title;
    }

    private static TrackPage? ReadTrackLink(AutomationElement link)
    {
        string? value = link.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
        return value is null ? null : FromDesktopUri(value) ?? Parse(value);
    }

    private static Task<T> RunUiAutomationAsync<T>(nint handle, Func<UIA3Automation, AutomationElement, T> action)
    {
        if (handle == 0)
            throw new InvalidOperationException("Open the Yandex Music window to use UI Automation.");

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // UIA and WinRT use separate threads. Keep each UIA object within this apartment.
        var thread = new Thread(() =>
        {
            try
            {
                T result;
                using (var automation = new UIA3Automation
                {
                    ConnectionTimeout = TimeSpan.FromSeconds(2),
                    TransactionTimeout = TimeSpan.FromSeconds(2)
                })
                {
                    result = action(automation, automation.FromHandle(handle));
                }
                completion.SetResult(result);
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Yandex Music UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    private string InvokeOnTrackPage(YandexClient client, UIA3Automation automation, AutomationElement window)
    {
        if (IsRequestedCardOpen(window))
            return StartPlayback(automation, window);

        using var background = BackgroundPlayerWindow.TryBegin(client.Window);
        client.OpenTrackPage(new Uri($"yandexmusic://album/{AlbumId}/track/{TrackId}"));
        return StartPlayback(automation, window, background);
    }

    private string StartPlayback(UIA3Automation automation, AutomationElement window,
        BackgroundPlayerWindow? background = null)
    {
        var action = WaitForAction(automation, window, playingOnly: false, TimeSpan.FromSeconds(6),
            "Yandex Music did not expose a playback button for the exact requested album and track IDs.", background);
        if (!action.Playing)
            action.Button.Patterns.Invoke.Pattern.Invoke();

        return WaitForAction(automation, window, playingOnly: true, TimeSpan.FromSeconds(5),
            "The requested track's dialog did not switch to Pause.", background).Title;
    }

    private bool HasRoute(AutomationElement? document) =>
        FromDesktopUri(document?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault) == this;

    private bool IsRequestedCardOpen(AutomationElement window)
    {
        try { return HasRoute(FindDocument(window)); }
        catch (COMException ex) when (ex.HResult == ElementUnavailable) { return false; }
    }

    private TrackAction? FindAction(UIA3Automation automation, AutomationElement window)
    {
        var document = FindDocument(window);
        if (!HasRoute(document))
            return null;

        var dialog = document!.FindFirstDescendant(new PropertyCondition(automation.PropertyLibrary.Element.AriaRole, "dialog"));
        if (dialog is null)
            return null;

        // The current track dialog exposes the entity label, then the track title, as direct text children.
        var text = dialog.FindAllChildren(c => c.ByControlType(ControlType.Text));
        if (text.Length < 2 || text[0].Properties.Name.ValueOrDefault is not ("Трек" or "Track"))
            return null;
        var title = text[1].Properties.Name.ValueOrDefault;
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var candidates = dialog.FindAllChildren(c => c.ByControlType(ControlType.Button))
            .Where(b => b.Properties.IsEnabled.ValueOrDefault && b.Patterns.Invoke.IsSupported)
            .Select(b => (Button: b, Name: b.Properties.Name.ValueOrDefault))
            .Where(b => b.Name is "Воспроизведение" or "Play" or "Пауза" or "Pause")
            .ToArray();
        return candidates.Length == 1
            ? new TrackAction(candidates[0].Button, candidates[0].Name is "Пауза" or "Pause", title)
            : null;
    }

    private static AutomationElement? FindDocument(AutomationElement window) =>
        window.FindFirstDescendant(c => c.ByControlType(ControlType.Document));

    private static TrackPage? FromDesktopUri(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var route) || route.Scheme != "music-application" ||
            route.Host != "desktop" || route.AbsolutePath != "/album/track")
            return null;
        return ParsePath($"/album/{QueryValue(route, "albumId")}/track/{QueryValue(route, "trackId")}");
    }

    private static TrackPage? ParsePath(string path)
    {
        return FromId(MusicSync.Core.TrackId.ParsePath(path));
    }

    private static string? QueryValue(Uri uri, string key)
    {
        string prefix = key + "=";
        var values = uri.Query.TrimStart('?').Split('&')
            .Where(part => part.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0][prefix.Length..] : null;
    }

    private TrackAction WaitForAction(UIA3Automation automation, AutomationElement window,
        bool playingOnly, TimeSpan timeout, string message, BackgroundPlayerWindow? background)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            background?.KeepBehind();
            try
            {
                var action = FindAction(automation, window);
                if (action is not null && (!playingOnly || action.Playing))
                    return action;
            }
            catch (COMException ex) when (ex.HResult == ElementUnavailable)
            {
                // The renderer rebuilt this element during navigation. Inspect the new tree.
            }
            Thread.Sleep(180);
        } while (timer.Elapsed < timeout);
        throw new TimeoutException(message);
    }

    private static async Task<MediaSession> WaitForSessionAsync()
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var media = await MediaSession.ConnectAsync();
            if (media is not null)
                return media;
            await Task.Delay(180);
        } while (timer.Elapsed < TimeSpan.FromSeconds(4));
        throw new TimeoutException("Yandex Music did not publish a Windows media session after the track command.");
    }

    private sealed record TrackAction(AutomationElement Button, bool Playing, string Title);
}
