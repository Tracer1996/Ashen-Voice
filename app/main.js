const { app, BrowserWindow, ipcMain, safeStorage, dialog } = require('electron');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { spawn } = require('child_process');
const { Client, GatewayIntentBits } = require('discord.js');
const { joinVoiceChannel, entersState, VoiceConnectionStatus } = require('@discordjs/voice');

let win;
let client = null;
let connection = null;
let injector = null;
let speaking = new Map();

const dataDir = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'AshenVoice');
const settingsPath = path.join(app.getPath('userData'), 'settings.json');
const speakersPath = path.join(dataDir, 'speakers.txt');

function nativePath(file) {
  return app.isPackaged
    ? path.join(process.resourcesPath, 'native', file)
    : path.join(__dirname, '..', 'release', file);
}

function readSettings() {
  try {
    const s = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
    if (s.tokenEncrypted && safeStorage.isEncryptionAvailable()) {
      s.token = safeStorage.decryptString(Buffer.from(s.tokenEncrypted, 'base64'));
    }
    delete s.tokenEncrypted;
    return s;
  } catch { return {}; }
}

function saveSettings(settings) {
  fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
  const out = { ...settings };
  if (settings.token && safeStorage.isEncryptionAvailable()) {
    out.tokenEncrypted = safeStorage.encryptString(settings.token).toString('base64');
    delete out.token;
  }
  fs.writeFileSync(settingsPath, JSON.stringify(out, null, 2), 'utf8');
}

function publish() {
  fs.mkdirSync(dataDir, { recursive: true });
  const tmp = `${speakersPath}.tmp`;
  fs.writeFileSync(tmp, [...speaking.values()].slice(0, 10).join('\n'), 'utf8');
  fs.renameSync(tmp, speakersPath);
}

function status(message, state = 'info') {
  if (win && !win.isDestroyed()) win.webContents.send('status', { message, state });
}

async function stopOverlay() {
  speaking.clear();
  publish();
  try { connection?.destroy(); } catch {}
  connection = null;
  try { await client?.destroy(); } catch {}
  client = null;
  try { injector?.kill(); } catch {}
  injector = null;
  status('Overlay stopped.', 'idle');
}

async function startOverlay(settings) {
  await stopOverlay();
  if (!settings.token || !settings.guildId || !settings.channelId) throw new Error('Enter the bot token, server ID, and voice channel ID.');
  saveSettings(settings);
  fs.mkdirSync(dataDir, { recursive: true });
  publish();

  const dll = nativePath('AshenVoice.dll');
  const exe = nativePath('AshenVoiceInjector.exe');
  if (!fs.existsSync(dll) || !fs.existsSync(exe)) throw new Error('The native overlay files are missing. Reinstall the application.');

  client = new Client({ intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildVoiceStates] });
  client.on('error', e => status(`Discord error: ${e.message}`, 'error'));
  client.once('ready', async () => {
    try {
      const guild = await client.guilds.fetch(settings.guildId);
      const channel = await guild.channels.fetch(settings.channelId);
      if (!channel?.isVoiceBased()) throw new Error('The configured channel is not a voice channel.');
      connection = joinVoiceChannel({
        channelId: settings.channelId,
        guildId: settings.guildId,
        adapterCreator: guild.voiceAdapterCreator,
        selfMute: true,
        selfDeaf: false
      });
      await entersState(connection, VoiceConnectionStatus.Ready, 20000);
      connection.receiver.speaking.on('start', async id => {
        try {
          const member = await guild.members.fetch(id);
          speaking.set(id, member.displayName);
          publish();
        } catch {}
      });
      connection.receiver.speaking.on('end', id => {
        speaking.delete(id);
        publish();
      });
      status(`Connected to ${channel.name}. Waiting for WoW...`, 'running');
    } catch (e) {
      status(e.message, 'error');
      await stopOverlay();
    }
  });

  status('Connecting to Discord...', 'working');
  await client.login(settings.token);

  injector = spawn(exe, [dll], { detached: false, windowsHide: true });
  injector.on('exit', code => {
    if (code && code !== 0) status(`Injector exited with code ${code}. Try running the app as administrator.`, 'error');
  });
}

function createWindow() {
  win = new BrowserWindow({
    width: 620,
    height: 650,
    minWidth: 560,
    minHeight: 600,
    backgroundColor: '#11141a',
    title: 'Ashen Voice',
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false }
  });
  win.setMenuBarVisibility(false);
  win.loadFile(path.join(__dirname, 'index.html'));
}

ipcMain.handle('load-settings', () => readSettings());
ipcMain.handle('save-settings', (_e, settings) => { saveSettings(settings); return true; });
ipcMain.handle('start-overlay', async (_e, settings) => { await startOverlay(settings); return true; });
ipcMain.handle('stop-overlay', async () => { await stopOverlay(); return true; });
ipcMain.handle('show-error', (_e, message) => dialog.showErrorBox('Ashen Voice', message));

app.whenReady().then(createWindow);
app.on('window-all-closed', async () => { await stopOverlay(); app.quit(); });
