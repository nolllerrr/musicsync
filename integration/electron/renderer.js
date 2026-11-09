(() => {
  'use strict';

  const url = window.location;
  const api = window.musicSyncTogether;
  if (url.protocol !== 'music-application:' || url.host !== 'desktop' ||
      document.getElementById('music-sync-together-panel')) return;
  if (!api) {
    console.warn('MusicSync Together: bridge-unavailable');
    return;
  }

  const navSelector = 'aside[data-test-id="NAVBAR"]';
  const homeSelector = '[data-test-id="NAVBAR_NAVIGATION_ITEM_HOME"]';
  const trendsSelector = '[data-test-id="NAVBAR_NAVIGATION_ITEM_FOR_YOU_AND_TRENDS"]';
  const statuses = {
    idle: 'Нет подключения', connecting: 'Подключение…', connected: 'Подключено',
    reconnecting: 'Переподключение…', ended: 'Связь завершена'
  };
  const errors = {
    'invalid-room-code': 'Проверьте код комнаты.',
    'already-in-room': 'Подключение к комнате уже открыто.',
    'instance-in-use': 'Закройте другой MusicSync в этом сеансе Windows.',
    'room-full': 'В комнате уже есть второй участник.',
    'room-unavailable': 'Комната недоступна. Проверьте код или создайте новую.',
    'server-full': 'На сервере достигнут лимит комнат.',
    'room-creation-limited': 'Для вашего IP-адреса достигнут лимит создания комнат. Попробуйте позже или войдите по коду.',
    'role-swap-stale': 'Время запроса истекло. Отправьте новый запрос.',
    'role-swap-unavailable': 'Для смены ролей оба участника должны быть в сети.',
    'leader-left': 'Ведущий вышел. Комната закрыта.',
    'leader-timeout': 'Ведущий не вернулся. Комната закрыта.',
    'agent-config-invalid': 'Проверьте настройки сервера MusicSync.',
    'agent-unavailable': 'Не удалось запустить MusicSync. Проверьте журнал.',
    'agent-timeout': 'MusicSync не ответил вовремя. Сверьте состояние комнаты.',
    'connection-failed': 'Не удалось подключиться к серверу.',
    'delivery-unknown': 'Ответ на команду потерян. Сверьте состояние комнаты.',
    'connection-lost': 'Связь с сервером потеряна.',
    offline: 'Сервер недоступен.',
    'no-room': 'Комната недоступна.'
  };
  const playbackErrors = {
    'no-player': 'Плеер недоступен.',
    'no-session': 'Откройте трек в плеере.',
    'track-unavailable': 'Трек недоступен в плеере.',
    'track-unconfirmed': 'Плеер не подтвердил нужный трек.',
    'command-unconfirmed': 'Плеер не подтвердил команду.',
    'position-unconfirmed': 'Плеер не подтвердил позицию.',
    'track-ended': 'Трек закончился.'
  };

  function element(tag, attributes = {}, text = '') {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, value);
    node.textContent = text;
    return node;
  }

  function setText(node, text) {
    if (node.textContent !== text) node.textContent = text;
  }

  const item = element('li', { 'data-test-id': 'NAVBAR_NAVIGATION_ITEM_TOGETHER' });
  const trigger = element('button', {
    id: 'music-sync-together-button', type: 'button', title: 'Вместе', 'aria-label': 'Вместе',
    'aria-controls': 'music-sync-together-panel', 'aria-haspopup': 'dialog', 'aria-expanded': 'false'
  });
  const iconContainer = element('div');
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('aria-hidden', 'true');
  icon.setAttribute('focusable', 'false');
  const people = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  people.setAttribute('href', '/icons/sprite.svg#twoUsers_xxs');
  icon.append(people);
  iconContainer.append(icon);
  const textContainer = element('div');
  const label = element('span', { title: 'Вместе' }, 'Вместе');
  textContainer.append(label);
  trigger.append(iconContainer, textContainer);
  item.append(trigger);

  const panel = element('section', {
    id: 'music-sync-together-panel', role: 'dialog', 'aria-labelledby': 'music-sync-together-title', hidden: ''
  });
  const title = element('h2', { id: 'music-sync-together-title', tabindex: '-1' }, 'Вместе');
  const connection = element('p', { class: 'music-sync-together-subtitle', role: 'status' });
  const error = element('p', { class: 'music-sync-together-error', role: 'alert', hidden: '' });
  const enter = element('div', { class: 'music-sync-together-stack' });
  const create = element('button', { type: 'button', 'data-primary': '' }, 'Создать комнату');
  const join = element('form', { class: 'music-sync-together-stack' });
  const codeLabel = element('label', { for: 'music-sync-together-code' }, 'Войти по коду');
  const input = element('input', {
    id: 'music-sync-together-code', name: 'code', type: 'text', autocomplete: 'off',
    autocapitalize: 'characters', spellcheck: 'false', maxlength: '64', placeholder: 'Код комнаты', required: ''
  });
  const enterButton = element('button', { type: 'submit' }, 'Войти');
  const cancel = element('button', { type: 'button', hidden: '' }, 'Отменить подключение');
  join.append(codeLabel, input, enterButton);
  enter.append(create, join, cancel);

  const room = element('div', { class: 'music-sync-together-stack', hidden: '' });
  const code = element('code');
  const copy = element('button', { type: 'button', 'aria-label': 'Копировать код комнаты' }, 'Копировать');
  const codeHeading = element('p', { class: 'music-sync-together-subtitle' }, 'Код комнаты');
  const codeRow = element('div', { class: 'music-sync-together-code' });
  codeRow.append(code, copy);

  const role = element('dd');
  const peer = element('dd');
  const details = element('dl');
  details.append(element('dt', {}, 'Ваша роль'), role, element('dt', {}, 'Второй участник'), peer);
  const playback = element('div', { class: 'music-sync-together-stack', hidden: '' });
  const playbackError = element('p', { class: 'music-sync-together-error', role: 'alert' });
  const retry = element('button', { type: 'button' }, 'Повторить синхронизацию');
  playback.append(playbackError, retry);

  const swapRequest = element('div', { class: 'music-sync-together-stack music-sync-together-request', hidden: '' });
  const swapMessage = element('p');
  const countdown = element('p', { class: 'music-sync-together-subtitle', role: 'timer' });
  const confirm = element('button', { type: 'button', 'data-primary': '' }, 'Подтвердить смену');
  swapRequest.append(swapMessage, countdown, confirm);
  const actions = element('div', { class: 'music-sync-together-actions' });
  const swap = element('button', { type: 'button' }, 'Сменить роли');
  const leave = element('button', { type: 'button' }, 'Выйти');
  actions.append(swap, leave);
  room.append(codeHeading, codeRow, details, playback, swapRequest, actions);
  panel.append(title, connection, error, enter, room);
  document.body.append(panel);

  let snapshot = { status: 'idle' };
  let pending = false;
  let commandError;
  let copyTimer;
  let sidebar;
  let warning;
  let frame = 0;

  function position() {
    if (panel.hidden) return;
    const anchor = trigger.getBoundingClientRect();
    const gap = 8;
    const maxLeft = window.innerWidth - panel.offsetWidth - gap;
    const maxTop = window.innerHeight - panel.offsetHeight - gap;
    panel.style.left = `${Math.max(gap, Math.min(anchor.right + gap, maxLeft))}px`;
    panel.style.top = `${Math.max(gap, Math.min(anchor.top, maxTop))}px`;
  }

  function paint() {
    const hasRoom = Boolean(snapshot.code);
    const swapPending = Boolean(snapshot.swap?.requestedBy);
    const incoming = swapPending && snapshot.swap.requestedBy !== snapshot.role;
    const canSwap = snapshot.status === 'connected' && snapshot.peerConnected && snapshot.swap?.id && !pending;
    const waiting = !hasRoom && (snapshot.status === 'connecting' || snapshot.status === 'reconnecting');
    const blocked = pending || waiting;
    const errorCode = snapshot.status === 'ended' && snapshot.error ? snapshot.error : commandError ?? snapshot.error;
    setText(connection, statuses[snapshot.status] ?? statuses.idle);
    error.hidden = !errorCode;
    setText(error, errorCode ? errors[errorCode] ?? 'Операция завершилась с ошибкой. Проверьте журнал.' : '');
    enter.hidden = hasRoom;
    room.hidden = !hasRoom;
    create.disabled = blocked;
    input.disabled = blocked;
    enterButton.disabled = blocked;
    cancel.hidden = !waiting;
    cancel.disabled = pending;
    setText(code, snapshot.code ?? '');
    setText(role, snapshot.role === 'leader' ? 'Ведущий' : snapshot.role === 'listener' ? 'Слушатель' : '—');
    setText(peer, snapshot.status === 'reconnecting' ? 'Статус неизвестен' :
      snapshot.peerConnected ? 'В комнате' : 'Ожидание участника');
    playback.hidden = !snapshot.playbackError;
    setText(playbackError, snapshot.playbackError
      ? playbackErrors[snapshot.playbackError] ?? 'Не удалось синхронизировать воспроизведение.'
      : '');
    retry.disabled = pending || snapshot.status !== 'connected';
    swap.disabled = !canSwap || swapPending;
    leave.disabled = pending;
    swapRequest.hidden = !swapPending;
    setText(swapMessage, incoming ? 'Второй участник предлагает сменить роли.' : 'Запрос отправлен. Ожидание ответа.');
    const seconds = Math.ceil(Math.max(0, snapshot.swap?.remainingMs ?? 0) / 1000);
    setText(countdown, `Осталось ${seconds} с`);
    confirm.hidden = !incoming;
    confirm.disabled = !canSwap || seconds === 0;
    position();
  }

  async function run(command, value) {
    if (pending) return;
    pending = true;
    commandError = undefined;
    paint();
    try {
      commandError = (await command(value)).error;
    } catch {
      commandError = 'agent-unavailable';
    } finally {
      pending = false;
      paint();
    }
  }

  function setOpen(open, restoreFocus = false) {
    panel.hidden = !open;
    trigger.setAttribute('aria-expanded', String(open));
    if (open) {
      position();
      title.focus({ preventScroll: true });
    } else if (restoreFocus && trigger.isConnected) {
      trigger.focus({ preventScroll: true });
    }
  }

  trigger.addEventListener('click', event => {
    event.stopPropagation();
    const open = panel.hidden;
    setOpen(open, !open);
  });
  document.addEventListener('pointerdown', event => {
    if (!panel.hidden && !panel.contains(event.target) && !trigger.contains(event.target)) setOpen(false);
  }, true);
  document.addEventListener('keydown', event => {
    if (!panel.hidden && event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      setOpen(false, true);
    }
  }, true);

  create.addEventListener('click', () => void run(api.create));
  cancel.addEventListener('click', () => void run(api.leave));
  join.addEventListener('submit', event => {
    event.preventDefault();
    void run(api.join, input.value);
  });
  swap.addEventListener('click', () => void run(api.swapRoles, snapshot.swap?.id));
  confirm.addEventListener('click', () => void run(api.swapRoles, snapshot.swap?.id));
  retry.addEventListener('click', () => void run(api.retryPlayback));
  leave.addEventListener('click', () => void run(api.leave));
  copy.addEventListener('click', async () => {
    try {
      if ((await api.copyCode()).error) return;
      clearTimeout(copyTimer);
      setText(copy, 'Скопировано');
      copyTimer = setTimeout(() => setText(copy, 'Копировать'), 1500);
      position();
    } catch {
      commandError = 'agent-unavailable';
      paint();
    }
  });
  api.subscribe(state => {
    if (state.code !== snapshot.code || state.status !== snapshot.status) commandError = undefined;
    if (state.code && state.code !== snapshot.code) input.value = '';
    snapshot = state;
    paint();
  });

  function copyClasses(target, source, exclude = []) {
    const value = [...source.classList].filter(name => !exclude.includes(name)).join(' ');
    if (target.getAttribute('class') !== value) target.setAttribute('class', value);
  }

  function findSidebar() {
    sidebar = document.querySelector(navSelector);
    if (!sidebar) return 'sidebar-missing';
    const home = sidebar.querySelector(homeSelector)?.closest('li');
    const trendLink = sidebar.querySelector(trendsSelector);
    const trends = trendLink?.closest('li');
    if (!home) return 'my-vibe-missing';
    if (!trends) return 'trends-missing';
    const templateIcon = trendLink.querySelector('svg');
    const templateLabel = trendLink.querySelector('span[title]');
    const next = home.nextElementSibling === item ? item.nextElementSibling : home.nextElementSibling;
    if (home.parentElement !== trends.parentElement || next !== trends || !templateIcon || !templateLabel) {
      return 'unexpected-sidebar-layout';
    }

    copyClasses(item, trends);
    copyClasses(trigger, trendLink, ['Eg3pt5lTL33sOlxorSbN', 'mAd9pgMkWVX5ktCgYINQ',
      'NavbarDesktop_disabledNavigationItem__Qp_hs']);
    copyClasses(iconContainer, templateIcon.parentElement);
    copyClasses(icon, templateIcon);
    copyClasses(textContainer, templateLabel.parentElement, ['jhnLSZpmG69Hlxi8n6IO', 'xENlRAFvRskKnt8LUObC']);
    copyClasses(label, templateLabel);
    label.style.webkitLineClamp = templateLabel.style.webkitLineClamp;
    if (home.nextElementSibling !== item) trends.before(item);
    return undefined;
  }

  function refresh() {
    frame = 0;
    const problem = findSidebar();
    if (problem) {
      item.remove();
      if (!panel.hidden) setOpen(false);
      if (problem !== warning) {
        warning = problem;
        // The sidebar can be absent briefly while Yandex replaces a route.
        setTimeout(() => {
          if (warning === problem && findSidebar() === problem) console.warn(`MusicSync Together: ${problem}`);
        }, 2000);
      }
    } else {
      warning = undefined;
      position();
    }
  }

  function schedule() {
    if (!frame) frame = requestAnimationFrame(refresh);
  }

  const observer = new MutationObserver(records => {
    if (!sidebar?.isConnected || records.some(record => sidebar.contains(record.target))) schedule();
  });
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  window.addEventListener('resize', schedule);
  window.addEventListener('scroll', position, true);
  refresh();
  paint();
})();
