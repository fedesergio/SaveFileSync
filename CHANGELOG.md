# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [0.2.0] — 2026-11-18 — Phase 1b

### Added
- Merge at folder and file granularity: changes to different saves made on
  different PCs are merged automatically (e.g. RPCS3 `savedata`), and only the
  saves changed on both PCs are conflicts.
- Conflict dialog with a per-save choice; non-conflicting changes of every
  version are kept.
- "Conflict detection" option when adding a game and in game details.
- Full first-run wizard (welcome, shared folder, this PC and library folders,
  startup, summary).
- Activity window, Settings window (PC name, timings, library folders, logs,
  diagnostics export with user name/profile path redacted).
- "Export latest version…" in game details.
- Dev-only `--selftest-ui` switch that opens every window to catch XAML errors.
- Simulation harness with a lagging OneDrive (`LaggyRemoteStore`): out-of-order
  delivery, unreadable cloud-only blobs, offline edits, three PCs converging,
  OneDrive conflict copies, cloned device identity, lost local state.

### Fixed
- OneDrive conflict copies in the repository (`…-DESKTOP.json`) are ignored
  instead of being counted as versions still arriving or breaking the device list.
- "Criss-cross" histories (the same conflict resolved differently on two PCs)
  now ask again instead of silently keeping one of the resolutions.
- Merge and push versions no longer list redundant parents.

## [0.1.0] — 2026-11-18 — Phase 1a

### Added
- Repository format v1 on a OneDrive-synced folder: content-addressed blobs,
  immutable version manifests, one writer per file, temp + rename writes,
  `README.txt` for humans.
- Sync engine: local scan with hash cache, three-way decision against the last
  synced version (unit granularity), push, journaled atomic apply with crash
  recovery, conflict recording, protection against propagating an emptied or
  missing save folder.
- Scheduler: folder watchers + periodic rescans, 2-minute quiet period, game
  process detection (no pull while running, push 15 s after exit, 15-minute
  in-play cap), pause, retry backoff.
- "Newer save available — close the game" notice, also after resume from sleep.
- WPF app: first-run dialog, library, add game (manual folders, filters,
  process), game details / set up on this PC, conflict dialog, tray icon with
  balloons, start with Windows, optional install to `%LOCALAPPDATA%\Programs`.
- Read-only recovery CLI: `list` and `export`; documented format with a
  PowerShell restore script.
- Tests: planner decision table, version graph, globs, two-PC engine
  integration tests and the handheld-sleep scenario.
- CI on GitHub Actions (build, tests, secret scan; release exe on tags).

[0.2.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.2.0
[0.1.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.1.0
