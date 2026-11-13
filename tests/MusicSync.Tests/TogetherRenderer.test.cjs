'use strict';

const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const { createDom } = require('./fixtures/together-dom.cjs');

const source = readFileSync(resolve(__dirname, '../../integration/electron/renderer.js'), 'utf8');
const code = '0123456789ABCDEF01234567';
const member = (participantId, role = 'listener', connected = true) => ({ participantId, role, connected });
const room = (size = 1, extra = {}) => ({
  code, status: 'connected', selfParticipantId: 'self', role: 'leader', capacity: 10,
  participants: [member('self', 'leader'), ...Array.from({ length: size - 1 }, (_, i) => member(`p${i + 2}`))],
  ...extra
});

function setup() {
  const { document, sidebar } = createDom();
  let receive;
  let subscriptions = 0;
  let observers = 0;
  let observe;
  let animation;
  const calls = [];
  const timers = new Map();
  const api = Object.fromEntries(['create', 'join', 'leave', 'swapRoles', 'transferLeadership', 'acceptLeadership', 'cancelLeadership', 'retryPlayback', 'copyCode'].map(name =>
    [name, async value => { calls.push([name, value]); return {}; }]));
  api.subscribe = listener => { receive = listener; subscriptions++; return () => {}; };
  const window = {
    location: { protocol: 'music-application:', host: 'desktop' }, musicSyncTogether: api,
    innerWidth: 1000, innerHeight: 800, addEventListener() {}
  };
  const context = {
    document, window, console,
    setTimeout: callback => { const id = timers.size + 1; timers.set(id, callback); return id; },
    clearTimeout: id => timers.delete(id),
    requestAnimationFrame: callback => { animation = callback; return 1; },
    MutationObserver: class {
      constructor(callback) { observers++; observe = callback; }
      observe() {}
    }
  };
  runInNewContext(source, context);
  const get = selector => document.querySelector(selector);
  const byClass = name => get(`[class="music-sync-together-${name}"]`);
  const panel = document.getElementById('music-sync-together-panel');
  const button = text => panel.querySelectorAll('button').find(node => node.textContent === text);
  const trigger = document.getElementById('music-sync-together-button');
  const list = byClass('members');
  return {
    document, panel, trigger, list, button, byClass, api, calls, timers,
    state: state => receive(state), rows: () => list.children,
    reinject: () => runInNewContext(source, context),
    counts: () => ({ subscriptions, observers }),
    replaceSidebar() {
      get('aside').remove();
      sidebar();
      observe([{ target: document.body }]);
      animation();
    }
  };
}

for (const size of [1, 2, 5, 10]) {
  test(`renders ${size} participants, self, leader and capacity`, () => {
    const ui = setup();
    ui.state(room(size));
    assert.equal(ui.rows().length, size);
    assert.equal(ui.byClass('count').textContent, `${size}/10`);
    assert.match(ui.rows()[0].textContent, /ВыВедущийПодключён/);
    assert.equal(ui.rows()[0].dataset.self, 'true');
    assert.equal(ui.rows()[0].dataset.leader, 'true');
    if (size > 1) assert.match(ui.rows()[1].textContent, /Участник 2СлушательПодключён/);
    assert.match(ui.panel.textContent, size === 1 ? /Ожидание участников/ : size === 10 ? /Комната заполнена/ : /Можно пригласить/);
  });
}

test('joins, disconnects, reconnects and removes participants without rebuilding other rows', () => {
  const ui = setup();
  ui.state(room(2));
  const [self, listener] = ui.rows();
  ui.list.focus();
  ui.list.scrollTop = 60;
  ui.state(room(3));
  assert.equal(ui.rows()[0], self);
  assert.equal(ui.rows()[1], listener);
  assert.equal(ui.rows().length, 3);
  ui.state(room(3, { participants: [member('self', 'leader'), member('p2', 'listener', false), member('p3')] }));
  assert.match(listener.textContent, /Временно отсутствует/);
  ui.state(room(3));
  assert.match(listener.textContent, /Подключён/);
  ui.state(room(2, { participants: [member('self', 'leader'), member('p3')] }));
  assert.equal(listener.isConnected, false);
  assert.equal(ui.rows().length, 2);
  assert.match(ui.rows()[1].textContent, /Участник 3/);
  assert.equal(ui.byClass('count').textContent, '2/10');
  assert.equal(ui.document.activeElement, ui.list);
  assert.equal(ui.list.scrollTop, 60);
});

