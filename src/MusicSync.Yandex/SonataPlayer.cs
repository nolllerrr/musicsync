using System.Globalization;
using System.Text.Json;
using MusicSync.Core;

namespace MusicSync.Yandex;

public interface ISonataTransport
{
    Task<JsonElement> EvaluateAsync(string expression, CancellationToken token);
}

public sealed class SonataPlayer(ISonataTransport transport, Func<long> now) : IPlayer
{
    private sealed record Sample(bool Running, string? Status, string? TrackId, string? AlbumId,
        long? PositionMs, long? DurationMs, string? Title, string? Artist, string? Album, string? Error);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private TrackId? expectedTrack;

    public SonataPlayer(int port, Func<long> now) : this(new SonataCdp(port), now) { }

    public async Task<PlayerReading> ObserveAsync(bool identify, CancellationToken token)
    {
        try
        {
            return await ReadAsync("return read();", token).ConfigureAwait(false);
        }
        catch (PlayerException ex) when (ex.Code == "no-player")
        {
            expectedTrack = null;
            return new(false);
        }
    }

    public Task<PlayerReading> LoadAsync(TrackId track, CancellationToken token)
    {
        // The album context takes a JS number. Reject IDs that would lose precision.
        if (!track.IsValid || !long.TryParse(track.AlbumId, NumberStyles.None, CultureInfo.InvariantCulture,
                out long album) || album > 9_007_199_254_740_991)
            throw new PlayerException("track-unavailable");

        return ReadAsync($$"""
            const expected = {{SerializeTrack(track)}};
            const before = read();
            if (sameTrack(before, expected)) return before;
            const contextData = {type: 'album', meta: {id: Number(expected.albumId)}, from: ''};
            const factory = core.factory;
            const context = factory.createContext({data: contextData});
            const isRequested = entry => idText(entry?.meta?.id).split(':')[0] === expected.trackId;
            const entries = await context.getContextEntitiesData();
            const item = entries.find(isRequested);
            if (!item) return {error: 'track-unavailable'};

            const playback = core.playbackController.getPlayback();
            const pairFor = data => factory.createContextEntityPair(context, factory.createEntity({data}));
            const resolved = await playback.queueController.entityLoader.entityProvider.loadEntities([pairFor(item)]);
            const selected = resolved.find(isRequested);
            const filterParams = playback.state.queueState.filterParams.value;
            if (!selected || !pairFor(selected).canBePlayed({entityChangeMethod: 'playContext', index: 0, filterParams}))
                return {error: 'track-unavailable'};

            // Sonata may scan ahead when an entity is absent or unplayable. Give it only the requested entity.
            const request = {
                contextData, entitiesData: [selected], loadContextMeta: true,
                queueParams: {entityId: expected.trackId, index: 0, shuffle: false}
            };
            const previousContext = core.getState().currentContext?.value?.data;
            if (!canMutate()) return {error: 'sonata-command-failed'};
            if (active() && previousContext?.type === 'album' && idText(previousContext.meta?.id) === expected.albumId) {
                // playContext reuses the old queue for the same album. Rebuild and verify the selection first.
                await core.setContext(request);
                if (!sameTrack(queuedTrack(), expected)) return {error: 'track-unconfirmed'};
                if (!canMutate()) return {error: 'sonata-command-failed'};
                await active().play();
            } else {
                await core.playContext(request);
            }
            return confirm(sample => sameTrack(sample, expected), 'track-unconfirmed');
            """, token);
    }

    public Task<PlayerReading> SetPlaybackAsync(Playback playback, CancellationToken token) =>
        SetPlaybackAsync(playback, expectedTrack ?? throw new PlayerException("track-unconfirmed"), token);

    public Task<PlayerReading> PauseForHandoffAsync(CancellationToken token) =>
        SetPlaybackAsync(Playback.Paused, null, token);

