# Ashen Voice 1.4.4

## Fixed

- Fixed the Phase 4.3 build error in `DiscordRpcClient.cs`:
  `CS0165: Use of unassigned local variable 'botElement'`.
- Discord bot detection now initializes the bot flag safely before it is used.
- Keeps the existing LunaBot/music-bot filtering, persistent preview, expanded offsets, file-lock protection, administrator launch, Direct Discord connection, and fullscreen DirectX 9 overlay.
