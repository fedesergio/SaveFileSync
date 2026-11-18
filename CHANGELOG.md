# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

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

[0.1.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.1.0
