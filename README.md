# Ashen Voice

**Discord voice activity overlay for OctoWoW and compatible 32-bit DirectX 9 Vanilla WoW clients.**

Ashen Voice displays active Discord speakers inside true exclusive-fullscreen WoW. The public download is a normal Windows installer and does not require players to install CMake, Visual Studio, Node.js, or use Command Prompt.

## Player setup

1. Run `AshenVoice-Setup-1.1.0.exe`.
2. Open **Ashen Voice**.
3. Enter the Discord bot token, server ID, and voice-channel ID once.
4. Click **Start Overlay**.
5. Launch WoW.

The Discord bot requires only **View Channel** and **Connect** permissions. The bot is used because Discord's public local RPC does not provide third-party applications with live speaking-state events.

## Maintainer build

The project includes an automated Windows GitHub Actions workflow. It compiles the Win32 DirectX 9 DLL and injector, packages the Electron application, and produces the distributable installer plus its checksum.

See `BUILD_RELEASE.md`.

## Security and distribution

Ashen Voice injects its rendering DLL into the 32-bit WoW process to support exclusive fullscreen. Only distribute it where this overlay method is explicitly permitted. Unsigned builds may trigger Windows SmartScreen or antivirus warnings. A broadly distributed release should be code-signed.
