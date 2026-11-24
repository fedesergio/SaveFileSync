# SaveFileSync — Plan

Status: **approved** (2026-11-18) · Implemented: phase 1a (`v0.1.0`), phase 1b (`v0.2.0`), phase 2 (`v0.3.0`), phase 3 (`v0.4.0`), phase 4 (`v0.5.0`), phase 5 (`v0.6.0`)

SaveFileSync is a personal Windows tool that keeps game saves in sync across
several PCs (Windows 11 desktop, ROG Xbox Ally X, future machines) using the
user's personal OneDrive as the transport. Targets: DRM-free PC games and
emulators (RPCS3, Ryujinx). Steam games are out of scope for sync (Steam Cloud
covers them); a read-only Steam *backup* is a later phase.

---

## 1. Decisions taken

| # | Topic | Decision |
|---|---|---|
| D1 | Build vs reuse | **Own app.** Reuse only the Ludusavi manifest (format + data), downloaded at runtime. Ludusavi is a backup tool: it has no background/tray mode, no per-device sync state, and no conflict UI ("warns you … and leaves the cloud data alone"). Those are the core of this project. |
| D2 | OneDrive access | **The folder that the OneDrive client syncs**, not Microsoft Graph. No app registration and no tokens. All remote I/O goes through an `IRemoteStore` interface, so a Graph backend can be added later. |
| D3 | Sync timing | **Hybrid, with no strict launch-time sync.** Push after a quiet period. When the game/emulator process is known, wait for it to exit. Pull eagerly whenever the unit is idle. A launch wrapper is not required. |
| D4 | Stack | **.NET 10 (LTS) + WPF**, shipped as a self-contained single-file `win-x64` exe. |
| D5 | Scope of games | DRM-free games and emulators (RPCS3, Ryujinx). Xbox/Game Pass games are excluded because they have their own cloud. |
| D6 | Emulator sync granularity | One **sync unit** per emulator save area, with a **three-way merge**. Each unit sets how finely changes are compared: whole unit, folder, or file. |
| D7 | Language | Everything in English: GUI, code, comments, commits, docs. |
| D8 | Metadata (MVP+) | SteamGridDB for covers, plus IGDB (optional) for official names. The user supplies the API keys. |
| D9 | Defaults | 2-minute quiet period. Push 15 s after the process exits. Pause from the tray. 10 versions per unit. A first setup that finds data on both sides is treated as a conflict. Single exe in `%LOCALAPPDATA%\Programs\SaveFileSync`, autostart via `HKCU\…\Run`, manual updates. |
| D10 | Git | Private repo `github.com/fedesergio/SaveFileSync`, branch `main`. |
| D11 | Phasing | Phase 1 is split in two. **1a:** `unit` granularity only, essential GUI. **1b:** folder/file merge, full wizard, Activity screen, full simulation harness. |
| D12 | Recovery | Saves can be recovered without the sync engine from phase 1a: a read-only `export` CLI, a `README.txt` in the repository, and a documented format (§4.1). |
| D13 | Handheld sleep | A "newer save available — close the game" notice when a running game's save is outdated, also after resume from sleep (§6.6). |
| D14 | Old versions | **Archive, never delete manifests**: archived versions keep the history connected for merges; only their files are cleaned up, in two steps with a 14-day wait (§11, phase 3). |

| D15 | Metadata match | The match (SteamGridDB game, chosen cover URL, IGDB id, official name) is stored **in the unit definition**, so a correction made once applies on every PC. Covers are downloaded and cached **per PC** from the public URL (no key needed there); keys stay on each PC, DPAPI-encrypted. Only game names and Steam ids are sent to the services (§11, phase 4). |

---

## 2. Architecture

### 2.1 Process model

One per-user process, `SaveFileSync.exe`, started at logon through
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--background`. It
needs no admin rights and no Windows service.

```
┌───────────────────────────── SaveFileSync.exe ─────────────────────────────┐
│  Tray icon (H.NotifyIcon)      WPF windows (created on demand, closed      │
│  menu · status · balloons      when hidden to free memory)                 │
│            │                              │                                │
│            └──────────── App layer (MVVM, CommunityToolkit.Mvvm) ──────────┤
│                                           │                                │
│  ┌────────────────────────── Sync engine (Core) ────────────────────────┐  │
│  │ Scheduler ─ per-unit state machine (Idle/Dirty/Busy/Syncing/…)       │  │
│  │ Scanner   ─ snapshot local files (size/mtime → hash only if changed) │  │
│  │ Planner   ─ 3-way compare base/local/remote → push/pull/merge/conflict│ │
│  │ Applier   ─ journaled, atomic local writes                           │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
│        │                 │                    │                 │          │
│  IRemoteStore      ILocalState          IPathResolver     IProcessMonitor  │
│  (OneDrive folder) (SQLite in           (manifest, known  (polling,        │
│                     %LOCALAPPDATA%)      folders, roots)   no admin)       │
└────────────────────────────────────────────────────────────────────────────┘
```

- A small hand-written composition root (`AppHost`) wires config, state,
  repository, engine and service. The Generic Host was planned but is not used:
  with a single background worker it added memory and startup cost for no benefit.
  Logging uses `Microsoft.Extensions.Logging` abstractions backed by Serilog.
- A **single-instance** named mutex keeps one engine per user. Launching the exe
  again only opens the main window of the running instance (via a named pipe).
- **Background memory target:** under about 80 MB working set, with no window
  open. The parsed manifest index is loaded only when it is needed.
  Measured at v0.1.0 (release, no window): 97 MB working set, 31 MB private.

### 2.2 Solution layout

```
SaveFileSync.slnx
Directory.Build.props            # common settings, nullable, analyzers, MinVer
src/
  SaveFileSync.Core/             # domain model, planner, merge, scheduler (no Win32, no WPF)
  SaveFileSync.Repository/       # OneDrive-folder repository format v1 (IRemoteStore), blobs, GC
  SaveFileSync.Manifest/         # Ludusavi manifest download/index, path patterns, emulator presets
  SaveFileSync.Windows/          # known folders, OneDrive detection/pinning, process monitor,
                                 # autostart, DPAPI, registry, Controlled Folder Access checks
  SaveFileSync.Metadata/         # SteamGridDB / IGDB clients, name matching, cover cache (phase 4)
  SaveFileSync.Steam/            # Steam library/userdata discovery, VDF parser, backup finder (phase 5)
  SaveFileSync.App/              # WPF app, tray, views, composition root
tests/
  SaveFileSync.Core.Tests/
  SaveFileSync.Repository.Tests/
  SaveFileSync.Manifest.Tests/
  SaveFileSync.Metadata.Tests/   # API clients against fake HTTP, DPAPI store, matching
  SaveFileSync.Steam.Tests/      # VDF, library discovery, backup finder on synthetic Steam folders
  SaveFileSync.Simulation.Tests/ # two-PC simulation (see §10)
