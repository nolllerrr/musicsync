const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

// Run the same JavaScript that SonataPlayer sends to Electron, with a synthetic player state.
const source = readFileSync(resolve(__dirname, '../../src/MusicSync.Yandex/SonataPlayer.cs'), 'utf8');
const [, prelude] = /private const string Prelude = """([\s\S]*?)""";/.exec(source);
const [, seekBody] = /public Task<PlayerReading> SeekAsync[\s\S]*?return ReadAsync\(\$\$"""([\s\S]*?)""", token\);/.exec(source);

function evaluate(meta, { album, mediaMatches = true, body = 'return read();' } = {}) {
    const entity = { data: { meta } };
    const entry = { entity, context: { data: { type: 'album', meta: { id: album } } } };
    const media = { currentContextEntityPair: mediaMatches ? entry : { entity: {} } };
    const playback = {
        mediaController: { currentMediaPlayer: { value: media } },
        setProgress: () => assert.fail('Seeking requires a confirmed track')
    };
    const core = {
        playbackController: { activePlayback: { value: playback } },
        getState: () => ({
            queueState: { currentEntity: { value: entry } },
            playerState: { status: { value: 'playing' }, progress: { value: { position: 1, duration: 10 } } }
        })
    };
    const document = {
        body: { __reactContainer$test: { memoizedProps: { value: core } } },
        getElementById: () => null,
        querySelectorAll: () => []
    };
    return runInNewContext(`(async () => { ${prelude}\n${body} })()`, { document });
}

for (const [name, meta, album, expectedTrack, expectedAlbum] of [
    ['album queue identifies the release', { id: '2:4', albumId: 4 }, 3, '2', '3'],
    ['album ID on the entity', { id: 2, albumId: 3 }, undefined, '2', '3'],
    ['composite track ID', { id: '2:3', albums: [{ id: 4 }, { id: 3 }] }, undefined, '2', '3'],
    ['one distinct album in the catalog', { id: 2, albums: [{ id: 3 }, { id: '3' }] }, undefined, '2', '3'],
    ['track ID alone', { id: 2 }, undefined, '2', undefined],
    ['multiple catalog albums', { id: 2, albums: [{ id: 3 }, { id: 4 }] }, undefined, '2', undefined],
    ['incomplete catalog albums', { id: 2, albums: [{ id: 3 }, {}] }, undefined, '2', undefined],
    ['conflicting entity IDs', { id: '2:4', albumId: 3 }, undefined, '2', undefined],
    ['missing track ID', {}, 3, undefined, undefined],
    ['malformed track ID', { id: '2:3:4' }, 3, undefined, undefined],
    ['unsafe numeric track ID', { id: 9007199254740992 }, 3, undefined, undefined]
]) {
    test(name, async () => {
        const reading = await evaluate(meta, { album });
        assert.equal(reading.trackId, expectedTrack);
        assert.equal(reading.albumId, expectedAlbum);
        assert.equal(reading.positionMs, 1000);
    });
}

for (const [name, meta, mediaMatches] of [
    ['seek rejects a different track in the same album', { id: '4:3' }, true],
    ['seek rejects a different release of the same track', { id: '2:4' }, true],
    ['seek rejects ambiguous identity', { id: 2, albums: [{ id: 3 }, { id: 4 }] }, true],
    ['seek rejects an exact queue ID while different media is playing', { id: '2:3' }, false]
]) {
    test(name, async () => {
        const body = seekBody.replace('{{expected}}', '{"trackId":"2","albumId":"3"}').replace('{{position}}', '0');
        const reading = await evaluate(meta, { mediaMatches, body });
        assert.equal(reading.error, 'track-unconfirmed');
    });
}