test('updates leader and current user from state, independent of list order', () => {
  const ui = setup();
  ui.state(room(2));
  const [self, listener] = ui.rows();
  ui.state(room(2, { role: 'listener', participants: [member('p2', 'leader'), member('self')] }));
  assert.equal(ui.rows()[0], listener);
  assert.equal(ui.rows()[1], self);
  assert.equal(listener.dataset.leader, 'true');
  assert.equal(self.dataset.leader, 'false');
  assert.equal(self.dataset.self, 'true');
  assert.match(self.textContent, /ВыСлушатель/);
});

test('reconnecting state does not report stale participant connections as confirmed', () => {
  const ui = setup();
  ui.state(room(2, { status: 'reconnecting' }));
  assert.match(ui.rows()[0].textContent, /Переподключается/);
  assert.match(ui.rows()[1].textContent, /Статус неизвестен/);
  assert.doesNotMatch(ui.list.textContent, /Подключён/);
  assert.match(ui.panel.textContent, /Состав комнаты может быть неактуален/);
  ui.state(room(2));
  assert.match(ui.rows()[1].textContent, /Подключён/);
});

test('missing or malformed fields stay unknown and never fabricate participants', () => {
  const ui = setup();
  for (const state of [null, undefined, [], {}, { code }, { code, participants: null }]) {
    assert.doesNotThrow(() => ui.state(state));
    assert.equal(ui.rows().length, 0);
  }
  ui.state(room(1, { capacity: null, participants: [member('self', undefined, undefined), null, {}, member(4)] }));
  assert.equal(ui.rows().length, 1);
  // Explicitly unknown values (the fixture's defaults are intentionally bypassed).
  ui.state(room(1, { capacity: null, participants: [{ participantId: 'self', connected: 'true' }] }));
  assert.equal(ui.byClass('count').textContent, '1');
  assert.match(ui.rows()[0].textContent, /Роль неизвестнаСтатус неизвестен/);
  ui.state(room(1, { participants: [] }));
  assert.equal(ui.rows().length, 0);
  assert.equal(ui.byClass('count').textContent, '0/10');
  assert.equal(ui.list.hidden, true);
});

test('legacy state shows only the known role and peer summary', () => {
  const ui = setup();
  ui.state({ code, status: 'connected', role: 'leader', peerConnected: true, swap: { id: 'swap-id' } });
  assert.equal(ui.rows().length, 0);
  assert.equal(ui.byClass('count').textContent, '—');
  assert.match(ui.panel.textContent, /Список участников пока недоступен/);
  assert.match(ui.panel.textContent, /Другие участникиВ комнате/);
  assert.equal(ui.button('Сменить роли').disabled, false);
  ui.state({ code, status: 'reconnecting', role: 'leader', peerConnected: true });
  assert.match(ui.panel.textContent, /Другие участникиСтатус неизвестен/);
  assert.equal(ui.button('Сменить роли').disabled, true);
});

test('deduplicates IDs and repeated snapshots and script injection', () => {
  const ui = setup();
  const state = room(2, { participants: [member('self', 'leader'), member('p2'), member('p2'), null, {}, member('')] });
  ui.state(state);
  const rows = [...ui.rows()];
  for (let i = 0; i < 30; i++) ui.state(state);
  assert.deepEqual(ui.rows(), rows);
  assert.equal(ui.byClass('count').textContent, '2/10');
  ui.reinject();
  assert.deepEqual(ui.counts(), { subscriptions: 1, observers: 1 });
  assert.equal(ui.document.querySelectorAll('#music-sync-together-panel').length, 1);
});

