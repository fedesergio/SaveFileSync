# Repository format (v1)

The repository is a folder inside OneDrive that SaveFileSync manages
(see PLAN.md §4). This document describes it well enough to recover saves
**without** SaveFileSync.

## Layout

```
<repository>\
  repo.json                              { format: 1, repoId, createdUtc }
  README.txt                             plain-text explanation for humans
  devices\<deviceId>.json                one per PC: name, shortId, appVersion, lastSeenUtc
  units\<unitId>\
    def\<revision>-<devShort>.json       unit definition; highest revision wins
    versions\<devShort>-<seq>.json       immutable version manifests
    heads\<deviceId>.json                version each PC currently has
    pins\<deviceId>.json                 versions that PC pinned (kept restorable)
    archive\<deviceId>.json              versions whose files were removed on purpose
  gc\<deviceId>.json                     blobs a PC found unused, and since when
  retired\<deviceId>.json                PCs retired by the user
  blobs\<sha[0..2]>\<sha256>             file contents, named by SHA-256 (lowercase hex)
```

Files whose name contains `.tmp-` are writes in progress: ignore them.

## Unit definition (`def\*.json`)

```json
{
  "unitId": "hollow-knight-k7m2qa",
  "name": "Hollow Knight",
  "locations": [ { "id": "saves", "label": "Hollow Knight", "includes": ["**"], "excludes": [] } ],
  "processNames": ["hollow_knight"],
  "granularity": "unit",
  "mode": "sync",
  "revision": 1,
  "removed": false
}
```

A location is a logical folder. Its absolute path is different on each PC and
is **never** stored in the repository.

Units added from the game database or an emulator preset also carry (all
optional; older definitions simply lack them):

```json
{
  "locations": [
    { "id": "saves", "includes": ["**"],
      "templates": ["<winAppData>/Hollow Knight/saves"] }
  ],
  "source": {
    "kind": "manifest",          // manual | manifest | preset
    "manifestName": "Hollow Knight",
    "steamId": 367520,
    "installDirs": ["Hollow Knight"],
    "presetId": null,            // e.g. "rpcs3" for emulator presets
    "registryKeys": 0            // Windows registry keys that are NOT synced
  }
}
```

`templates` say where another PC should look for the folder, with tokens such
as `<winAppData>`, `<winLocalAppData>`, `<winDocuments>`, `<home>` and
`<base>` (= a library folder plus one of `installDirs`). They contain no
absolute paths and no user names.

## Version manifest (`versions\*.json`)

```json
{
  "unitId": "hollow-knight-k7m2qa",
  "versionId": "a1b2c3d4-000042",
  "deviceId": "a1b2c3d45e6f…",
  "seq": 42,
  "parents": ["e5f6a7b8-000017"],
  "kind": "normal",
  "createdUtc": "2026-11-18T20:31:05Z",
  "files": [
    { "loc": "saves", "path": "user1.dat", "size": 123456,
      "sha256": "9f…", "mtimeUtc": "2026-11-18T20:29:47Z" }
  ],
  "totalSize": 123456
}
```

- `parents` link versions into a graph; versions that are nobody's parent are
  the latest ones ("tips").
- `createdUtc` comes from the clock of the PC that created the version and is
  informational only.
- A version is complete only when every blob it lists exists.
- `kind` is `normal`, `merge`, `resolve` (a conflict choice), `restore` (an older
  version put back on top) or `initial`.

## Old versions: archived, not deleted

To limit OneDrive space, versions beyond a game's limit (`keepVersions` in the
definition, default 10) are **archived**: the id is listed in
`units\<id>\archive\<deviceId>.json` (`{"versionIds": [...]}`), the manifest stays
(so the history stays connected), and the files may later be deleted from
`blobs\` by the cleanup (first marked in `gc\`, deleted at least 14 days later
and only if no restorable version uses them). An archived version cannot be
restored or exported; the latest version, pinned versions and each PC's current
version are never archived. `SaveFileSync.exe list` marks them `ARCHIVED`.

## Recovering a version by hand (last resort)

Prefer `SaveFileSync.exe export` (see README). If the exe is not available,
this PowerShell script rebuilds one version into a folder:

```powershell
# Usage: .\Restore-Version.ps1 -Repo "C:\Users\me\OneDrive\SaveFileSync" `
#            -UnitId "hollow-knight-k7m2qa" -VersionId "a1b2c3d4-000042" -Target "C:\Recovered"
param(
    [Parameter(Mandatory)] [string] $Repo,
    [Parameter(Mandatory)] [string] $UnitId,
    [Parameter(Mandatory)] [string] $VersionId,
    [Parameter(Mandatory)] [string] $Target
)

$manifest = Get-Content -Raw -LiteralPath (Join-Path $Repo "units\$UnitId\versions\$VersionId.json") | ConvertFrom-Json
foreach ($f in $manifest.files) {
    $blob = Join-Path $Repo ("blobs\" + $f.sha256.Substring(0, 2) + "\" + $f.sha256)
    $dest = Join-Path (Join-Path $Target $f.loc) ($f.path -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Copy-Item -LiteralPath $blob -Destination $dest -Force
    (Get-Item -LiteralPath $dest).LastWriteTimeUtc = ([datetime]$f.mtimeUtc).ToUniversalTime()
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLower() -ne $f.sha256) {
        Write-Warning "Hash mismatch for $($f.path): the blob may be corrupt or still syncing."
    }
}
Write-Host "Restored $($manifest.files.Count) file(s) to $Target"
```

To find the latest version of a game: open `units\<unitId>\versions\`, and pick
the manifest that no other manifest lists in its `parents` (usually the most
recently modified file).
