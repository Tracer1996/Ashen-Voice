# Ashen Voice development phases

## Phase 1 — Complete

Core Windows application, installer, settings, tray behavior, and WoW process detection.

## Phase 2 — Complete

32-bit DirectX 9 injected overlay working in exclusive-fullscreen OctoWoW.

## Phase 3 — Superseded

The first speaker integration used a bot token and per-user channel IDs. That architecture was rejected because normal users should not configure or host a Discord bot.

## Phase 3.1 — Current

Direct local Discord account connection:

- One **Connect Discord** button
- No bot or bridge
- Automatic current voice-channel detection
- Live speaking metadata
- Real display names and avatars
- Compact fullscreen overlay rows

Development access is limited to the app owner and invited testers until Discord grants public RPC voice-scope access.

## Phase 4 — Planned

Position, scale, opacity, avatar sizing, layouts, themes, hotkey, and multi-monitor profiles.

## Phase 5 — Planned

Public release workflow, Discord RPC approval, signed installers, releases, update checking, documentation, and rollback support.