test('room changes clear old rows and temporary names', () => {
  const ui = setup();
  ui.state(room(3));
  const original = ui.rows()[1];
  ui.state({ status: 'idle' });
  assert.equal(ui.rows().length, 0);
  ui.state(room(2, { code: 'new-room', participants: [member('self', 'leader'), member('new-person')] }));
  assert.notEqual(ui.rows()[1], original);
  assert.match(ui.rows()[1].textContent, /Участник 2/);
});

test('opens, preserves focus on updates and closes with Escape or outside pointer', async () => {
  const ui = setup();
  assert.equal(ui.panel.hidden, true);
  await ui.trigger.dispatch('click');
  assert.equal(ui.panel.hidden, false);
  assert.equal(ui.trigger.getAttribute('aria-expanded'), 'true');
  assert.equal(ui.document.activeElement.getAttribute('id'), 'music-sync-together-title');
  const input = ui.document.getElementById('music-sync-together-code');
  input.value = 'partially entered code';
  input.focus();
  ui.state({ status: 'idle' });
  assert.equal(ui.document.activeElement, input);
  assert.equal(input.value, 'partially entered code');
  await ui.document.dispatch('keydown', { key: 'Escape' });
  assert.equal(ui.panel.hidden, true);
  assert.equal(ui.document.activeElement, ui.trigger);
  await ui.trigger.dispatch('click');
  await ui.document.dispatch('pointerdown', { target: input });
  assert.equal(ui.panel.hidden, false);
  await ui.document.dispatch('pointerdown', { target: ui.document.body });
  assert.equal(ui.panel.hidden, true);
  await ui.trigger.dispatch('click');
  await ui.trigger.dispatch('click');
  assert.equal(ui.panel.hidden, true);
});

test('sidebar replacement reuses one trigger and preserves the room', async () => {
  const ui = setup();
  ui.state(room(10));
  const rows = [...ui.rows()];
  ui.replaceSidebar();
  assert.equal(ui.document.querySelectorAll('#music-sync-together-button').length, 1);
  assert.deepEqual(ui.rows(), rows);
  await ui.trigger.dispatch('click');
  assert.equal(ui.panel.hidden, false);
});

test('treats state values as text and never uses IDs or external names as markup', () => {
  const ui = setup();
  const malicious = '<img src=x onerror=alert(1)>';
  ui.state(room(1, { code: malicious, selfParticipantId: malicious,
    participants: [{ participantId: malicious, role: malicious, connected: true, displayName: malicious }],
    error: malicious, playbackError: malicious }));
  assert.equal(ui.panel.querySelector('code').textContent, malicious);
  assert.match(ui.rows()[0].textContent, /ВыРоль неизвестнаПодключён/);
  assert.equal(ui.panel.querySelectorAll('img').length, 0);
  assert.equal(ui.panel.querySelectorAll('script').length, 0);
  assert.doesNotMatch(ui.list.textContent, /onerror/);
});

test('preserves create, join, copy, retry, leave and legacy role requests', async () => {
  const ui = setup();
  await ui.button('Создать комнату').dispatch('click');
  const input = ui.document.getElementById('music-sync-together-code');
  input.value = code;
  await ui.panel.querySelector('form').dispatch('submit');
  ui.state({ code, status: 'connected', role: 'leader', peerConnected: true,
    swap: { id: 'swap-id' }, playbackError: 'track-unavailable' });
  await ui.button('Копировать').dispatch('click');
  assert.ok(ui.button('Скопировано'));
  await ui.button('Повторить синхронизацию').dispatch('click');
  await ui.button('Сменить роли').dispatch('click');
  ui.state({ code, status: 'connected', role: 'listener', peerConnected: true,
    swap: { id: 'swap-id', requestedBy: 'leader', remainingMs: 10_000 } });
  assert.equal(ui.button('Подтвердить смену').hidden, false);
  await ui.button('Подтвердить смену').dispatch('click');
  await ui.button('Выйти').dispatch('click');
  assert.deepEqual(ui.calls, [
    ['create', undefined], ['join', code], ['copyCode', undefined], ['retryPlayback', undefined],
    ['swapRoles', 'swap-id'], ['swapRoles', 'swap-id'], ['leave', undefined]
  ]);
});

