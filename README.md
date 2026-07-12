# Ashen Voice — Phase 3.1 Local Discord RPC

Ashen Voice is a compact Discord speaking overlay for 32-bit DirectX 9 World of Warcraft clients such as OctoWoW and other 1.12-era clients.

Phase 3.1 removes the bot-based connection screen. Players click **Connect Discord** once, approve Ashen Voice, and the app follows the voice channel used by their locally running Discord desktop account.

## What is included

- Existing exclusive-fullscreen DirectX 9 overlay and injector
- Compact avatar + display-name speaker rows
- Direct connection to the local Discord desktop client
- Automatic active voice-channel detection
- Speaking start/stop events
- Local avatar caching
- Encrypted OAuth token storage using Windows DPAPI
- No bot token
- No server ID or voice-channel ID
- No Node.js companion
- No bridge process
- Self-contained Windows installer

## Important Discord restriction

Discord RPC access is restricted during development to the application owner, developer-team members, and approved app testers. Discord currently allows up to 50 testers. A public release that uses the RPC voice scopes requires Discord approval.

This build is therefore a **Phase 3.1 connection probe**. It lets us verify the correct no-bot architecture with the developer account and invited testers before applying for public RPC access.

## Developer Portal setup

Follow `DISCORD_SETUP.md` before running the GitHub Actions build.

The build requires one GitHub repository variable:

```text
DISCORD_CLIENT_ID
```

This is the public Discord Application ID, not a bot token or client secret.

## Build

Run the GitHub Actions workflow:

```text
Build Ashen Voice Phase 3.1 Local Discord
```

Download the artifact:

```text
Ashen-Voice-Phase3.1-Local-Discord-Installer
```

It contains:

```text
AshenVoice-Setup-1.3.0.exe
AshenVoice-Setup-1.3.0.exe.sha256
```

## Test order

1. Open the Discord desktop app and join a normal server voice channel.
2. Install and open Ashen Voice 1.3.0.
3. Click **Connect Discord**.
4. Approve the Discord authorization page.
5. Confirm Ashen Voice shows the connected account and active voice channel.
6. Launch OctoWoW.
7. Click **Start Overlay**.
8. Have another voice-channel member speak.
9. Confirm their compact avatar/name row appears in fullscreen WoW and disappears shortly after they stop.

## Privacy

Ashen Voice reads local Discord voice-state metadata needed for the overlay: account identity, active channel, participant display names, avatar identifiers, and speaking start/stop events. It does not capture, decode, save, transmit, or play voice audio.
