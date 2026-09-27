# PalServerManager

PalServerManager is an unofficial, Windows-only desktop manager for a local **Palworld Dedicated Server**. It is a WinUI 3 / .NET 10 application for people who run a server on their own PC and switch between multiple worlds.

It can discover and register an existing PalServer installation, manage several saves with identity-checked switching, keep separate settings profiles for each world, and monitor server health and players. Server controls include saving, safe shutdown and restart, scheduled and empty-server actions, protective backups and rollback, and a KeepAwake helper that prevents sleep while the server runs. An inactive managed world can be deleted after a verified recovery snapshot is made.

PalServerManager is a community project. It is **not affiliated with or endorsed by Pocketpair**.

## Install

Download `PalServerManager-v2.4.0-win-x64.zip` from [GitHub Releases](https://github.com/Zhehao-w/pal-server-manager/releases), extract it to a normal application folder **separate from PalServer**, and run `PalServerManager.WinUI.exe`. This is a framework-dependent Windows x64 build, not an installer. Install the [x64 .NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [Windows App SDK 2.4 runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads), and [x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) if they are not already present.

On first launch, choose **Auto Detect** or **Select PalServer.exe**. For an existing unmanaged server, attach its current `SaveGames\0` world; the Manager adopts that active world in place without moving it. Other saves can then be imported separately. If PalServer has not yet created a world, start it once outside the Manager, wait for its first save, stop it, then recheck in the Manager.

The application, PalServer installation, and Manager state remain separate. Manager registration and state live under `%LOCALAPPDATA%\PalServerManager`; game saves and configuration remain under the PalServer installation. Attaching an existing installation does not modify `SaveGames` or game configuration. Keep your own independent backups of important worlds.

To add an existing world to an already managed server, stop PalServer and choose **Import Save…** in the save selector. Select a SaveGames-like folder, one save container, or a UID world folder; the Manager scans only that level and lets you select one or more valid worlds. Choose a World Settings source (and a tag for a single import). External and arbitrary/noncanonical sources are copied and verified into a canonical `0 - Slot NNN - <tag>` parked folder before registration; external sources are never changed. An unregistered canonical parked folder that is already an immediate child of the managed SaveGames can instead be explicitly adopted in place, preserving its available slot number and path. **Rescan** remains read-only.

For a fresh-PC migration: (1) stop the old PalServer; (2) copy its `PalServer/Saved` data to the new PC; (3) launch PalServerManager and select `PalServer.exe`; (4) attach the active `SaveGames\0` world in place; and (5) explicitly import any copied canonical parked folders such as `0 - Slot NNN - ...`, which are adopted in place when safe. Arbitrary or external sources continue through the normal copy-and-verify import path. No prior Manager state is required.

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

## Maintainer release process

For releases after v2.4.0, merge a small version-prep PR into `main` and wait for main CI to pass. Then create and push a `vX.Y.Z` tag matching the product and WinUI project versions. GitHub Actions builds and tests that tagged commit on a clean Windows runner, packages the framework-dependent win-x64 ZIP, generates its SHA-256 checksum, and publishes the official GitHub Release. Developer workspaces are not the source of those binaries.

## Privacy, assets, and license

Never publish real saves, configuration, passwords, player identities, logs, or Manager runtime state. See [SECURITY.md](SECURITY.md) before sharing diagnostics. The included circular icon is derived from user-supplied artwork; its provenance and third-party IP considerations are described in [docs/ASSETS.md](docs/ASSETS.md). The project source code is licensed under [MIT](LICENSE); this does not grant rights to third-party Palworld characters or trademarks.
