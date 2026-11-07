'use strict';

const { app, clipboard, ipcMain } = require('electron');
const { spawn } = require('node:child_process');
const { appendFile, readFileSync } = require('node:fs');
const path = require('node:path');
const { createInterface } = require('node:readline');

const namespace = 'musicsync-together:';
const roomCode = /^[0-9A-F]{24}$/;
const swapId = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const statuses = new Set(['idle', 'connecting', 'connected', 'reconnecting', 'ended']);
const sidebarWarnings = new Set(['bridge-unavailable', 'sidebar-missing', 'my-vibe-missing', 'trends-missing',
  'unexpected-sidebar-layout']);
const initialState = { status: 'idle' };

function isDesktop(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'music-application:' && url.host === 'desktop' && !url.username && !url.password;
  } catch {
    return false;
  }
}

function readEndpoint(directory) {
  const { endpoint } = JSON.parse(readFileSync(path.join(directory, 'config.json'), 'utf8'));
  const url = new URL(endpoint);
  const loopback = ['127.0.0.1', '[::1]', 'localhost'].includes(url.hostname);
  if ((url.protocol !== 'wss:' && !(url.protocol === 'ws:' && loopback)) || url.pathname !== '/ws' ||
      url.username || url.password || url.search || url.hash) throw new Error('Invalid MusicSync server endpoint');
  return url.href;
}

function log(message) {
  const file = path.join(app.getPath('userData'), 'musicsync-together.log');
  appendFile(file, `${new Date().toISOString()} ${message}\n`, () => {});
}

function install() {
  const rendererSource = readFileSync(path.join(__dirname, 'renderer.js'), 'utf8') +
    '\n//# sourceURL=musicsync-together/renderer.js';
  const rendererCss = readFileSync(path.join(__dirname, 'renderer.css'), 'utf8');
  let contents;
  let ready = false;
  let revision = 0;
  let agent;
  let state = initialState;
  let quitting = false;
  let stopped = false;

  function trusted(event) {
    return contents && !contents.isDestroyed() && event.senderFrame && event.sender === contents &&
      event.senderFrame === contents.mainFrame && isDesktop(event.senderFrame.url);
  }

  function sendState() {
    if (contents && !contents.isDestroyed() && isDesktop(contents.mainFrame?.url)) {
      contents.send(namespace + 'state', state);
    }
  }

  function updateThrottling() {
    if (contents && !contents.isDestroyed() && isDesktop(contents.getURL())) {
      const active = ['connecting', 'connected', 'reconnecting'].includes(state.status);
      if (contents.getBackgroundThrottling() === active) contents.setBackgroundThrottling(!active);
    }
  }

  function setState(next) {
    if (state.status !== next.status) log(`room-state: ${next.status}`);
    state = next;
    updateThrottling();
    sendState();
  }

  app.on('browser-window-created', (_event, window) => {
    const target = window.webContents;
    const settings = target.getLastWebPreferences();
    if (!settings?.contextIsolation || settings.nodeIntegration || !settings.sandbox || !settings.webSecurity) return;

    target.on('did-frame-navigate', (_event, _url, _responseCode, _status, isMainFrame) => {
      if (!isMainFrame || target !== contents) return;
      ready = false;
      revision++;
    });
    target.on('dom-ready', () => {
      if (!isDesktop(target.getURL()) || contents && contents !== target) return;
      contents = target;
      ready = true;
      updateThrottling();
      const observedRevision = revision;
      void target.insertCSS(rendererCss).then(() => {
        if (ready && observedRevision === revision && isDesktop(target.getURL())) {
          return target.executeJavaScript(rendererSource);
        }
      }).catch(() => log('sidebar-script-failed'));
    });
    target.on('destroyed', () => {
      if (target !== contents) return;
      contents = undefined;
      ready = false;
      revision++;
    });
    target.on('console-message', (details, _level, legacyMessage) => {
      if (target !== contents || !isDesktop(target.getURL()) || details.frame && details.frame !== target.mainFrame) return;
      const message = details.message ?? legacyMessage;
      const prefix = 'MusicSync Together: ';
      if (message?.startsWith(prefix) && sidebarWarnings.has(message.slice(prefix.length))) log(message);
    });
  });

  async function evaluate(current, message) {
    const target = contents;
    const observedRevision = revision;
    const response = { type: 'evalResult', id: message.id };
    if (!ready || !target || target.isDestroyed() || target.isLoadingMainFrame() || !isDesktop(target.getURL())) {
      response.error = 'no-player';
    } else {
      try {
        // executeJavaScript can wait for an in-progress load. Check the destination again in the page.
        const result = await target.executeJavaScript(`(() => {
          const url = window.location;
          if (url.protocol !== 'music-application:' || url.host !== 'desktop' || url.username || url.password) {
            return { error: 'no-player' };
          }
          return (${message.expression});
        })()`);
        if (observedRevision !== revision || target !== contents) response.error = 'no-player';
        else response.result = result ?? null;
      } catch {
        response.error = 'sonata-command-failed';
      }
    }
    if (agent === current) current.send(response);
  }

  function onMessage(current, message) {
    if (agent !== current) return;
    if (message?.type === 'state' && statuses.has(message.status)) {
      const { type, ...snapshot } = message;
      setState(snapshot);
    } else if (message?.type === 'result') {
      current.resolve(message.id, message.error);
    } else if (message?.type === 'evaluate' && Number.isSafeInteger(message.id) && message.id > 0 &&
        typeof message.expression === 'string' && message.expression.length <= 64 * 1024) {
      void evaluate(current, message);
    }
  }

  function startAgent() {
    const directory = path.join(process.resourcesPath, 'musicsync-together');
    let endpoint;
    try {
      endpoint = readEndpoint(directory);
    } catch {
      log('agent-config-invalid');
      return { error: 'agent-config-invalid' };
    }
    const child = spawn(path.join(directory, 'MusicSync.Agent.exe'), [endpoint], {
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe']
    });
    const current = createAgent(child, message => onMessage(current, message));
    current.exited.then(() => {
      if (agent !== current) return;
      agent = undefined;
      if (quitting) return;
      if (!state.error || state.status !== 'ended') setState({ status: 'ended', error: 'agent-unavailable' });
    });
    return current;
  }

  ipcMain.handle(namespace + 'command', (event, name, argument) => {
    if (!trusted(event) || quitting) return { error: 'invalid-command' };
    if (name !== 'create' && name !== 'join' && name !== 'swapRoles' && name !== 'leave') {
      return { error: 'invalid-command' };
    }

    const parameters = {};
    if (name === 'join') {
      const code = typeof argument === 'string' && argument.length <= 64 ? argument.trim().toUpperCase() : '';
      if (!roomCode.test(code)) return { error: 'invalid-room-code' };
      parameters.code = code;
    }
    if (name === 'swapRoles' && argument != null) {
      if (typeof argument !== 'string' || !swapId.test(argument)) return { error: 'invalid-command' };
      parameters.swapId = argument;
    }

    if (!agent) {
      if (name !== 'create' && name !== 'join') return { error: 'no-room' };
      const started = startAgent();
      if (started.error) return started;
      agent = started;
      setState({ status: 'connecting' });
    }
    return agent.command(name, parameters);
  });

  ipcMain.handle(namespace + 'copy-code', event => {
    if (!trusted(event) || typeof state.code !== 'string' || !roomCode.test(state.code)) return { error: 'no-room' };
    clipboard.writeText(state.code);
    return {};
  });

  ipcMain.on(namespace + 'subscribe', event => {
    if (trusted(event)) sendState();
  });

  app.on('before-quit', event => {
    if (!agent || stopped) return;
    event.preventDefault();
    if (quitting) return;
    quitting = true;
    void agent.stop().finally(() => {
      stopped = true;
      app.quit();
    });
  });
}