    private Task<PlayerReading> SetPlaybackAsync(Playback playback, TrackId? track, CancellationToken token)
    {
        var expected = track is null ? "null" : SerializeTrack(track);
        string method = playback == Playback.Playing ? "resume" : "pause";
        string status = playback == Playback.Playing ? "playing" : "paused";
        return ReadAsync($$"""
            const expected = {{expected}};
            const before = read();
            if (expected && !sameTrack(before, expected)) return {error: 'track-unconfirmed'};
            if (!active()) return {error: 'no-session'};
            if (before.status !== '{{status}}') {
                if (!canMutate()) return {error: 'sonata-command-failed'};
                await active().{{method}}();
            }
            return confirm(sample => (!expected || sameTrack(sample, expected)) && sample.status === '{{status}}',
                'command-unconfirmed');
            """, token);
    }

    public Task<PlayerReading> SeekAsync(long positionMs, CancellationToken token)
    {
        var expected = SerializeTrack(expectedTrack ?? throw new PlayerException("track-unconfirmed"));
        string position = Math.Clamp(positionMs, 0, Protocol.MaxPositionMs).ToString(CultureInfo.InvariantCulture);
        return ReadAsync($$"""
            const expected = {{expected}};
            const before = read();
            if (!sameTrack(before, expected)) return {error: 'track-unconfirmed'};
            const position = Math.min({{position}}, before.durationMs - 1);
            if (!canMutate()) return {error: 'sonata-command-failed'};
            await active().setProgress(position / 1000);
            return confirm(sample => sameTrack(sample, expected) && Math.abs(sample.positionMs - position) <= 250,
                'position-unconfirmed');
            """, token);
    }

    private static string SerializeTrack(TrackId track) => JsonSerializer.Serialize(new
    {
        trackId = track.Id,
        albumId = track.AlbumId
    });

    private async Task<PlayerReading> ReadAsync(string body, CancellationToken token)
    {
        var result = await transport.EvaluateAsync("(async () => {\n" + Prelude + "\n" + body + "\n})()", token)
            .ConfigureAwait(false);
        long sampledAt = now();
        Sample? sample;
        try { sample = result.Deserialize<Sample>(Json); }
        catch (JsonException) { throw new PlayerException("no-session"); }
        if (sample is null)
            throw new PlayerException("no-session");
        if (sample.Error is not null)
            throw new PlayerException(sample.Error);

        var track = sample is { TrackId: string id, AlbumId: string albumId } ? new TrackId(albumId, id) : null;
        Playback? playback = sample.Status switch { "playing" => Playback.Playing, "paused" => Playback.Paused, _ => null };
        if (playback is null || sample.DurationMs is not (> 0 and <= Protocol.MaxPositionMs) ||
            sample.PositionMs is not >= 0 || sample.PositionMs > sample.DurationMs)
        {
            expectedTrack = null;
            return new(sample.Running);
        }

        expectedTrack = track is { IsValid: true } ? track : null;
        return new(sample.Running, sample.Title ?? "", sample.Artist ?? "", sample.Album ?? "", playback,
            sample.PositionMs, sample.DurationMs, expectedTrack, sampledAt);
    }

