# SaveFileSync

A personal Windows tool that keeps non-Steam game saves (DRM-free games and
emulators) in sync across several PCs, using OneDrive as the transport.

- Automatic save-path detection via the
  [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
  (data from [PCGamingWiki](https://www.pcgamingwiki.com)), with manual paths as a fallback.
- Two-way background sync from the notification area, no admin rights required.
- Per-device conflict detection and a guided choice of which version to keep.

**Status:** planning — see [PLAN.md](PLAN.md).

**Stack:** .NET 10, WPF.

> This repository contains code and documentation only: never real save files,
> personal paths, tokens or API keys.
