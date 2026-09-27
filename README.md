<div align="center">
  <img src="PalServerManager.WinUI/Assets/ManagerIconCircular.png" alt="PalServerManager icon" width="112" height="112">
  <h1>PalServerManager</h1>
  <p>A lightweight Windows manager for Palworld Dedicated Servers, with multi-world management, per-world settings, safe switching, backups, rollback, and save migration.</p>

  <p>
    <a href="https://github.com/Zhehao-w/pal-server-manager/releases/latest"><img alt="Latest GitHub release" src="https://img.shields.io/github/v/release/Zhehao-w/pal-server-manager?display_name=tag&sort=semver"></a>
    <a href="https://github.com/Zhehao-w/pal-server-manager/actions/workflows/ci.yml"><img alt="CI status" src="https://github.com/Zhehao-w/pal-server-manager/actions/workflows/ci.yml/badge.svg"></a>
    <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/Zhehao-w/pal-server-manager"></a>
    <img alt="Platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4">
    <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  </p>
</div>

## Overview

PalServerManager is an unofficial WinUI 3 desktop utility for people who run a local **Palworld Dedicated Server** on Windows and want to manage multiple worlds without mixing Manager state with their server installation. It is not affiliated with or endorsed by Pocketpair.

## Features

- Manage multiple Palworld worlds from one registered server installation.
- Attach an existing active `SaveGames\0` world in place without modifying its saves or game configuration.
- Import external saves through a complete copy-and-verify process; the original external source remains untouched.
- Import unregistered noncanonical saves already inside the managed SaveGames into a verified canonical folder, with safe cleanup of an eligible old container only after registration succeeds.
- Adopt copied canonical parked worlds such as `0 - Slot NNN - <tag>` in place during fresh-PC migration.
- Maintain a separate settings profile for each world.
- Switch worlds safely with identity checks, transaction records, protective backups, and rollback.
- Delete an inactive world only after validating it and creating a verified recovery snapshot.
- Protect updates and other sensitive operations with integrity and recovery checks.
- Monitor server health and players, and save, stop, restart, or schedule server actions.
- Use KeepAwake to prevent the PC from sleeping while the server is running.
- Keep local-only Manager registration and state separate from PalServer data.

## Download

Download the current `PalServerManager-vX.Y.Z-win-x64.zip` from the **[latest GitHub Release](https://github.com/Zhehao-w/pal-server-manager/releases/latest)**. Extract it to a normal application folder separate from PalServer, then run `PalServerManager.WinUI.exe`.

Official release archives are framework-dependent Windows x64 builds produced by GitHub Actions. They are not installers.

## Requirements

- Windows x64
- [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Windows App SDK 2.4 runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Visual C++ Redistributable (x64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)

## Quick Start

1. Download the latest release ZIP and extract it.
2. Run `PalServerManager.WinUI.exe`.
3. Select your server's `PalServer.exe` (or use **Auto Detect**).
4. For an existing server, choose **接入并继续** (“Attach and continue”) to attach its current `SaveGames\0` world in place.
5. Import any additional saves you want to manage.
6. Select a world, then start and manage the server.

If PalServer has not created a world yet, start it once outside the Manager, wait for the first save, stop it, and then recheck in the Manager.

## Migrating an Existing Server

1. Stop PalServer on the old PC, then copy its `PalServer/Saved` data to the new PC.
2. Launch PalServerManager and select the copied `PalServer.exe`.
3. Attach the current `SaveGames\0` world in place.
4. Explicitly import any additional copied canonical folders such as `0 - Slot NNN - <tag>`; when safe, the Manager adopts them in place without requiring prior Manager state.

External worlds use the safe copy-and-verify import path, and their original source remains untouched. An unregistered noncanonical source already inside the current managed SaveGames is also copied and verified into a canonical folder. After successful registration, its old container may be removed only if it is unchanged and contains no extra content; cleanup failure does not invalidate the successfully imported copy. Stop PalServer before importing worlds.

## Data & Safety

Manager state is local to the PC and stored under `%LOCALAPPDATA%\PalServerManager`, separate from the application and PalServer installation. PalServer saves and configuration remain under the server installation, and `SaveGames\0` is always the active world.

Operations fail closed when world identity is ambiguous, Manager state is incomplete, or recovery work remains unresolved. Rescanning is read-only and does not guess which data is authoritative. World switching, rollback, and inactive-world deletion use verified protective snapshots and transaction records.

When a managed settings profile takes precedence, `WorldOption.sav` or `WorldOptions.sav` overrides are backed up and disabled under a reversible name rather than silently deleted. Keep independent backups of important worlds.

## Build and Test

Build on Windows x64 with the .NET 10 SDK. Windows App SDK and other package dependencies are restored through NuGet. From the repository root:

```powershell
dotnet restore .\pal-server-manager.slnx
dotnet build .\pal-server-manager.slnx -c Release --no-restore
dotnet run --project .\PalServerManager.RegressionTests\PalServerManager.RegressionTests.csproj -c Release --no-build
dotnet publish .\PalServerManager.WinUI\PalServerManager.WinUI.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\WinUI
dotnet publish .\PalServer-KeepAwake.Native\PalServer-KeepAwake.Native.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\WinUI\Helpers
```

Regression tests use disposable synthetic server and state roots. Never point them at a real PalServer installation or a user's LocalAppData. Generated `artifacts/` output is excluded from Git.

## License & Asset Notice

PalServerManager source code is available under the [MIT License](LICENSE). The circular project icon is derived from user-supplied artwork; see the existing provenance and third-party IP notes in [docs/ASSETS.md](docs/ASSETS.md). The source license does not grant rights to third-party Palworld characters or trademarks.

Before sharing diagnostics, review [SECURITY.md](SECURITY.md) and remove saves, configuration, passwords, player identities, logs, and Manager runtime state.