```

### 2.3 Libraries (all MIT/Apache)

| Purpose | Library |
|---|---|
| MVVM | CommunityToolkit.Mvvm |
| Fluent styling | WPF-UI (lepoco/wpfui) |
| Tray icon | WinForms `NotifyIcon` (built into .NET; icons drawn at runtime) instead of H.NotifyIcon: one dependency less |
| Logging abstractions | Microsoft.Extensions.Logging.Abstractions |
| Log files | Serilog (rolling file sink) |
| Local state | Microsoft.Data.Sqlite |
| Manifest YAML | YamlDotNet |
| Tests | xUnit, plain assertions or Shouldly (**not** FluentAssertions ≥ 8, whose licence is commercial) |
| Versioning | MinVer (version read from git tags) |

Prerequisite: install the **.NET 10 SDK**. Only 8.0.200 is installed today, and
.NET 8 went out of support on 2026-11-10.

### 2.4 Distribution

`dotnet publish src/SaveFileSync.App -c Release` (the project sets `win-x64`,
self-contained, single-file, native libraries included).

- The output is about 160 MB. Single-file compression would bring it to about
  70 MB, but it was measured to raise background memory from 31 to 113 MB
  private (97 vs 244 MB working set), so it is disabled.
- On first run the exe offers to copy itself to
  `%LOCALAPPDATA%\Programs\SaveFileSync\` and to register autostart.
- Updates are manual: replace the exe. "Exit" in the tray releases the file.
- Code signing: none. A locally built exe carries no Mark-of-the-Web, so
  SmartScreen does not warn.

---

## 3. Core concepts

- **Device:** one PC. It has a random `deviceId` (GUID) generated on first run
  and stored only on that PC, a user-chosen display name, and an 8-hex-character
  short id.
- **Sync unit ("game"):** what gets synced as one entity. It can be a PC game, an
  emulator save area, or (phase 5) a Steam backup. A unit has:
  - one or more **locations**: logical folders such as `savedata` or `save-root`.
    Their absolute path is resolved on each device, never stored in the repository;
  - include/exclude globs per location;
  - process names, used to detect when the game is running;
  - a **merge granularity**: `unit` | `folder` | `file` (see §6.4);
  - a **mode**: `sync` (two-way) | `backupOnly` (phase 5, Steam).
- **Binding:** the per-device mapping *location → absolute path*, either resolved
  automatically or entered by hand. A unit that exists in the repository but has
  no binding on this PC is shown as "Available — set up on this PC".
- **Version:** an immutable snapshot of a unit, made of a file list with hashes.
  Versions form a **DAG**: each version has one parent, or two for a merge.
- **Base:** for each (device, unit), the version that the device's local files
  last matched exactly. It is the common ancestor used in every three-way
  decision.

---

## 4. Repository structure on OneDrive

The user picks one folder inside OneDrive, for example
`%OneDrive%\SaveFileSync\`. The tool owns everything inside it.

```
SaveFileSync\
  repo.json                              # { format: 1, repoId, createdUtc }
  devices\
    <deviceId>.json                      # name, short id, app version, lastSeenUtc  (writer: that device)
  units\
    <unitId>\                            # e.g. "rpcs3-k7m2qa" (slug + random suffix)
      def\
        <lamport:000000>-<devShort>.json # unit definition revisions; highest (lamport, devShort) wins
      versions\
        <devShort>-<seq:000000>.json     # immutable version manifests (writer: creating device)
      heads\
        <deviceId>.json                  # { versionId, updatedUtc }: device's current base (writer: that device)
      pins\
        <deviceId>.json                  # versions this device pinned (phase 3)
      archive\
        <deviceId>.json                  # versions whose files were removed on purpose (phase 3)
  gc\
    <deviceId>.json                      # blobs this device found unused, and since when (phase 3)
  retired\
    <deviceId>.json                      # written by the PC that retired it (phase 3)
  blobs\
    <sha256[0..2]>\<sha256>              # content-addressed file contents, immutable
```

### Rules that make it safe on OneDrive

1. **One writer per file.**
   - Blobs and versions are immutable, and their names are unique: a content
     hash, or a device-prefixed sequence number.
   - Mutable files (`devices/<id>.json`, `heads/<id>.json`) are written only by
     the device they are named after.
   - Unit definitions are append-only revisions.

   So OneDrive never sees two PCs editing the same file, and it has no reason to
   create its own `name-PCNAME.ext` conflict copies.

2. **Atomic publish.** Every file is written as `<name>.tmp-<devShort>-<rand>`,
   flushed, then renamed. Readers ignore anything that matches `*.tmp-*`.

3. **Commit ordering.** A push writes blobs first, then the version manifest,
   then the head. A reader treats a version as **complete** only when every blob
   it lists exists, is hydrated, and has the right SHA-256. Incomplete versions
   are ignored and retried, so an upload interrupted half-way is never applied.

4. **Original filenames never become OneDrive filenames.** Contents live in
   `blobs/` under hash names. This avoids OneDrive's name restrictions
   (`" * : < > ? | `, reserved names, trailing dots and spaces, `desktop.ini`,
   path length) and leaks no folder layout.

5. **No personal data.** The repository holds no absolute paths and no Windows
   usernames. It contains only logical location ids, relative paths inside save
   folders, and the device names the user chose.

### Example version manifest

```json
{
  "format": 1,
  "unitId": "rpcs3-k7m2qa",
  "versionId": "a1b2c3d4-000042",
  "deviceId": "a1b2c3d4-5e6f-…",
  "seq": 42,
  "parents": ["e5f6a7b8-000017"],
  "kind": "normal",                       // normal | merge | resolve | restore | initial
  "createdUtc": "2026-11-18T20:31:05Z",   // device clock: display only, never used for ordering
  "files": [
    { "loc": "savedata", "path": "BLUS30443-AUTOSAVE/SAVEDATA.DAT",
      "size": 123456, "sha256": "9f…", "mtimeUtc": "2026-11-18T20:29:47Z" }
  ],
  "totalSize": 123456,
  "note": null
}
```

Deletions need no tombstones: a file missing from a version has been deleted in
that version.

Blob compression: phase 1 stores blobs raw. The format reserves a `.br` suffix
for Brotli-compressed blobs, and readers will accept both (open decision O1).

### 4.1 Recovering saves without the app (from phase 1a)

Blobs have hash names, so the repository cannot be browsed by hand. Three
safeguards make sure the saves can always be found and rebuilt, even if the app
has a bug or does not start:

1. **Recovery CLI**, built into the same exe:
   - `SaveFileSync.exe list [--repo <path>]` lists units (name, id) and their
     versions (id, device, time, size, tip or not).
   - `SaveFileSync.exe export <unit name or id> <target folder> [--version <id>] [--repo <path>]`
     writes the files of a version to `<target>\<location>\<relative path>`,
     with the original mtimes.
     - Default: the latest tip. If there are several tips, each one goes to its
       own subfolder named after its device.

   Both commands only **read** the repository. They do not need the app's
   config or state and do not start the sync engine. With `--repo` they also
   work on a copy of the folder, or on a PC where the app was never set up.
   They check every blob's SHA-256 and report the missing or corrupt ones.

   Technical note: the exe is a WPF (`WinExe`) app. For CLI commands it attaches
   to the parent console (`AttachConsole`). In `cmd` the prompt may come back
   before the output ends. The README recommends
   `start /wait SaveFileSync.exe export …`, or running it from PowerShell.
2. **`README.txt` at the repository root**, written by the tool. It explains in
   plain text what the folder is, that it must not be edited by hand, and the
   two commands above.
3. **The format is documented** in `docs/repository-format.md` in the git
   repository, and it is simple: JSON manifests + SHA-256-named blobs. A
   20-line PowerShell script could rebuild a version even without the exe. The
   documentation includes such a script as a last resort.

Each unit's definition (`def\*.json`) holds the game's display name, so a
unit can be found by name.

---

## 5. Local state per device (never in the repository)

