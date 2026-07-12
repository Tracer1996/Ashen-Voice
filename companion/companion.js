'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {
  Client,
  Events,
  GatewayIntentBits,
} = require('discord.js');
const {
  entersState,
  joinVoiceChannel,
  VoiceConnectionStatus,
} = require('@discordjs/voice');

const token = process.env.ASHEN_DISCORD_TOKEN?.trim();
const guildId = process.env.ASHEN_DISCORD_GUILD_ID?.trim();
const channelId = process.env.ASHEN_DISCORD_CHANNEL_ID?.trim();
const stateDirectory = process.env.ASHEN_STATE_DIRECTORY?.trim();

if (!token || !guildId || !channelId || !stateDirectory) {
  emit('error', 'Missing Discord token, server ID, voice channel ID, or state directory.');
  process.exit(2);
}

const statePath = path.join(stateDirectory, 'speakers.tsv');
const avatarDirectory = path.join(stateDirectory, 'avatars');
const activeSpeakers = new Map();
const endTimers = new Map();
let connection = null;
let shuttingDown = false;

fs.mkdirSync(avatarDirectory, { recursive: true });
writeState();

const client = new Client({
  intents: [
    GatewayIntentBits.Guilds,
    GatewayIntentBits.GuildVoiceStates,
  ],
});

client.once(Events.ClientReady, async (readyClient) => {
  try {
    emit('status', `Signed in as ${readyClient.user.tag}.`);

    const guild = await readyClient.guilds.fetch(guildId);
    const channel = await guild.channels.fetch(channelId);

    if (!channel || !channel.isVoiceBased()) {
      throw new Error('The configured channel ID is not a Discord voice channel.');
    }

    connection = joinVoiceChannel({
      channelId: channel.id,
      guildId: guild.id,
      adapterCreator: guild.voiceAdapterCreator,
      selfMute: true,
      selfDeaf: false,
    });

    connection.on('error', (error) => {
      emit('error', `Discord voice error: ${error.message}`);
    });

    connection.on(VoiceConnectionStatus.Disconnected, () => {
      emit('status', 'Discord voice connection was interrupted. Reconnecting...');
    });

    connection.on(VoiceConnectionStatus.Destroyed, () => {
      emit('status', 'Discord voice connection closed.');
      activeSpeakers.clear();
      writeState();
    });

    await entersState(connection, VoiceConnectionStatus.Ready, 20_000);
    emit('connected', `Connected to ${guild.name} / ${channel.name}.`);

    connection.receiver.speaking.on('start', async (userId) => {
      const existingTimer = endTimers.get(userId);
      if (existingTimer) {
        clearTimeout(existingTimer);
        endTimers.delete(userId);
      }

      const previous = activeSpeakers.get(userId);
      activeSpeakers.set(userId, {
        userId,
        displayName: previous?.displayName ?? 'Discord User',
        avatarPath: previous?.avatarPath ?? '',
        startedAt: previous?.startedAt ?? Date.now(),
      });
      writeState();

      try {
        const speaker = await resolveSpeaker(guild, channel, userId);
        const current = activeSpeakers.get(userId);
        if (!current) {
          return;
        }

        activeSpeakers.set(userId, {
          ...current,
          displayName: speaker.displayName,
          avatarPath: speaker.avatarPath,
        });
        writeState();
        emit('speaker', `${speaker.displayName} started speaking.`);
      } catch (error) {
        emit('error', `Could not resolve Discord speaker ${userId}: ${error.message}`);
      }
    });

    connection.receiver.speaking.on('end', (userId) => {
      const timer = setTimeout(() => {
        endTimers.delete(userId);
        const speaker = activeSpeakers.get(userId);
        if (speaker) {
          activeSpeakers.delete(userId);
          writeState();
          emit('speaker', `${speaker.displayName} stopped speaking.`);
        }
      }, 425);

      endTimers.set(userId, timer);
    });
  } catch (error) {
    emit('error', error?.message ?? String(error));
    await shutdown(3);
  }
});

client.on(Events.Error, (error) => {
  emit('error', `Discord client error: ${error.message}`);
});

client.login(token).catch(async (error) => {
  emit('error', `Discord login failed: ${error.message}`);
  await shutdown(4);
});

async function resolveSpeaker(guild, channel, userId) {
  let member = channel.members?.get(userId) ?? guild.members.cache.get(userId);

  if (!member) {
    try {
      member = await guild.members.fetch(userId);
    } catch {
      member = null;
    }
  }

  const user = member?.user ?? await client.users.fetch(userId);
  const displayName = sanitize(member?.displayName || user.globalName || user.username || 'Discord User');
  const avatarUrl = member
    ? member.displayAvatarURL({ extension: 'png', size: 64, forceStatic: true })
    : user.displayAvatarURL({ extension: 'png', size: 64, forceStatic: true });
  const avatarPath = await cacheAvatar(userId, avatarUrl);

  return { displayName, avatarPath };
}

async function cacheAvatar(userId, url) {
  if (!url) {
    return '';
  }

  const hash = crypto.createHash('sha1').update(url).digest('hex').slice(0, 12);
  const target = path.join(avatarDirectory, `${userId}-${hash}.png`);
  if (fs.existsSync(target)) {
    return target;
  }

  const response = await fetch(url, { signal: AbortSignal.timeout(10_000) });
  if (!response.ok) {
    throw new Error(`Avatar download returned HTTP ${response.status}.`);
  }

  const bytes = Buffer.from(await response.arrayBuffer());
  const temporary = `${target}.tmp`;
  fs.writeFileSync(temporary, bytes);
  fs.renameSync(temporary, target);
  return target;
}

function writeState() {
  fs.mkdirSync(stateDirectory, { recursive: true });

  const rows = [...activeSpeakers.values()]
    .sort((left, right) => left.startedAt - right.startedAt)
    .slice(0, 8)
    .map((speaker) => `${sanitize(speaker.displayName)}\t${sanitizePath(speaker.avatarPath)}`);

  const temporary = `${statePath}.tmp`;
  fs.writeFileSync(temporary, rows.join('\n'), 'utf8');

  try {
    fs.rmSync(statePath, { force: true });
    fs.renameSync(temporary, statePath);
  } catch (error) {
    fs.rmSync(temporary, { force: true });
    emit('error', `Could not update speaker state: ${error.message}`);
  }
}

function sanitize(value) {
  return String(value).replace(/[\t\r\n]/g, ' ').trim().slice(0, 48);
}

function sanitizePath(value) {
  return String(value || '').replace(/[\t\r\n]/g, '').trim();
}

function emit(type, message) {
  process.stdout.write(`${JSON.stringify({ type, message, time: new Date().toISOString() })}\n`);
}

async function shutdown(exitCode = 0) {
  if (shuttingDown) {
    return;
  }

  shuttingDown = true;
  for (const timer of endTimers.values()) {
    clearTimeout(timer);
  }
  endTimers.clear();
  activeSpeakers.clear();
  writeState();

  try {
    connection?.destroy();
  } catch {
    // Ignore shutdown races.
  }

  try {
    client.destroy();
  } catch {
    // Ignore shutdown races.
  }

  setTimeout(() => process.exit(exitCode), 50);
}

process.on('SIGINT', () => void shutdown(0));
process.on('SIGTERM', () => void shutdown(0));
process.on('uncaughtException', (error) => {
  emit('error', `Unhandled companion error: ${error.stack || error.message}`);
  void shutdown(5);
});
process.on('unhandledRejection', (error) => {
  emit('error', `Unhandled companion promise: ${error?.stack || error}`);
  void shutdown(6);
});
