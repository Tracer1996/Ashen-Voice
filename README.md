# Ashen Voice — Phase 1

This package contains only the validated Phase 1 scope:

- Native Windows desktop application built with C# and .NET 8 WPF
- Normal Windows installer
- OctoWoW / WoW process detection
- Start and stop monitoring controls
- Local activity log
- Persistent settings
- Minimize to system tray
- Optional launch with Windows
- Normal Windows uninstall support

DirectX 9 rendering and Discord integration are intentionally not included in this phase.

## Build the installer using GitHub

1. Replace the contents of your existing GitHub repository with this package, while keeping the repository's hidden `.git` folder.
2. Commit and push the files.
3. Open the repository on GitHub.
4. Select **Actions**.
5. Select **Build Ashen Voice Phase 1**.
6. Click **Run workflow**.
7. Open the completed run and download the **Ashen-Voice-Phase1-Installer** artifact.
8. Extract the artifact and run `AshenVoice-Setup-1.0.0.exe`.

End users do not need .NET, Visual Studio, CMake, Node.js, or Inno Setup. The installer contains a self-contained application.

## Phase 1 acceptance test

- Installer opens normally.
- App launches from the desktop or Start Menu.
- Opening `WoW.exe` changes the WoW status to **Detected** within two seconds.
- Custom process names can be entered without `.exe`.
- Settings remain after closing and reopening the app.
- Minimize-to-tray works.
- Ashen Voice appears in Windows Installed Apps and uninstalls normally.