    // Sonata is in the page's React tree. Every evaluation resolves the current provider again,
    // so a reload or an app restart cannot leave a stale controller in MusicSync.
    private const string Prelude = """
        const canMutate = () => typeof isMusicSyncEvaluationCurrent !== 'function' || isMusicSyncEvaluationCurrent();

        function findCore() {
            const roots = new Set();
            const elements = [document.getElementById('__next'), document.getElementById('root'), document.body];
            for (const element of document.querySelectorAll('*')) {
                if (elements.length >= 80) break;
                elements.push(element);
            }
            for (const element of elements) {
                if (!element) continue;
                const key = Object.keys(element).find(k => k.startsWith('__reactContainer$') || k.startsWith('__reactFiber$'));
                if (!key) continue;
                let fiber = element[key];
                while (fiber?.return) fiber = fiber.return;
                if (fiber) roots.add(fiber.stateNode?.current ?? fiber.current ?? fiber);
            }
            const pending = [...roots];
            const visited = new Set();
            while (pending.length && visited.size < 100000) {
                const fiber = pending.pop();
                if (visited.has(fiber)) continue;
                visited.add(fiber);
                const candidate = fiber.memoizedProps?.value;
                if (candidate?.playbackController && typeof candidate.getState === 'function') return candidate;
                if (fiber.sibling) pending.push(fiber.sibling);
                if (fiber.child) pending.push(fiber.child);
            }
            return null;
        }

        const core = findCore();
        if (!core) return {error: 'no-player'};
        const active = () => core.playbackController.activePlayback?.value;

        function idText(value) {
            return typeof value === 'string' ? value : Number.isSafeInteger(value) ? String(value) : '';
        }

        function track(entry) {
            const meta = entry?.entity?.data?.meta;
            const parts = /^([1-9][0-9]{0,18})(?::([1-9][0-9]{0,18}))?$/.exec(idText(meta?.id));
            if (!parts) return {};
            const context = entry.context?.data;
            const album = [context?.type === 'album' ? context.meta?.id : null, meta.albumId, parts[2], meta.albums?.[0]?.id]
                .map(idText).find(id => /^[1-9][0-9]{0,18}$/.test(id));
            return {trackId: parts[1], albumId: album};
        }

        const queuedTrack = () => track(core.getState().queueState?.currentEntity?.value);
        const sameTrack = (actual, expected) => actual.trackId === expected.trackId && actual.albumId === expected.albumId;

        function matchesMedia(entity, media) {
            if (!entity || !media) return false;
            if ('currentContextEntityPair' in media) return media.currentContextEntityPair?.entity === entity;

            // The player without crossfade exposes the loaded source in place of the entity.
            const audio = media.currentAudioElement?.value;
            const loaded = media.core?.hls?.url || audio?.src;
            const expected = entity.mediaSourceData?.src;
            if (!loaded || !expected || !audio || audio.readyState < 2) return false;
            try {
                const actualUrl = new URL(loaded);
                const expectedUrl = new URL(expected);
                actualUrl.hash = expectedUrl.hash = '';
                return actualUrl.href === expectedUrl.href;
            } catch {
                return false;
            }
        }

        function read() {
            const state = core.getState();
            const entry = state.queueState?.currentEntity?.value;
            const entity = entry?.entity;
            const media = active()?.mediaController?.currentMediaPlayer?.value;
            const status = state.playerState?.status?.value;
            const {position, duration} = state.playerState?.progress?.value ?? {};
            // The queue can change before the audio source does. Match the live media entity too.
            if (!matchesMedia(entity, media) ||
                (status !== 'playing' && status !== 'paused') ||
                !Number.isFinite(position) || !Number.isFinite(duration) || duration <= 0) return {running: true};

            const meta = entity.data?.meta;
            const identity = track(entry);
            const artists = Array.isArray(meta?.artists) ? meta.artists.map(artist => artist?.name).filter(Boolean) : [];
            const album = Array.isArray(meta?.albums) ? meta.albums.find(album => idText(album?.id) === identity.albumId) : null;
            const text = value => typeof value === 'string' ? value.slice(0, 512) : '';
            return {
                running: true, status, ...identity,
                positionMs: Math.round(Math.max(0, Math.min(position, duration)) * 1000),
                durationMs: Math.round(duration * 1000),
                title: text(meta?.title), artist: text(artists.join(', ')), album: text(album?.title)
            };
        }

        async function confirm(matches, error) {
            const deadline = performance.now() + 4500;
            do {
                const sample = read();
                if (matches(sample)) return sample;
                await new Promise(resolve => setTimeout(resolve, 80));
            } while (performance.now() < deadline);
            return {error};
        }
        """;
}
