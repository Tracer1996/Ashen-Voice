# Ashen Voice — Phase 2

Ashen Voice is a Windows desktop application and fullscreen DirectX 9 overlay for 32-bit classic World of Warcraft clients.

## Phase 2 scope

This phase adds the first real in-game overlay test while preserving the validated Phase 1 desktop app and installer.

Included:

- Windows desktop application built with C# and .NET 8 WPF
- Normal self-contained Windows installer
- OctoWoW and WoW process detection
- 32-bit native injector
- 32-bit DirectX 9 injected overlay DLL
- Test panel rendered inside true exclusive fullscreen
- Safe stop signal and DLL unload
- Native troubleshooting log
- Three fake speaking users for visual testing

Not included yet:

- Discord connection
- Live speaker detection
- Discord avatars
- Overlay positioning and appearance settings
- Automatic updates

## Build with GitHub Actions

1. Commit the Phase 2 files to the repository.
2. Open **Actions** on GitHub.
3. Run **Build Ashen Voice Phase 2**.
4. Download the artifact named **Ashen-Voice-Phase2-Installer**.
5. Extract it and run `AshenVoice-Setup-1.1.0.exe`.

End users do not need CMake, Visual Studio, .NET, Node.js, or Inno Setup. GitHub compiles and packages everything.

## Phase 2 test

1. Install Ashen Voice 1.1.0.
2. Launch OctoWoW and wait for the app to show **Detected**.
3. Click **Start Test Overlay**.
4. Switch back to WoW in true fullscreen.
5. Look in the upper-right corner for the Ashen Voice test panel.
6. Alt-tab back and click **Stop Overlay**.
7. Confirm the panel disappears and WoW continues running normally.

If the DLL loads but the hook does not become ready, inspect:

`%LOCALAPPDATA%\AshenVoice\overlay-native.log`

## Important compatibility notes

- The native overlay and injector are built as Win32/x86 because the classic 1.12 WoW client is 32-bit.
- Ashen Voice and WoW must run at the same Windows permission level. If WoW is launched as administrator, Ashen Voice must also be launched as administrator.
- Phase 2 targets the DirectX 9 renderer. Other renderers are not supported in this test build.