Everything device-specific lives in `%LOCALAPPDATA%\SaveFileSync\`. LocalAppData
is used instead of Roaming because none of this may move to another machine.

| File | Content |
|---|---|
| `device.json` | `deviceId`, short id, a machine fingerprint (hash of the MachineGuid) used to detect a cloned identity |
| `config.json` | repository path, device name, library roots (e.g. `C:\Games`), per-unit bindings, quiet periods, history size, UI preferences |
| `secrets.dat` | metadata API keys, encrypted with DPAPI (`CurrentUser`) |
| `state.db` (SQLite) | sync state, see below |
| `cache\` | Ludusavi manifest + ETag, parsed index, covers (phase 4) |
| `logs\` | Serilog rolling logs, 14 days |

`state.db` tables:

- `unit_state(unitId, baseVersionId, status, lastPushUtc, lastPullUtc, lastError, conflictJson)`
- `file_index(unitId, loc, path, size, mtimeUtc, sha256)`: local snapshot at
  the base, used for fast change detection.
- `version_cache(unitId, versionId, parentsJson, complete, filesJson)`: parsed
  remote versions, so the DAG can be walked without re-reading OneDrive.
- `apply_journal(unitId, targetVersionId, opsJson, startedUtc)`: present only
  while a pull is being applied. Used to resume or roll back after a crash.
- `seq(deviceId, nextSeq)`.

Dev-only switch: `--profile <name>` redirects all of the above to
`%LOCALAPPDATA%\SaveFileSync\profiles\<name>\` and changes the mutex name. This
lets two engines run on one PC (§10.3).

---

## 6. Sync algorithm

### 6.1 Inputs per unit

- **B**: base version (local state).
- **L**: current local snapshot. Scan bound locations, apply the
  include/exclude globs, and compare (size, mtime) with `file_index`. Only files
  that differ are re-hashed with SHA-256.
- **Remote DAG**: all complete versions of the unit. **Tips** are versions that
  are not the parent of any other version.

Ordering comes **only** from the DAG and the per-device sequence numbers, never
from timestamps. Wrong or unsynchronised clocks therefore cannot cause a wrong
decision. Timestamps are shown in the GUI, labelled with the device that
reported them.

### 6.2 Decision table

```
localChanged  = content(L) ≠ content(B)
remoteChanged = there is a tip T ≠ B (with content(T) ≠ content(B))
```

| localChanged | remoteChanged | Action |
|---|---|---|
| no | no | nothing |
| yes | no | **Push**: new version V (parents [B]); B ← V |
| no | yes, one tip T and B is an ancestor of T | **Pull** (fast-forward): apply T; B ← T |
| no | yes, several tips | **Merge the remote tips** (§6.4) into M, apply M, push M |
| yes | yes | **Push L as V first** (parents [B]), so local work is preserved in the repository whatever happens next. Then three-way merge V and the tip(s) with B as base (§6.4): no conflicts → merge version M, apply, push; conflicts → unit goes to **Conflict** |

Short-circuits:
- If content(L) = content(T), set B ← T and stop.
- A merge whose inputs have identical content is trivially clean. So if two
  devices merge the same tips at the same time, the result still converges.

Unknown base (B was pruned, or this is the first setup on this PC):
- If L is empty → pull the tip.
- If content(L) equals a known version → adopt that version as B.
- Otherwise → **initial conflict** (the user chooses).

### 6.3 Push and pull mechanics

**Push**
1. Read each changed file without locking it: `FileShare.ReadWrite | Delete`.
   Copy it into a blob temp file while hashing it. Never open a game's files
   with exclusive sharing: that could make the game's own save fail.
2. Re-check size and mtime after copying. If the file changed, abort and go back
   to Dirty, because the game is still writing.
3. Rename blob temps into place, skipping blobs that already exist.
4. Write the version manifest (temp + rename), then `heads/<deviceId>.json`.
5. Update `unit_state` and `file_index` in one SQLite transaction.

**Pull / apply**
1. Preconditions:
   - the unit is not Busy (its process is not running);
   - L is still equal to B (quick stat check);
   - every blob of the target is present, hydrated and verified.
2. Write `apply_journal`.
3. For each file to write: copy the blob to `<target>.sfs-tmp` in the same
   directory, flush, `File.Move(overwrite: true)`, then restore the version's
   `mtimeUtc`. Then delete the files that the target version no longer has.
4. Commit B ← target and the new `file_index`, then clear the journal.
5. Crash recovery at startup: if the journal is present, delete the
   `*.sfs-tmp` files and re-apply the target, which is idempotent. If that fails,
   re-apply B, which is always in the repository.

Nothing needs a separate local backup before overwriting: the overwritten
content is B, and B is already in the repository.

### 6.4 Three-way merge and granularity

Files are grouped by the unit's merge granularity:

| Granularity | Group key | Default for |
|---|---|---|
| `unit` | the whole unit | generic PC games, **Ryujinx** (save index must stay consistent with save folders) |
| `folder` | first path segment under a location | **RPCS3** (`savedata/<SAVE_DIR>` is self-contained) |
| `file` | each file | games with independent slot files (user opt-in) |

For each group, compare the base, local and remote versions of the group:
- the group changed on one side only → take that side;
- it changed on both sides to the same content → take it;
- it changed on both sides to different content → **conflict group**.

Modify-versus-delete counts as a conflict.

Phase 1a implements only `unit`; `folder` and `file` arrive in phase 1b.
Until then, a unit set to a finer granularity is treated as `unit`.

**Conflict state:**
- the unit stops automatic push and pull;
- the tray shows the attention icon and a balloon notification;
- the GUI shows the conflict dialog (§8);
- the resolution creates a `resolve` version with parents [V, T] that holds the
  chosen side for each group;
- both originals stay in the history.

### 6.5 Scheduling (when sync runs)

Per-unit state machine:

```
          file change / periodic scan
 Idle ───────────────────────────────▶ Dirty ──quiet period elapsed──▶ Syncing ──▶ Idle
  ▲  ▲                                   │                                 │
  │  └──── process exit + 15 s ─── Busy ◀┘ process starts                  ├──▶ Conflict (user)
  │                                (no pull; push only after the           └──▶ Error (retry w/ backoff)
  │                                 15-min in-play cap)
  └── remote change detected & not Busy & not Dirty → pull
 Paused (global or per unit) blocks everything except scans.