function createAgent(child, onMessage) {
  const pending = new Map();
  let nextId = 1;
  let closed = false;

  function resolve(id, error) {
    const request = pending.get(id);
    if (!request) return;
    clearTimeout(request.timeout);
    pending.delete(id);
    request.complete(error ? { error } : {});
  }

  const exited = new Promise(complete => {
    child.once('close', () => {
      closed = true;
      for (const id of pending.keys()) resolve(id, 'agent-unavailable');
      complete();
    });
  });
  child.on('error', () => log('agent-launch-failed'));
  child.stdin.on('error', () => log('agent-input-closed'));
  const stdout = createInterface({ input: child.stdout, crlfDelay: Infinity });
  stdout.on('line', line => {
    if (line.length > 128 * 1024) return;
    let message;
    try { message = JSON.parse(line); } catch { return; }
    onMessage(message);
  });
  const stderr = createInterface({ input: child.stderr, crlfDelay: Infinity });
  stderr.on('line', line => log(`agent: ${line.slice(0, 1024)}`));

  function send(message, callback = () => {}) {
    if (closed || !child.stdin.writable) return callback(new Error('Agent input closed'));
    try {
      child.stdin.write(JSON.stringify(message) + '\n', callback);
    } catch (error) {
      callback(error);
    }
  }

  function command(name, parameters, timeoutMs = 20_000) {
    return new Promise(complete => {
      const id = nextId++;
      const timeout = setTimeout(() => resolve(id, 'agent-timeout'), timeoutMs);
      pending.set(id, { complete, timeout });
      send({ type: 'command', id, command: name, ...parameters }, error => {
        if (error) resolve(id, 'agent-unavailable');
      });
    });
  }

  async function stop() {
    const timeout = setTimeout(() => child.kill(), 8_000);
    try {
      await command('shutdown', {}, 7_500);
      child.stdin.end();
      await exited;
    } finally {
      clearTimeout(timeout);
    }
  }

  return { send, command, resolve, stop, exited };
}

module.exports = { install };
