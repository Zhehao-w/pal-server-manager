# PalServerManager

PalServerManager is an unofficial, Windows-only desktop manager for a local **Palworld Dedicated Server**. It is a WinUI 3 / .NET 10 application for people who run a server on their own PC and switch between multiple worlds.

It can discover and register an existing PalServer installation, manage several saves with identity-checked switching, keep separate settings profiles for each world, and monitor server health and players. Server controls include saving, safe shutdown and restart, scheduled and empty-server actions, protective backups and rollback, and a KeepAwake helper that prevents sleep while the server runs. An inactive managed world can be deleted after a verified recovery snapshot is made.

PalServerManager is a community project. It is **not affiliated with or endorsed by Pocketpair**.

## Getting started

This repository currently provides source code, not a GitHub Release or downloadable binary. After building (or obtaining a separately distributed application), extract the application to a folder outside your PalServer installation and run `PalServerManager.WinUI.exe`.

1. Choose **Auto Detect** or **Select PalServer.exe** and register the installation.
2. The Manager inspects existing worlds without changing their game saves. Choose **Attach Existing Worlds** to initialize Manager-owned state for the discovered worlds.
3. Open the Manager and select a world to start the server. Existing registered servers reopen after state and world-identity validation.

If PalServer has not yet created a world, start it once outside the Manager, wait for its first save, stop it, then choose **Recheck**. When attaching multiple existing worlds, their settings profiles initially come from the current `PalWorldSettings.ini`; review each profile if the worlds previously used different settings.

The application, PalServer installation, and Manager state remain separate. Manager registration and state live under `%LOCALAPPDATA%\PalServerManager`; game saves and configuration remain under the PalServer installation. Attaching an existing installation does not modify `SaveGames` or game configuration. Keep your own independent backups of important worlds.

To add an existing world to an already managed server, stop PalServer and choose **Import Save…** in the save selector. Select a SaveGames-like folder, one save container, or a UID world folder; the Manager scans only that level and lets you select one or more valid worlds. Choose a World Settings source (and a tag for a single import). The Manager copies each selected world into a verified canonical `0 - Slot NNN - <tag>` parked folder, then registers it. An external source is never changed. **Rescan** remains read-only: it reports unregistered worlds already inside the current SaveGames and registry/profile discrepancies. Importing one of those disk-only worlds uses the same canonical copy pipeline; only after successful registration does the Manager remove its old noncanonical source if it is unchanged and contains no extra data.

PalServerManager does not transfer Palworld saves between PCs. If moving to a new PC, copy the PalServer installation and saves yourself, then install the Manager and select `PalServer.exe`. First attach adopts only the active `SaveGames\0` world in place, with a reserved canonical parked folder for later switching. Use **Import Save…** for any additional worlds. No prior Manager state is required for that first-time attachment.

## World safety

Unknown or duplicate world UIDs, incomplete Manager state, and unresolved operations block startup or switching. Switching and rollback use protective snapshots and transaction records.

Rescan is read-only and never repairs discrepancies. Missing or identity-mismatched registered profiles remain fail-closed at startup; recovery of those cases needs explicit identity verification and is not automated by this feature.

PalServerManager's dedicated-server settings policy uses its per-world profile and `PalWorldSettings.ini`. When an imported copy contains `WorldOption.sav` or `WorldOptions.sav`, the Manager preserves its bytes under a reversible `.disabled-…` name in the managed destination before registration; the external source is untouched. First attach does not change the active world, but the first managed start requires backing up and disabling any active WorldOption file or cancelling startup.

**Delete Save** is available only for a registered inactive world while PalServer is stopped and the identity audit is healthy. The Manager first creates and verifies a complete recovery snapshot of the parked world and its world-specific metadata. To delete the active world, first switch another world to active and stop the server. Retain the snapshot if you may need to recover a deleted world.

## Build and test

Build on Windows x64 with the .NET 10 SDK. Windows App SDK and other package dependencies are restored through NuGet. From the repository root:

```powershell
dotnet restore .\pal-server-manager.slnx
dotnet build .\pal-server-manager.slnx -c Release --no-restore
dotnet run --project .\PalServerManager.RegressionTests\PalServerManager.RegressionTests.csproj -c Release --no-build
dotnet publish .\PalServerManager.WinUI\PalServerManager.WinUI.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\WinUI
dotnet publish .\PalServer-KeepAwake.Native\PalServer-KeepAwake.Native.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\WinUI\Helpers
```

The regression tests use disposable synthetic server and state roots. Do not point tests at a real PalServer or a user's LocalAppData. `artifacts/` is generated output and is excluded from Git.

## Privacy, assets, and license

Never publish real saves, configuration, passwords, player identities, logs, or Manager runtime state. See [SECURITY.md](SECURITY.md) before sharing diagnostics. The included circular icon is derived from user-supplied artwork; its provenance and third-party IP considerations are described in [docs/ASSETS.md](docs/ASSETS.md). The project source code is licensed under [MIT](LICENSE); this does not grant rights to third-party Palworld characters or trademarks.