```

- **Local change detection:**
  - a `FileSystemWatcher` on bound locations, used only as a trigger, with
    debouncing;
  - a full rescan every 15 minutes, because the watcher drops events;
  - a scan at startup and on resume from sleep.
- **Remote change detection:**
  - a `FileSystemWatcher` on `units\*\versions`;
  - a poll every 2 minutes as a safety net (the directory listings are small).
- **Quiet period:** 2 minutes without local writes before a push. Autosaves
  reset the timer, so a game that saves often is never copied repeatedly.
- **Process awareness:**
  - polls `Process.GetProcesses()` every 5 seconds, matching the unit's process
    names and, optionally, an executable path prefix. This needs no admin rights;
    WMI process-start traces would need them;
  - while the process runs, the unit is Busy: **no pull**, and push waits for
    the process to exit (+15 s);
  - **in-play cap:** if the process stays open but nothing has changed for
    15 minutes, push anyway. This covers an emulator left open all day.
- **Eager pull:** remote changes are applied as soon as the unit is Idle, so
  the PC is usually already up to date when you start playing. There is no
  launch-time gate (D3).
- **Pause:** from the tray menu, either "until resumed" or "for 1 hour", for all
  units or for one.

### 6.6 Stale while running (handheld sleep)

On the Ally X it is common to put the device to sleep with a game still open,
sometimes for days. The process then stays "running" the whole time, so
pulling is never allowed.

Example: you play on the desktop in the meantime, then wake the Ally and keep
playing from the old save. This guarantees a conflict when the game is closed.
No data is lost (both versions are preserved, §6.2), but the conflict is easy
to avoid with a warning.

- **Stale detection:** a unit is **Stale** when it is Busy (process running)
  and the remote DAG has a tip that this device has not yet applied (a remote
  change).
  - Checked at every remote poll.
  - Checked **right after resume from sleep**: `SystemEvents.PowerModeChanged`
    → `Resume` triggers an immediate repository poll. The poll waits up to
    about 60 s for the network and for OneDrive to catch up.
- **Notice:** a balloon reads *"A newer save for <Game> is available from
  Desktop (today 21:40). Close the game to update this PC before playing."* It
  appears:
  - once per remote version;
  - again at every resume while the unit is still Stale.

  The status chip shows *Game running · newer save on Desktop*, and the tray
  icon is in the attention state.
- **When the game closes:**
  - if nothing changed locally, the pending version is pulled normally and no
    conflict occurs;
  - if the game wrote in the meantime, the unit goes to Conflict. The conflict
    dialog then says explicitly: *"The remote version was created while <Game>
    was open on this PC (since <time>)."*
- **Limitation:** with a full-screen game, Windows may hide balloons ("Do not
  disturb" while gaming). The persistent tray state and the status in the GUI
  still show it. Toast notifications (O6) might improve this.

---

## 7. Path resolution across PCs

### 7.1 Resolver

Paths in the manifest and in presets are templates. They are resolved per device.

| Token | Resolution |
|---|---|
| `<home>` | `SHGetKnownFolderPath(FOLDERID_Profile)` |
| `<winAppData>` | `FOLDERID_RoamingAppData` |
| `<winLocalAppData>` | `FOLDERID_LocalAppData` |
| `<winLocalAppDataLow>` | `FOLDERID_LocalAppDataLow` |
| `<winDocuments>` | `FOLDERID_Documents`. This follows the redirect when Documents has been moved into OneDrive or to another drive |
| `<winPublic>`, `<winProgramData>`, `<winDir>` | the matching known folders |
| `<root>` | each library root configured on this PC (e.g. `C:\Games`, `D:\Games`) |
| `<game>` / `<base>` | `installDir` from the manifest (or the game name), searched under each root; `<base>` = `<root>/<game>` |
| `<storeUserId>`, `<storeGameId>` | treated as `*` (DRM-free) |
| `<osUserName>` | the current Windows user name |

The resolver uses **known-folder APIs, not environment-variable string
concatenation**, so a moved Documents folder and localized folder names work.
Results are globbed, and the matches become the binding.

### 7.2 Special cases

- **Save folder inside OneDrive.** If a resolved path lies under a OneDrive root
  (read from `HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`), OneDrive
  already syncs those files directly. Syncing them again would create OneDrive
  conflict copies of game files. The GUI warns and offers two choices:
  - *backup-only* (history only, no pull);
  - *skip*.
- **Different drives or install folders:** handled by per-device roots and
  bindings.
- **Portable emulators:** the location's path is anchored on the emulator
  folder. It is detected from the running process path the first time the
  emulator is seen, or picked by hand once per PC.
- **Registry saves:** the manifest marks them under `registry:`.
  - Phases 1–5: the unit shows a "registry data not synced" badge, so the user
    knows the sync is incomplete.
  - Phase 6, if needed: export the `HKCU` keys to a canonical JSON blob
    (included in the version as a pseudo-file `registry/<key>.json`). Detect
    changes by hashing the export after the process exits. Restore by writing
    `HKCU` only; `HKLM` entries are reported as unsupported because they would
    need admin rights.
- **Controlled Folder Access** (Windows Security) can block writes to Documents
  by unknown apps. When `UnauthorizedAccessException` is raised in a protected
  folder, the app shows a dedicated error that explains how to allow the app.
- **Long paths** are handled by .NET's long-path support. Repository names stay
  short (hash names).

### 7.3 Ludusavi manifest

- **Source:** `https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/refs/heads/data/data/manifest.yaml`
  (17 MB, 53,575 entries, served with an `ETag`).
  - Downloaded with `If-None-Match` (ETag). It is only needed when a game is
    added, so it is checked **on demand**: when the *Add game* window opens and
    the last check is older than 7 days, or from *Settings → Update now*. (The
    first plan said "once a day in the background"; nothing needs the manifest
    in the background, and 17 MB a day on a handheld is wasteful.)
  - Parsed as a stream, entry by entry (4 s, 64 MB peak), and reduced to a
    compact JSON index in `%LOCALAPPDATA%\SaveFileSync\cache\` (5 MB). The YAML
    itself is deleted after parsing. The index is loaded (0.2 s) only while the
    *Add game* window is open: background memory is unchanged.
  - Kept per game: name and aliases, Windows file paths tagged `save`/`config`,
    registry keys, `installDir`, `launch` executables, Steam/GOG ids. Of the
    53,575 entries, 14,512 have Windows data.
  - Filtering rules (checked against the real data): paths for other OSes and
    `<xdg…>` tokens are dropped; entries that apply only to the Microsoft store
    (Game Pass) are dropped; other `store` constraints are ignored, because a
    DRM-free copy usually keeps its saves where the Steam/GOG copy does; paths
    under `<root>/userdata` and `<root>/steamapps` are Steam's own and are kept
    with a flag for phase 5 but not used here.
  - The real data has `**` in about 2,000 paths, 1,100 aliases, and names that
    differ only by case; the parser handles all of them.
- **Licence:** the manifest *code* is MIT. The repository does not declare a
  licence for the *data*, which is "compiled from data on PCGamingWiki".
  PCGamingWiki content is believed to be CC BY-NC-SA 3.0. This could not be
  verified: the wiki's licence page returned 403 to automated fetches.
  Consequences:
  - personal, non-commercial use only;
  - the manifest is **never committed or bundled**, only downloaded at runtime;
  - the About screen credits PCGamingWiki and Ludusavi.

  Open decision O2 covers the verification.
- **How a manifest game becomes a unit.** Each manifest path is split at its
  first wildcard: the part before it is the location's **folder**, the rest
  becomes include globs (so a location always has exactly one folder and the
  same logical shape on every PC). `<storeUserId>` counts as a wildcard. The
  unit stores, per location, the folder as a **template** (`<winAppData>/Game`,
  `<base>/saves`) plus `source.installDirs`; it never stores an absolute path or
  a user name. On another PC the templates are expanded with that PC's known
  folders and library roots, and the location is bound automatically if it
  resolves to exactly one existing folder (§7.4).
- **Emulator presets:** written by us in code (`EmulatorPresets`), not taken
  from the manifest, which does not describe per-game emulator saves. A preset
  unit stores only `source.presetId` and logical locations; each PC finds the
  emulator folder itself (saved setting, a running instance, library folders and
  one level below them).

| Preset | Location (found per PC) | Includes | Merge | Process |
|---|---|---|---|---|
| RPCS3 | `savedata` = `<dev_hdd0>\home\<user>\savedata`; `dev_hdd0` follows `config\vfs.yml` (`/dev_hdd0/`, `$(EmulatorDir)`), `<user>` is `00000001` or the only user folder | everything | `folder` (each `<SAVE_DIR>` is one save) | `rpcs3` |
| Ryujinx | `data` = `<exe dir>\portable` if it exists, else `%APPDATA%\Ryujinx` | `bis/user/save/**`, `bis/user/saveMeta/**`, `bis/system/save/8000000000000000/**` (save index), `system/Profiles.json` | `unit` | `ryujinx`, `ryujinx.ava` |

RPCS3's location is `…\savedata` (not `dev_hdd0`) on purpose: the merge groups
files by their first folder, which must be the save directory. Trophies are not
synced (O3, decided: they can be added as a manual location).

The Ryujinx layout is an **assumption to verify with real data** (A6). Its save
folders are numbered by an index, not by title, so the whole save area plus the
index must be synced as one atomic unit. The first setup must seed from one PC.

### 7.4 Automatic setup on another PC

Every few minutes, for each game that exists in the repository but is not bound
on this PC, the service asks the resolver to find its folders. It binds the game
only if **every** location resolves to **exactly one existing** folder (zero or
several candidates leave it to the user). Binding does not bypass the sync
rules: if saves already exist there and differ from the repository, the first
sync is an initial conflict to choose from, never a silent overwrite. It can be
switched off in *Settings*. Games installed under a library root (e.g.
`C:\Games`) need that folder configured on each PC, since `<base>` is
`<library root>\<installDir>`.

---

## 8. GUI (WPF + WPF-UI, English)

1. **First-run wizard**
   - Welcome.
   - Choose the repository folder. The wizard checks that it is inside the
     detected OneDrive folder, offers to create `SaveFileSync\`, and pins it
     ("Always keep on this device").
   - Device name (defaults to the computer name).
   - Library roots (optional).
   - Start with Windows (on by default).
   - Done.
2. **Library (main window)**
   - A list (phase 1), then a grid with covers (phase 4).
   - Each unit shows its name, kind icon (game / emulator / Steam), a status chip
     (*Synced · Waiting 1:23 · Game running · Game running · newer save on
     <device> · Syncing · Conflict · Error · Paused · Not set up on this PC*),
     the last sync time, and the device of the last change.
   - Filters: All, Games, Emulators, Steam backups, Needs attention.
3. **Add game**
   - A tab per source: *Search* (manifest, games detected on this PC first),
     *Emulator presets*, *Manual*.
   - *Manual* takes one or more folders, include/exclude globs, process names,
     and the merge granularity.
   - Before confirming, a preview shows the files found and their total size.
4. **Unit details**
   - Locations and their resolved paths on this PC (editable binding), process
     names, merge granularity, mode.
   - Actions: *Sync now*, *Pause*, *Open folder*, *Remove from sync* (keep or
     delete the repository data).
   - From phase 1b: *Export to folder* (latest version).
   - From phase 3: a **History** tab with versions (device, time, size, kind)
     and *Restore*, *Pin*, *Export to folder* for any version.
5. **Set up on this PC** (for units already in the repository)
   - Automatic resolution, or a path picker.
   - If local data exists and differs from the repository, the initial-conflict
     dialog opens.
6. **Conflict dialog**
   - Two columns, **This PC** and **Remote**. Each shows the device name, the
     time (as reported by that device), the total size, the file count, and the
     list of conflicting groups/files with their size and modified time.
   - Per-group choice: *Keep this PC* / *Keep remote*, plus *Apply to all*.
   - *Decide later* keeps the unit in Conflict.
   - A note says that both versions stay in the history.
7. **Settings**
   - Repository folder, device name, autostart, quiet period, in-play cap,
     versions to keep, library roots.
   - Manifest status, with an *Update now* action.
   - API keys (phase 4).
   - *Open logs*, and *Export diagnostics* (redacted: the username is replaced
     by `<user>`).
8. **Devices:** the devices in the repository, with last seen time and app
   version. *Retire* releases a device's heads so its old versions can be pruned.
9. **Activity:** the most recent sync operations and errors.
10. **Tray**
    - Icon states: OK, syncing, attention, paused.
    - Menu: *Open*, *Sync all now*, *Pause (1 h / until resumed)*, *Resume*,
      *Exit*.
    - Balloon notifications for conflicts, persistent errors, and "newer save
      available — close the game" (§6.6).
11. **About:** version, licences, and credits to the Ludusavi manifest,
    PCGamingWiki, SteamGridDB and IGDB.

---

## 9. Error handling

| Situation | Behaviour |
|---|---|
| Repository folder missing / OneDrive not running / account unlinked | All units are paused; the status reads "Repository unavailable"; recheck every minute |
| Blob is cloud-only (Files On-Demand) | Reading it triggers hydration, with a timeout. If that fails, the version is "incomplete, retrying". The repository folder is pinned (`attrib +P -U /S /D`) so this is rare. The pin is re-checked at startup |
| Version manifest present, blobs missing (sync lag) | Version ignored until complete; warning after 24 h |
| Hash mismatch | Treated as still arriving; after repeated failures the unit goes to Error with details |
| OneDrive conflict copy found in the repository | Should not happen (single writer). If it does: <br>• `heads`/`devices` copies → removed and logged; <br>• anything else → logged, and listed in the diagnostics |
| Two PCs with the same `deviceId` (cloned profile/image) | Detected through the fingerprint in `devices/<id>.json`. That device stops syncing and asks to regenerate its identity |
| Game file locked / changing during push | Abort without side effects; back to Dirty |
| Crash or power loss during push | Orphan blobs are removed later by the cleanup, after the 14-day grace period; nothing points to them |
| Blob of this PC's current version vanished from the repository (cleaned while offline) | `RepairMissingBlobs` uploads it again from the local files; other PCs then complete the version |
| Cleanup finds an unreadable version file or a write in progress | The whole cleanup is skipped and retried the next day |
| Crash or power loss during pull | The apply journal resumes or rolls back at startup (§6.3) |
| Access denied (Controlled Folder Access, permissions) | Unit goes to Error with an explanation of the specific cause |
| Disk full (local or OneDrive quota) | Unit goes to Error; retries back off up to 1 h; tray attention icon |
| Manifest download fails | Use the cached copy. With no cache, only manual and preset units are available |
| Unknown repository `format` (newer app on another PC) | Read-only mode, with a prompt to update the app |
| Game left running (e.g. handheld asleep for days) while another PC pushed | Unit is Stale: no pull, a "newer save available — close the game" notice, also after resume (§6.6) |
| App does not start or has a bug | Saves can be recovered with the read-only `export` CLI or the documented format (§4.1) |

Retries back off exponentially: 30 s, 2 min, 10 min, 1 h.

---

## 10. Test strategy

### 10.1 Unit tests

- **Planner decision table:** every combination of base, local and remote.
  Covers unknown base, several tips, identical content and delete-versus-modify.
- **Merge:** each granularity (`unit` / `folder` / `file`).
- **DAG:** tips, ancestry, and common ancestor, including truncated (pruned)
  histories.
- **Path resolver:** with fake known folders (moved Documents, a different
  username, another drive) and roots.
- **Manifest parser:** on a small **synthetic** fixture written by us. Real
  manifest data is never copied into the repository.
- **Retention and GC selection.**

### 10.2 Abstractions for testability

`IRemoteStore`, `ILocalState`, `IClock`, `IProcessMonitor`, `IKnownFolders`.
Integration tests use the real file system in temporary folders: OneDrive-like
rename and visibility behaviour matters more than mocking speed.

### 10.3 Simulating two (or more) PCs on one machine

- **In-process harness** (`SaveFileSync.Simulation.Tests`). It builds a temporary
  folder tree:
  - `repo/` — a fake OneDrive folder;
  - `pcA/` and `pcB/` — each with its own state dir, save folders, device id,
    fake clock and fake process monitor.

  Two engines run against the same `repo/` through a **`LaggyRemoteStore`
  decorator** that simulates OneDrive behaviour:
  - per-device visibility delay;
  - out-of-order arrival (a version before its blobs);
  - cloud-only placeholders that fail to read;
  - injected OneDrive-style conflict copies.

  Scenarios:
  - simple push and pull;
  - eager pull while Busy (pull must not happen);
  - concurrent edits in the same group → conflict and resolution;
  - disjoint edits → automatic merge (RPCS3-like layout);
  - Ryujinx-like `unit` granularity → conflict;
  - partial upload;
  - crash in the middle of an apply (journal recovery);
  - **handheld sleep** (§6.6):
    1. A has the game running (fake process) and is suspended (fake power
       event).
    2. B pushes.
    3. A resumes → expect exactly one stale notice and no pull.
    4. Variant (a): A's process exits with no local change → pull, no conflict.
    5. Variant (b): A writes after resume → conflict, with both versions
       preserved and the "created while open" hint.
  - clocks hours apart;
  - duplicate device id;
  - three devices converging.
- **Manual two-instance run:** `SaveFileSync.exe --profile pcA` and
  `--profile pcB` run against one repository folder. That folder can be a real
  OneDrive subfolder, to observe the real client. Each profile is bound to a
  different fake save folder under `sandbox/` (git-ignored).
- **Real hardware**, once the user is ready: desktop and Ally X with RPCS3,
  Ryujinx and one DRM-free game, following the acceptance checklists of phases
  1a and 1b (§11).
- **CI:** GitHub Actions on `windows-latest` runs build + all tests on every
  push and PR. On a `v*` tag it also publishes the single-file exe as a release
  artifact. Private repositories have a limited free minutes quota, which is
  enough for this project.

---

## 11. Phases

Each phase ends with an annotated tag on `main`, an updated `CHANGELOG.md`, and
a CI-built release artifact.

### Phase 1a — Minimal end-to-end sync, `unit` granularity only → `v0.1.0`

**Goal:** the shortest path to real use. One DRM-free game and Ryujinx sync
between two PCs, with paths entered by hand. Every conflict is resolved by
choosing **all local** or **all remote**.

- Solution skeleton, `Directory.Build.props`, MinVer, CI workflow (build + tests).
- Repository format v1 (§4), the folder-based `IRemoteStore`, OneDrive root
  detection, pinning, temp-file ignore rules, and the repository `README.txt`
  (§4.1).
- Device identity, `config.json`, `state.db`.
- Engine:
  - scanner, planner, push, pull/apply with journal;
  - three-way decision at **`unit` granularity only**: any change on both
    sides is a conflict;
  - Conflict state.
- Scheduler:
  - watchers + periodic scan, quiet period;
  - process monitor (manual process names), in-play cap;
  - pause, backoff;
  - **stale-while-running notice**, including resume from sleep (§6.6).
- Manual units: several locations, include/exclude globs, process names.
  Binding by hand on each PC.
- **Essential GUI**:
  - a first-run dialog (repository folder, device name, autostart), not the
    full wizard;
  - library list, add (manual), unit details and binding ("Set up on this PC");
  - conflict dialog with *Keep this PC* / *Keep remote* for the whole unit;
  - tray + balloons.
- **Recovery CLI**: `SaveFileSync.exe list` and `SaveFileSync.exe export`
  (§4.1). Documented in the project README.
- Autostart toggle (`HKCU\…\Run`); self-copy to `%LOCALAPPDATA%\Programs`.
- Tests:
  - planner decision-table unit tests;
  - one **minimal two-device integration test** without lag simulation:
    push/pull, conflict, and the sleep scenario from §10.3.
- History is kept but not pruned yet. Saves are small, so growth is acceptable
  until phase 3.

Acceptance checklist:
1. Fresh setup on PC A and PC B.
2. Edit on A → appears on B within about 5 minutes after the quiet period.
3. No copy while the game is running.
4. A deliberate conflict is shown and resolved, both ways.
5. Killing the app during a sync loses nothing.
6. `export` rebuilds the latest save into a folder, with the app's sync engine
   not running.
7. Ally X put to sleep with the game open while the desktop pushes: after
   resume the Ally shows the "newer save available" notice.

### Phase 1b — Finer merge and complete UX → `v0.2.0`

**Goal:** RPCS3 with automatic merge of disjoint saves, plus the complete GUI.

- Merge at **`folder` and `file` granularity** (§6.4), with per-group choice in
  the conflict dialog.
- RPCS3 configured as a manual unit with `folder` granularity. The preset itself
  comes in phase 2.
- Full first-run wizard (§8, item 1), Activity screen, Settings screen.
- *Export to folder* button in unit details (latest version only; browsing old
  versions comes in phase 3).
- Full simulation harness with `LaggyRemoteStore` and all the scenarios of
  §10.3; crash-recovery tests for the apply journal.

Acceptance checklist (in addition to 1a):
1. Disjoint RPCS3 saves made on two PCs merge without a prompt.
2. The same RPCS3 save changed on both PCs gives a conflict for that save
   folder only.
3. All simulation scenarios pass in CI.

### Phase 2 — Automatic detection → `v0.3.0` (MVP complete)

**Implemented** (see §7.3–7.4):
- Manifest download (ETag), streaming parser, compact index, name search, and
  detection of the games present on this PC (94 found on the development PC in
  0.2 s). *Add game* has three ways in: game database, emulator, manual.
- Library roots (wizard and Settings) and `installDir` scanning.
- Known-folder resolver (shell API, so a moved Documents folder works);
  automatic setup on the second PC.
- Process names suggested from the manifest's `launch` entries, narrowed to the
  executables that really exist in the install folder.
- RPCS3 and Ryujinx presets, with RPCS3 `vfs.yml` support and Ryujinx portable
  detection.
- Warning for saves inside OneDrive; "also uses the registry (not synced)" note
  in the list and in the details; Controlled folder access warning for save
  folders it would protect.
- Unit definitions carry `templates` (per location) and `source`; older
  definitions without them keep working (manual units).

**Still to verify by hand** (no such data on the development PC): A6 (Ryujinx
layout), A7 (RPCS3 layout), the real behaviour of Controlled folder access, and
O2 (data licence). The tests use synthetic folder trees that follow these
assumptions.

### Phase 3 — History & restore → `v0.4.0` (MVP+ part 1)

**Implemented.** A *History* window per game (restore, pin, export, versions to
keep), a *Devices* window (retire), and a daily cleanup.

- **Restore** brings the game in sync first (so what is replaced is already in
  the repository), then creates a `restore` version on top of the single current
  tip and applies it. Other PCs receive it like any change. It is refused during
  a conflict, while the game runs, when another PC has unmerged changes, or for
  an archived version. The replaced version stays in the list, so a restore can
  be undone.
- **Pin** keeps a version restorable. Pins are per device (one writer per file);
  a pin made on another PC can only be removed there.
- **Retention (archive, not delete).** Keep the newest N restorable versions
  (default 10, per game, stored in the game's definition so all PCs agree),
  plus every tip, pinned version, and the current version of each device seen
  in the last 90 days. The others are **archived**: their manifest stays, their
  files may be removed.
  - **Why not delete the manifests, as first planned.** Versions point to their
    parents. Deleting a manifest in the middle cuts the chain: the merge of two
    PCs would lose its common base and invent conflicts, and an old kept version
    would look like a "tip" and be applied on other PCs. Archived versions keep
    the history intact; they only stop being restorable. Manifests are a few KB
    each (O10 covers compaction if they ever matter).
  - A PC archives its own versions and those of inactive or retired devices.
  - Order comes from the history graph, never from device clocks.
- **Cleanup of files (blobs)**, once a day, 10 minutes after start at the
  earliest, never on a half-synced repository:
  1. Do nothing if any version file cannot be read, or if something is being
     written (temporary files younger than 2 days).
  2. A blob not referenced by any non-archived version is only **marked**.
  3. It is deleted only if this PC's own mark is at least **14 days** old, a
     fresh look still finds it unreferenced, and the file itself is older than
     14 days. Marks are per device and dropped as soon as the blob is referenced
     again, so an old mark can never make a later deletion instant.
  4. **Residual risk, and its repair:** a PC offline for more than 14 days that
     reused an old blob in a new version could find that blob gone. That PC
     detects missing blobs of its current version and uploads them again from
     its local files (`RepairMissingBlobs`), so the version completes.
- **Devices.** Active (seen in 90 days), inactive, retired. *Retire* writes
  `retired/<id>.json`; a retired PC that starts again re-registers (newer
  `lastSeen`) and is active again. Retired or inactive devices stop protecting
  their current version.
- **Settings:** versions to keep for new games, daily cleanup on/off, *Clean up
  now*. The recovery CLI knows archived versions (`list` marks them; `export`
  refuses them, and exports the latest version that is really available).

Tests include mutation-style checks (the "device far behind" scenario fails if
heads stop being protected) and GC safety scenarios (grace period, re-referenced
blobs, unreadable files, stale marks).

### Phase 4 — Metadata → `v0.5.0` (MVP+ part 2)

- SteamGridDB: covers and grids.
- IGDB (optional): official name. Uses Twitch client-credentials OAuth, about
  4 requests/s.
- Keys stored with DPAPI in `secrets.dat` and entered on each PC.
- Covers cached in `cache\covers\`, grid view, attribution.
- Matching: manifest name, then a search; the user can override the match.

| Source | Key | Limits / terms | Use |
|---|---|---|---|
| SteamGridDB | Free API key from a user account | Generous for personal use; community-uploaded art | **Covers/grids** (also many emulated titles) |
| IGDB | Twitch developer app (client id + secret) | ~4 req/s; attribution | **Official names**, platforms |
| RAWG | Free key | ~20k req/month; attribution required; weaker on consoles | Not used (fallback option) |

#### Implemented in phase 4

- **Keys.** *Settings → Covers and official names*: SteamGridDB API key, IGDB
  client id + secret, each with a *Test* button and *Remove*. Stored in
  `%LOCALAPPDATA%\SaveFileSync\secrets.dat`, encrypted with DPAPI for the Windows
  account (`DpapiSecretStore`); never shown again, logged, exported in
  diagnostics, or written to git or the repository. Without keys nothing is
  requested: the feature is simply off.
- **What leaves the PC:** the game name (the manifest name when known) and the
  Steam app id, to SteamGridDB and IGDB only. No paths, unit ids, PC names or
  save data. The HTTP client identifies itself as `SaveFileSync/<version>`.
- **Matching** (`MetadataService`, a background loop: 20 s after start, every
  30 minutes, and when games are added):
  1. SteamGridDB: by Steam app id when the unit has one, else autocomplete search
     by name. A result is accepted automatically only if its normalized name
     matches the game's (`NameMatcher`: case, punctuation, accents, "The", ™ ignored;
     different sequel numbers such as "Hades" / "Hades II" never match
     automatically). Then the best-rated static portrait cover (600×900 and the
     two other Steam sizes; NSFW/humor/epilepsy excluded) is chosen.
  2. IGDB: search by name, same acceptance rule, stores the official name and id.
  3. No confident match: tried again after 30 days; a network failure after 1 h; a
     rejected key stops that service until the key changes; HTTP 429 pauses the
     pass for the time the service asks (15 min by default). Bookkeeping is local
     (`cache\metadata-state.json`).
- **User override.** *Game → Change cover or match…*: search both services, pick
  the game, a cover from up to 30 thumbnails, and an official name. A choice made
  by hand is `confirmed` and never changed automatically; *Forget the match* clears
  it. *Use the official name* copies it into the name field.
- **Sharing.** The match is a new optional `metadata` object in the unit
  definition (`docs/repository-format.md`); other PCs download the cover from its
  URL on their next pass, with no key. Older apps ignore the field (and drop it
  if they write a new definition revision, after which a PC with keys matches again).
- **Cover cache** `cache\covers\<hash of url>.img`: only `https` URLs on
  `*.steamgriddb.com` (or `s3.amazonaws.com/steamgriddb/`) are fetched, images up to
  4 MB, atomically written; unused covers are removed after a day. Covers are
  decoded at 180 px wide, off the UI thread, so a large library stays cheap; the
  background process holds no images.
- **UI.** The main window has a list (small cover) and a grid (tiles with name and
  state) view, remembered per PC. The footer credits SteamGridDB while covers are
  shown, and *About* credits SteamGridDB and IGDB.
- **Rate limits.** IGDB: a request gap of 300 ms (limit is 4/s); SteamGridDB has no
  published limit, requests are sequential and a 429 is honoured.
- **Tests** (fake HTTP, no network): both clients (headers, token reuse and
  renewal, 401 refresh, error classes, query escaping), name matching,
  cover cache (allowed hosts, size/type checks, prune), DPAPI store (encrypted on
  disk, restart, corrupt file), the service (match by Steam id and by name,
  sequels not auto-matched, retry rules, rejected key, rate limit, confirmed
  choice, cover from another PC without a key, privacy of what is sent), and the
  definition round trip between two PCs.
- **Not verified yet** (needs real keys): the live responses of SteamGridDB and IGDB
  beyond the documented shape. Both services were only reached with deliberately
  wrong credentials, which they reject as expected (401 and 400).

### Phase 5 — Steam read-only backup → `v0.6.0` (MVP++)

- **Discovery**:
  - `HKCU\Software\Valve\Steam\SteamPath`;
  - `steamapps\libraryfolders.vdf`;
  - `appmanifest_<appid>.acf` (installed games);
  - `userdata\<accountId>\`.
- **Two save sources**:
  1. **Steam Cloud API saves:** `userdata\<accountId>\<appid>\remote\**`,
     backed up as is.
  2. **Auto-Cloud games:** their saves live in game-defined paths. These are
     resolved through the manifest entry matched by `steam.id`, and
     cross-checked with `userdata\<id>\<appid>\remotecache.vdf`, which lists the
     files Steam tracks.

     Parsing the binary `appcache\appinfo.vdf` (`ufs` section) is a later
     improvement (O7).
- **Mode `backupOnly`:**
  - each device pushes versions after the game process exits, using the same
    history and retention;
  - **never pull, never merge, never write into Steam folders automatically.**
    Several tips from different PCs are expected and simply listed.
- **Restore:** only from the GUI, on explicit request.
  - It requires `steam.exe` to be closed; the app detects it and offers to wait.
  - It shows a clear warning: Steam may raise a Cloud conflict at the next
    launch, and the user must choose the local copy to keep the restored save.
- **Distinguishing Steam games in the GUI:**
  - a unit is Steam if its install dir is inside a Steam library, or if it comes
    from `userdata`;
  - Steam units get a "Steam · backup only" badge and their own filter;
  - a game owned both on Steam and DRM-free is decided by where it is installed
    on each PC. The user can override this.

#### Implemented in phase 5

- **Discovery** (`SaveFileSync.Steam`, read-only): `HKCU\Software\Valve\Steam\SteamPath`,
  `libraryfolders.vdf`, `appmanifest_*.acf`, `userdata\<account>` (placeholder `0` ignored).
  A tolerant text-VDF parser returns `null` for half-written files.
- **What is backed up** (`SteamBackupFinder`), per game:
  1. `userdata\<account>\<appid>\remote` (Steam Cloud API saves) as location `steam-cloud`,
     template `<steamUserdata>/<appid>/remote`. With several accounts the most recently
     used one is bound here; on another PC an ambiguous account is left for the user.
  2. Manifest save folders matched by Steam id (Auto-Cloud games), found with the
     game's Steam `common` folders as library roots, cross-checked against the file
     names in `remotecache.vdf` (shown as *tracked by Steam Cloud* or *not confirmed*).
  Steam's own pseudo-apps (client 7, screenshots 760, controller configs 241100) are skipped.
  Games without any save file are not listed. Uninstalled games whose saves remain in
  `userdata` are listed too (named from the manifest when it is downloaded).
- **Mode `backupOnly`** (`SyncEngine.BackupSync`): this PC adds versions to **its own chain**
  and never pulls, merges or conflicts; other PCs' versions are only listed (one tip
  per PC). Rules: an emptied folder is never backed up as empty; identical content that
  another PC already stored is adopted instead of stored again; pushes wait for the game
  to close like any other unit.
- **Restore** only from the game's History window, on request: refused while the game or
  `steam.exe` runs; it continues this PC's chain with a `restore` version (other PCs are
  untouched); the confirmation warns about Steam's Cloud conflict dialog (choose the local copy).
- **GUI:** *Steam* button → *Back up Steam games* (list with checkboxes, tracked-by-Cloud
  ones preselected, already-added ones disabled); a *Steam · backup only* badge in list and
  grid; a filter (all / synced / Steam backup) appears once a Steam game exists.
- **Other PCs:** a Steam unit is bound automatically when its folder resolves to exactly one
  place (`<steamUserdata>` expands to each account folder; Steam `common` folders are added to
  the library roots for `<base>`).
- **Not done / open:** the DRM-free vs Steam override for the same game (add it as a normal
  game from the database instead); `appinfo.vdf` (O7) is still not parsed. The game list
  shows Steam apps found on the dev PC correctly (read-only run), but a real two-PC
  Steam test has not been done.

### Phase 6 — Registry saves (only if needed) → `v0.7.0`

HKCU export/import as described in §7.2.

### `v1.0.0`

Tagged after about a month of real daily use on 2+ PCs with no data-loss
issues.

---

## 12. Development conventions

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/),
  in English, imperative mood, subject ≤ 72 characters.
  - Types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`, `perf`.
  - Optional scope, e.g. `feat(engine): add three-way merge per folder`.
  - The body explains *why* when it is not obvious.
- **Branches:**
  - `main` is always buildable and green.
  - Work happens on short-lived branches `feat/<topic>`, `fix/<topic>`,
    `docs/<topic>`, squash-merged into `main` (a PR is optional for a solo
    project, but it runs CI).
  - Docs-only or trivial changes may go directly to `main`.
- **Versioning:** SemVer, `0.x` until 1.0.
  - At the end of each phase: `git tag -a vX.Y.0 -m "Phase N: …"` on `main`,
    then push the tag.
  - Fixes between phases → `vX.Y.Z`.
  - MinVer derives the assembly version from the tag.
- **Changelog:** `CHANGELOG.md` in "Keep a Changelog" format, updated at every
  tag.
- **Repository hygiene:**
  - never commit real saves, personal paths, tokens or API keys;
  - test fixtures are synthetic only;
  - `.gitignore` covers `*.local.json`, `secrets.json`, `.env`, `sandbox/`, and
    any `manifest.yaml`;
  - a secret scan (gitleaks) runs in CI.

### Where configuration and secrets live

| Data | Location | In git? | In OneDrive repo? |
|---|---|---|---|
| Code, docs, presets, synthetic fixtures | this git repo | yes | no |
| Device id, config, bindings, roots | `%LOCALAPPDATA%\SaveFileSync\` | **never** | no |
| Metadata API keys | `%LOCALAPPDATA%\SaveFileSync\secrets.dat` (DPAPI) | **never** | no |
| Sync state, manifest cache, covers, logs | `%LOCALAPPDATA%\SaveFileSync\` | **never** | no |
| Save contents, versions, unit definitions | OneDrive repo folder | **never** | yes |
| OneDrive credentials | handled by the OneDrive client; the app has no token | n/a | n/a |

---

## 13. Assumptions

- **A1** Every PC runs the OneDrive client, signed in to the same personal
  account. The repository folder is inside the synced OneDrive folder.
- **A2** One person plays. Playing the same game on two PCs at the same time is
  rare; it is handled as a conflict, not prevented.
- **A3** Save data is small to moderate (under about 1 GB per unit) and fits the
  OneDrive quota.
- **A4** All PCs run Windows 10 22H2+ or 11, x64 (the ROG Xbox Ally X is x64). An
  ARM64 build is possible later.
- **A5** The same game or emulator version, or a compatible one, is used on all
  PCs. Save-format compatibility between versions is the user's responsibility.
- **A6** The Ryujinx (or fork) data layout matches §7.3, including the save
  index at `bis/system/save/8000000000000000`. It **must be verified** on real
  data: the Ryujinx preset is built on this assumption (tests use a synthetic
  folder tree). Check on a PC that has Ryujinx saves before relying on it; the
  unit can always be edited into a manual one.
- **A7** The RPCS3 layout is `dev_hdd0/home/<user>/savedata/<SAVE_DIR>/`, with
  stable save-directory names across PCs.
- **A8** PCGamingWiki data is CC BY-NC-SA 3.0 (unverified). Personal,
  non-commercial use with runtime download is acceptable.
- **A9** No code signing is needed: the app is built locally or downloaded from
  your own private repository.
- **A10** Metadata API keys are entered on each PC. DPAPI keys are tied to the
  PC, so they cannot be shared through the repository.

## 14. Open decisions

- **O1** Blob compression (none / Brotli / zstd). Proposal: none in phase 1, and
  re-evaluate once real save sizes are known.
- **O2** Verify the PCGamingWiki/manifest data licence by hand in a browser
  before phase 2. Also add the matching attribution text.
- **O3** *(decided in phase 2)* RPCS3 trophies (`home/*/trophy`) are **not**
  synced by default; they can be added as a manual location.
- **O4** Which Ryujinx fork is used (the original project was discontinued), and
  how its user profiles map across PCs.
- **O5** Whether API keys should instead be stored once, encrypted in the
  OneDrive repository with a user passphrase.
- **O6** Notifications: tray balloons (simple) or Windows toast notifications
  (these need an AUMID/shortcut for an unpackaged app). Toasts may be more
  visible for the stale notice on the Ally (§6.6). Re-evaluate after 1a has
  been used on real hardware.
- **O7** Steam Auto-Cloud: is manifest + `remotecache.vdf` enough, or is
  `appinfo.vdf` parsing needed?
- **O8** An optional launch wrapper ("Launch via SaveFileSync") for a strict
  pull before launch. It is not planned, given D3.
- **O9** An optional Graph `IRemoteStore` for PCs without the OneDrive client.
- **O10** Version manifests of archived versions are never removed (D14). After
  years of use they could be compacted into a checkpoint; not needed until the
  numbers show it.
