# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/).

## [0.6.0] — 2026-11-24 — Phase 5

### Added
- **Steam read-only backup**: *Steam* button lists games with saves in Steam's `userdata`
  (Cloud) or in the folders the game database knows (Auto-Cloud, cross-checked with
  `remotecache.vdf`), and adds them in a new `backupOnly` mode.
- Each PC adds versions to its own history chain; it never pulls, merges or conflicts, and an
  emptied folder is never backed up as empty. Same history, retention and cleanup as other games.
- Restore for Steam games only on request, refused while the game or Steam runs, with a
  warning about Steam's Cloud conflict dialog.
- *Steam · backup only* badge and a filter in the library (list and grid).
- Steam games added on one PC are set up on others automatically when their folder is unambiguous.

### Notes
- Nothing is ever written into Steam's folders except by an explicit restore.

## [0.5.0] — 2026-11-24 — Phase 4

### Added
- **Covers** from SteamGridDB and **official names** from IGDB, both optional and
  using your own free keys (*Settings → Covers and official names*, with *Test*).
  Keys are encrypted with DPAPI in `secrets.dat` on each PC and never leave it.
- Automatic matching by Steam id or by name (a different sequel number never
  matches by itself), a **Change cover or match…** window to correct it, and
  *Use the official name*.
- **Grid view** of the library with covers (and a list with small covers),
  remembered per PC; attribution in the footer and in *About*.
- The match is shared with your other PCs in the game's definition; each PC
  downloads the cover itself, with no key.

### Notes
- Only game names and Steam ids are sent to SteamGridDB / IGDB. Only `https`
  images from SteamGridDB are downloaded (max 4 MB).
- Without keys nothing changes and nothing is requested.

## [0.4.0] — 2026-11-24 — Phase 3

### Added
- **History** window per game: every version with the PC that saved it, status
  (latest, on this PC, pinned, archived), and **Restore**, **Pin/Unpin**,
  **Export** for any version.
  - Restore first syncs the game (so nothing is lost), then puts the old saves on
    top as a new version that every PC receives normally. It can be undone.
- **Retention:** the latest N versions per game stay restorable (default 10,
  shared by all PCs, configurable per game and as a default in Settings).
  Older ones are **archived**.
- **Cleanup** once a day (or *Settings → Clean up now*): unused files are first
  marked, and deleted only after 14 days, only if still unused, and never while the
  repository looks half-synced.
- **Devices** window: active / inactive (90 days) / retired PCs, with *Retire*.
  Inactive or retired PCs stop protecting their current version.
- Automatic repair: a PC re-uploads files of its current version that vanished
  from the repository.
- `SaveFileSync.exe list` marks archived versions; `export` refuses them and
  exports the latest version that is really available.
- Tests: retention planner, restore, archived history and merges, far-behind
  PC, pins, retired devices, cleanup safety (grace period, re-referenced files,
  unreadable files, stale marks), repair, scheduled maintenance.

### Changed
- Old versions are **archived, not deleted**: removing a manifest would cut the
  history chain and could invent conflicts (see PLAN.md D14).
- Unit definitions can carry `keepVersions`; the repository gains `pins\`,
  `archive\`, `gc\` and `retired\` folders (older repositories work unchanged).

## [0.3.0] — 2026-11-23 — Phase 2

### Added
- Game database: the Ludusavi manifest is downloaded on demand (conditional
  request with ETag, at most once a week when adding a game, or *Settings →
  Update now*), parsed as a stream and cached as a 5 MB index. Search by name,
  with aliases, and games found on this PC marked.
- *Add game* from the database, from an emulator preset (RPCS3, Ryujinx) or
  manually. Save folders are stored as templates (`<winAppData>/…`, `<base>/…`)
  without absolute paths or user names.
- Automatic setup on other PCs: games added elsewhere are bound when their save
  folder is found in exactly one place (known folders, library folders,
  emulator folders). Existing different saves become a conflict, never an
  overwrite. Can be switched off in Settings.
- Known folders through the shell API (moved Documents folders work); library
  folders in the wizard and Settings.
- Process names suggested from the database and checked against the install folder.
- RPCS3 preset (`vfs.yml` aware, merges per save folder) and Ryujinx preset
  (portable or AppData, one whole unit).
- Notes for games that also use the Windows registry (not synced) and warnings for
  save folders protected by Controlled folder access.
- *Find folders automatically* in game details.
- `--selftest-ui` now exercises the add-game flow and reports WPF binding errors.
- Tests: manifest parser, index and cache, update with ETag, templates, folder
  finder, presets, resolver, and two-PC automatic setup.

### Changed
- Unit definitions can carry `templates` and `source` (older ones still work).

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

[0.4.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.4.0
[0.3.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.3.0
[0.2.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.2.0
[0.1.0]: https://github.com/fedesergio/SaveFileSync/releases/tag/v0.1.0
