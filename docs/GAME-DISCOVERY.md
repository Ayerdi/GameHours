# Game discovery

GameHours uses layered discovery instead of pretending that Windows exposes a reliable global "installed games" API.

## High-confidence installed sources

The first implementation reads local metadata from:

- **Steam**: Steam root + `libraryfolders.vdf` + `steamapps/appmanifest_*.acf`.
- **Epic Games Launcher**: `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON manifests, filtered to game applications.
- **GOG**: `HKLM\SOFTWARE\GOG.com\Games` in both 32-bit and 64-bit registry views.

These sources yield a title, provider identity and installation root. A running executable inside a known installation root can then be resolved to that game. Standard crash reporters, web helpers, updaters and launchers are excluded as helpers.

## Launcher-independent runtime discovery

Games copied manually, DRM-free installs, repacks and other loose executables do not have launcher manifests. GameHours therefore has a conservative runtime fallback.

Current high-confidence signatures:

- Unreal Engine packaged executables ending in `-Win64-Shipping.exe` or `-Win32-Shipping.exe` under `Binaries\Win64` / `Binaries\Win32`;
- Unity executables with a sibling `UnityPlayer.dll` or `<exe>_Data` directory.

A stable local game id is derived from the provider id or local installation identity. Loose runtime discovery is deliberately stricter than launcher discovery to avoid counting normal desktop applications as games.

## Not covered yet

- Xbox / Microsoft Store / Game Pass;
- EA app;
- Ubisoft Connect;
- Battle.net;
- arbitrary folder scanning of every disk;
- user-confirmed executable mappings.

Those should be added as independent discovery sources instead of complicating the tracker core.