test('group state hides the untargeted legacy command', () => {
  const ui = setup();
  ui.state(room(2, { peerConnected: true, swap: { id: 'legacy', requestedBy: 'listener', remainingMs: 30_000 } }));
  assert.equal(ui.button('Сменить роли').disabled, true);
  assert.equal(ui.button('Сменить роли').hidden, true);
  assert.equal(ui.byClass('stack music-sync-together-request').hidden, true);
});

test('shows capacity and connection errors without losing playback retry', () => {
  const ui = setup();
  ui.state({ status: 'ended', error: 'room-full' });
  assert.match(ui.panel.textContent, /Ошибка подключения/);
  assert.match(ui.panel.textContent, /достигнут лимит участников/);
  assert.doesNotMatch(ui.panel.textContent, /Второй участник/);
  ui.state(room(10, { playbackError: 'track-unavailable' }));
  assert.equal(ui.button('Повторить синхронизацию').disabled, false);
  assert.match(ui.panel.textContent, /Трек недоступен в плеере/);
});


test('targets one listener and preserves the offer until explicit confirmation', async () => {
  const ui = setup();
  ui.state(room(10, { supportsLeadershipTransfer: true }));
  const transferButtons = ui.rows().map(row => row.querySelectorAll('button')[0]);
  assert.equal(transferButtons[0].hidden, true);
  assert.equal(transferButtons[7].hidden, false);
  await transferButtons[7].dispatch('click');
  assert.deepEqual(ui.calls, [['transferLeadership', 'p8']]);
  const transfer = { id: 'offer', initiatorParticipantId: 'self', targetParticipantId: 'p8', remainingMs: 20_000 };
  ui.state(room(10, { supportsLeadershipTransfer: true, transfer }));
  assert.equal(transferButtons[7].disabled, true);
  assert.match(ui.panel.textContent, /Запрос отправлен выбранному участнику/);
  assert.equal(ui.button('Принять управление').hidden, true);
  await ui.button('Отменить передачу').dispatch('click');
  assert.deepEqual(ui.calls.at(-1), ['cancelLeadership', 'offer']);
  ui.state(room(10, { supportsLeadershipTransfer: true, transfer, selfParticipantId: 'p8', role: 'listener' }));
  assert.equal(ui.button('Принять управление').hidden, false);
  assert.equal(ui.button('Принять управление').disabled, false);
  await ui.button('Принять управление').dispatch('click');
  assert.deepEqual(ui.calls.at(-1), ['acceptLeadership', 'offer']);
  ui.state(room(10, { supportsLeadershipTransfer: true, transfer, selfParticipantId: 'p2', role: 'listener' }));
  assert.equal(ui.byClass('stack music-sync-together-request').hidden, true);
});

test('transfer controls disable offline, expired and disconnected targets', () => {
  const ui = setup();
  ui.state(room(2, { supportsLeadershipTransfer: true, participants: [member('self', 'leader'), member('p2', 'listener', false)] }));
  assert.equal(ui.rows()[1].querySelector('button').disabled, true);
  const transfer = { id: 'offer', initiatorParticipantId: 'p2', targetParticipantId: 'self', remainingMs: 0 };
  ui.state(room(2, { supportsLeadershipTransfer: true, role: 'listener', transfer }));
  assert.equal(ui.button('Принять управление').disabled, true);
  ui.state(room(2, { supportsLeadershipTransfer: true, role: 'listener', transfer: { ...transfer, remainingMs: 20_000 }, status: 'reconnecting' }));
  assert.equal(ui.button('Принять управление').disabled, true);
  assert.equal(ui.button('Отменить передачу').disabled, true);
});
