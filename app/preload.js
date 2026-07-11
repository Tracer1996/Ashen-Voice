const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('overlay', {
  loadSettings: () => ipcRenderer.invoke('load-settings'),
  saveSettings: settings => ipcRenderer.invoke('save-settings', settings),
  start: settings => ipcRenderer.invoke('start-overlay', settings),
  stop: () => ipcRenderer.invoke('stop-overlay'),
  onStatus: callback => ipcRenderer.on('status', (_e, data) => callback(data))
});
