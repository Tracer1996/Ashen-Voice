# Ashen Voice — Phase 3

Ashen Voice is a fullscreen Discord speaker overlay for 32-bit DirectX 9 classic World of Warcraft clients, including OctoWoW.

## Phase 3 features

- Compact Discord-style speaker rows instead of a large permanent panel
- Only people who are actively speaking are shown
- Circular Discord avatars with a green speaking ring
- Discord display names rendered with a clean Segoe UI font
- Subtle translucent cards in the upper-right corner
- Up to five simultaneous speakers
- Real Discord voice activity through a companion bot
- The companion is bundled with the installer; players do not install Node.js
- DirectX 9 overlay remains visible in exclusive fullscreen
- Secure optional bot-token storage using Windows DPAPI
- Eight-second compact preview for testing without Discord

## Privacy behavior

The bot joins the selected Discord voice channel muted. It must receive Discord voice packets so that speaking activity can be detected, but Ashen Voice does not play, record, save, transcribe, or upload voice audio. The companion only writes active display names and cached avatar image paths to a local state file.

Discord voice receiving is not a documented Discord platform feature, so upstream library changes can require future compatibility updates.

## Build the installer

1. Copy this package over the contents of the existing Ashen Voice repository while keeping the hidden `.git` folder.
2. Commit and push the changes.
3. Open GitHub **Actions**.
4. Run **Build Ashen Voice Phase 3**.
5. Download the **Ashen-Voice-Phase3-Installer** artifact.
6. Extract it and run `AshenVoice-Setup-1.2.0.exe`.

End users do not need Visual Studio, CMake, .NET, Node.js, or Inno Setup.

## Discord bot setup

1. Create an application in the Discord Developer Portal.
2. Open the application's **Bot** page and create/reset the bot token.
3. Invite the bot to the server with **View Channels** and **Connect** permissions.
4. Enable Developer Mode in Discord.
5. Right-click the server and copy the Server ID.
6. Right-click the voice channel and copy the Channel ID.
7. Open Ashen Voice and enter the token and both IDs.
8. Click **Connect Discord**.
9. Launch OctoWoW and click **Start Overlay**.

The bot does not need Administrator permission and does not need permission to send messages.

## Phase 3 acceptance test

- The installer upgrades the existing Ashen Voice installation.
- Ashen Voice detects the WoW process.
- **Start Overlay** loads without displaying a permanent panel.
- **Preview Compact Style** shows three small rows for eight seconds.
- **Connect Discord** causes the bot to join the selected voice channel.
- Speaking users appear with their display name and circular avatar.
- Rows disappear shortly after speech stops.
- Alt-tabbing and exclusive fullscreen continue working.
- Stopping the overlay does not close WoW.
