# Game discovery

GameHours uses layered discovery instead of pretending that Windows exposes a reliable global "installed games" API.

## High-confidence installed sources

The first implementation reads local metadata from:

- **Steam**: Steam root + `libraryfolders.vdf` + `steamapps/appmanifest_*.acf`.
- **Epic Games Launcher**: `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON manifests, filtered to game applications.
- **GOG**: `HKLM\SOFTWARE\GOG.com\Games` in both 32-bit and 64-bit registry views.

These sources yield a title, provider identity and installation root. A running executable inside a known installation root can then be resolved to that game. Standard crash reporters, web helpers, updaters, anti-cheat processes and launchers are classified separately instead of being treated as primary gameplay processes.

## Detection evidence engine

Runtime identification now keeps the individual reasons behind a decision instead of collapsing detection into one yes/no heuristic. `GameResolution` can carry an executable role plus a list of weighted local evidence.

Current executable roles are:

- `PrimaryGame`;
- `SecondaryGame`;
- `Launcher`;
- `AntiCheat`;
- `Updater`;
- `CrashHandler`;
- `Helper`;
- `Ignored`;
- `Unknown`.

Current evidence sources include:

- exact launcher/install-directory membership;
- exact executable paths previously learned by GameHours;
- Windows `HKCU\System\GameConfigStore\Children` entries via `MatchedExeFullPath`;
- Unreal packaged runtime layout;
- Unity runtime layout;
- loaded Direct3D/OpenGL/Vulkan modules;
- ownership of a top-level window;
- ownership of the foreground window;
- parent-process executable relationships;
- conservative executable/folder-name similarity;
- negative executable-role patterns for known helpers.

Evidence is deliberately asymmetric. A known launcher, crash handler, updater, anti-cheat or web helper wins over positive game evidence so it cannot start a play session by itself.

### Windows GameConfigStore

Windows maintains per-user game-related entries under `HKCU\System\GameConfigStore\Children`. GameHours reads this store without modifying it and compares `MatchedExeFullPath` with the normalized running executable path.

An exact GameConfigStore match is a strong local signal and can resolve a loose game even when no Steam/Epic/GOG manifest exists. It is still supporting evidence rather than an unquestionable source: helper-role exclusions run first, and graphics/window observations may reinforce confidence.

Registry access is cached and fail-open. Missing keys, permission errors or unreadable values must never block normal tracking.

### Graphics and window evidence

At process-resolution time GameHours can inspect the live process for:

- a top-level window;
- whether that window is currently foreground;
- loaded graphics modules such as `d3d9.dll`, `d3d10.dll`, `d3d11.dll`, `d3d12.dll`, `vulkan-1.dll` and `opengl32.dll`.

Graphics evidence alone is intentionally insufficient for automatic tracking because browsers, chat clients and many desktop applications also use GPU APIs. A graphical unknown with a visible window is currently exposed internally as a low-confidence candidate (`0.65`), below the normal `0.80` automatic tracking/learning threshold. This is groundwork for the graphical unresolved-candidate UI.

### Launcher/process-family learning

When live process inspection can identify a parent executable, GameHours records that path as neutral relationship evidence. The relationship alone never raises confidence.

A low-confidence graphical candidate is promoted only when all of these conditions hold:

1. it already has the graphical-candidate evidence required by the normal resolver;
2. it is not classified as a helper-like executable;
3. its immediate parent executable has an exact local mapping already learned by GameHours;
4. that parent mapping is explicitly marked as a helper for a known game.

Only then is the child attached to the parent's canonical game at high confidence (`0.90`) using `learned_parent_process_family`, and the child's exact executable path is learned as a trackable process for later launches.

This is deliberately narrower than treating every child of a launcher as a game. Updaters, anti-cheat components and helper processes remain excluded by role before they can be promoted.

The relationship is currently observed live. A future grace-window/history layer is still needed for launchers that exit before GameHours has a chance to inspect the child-parent relationship.

## Launcher-independent runtime discovery

Games copied manually, DRM-free installs, repacks and other loose executables do not have launcher manifests. GameHours therefore has conservative runtime fallbacks.

Current high-confidence signatures:

- Unreal Engine packaged executables ending in `-Win64-Shipping.exe` or `-Win32-Shipping.exe` under `Binaries\Win64` / `Binaries\Win32`;
- Unity executables with a sibling `UnityPlayer.dll` or `<exe>_Data` directory;
- an exact non-helper Windows GameConfigStore executable match;
- a graphical process whose immediate parent is an exact learned helper for a known game.

A stable local game id is derived from the provider id or local installation identity. Loose runtime discovery is deliberately stricter than launcher discovery to avoid counting normal desktop applications as games.

Loose discoveries are canonicalized against an already remembered game with the same title. This prevents two executables belonging to one loose game from becoming two independent tracked games and producing overlapping sessions.

## Learned executable mappings

A process that resolves with sufficient confidence is learned locally. GameHours stores the normalized executable path -> local game mapping in SQLite and resolves that exact path with full confidence on later runs.

This gives loose games a useful lifecycle:

1. first run is discovered from a strong launcher, engine or Windows signal;
2. the game and executable mapping are persisted locally;
3. future runs use the exact learned path instead of repeating the heuristic;
4. helper-like decisions remain non-trackable when learned;
5. a verified graphical child of an already learned helper can join the same game and become a learned trackable executable;
6. `scan` can list previously tracked local games even while they are closed.

When an executable inside a known installation has no stronger role classification, GameHours can attach it to the same game as a `SecondaryGame` process. This preserves the existing multi-process session model: one game session stays active until its last trackable process exits.

If an older mapping points to a duplicate local game id with the same title, the resolver redirects it to the canonical remembered game and rewrites the mapping locally.

Full executable paths, process relationships and GameConfigStore contents remain local data and are not part of the backend sync contract.

## Unknown executables and manual confirmation

Not every game exposes a reliable launcher or engine signature. Development builds can inspect only newly started processes with:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- diagnose
```

An unresolved executable is reported locally and can be explicitly confirmed once:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- map "C:\Games\ProjectPIIT.exe" "Project P.I.I.T."
```

The exact path is stored locally and future launches resolve through `learned_executable_path`.

The next desktop slice should expose unresolved/low-confidence candidates with their evidence and let the user choose once between:

- game/new game;
- executable belonging to an existing game;
- secondary game process;
- launcher/helper/anti-cheat/updater/crash handler;
- ignore.

Those decisions should be durable so GameHours learns instead of repeatedly asking.

## Validation status

Synthetic/unit coverage now verifies conservative role classification, exact GameConfigStore resolution, helper precedence over GameConfigStore, secondary-process association inside a known install directory, promotion of a graphical child whose parent is a learned helper, and rejection of the same relationship when the parent is not learned as a helper. The full evidence/process-family engine builds and passes CI.

Real-machine validation is still pending for:

- reading actual Windows GameConfigStore entries on the user's machine;
- live graphics-module/window/foreground evidence;
- live parent-process capture and representative launcher -> helper -> real game process families;
- false-positive behavior across normal GPU-accelerated desktop applications.

This validation is explicitly non-blocking for continued implementation.

## Not covered yet

- graphical candidate confirmation / executable-role editor;
- durable persistence of the richer role taxonomy beyond the existing game/helper mapping;
- launcher grace-window/history for parent processes that disappear before child inspection;
- Xbox / Microsoft Store / Game Pass;
- EA app;
- Ubisoft Connect;
- Battle.net;
- arbitrary folder scanning of every disk;
- optional/community executable-name metadata.

Those should be added as independent evidence sources or UI flows instead of weakening the tracker core's confidence threshold.
