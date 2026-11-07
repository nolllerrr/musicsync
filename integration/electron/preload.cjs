;(() => {
  'use strict';

  const url = window.location;
  if (window !== window.top || url.protocol !== 'music-application:' || url.host !== 'desktop' ||
      url.username || url.password) return;

  const { contextBridge, ipcRenderer } = require('electron');
  const command = (name, argument) => ipcRenderer.invoke('musicsync-together:command', name, argument);
  contextBridge.exposeInMainWorld('musicSyncTogether', {
    create: () => command('create'),
    join: code => command('join', code),
    swapRoles: requestId => command('swapRoles', requestId),
    retryPlayback: () => command('retryPlayback'),
    leave: () => command('leave'),
    isEvaluationCurrent: ticket => ipcRenderer.sendSync('musicsync-together:evaluation-current', ticket),
    copyCode: () => ipcRenderer.invoke('musicsync-together:copy-code'),
    subscribe: listener => {
      const receive = (_event, state) => listener(state);
      ipcRenderer.on('musicsync-together:state', receive);
      // A fresh document needs the current state even if the agent is quiet.
      ipcRenderer.send('musicsync-together:subscribe');
      return () => ipcRenderer.removeListener('musicsync-together:state', receive);
    }
  });
})();
