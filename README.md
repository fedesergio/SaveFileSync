# SaveFileSync

A personal Windows tool that keeps non-Steam game saves (DRM-free games and
emulators) in sync across several PCs, using OneDrive as the transport.

- Two-way background sync from the notification area, no admin rights required.
- Waits for the game to close (or for a quiet period) before uploading; never
  downloads into a running game, and warns when a newer save arrived while the
  game was open (e.g. a handheld resumed from sleep).
- Per-device conflict detection: when the same save changed on two PCs you
  choose which version to keep, and both stay in the history. With
  "per save folder" detection (e.g. RPCS3), different saves changed on
  different PCs are merged automatically.
- Automatic save-path detection via the
  [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
  (data from [PCGamingWiki](https://www.pcgamingwiki.com)), with manual paths as
  a fallback, and presets for RPCS3 and Ryujinx.
- Planned: history browser and restore, cover art, read-only Steam backups.

**Status:** phase 2 (`v0.3.0`) — automatic save-folder detection, emulator
presets, automatic setup on other PCs.
See [PLAN.md](PLAN.md) and [CHANGELOG.md](CHANGELOG.md).

**Stack:** .NET 10, WPF (WPF-UI), SQLite for local state, YamlDotNet.

## Using it

1. Run `SaveFileSync.exe`. On first start choose a folder **inside OneDrive**
   (the same one on every PC) and a name for this PC.
2. In *Settings*, add the folders where you install games (e.g. `C:\Games`).
3. *Add game* has three ways in:
   - **Game database**: search by name. Games whose save folder exists on this
     PC are marked *Found on this PC*; select one and check what will be synced.
   - **Emulator**: RPCS3 and Ryujinx. Pick the emulator folder (or leave it empty
     to search); RPCS3 merges per save folder, Ryujinx is kept as one whole.
   - **Manual**: choose the folders yourself.

   Add the game's process (e.g. `rpcs3.exe`) so nothing is copied while you play;
   the database suggests it.
4. On your other PCs the game shows up by itself. If its save folder is found
   there (exactly one place), it is set up automatically; otherwise it appears
   as *Not set up on this PC*: open it and use *Find folders automatically* or
   *Browse…*. If saves already exist there and differ, you choose which version
   to keep: nothing is overwritten silently.

Some games also keep data in the Windows registry; that is **not synced** and
the game is marked accordingly. The game database is the
[Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest), compiled
from [PCGamingWiki](https://www.pcgamingwiki.com); it is downloaded on your PC
only (never stored in this repository) for personal, non-commercial use.

The app lives in the notification area. Closing the window does not stop sync;
use *Exit* in the tray menu.

## Getting your saves back without the sync engine

Inside OneDrive the files are stored under hash names, so the repository is not
meant to be browsed by hand. Two read-only commands rebuild any version into a
normal folder; they need no configuration when given `--repo`:

```powershell
SaveFileSync.exe list   --repo "C:\Users\me\OneDrive\SaveFileSync"
SaveFileSync.exe export "Hollow Knight" "C:\Recovered" --repo "C:\Users\me\OneDrive\SaveFileSync"
SaveFileSync.exe export "Hollow Knight" "C:\Recovered" --version a1b2c3d4-000042 --repo "…"
```

From `cmd.exe` use `start /wait SaveFileSync.exe export …` so that the prompt
waits for the output. Without `--repo`, the repository configured on this PC is
used. If even the exe is unavailable, [docs/repository-format.md](docs/repository-format.md)
describes the format and includes a PowerShell script that restores a version.

## Building

```powershell
dotnet test SaveFileSync.slnx
dotnet publish src/SaveFileSync.App -c Release -o publish   # single self-contained SaveFileSync.exe
```

Dev only: `--profile <name>` keeps a separate config/state under
`%LOCALAPPDATA%\SaveFileSync\profiles\<name>`, so two instances can simulate two
PCs on one machine. `--selftest-ui` (with a configured profile) opens and closes
every window and exits with 0 if they all load.

## Where data lives

| What | Where |
|---|---|
| Config, device id, sync state, logs | `%LOCALAPPDATA%\SaveFileSync\` (never in git, never in OneDrive) |
| Save versions | the repository folder in OneDrive |
| OneDrive credentials | the OneDrive client; SaveFileSync has no token |

> This git repository contains code and documentation only: never real save
> files, personal paths, tokens or API keys.
