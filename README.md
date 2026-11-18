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
- Planned: automatic save-path detection via the
  [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
  (data from [PCGamingWiki](https://www.pcgamingwiki.com)), history browser,
  cover art, read-only Steam backups.

**Status:** phase 1b (`v0.2.0`) — manual save folders; conflicts per game,
per save folder or per file.
See [PLAN.md](PLAN.md) and [CHANGELOG.md](CHANGELOG.md).

**Stack:** .NET 10, WPF (WPF-UI), SQLite for local state.

## Using it

1. Run `SaveFileSync.exe`. On first start choose a folder **inside OneDrive**
   (the same one on every PC) and a name for this PC.
2. *Add game*: pick the save folder(s) and, ideally, the game's process
   (e.g. `rpcs3.exe`) so nothing is copied while you play. For emulators that
   keep one folder per game (RPCS3: `dev_hdd0\home\00000001\savedata`), choose
   *Per save folder* conflict detection; keep *Whole game* for Ryujinx.
3. On the other PCs the game appears as *Not set up on this PC*: open it, choose
   where its saves live on that PC, and *Set up on this PC*.

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
