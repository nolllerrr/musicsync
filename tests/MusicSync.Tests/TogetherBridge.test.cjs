'use strict';

const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { PassThrough, Writable } = require('node:stream');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const directory = path.resolve(__dirname, '../../integration/electron');
const id = '12345678-1234-1234-1234-123456789abc';

test('preload exposes narrow leadership methods with one identifier each', async () => {
  let api;
  const calls = [];
  const window = { location: new URL('music-application://desktop/') };
  window.top = window;
  runInNewContext(fs.readFileSync(path.join(directory, 'preload.cjs'), 'utf8'), {
    window,
    require: name => {
      assert.equal(name, 'electron');
      return {
        contextBridge: { exposeInMainWorld: (name, value) => { assert.equal(name, 'musicSyncTogether'); api = value; } },
        ipcRenderer: { invoke: async (...args) => { calls.push(args); return {}; } }
      };
    }
  });
  await api.transferLeadership(id);
  await api.acceptLeadership(id);
  await api.cancelLeadership(id);
  assert.deepEqual(calls, ['transferLeadership', 'acceptLeadership', 'cancelLeadership']
    .map(name => ['musicsync-together:command', name, id]));
});

test('main validates trusted frame and GUID before sending leadership commands to agent', async () => {
  const app = new EventEmitter();
  app.getPath = () => directory;
  const handles = new Map();
  const requests = [];
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.stdin = new Writable({
    write(chunk, _encoding, done) {
      const request = JSON.parse(chunk.toString());
      requests.push(request);
      child.stdout.write(JSON.stringify({ type: 'result', id: request.id }) + '\n');
      done();
    }
  });
  const target = new EventEmitter();
  target.mainFrame = { url: 'music-application://desktop/' };
  target.getLastWebPreferences = () => ({ contextIsolation: true, sandbox: true, webSecurity: true });
  target.getURL = () => target.mainFrame.url;
  target.isDestroyed = () => false;
  target.getBackgroundThrottling = () => false;
  target.setBackgroundThrottling = () => {};
  target.insertCSS = async () => {};
  target.executeJavaScript = async () => {};
  target.send = () => {};
  const module = { exports: {} };
  runInNewContext(fs.readFileSync(path.join(directory, 'main.cjs'), 'utf8'), {
    module, __dirname: directory, URL, process: { resourcesPath: directory }, setTimeout, clearTimeout,
    require: name => {
      if (name === 'electron') return { app, clipboard: {}, ipcMain: { on() {}, handle: (name, fn) => handles.set(name, fn) } };
      if (name === 'node:child_process') return { spawn: () => child };
      if (name === 'node:fs') return {
        appendFile() {},
        readFileSync: (file, encoding) => file.endsWith('config.json')
          ? '{"endpoint":"ws://127.0.0.1:5050/ws"}' : fs.readFileSync(file, encoding)
      };
      return require(name);
    }
  });
  module.exports.install();
  app.emit('browser-window-created', {}, { webContents: target });
  target.emit('dom-ready');
  const command = handles.get('musicsync-together:command');
  const event = { sender: target, senderFrame: target.mainFrame };
  assert.equal((await command(event, 'create')).error, undefined);
  for (const name of ['transferLeadership', 'acceptLeadership', 'cancelLeadership']) {
    assert.equal((await command({ sender: target, senderFrame: { url: target.mainFrame.url } }, name, id)).error, 'invalid-command');
    for (const invalid of [null, undefined, '', {}, 'not-a-guid', id + '\n'])
      assert.equal((await command(event, name, invalid)).error, 'invalid-command');
    assert.equal((await command(event, name, id)).error, undefined);
  }
  assert.deepEqual(requests.slice(1).map(({ id: _id, ...request }) => request), [
    { type: 'command', command: 'transferLeadership', targetParticipantId: id },
    { type: 'command', command: 'acceptLeadership', transferId: id },
    { type: 'command', command: 'cancelLeadership', transferId: id }
  ]);
  child.stdout.end();
  child.stderr.end();
  child.stdin.end();
});
