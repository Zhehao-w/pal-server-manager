# Repository instructions

Architecture: WinUI 3, .NET 10, Windows App SDK 2.4, Windows x64,
framework-dependent publish. `PalServerManager.Core` must remain UI-free.
Keep source checkout, app installation, Manager state, and PalServer data as
separate path domains. Use `AppPaths`, `ServerPaths`, and `ServerStatePaths`.

Automated development and tests must use explicit synthetic, disposable app,
state, and PalServer roots. Never infer the developer's LocalAppData in tests.
A process hosted by Work/Codex can see a redirected LocalAppData even when its
displayed path resembles the desktop path. Production-state acceptance requires
an independent normal desktop-user process and physical resolved-path
diagnostics; a Work launch is not evidence of desktop production state.

Do not activate Manager state without a matching registered server,
`StateActivation.json`, complete validation, and matching world identities.
Existing-server attachment must not modify SaveGames or game configuration.
Unknown or duplicate world UIDs fail closed. Preserve `PendingOperation`
blocking, atomic JSON writes, transactional switching, complete hash-verified
protective snapshots, and byte-for-byte `*_dps.sav` preservation. Do not use
cross-volume directory moves to store backups.

World deletion is limited to a registered inactive parked slot while PalServer
is stopped, identities pass audit, and no PendingOperation exists. Before any
mutation, preserve a complete hash-verified snapshot of the parked folder and
slot-bound Manager metadata. Record the transaction, roll back on ordinary
failures, and retain the journal and snapshot if recovery cannot be verified.
Never delete active SaveGames\\0 through this feature.

Discovery is read-only; registration/import is explicit; reconciliation never
guesses authority. Existing-world import registers a complete, unique parked
world in place only after fresh validation and an explicit per-world settings
source choice. It must not silently change global server settings or save data.
Keep missing/mismatched registered profiles fail-closed rather than weakening
startup authority to make a repair UI reachable.

Do not commit real saves, game Config, Manager runtime JSON, passwords, player
identities, world UIDs, logs, backups, or local recovery evidence. Review the
circular icon's provenance and redistribution rights, package notices, privacy,
and source license before distributing binaries. Build and run focused synthetic
regressions; do not retest unchanged UI exhaustively. Use Computer Use only with
explicit current-task authorization.
