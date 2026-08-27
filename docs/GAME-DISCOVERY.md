# Game discovery

GameHours uses layered evidence instead of assuming Windows exposes one reliable global "installed games" source.

## Installed sources

Current high-confidence local metadata comes from:

- **Steam**: Steam root, `libraryfolders.vdf` and `steamapps/appmanifest_*.acf`;
- **Epic Games Launcher**: `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`;
- **GOG**: `HKLM\SOFTWARE\GOG.com\Games` in 32/64-bit registry views.

An install root is identity context, not permission to count every executable below it. Exact launch executables and strong runtime evidence can resolve automatically; an otherwise unknown executable under a known root stays below the tracking threshold until it has stronger evidence or the user confirms it.

Utilities such as config tools, benchmarks, diagnostics, installers, redistributables, launchers, anti-cheat processes and crash reporters are classified separately.

## One process-observation pipeline

GameHours no longer has a second global polling loop for unresolved candidates. `WindowsProcessSnapshotProvider` captures the process set once per reconciliation pass, including when available:

- PID;
- actual Windows parent PID;
- executable path;
- process start time.

The snapshot populates a shared short-lived process identity history before the resolver runs. The authoritative resolver then produces exactly one `GameResolution`; a candidate-recorder decorator may persist that same result when it is useful but remains below the automatic tracking threshold.

```text
process snapshot
      |
      +--> shared recent identity history
      |
      v
WindowsGameResolver
      v
LearningGameResolver
      v
ExplicitExecutableRoleResolver
      v
CandidateRecordingGameResolver
      |
      +--> >= 0.80 and trackable --> session engine
      |
      +--> useful < 0.80 ---------> Pendientes
```

`ExplicitExecutableRoleResolver` is intentionally outside learning: a user decision such as `Ignored`, `Launcher`, `Helper`, `AntiCheat`, `Updater` or `CrashHandler` must win even if the executable had previously acquired a full-confidence learned mapping. A short-circuited helper-like process is still written to the shared relationship history so launcher -> child recovery keeps working when the parent exits quickly.

Candidate recording cannot increase confidence and cannot start a session.

## Executable roles

Current roles:

- `PrimaryGame`;
- `SecondaryGame`;
- `Launcher`;
- `AntiCheat`;
- `Updater`;
- `CrashHandler`;
- `Helper`;
- `Ignored`;
- `Unknown`.

Helper-like roles override positive game evidence for the executable itself. Explicit user decisions have higher precedence than learned mappings and automatic heuristics.

## Evidence

Current evidence sources include:

- exact locally learned executable path;
- store/launcher installation metadata;
- exact known launch executable where the source supplies it;
- Windows `HKCU\System\GameConfigStore\Children` / `MatchedExeFullPath`;
- Unreal packaged runtime layout;
- Unity runtime layout;
- Direct3D/OpenGL/Vulkan modules;
- visible/foreground window state;
- actual parent-process relationships;
- recent exact-parent-PID recovery after the parent exits;
- executable/folder-name similarity;
- negative role patterns and local user overrides.

Graphics/window evidence remains supporting evidence because browsers and desktop clients also use GPU APIs.

## Parent/process-family learning

A graphical low-confidence child can become the real executable of a known game only when its immediate parent is already mapped locally as that game's helper.

With a live parent the resolver uses `learned_parent_process_family` at confidence `0.90`. If the parent has already exited, GameHours can use `learned_recent_parent_process_family` at `0.88` only when the child still points to that exact Windows parent PID and the identity exists in the 30-second history.

The history stores normalized executable path, process start time, parent PID and last-seen time. Later observations that contain less metadata merge into the existing identity instead of erasing parent/start-time information. This matters because the session engine may resolve a reduced observation after the richer Windows snapshot has already populated history.

PID reuse is guarded when start times are known: a cached process that started after the child cannot be accepted as its parent. Timing proximity alone is never enough.

Explicit helper-like decisions also preserve this relationship record before they short-circuit the rest of resolution, so a launcher ignored for gameplay can still provide identity context to its real game child.

## Loose games and Program Files

Strong loose-game signatures are evaluated before the generic Program Files exclusion. This permits a DRM-free Unreal/Unity game installed under `Program Files` to resolve from its engine layout while still preventing weak generic application heuristics from treating ordinary installed software as games.

Current strong loose signatures:

- Unreal `*-Win64-Shipping.exe` / `*-Win32-Shipping.exe` under packaged `Binaries` layout;
- Unity executable with `UnityPlayer.dll` or `<exe>_Data` sibling;
- exact non-helper GameConfigStore path;
- verified child of a learned game helper.

## Known install roots

Install-directory membership adds strong identity context but an unknown child no longer receives automatic tracking confidence simply because it is located there.

Typical behavior:

- helper/utility role -> associated if useful for family learning, but never counted itself;
- exact source launch executable -> trackable;
- strong engine/GameConfigStore/graphics+window runtime evidence -> trackable process for the known game;
- no stronger runtime evidence -> `installed_path_candidate` at `0.70`, visible in **Pendientes** rather than counted.

This retains multiprocess support without making `ConfigTool.exe`, `Benchmark.exe` or similar binaries gameplay by default.

## Learned mappings

A sufficiently confident resolution is stored as exact executable path -> local game. Future launches of that path resolve with full local confidence instead of repeating heuristics.

Loose discoveries with the same remembered title are canonicalized to one local game identity, preventing multiple executables of one title from producing independent overlapping games.

A learned mapping is not stronger than a later explicit user exclusion/helper decision. The explicit-role gate is evaluated first.

## Pendientes

`Pendientes` is intentionally conservative. It is not a list of every unresolved process and should not resemble Task Manager.

Automatic admission currently requires a below-threshold resolution plus meaningful game context. Strong context such as a known game install root remains eligible. The generic `graphics + visible window` fallback is admitted only when the executable is also under an explicitly game-oriented path such as `Games`/`Juegos`; browsers, chat clients and ordinary GPU-accelerated desktop applications are therefore not candidates merely for rendering through Direct3D/OpenGL/Vulkan.

This trade-off is deliberate: false negatives are preferable to flooding the review queue. **Añadir EXE…** remains the escape hatch for a real DRM-free/portable game installed in an arbitrary location with no strong automatic evidence.

Low-confidence candidates are persisted in SQLite with:

- executable/process identity;
- suggested title;
- confidence and resolver method;
- individual evidence;
- first/last seen timestamps;
- observation count;
- final decision state.

When repeated observations disagree, the stored confidence, method, role, title and evidence remain aligned with the strongest observation; later weaker observations update recency/count without replacing the rationale shown to the user.

The graphical review flow can:

- create a new game;
- associate the EXE with an existing game;
- classify it as launcher/helper/anti-cheat/updater/crash reporter;
- ignore it.

Those actions now pass through one `CandidateDecisionService` instead of duplicating consistency rules in WPF event handlers. Its durable state transitions are deliberately conservative:

- confirmed game -> exact non-helper mapping, remove any helper/ignored override, close the candidate;
- helper-like role with a selected game -> exact helper mapping, persist the role override, close the candidate;
- helper-like role without a selected game -> remove any contradictory mapping, persist the role override, close the candidate;
- ignored -> remove any contradictory mapping, persist `Ignored`, close the candidate.

The ordering is fail-safe: helper/ignored decisions make the runtime non-trackable before the pending row is hidden. A failed final candidate update can therefore leave a retryable pending row, but cannot silently start counting an executable the user just excluded.

A resolved/ignored candidate does not become pending again merely because the executable runs later. Schema v3 also discards only legacy **pending** suggestions produced under the older broad admission rules while preserving resolved/ignored user decisions.

Role overrides are local under `%LOCALAPPDATA%\GameHours\executable-role-overrides.json` and are not part of the backend sync contract.

## Validation status

Automated coverage verifies, among other cases:

- helper precedence and utility-role classification;
- exact GameConfigStore resolution;
- conservative unknown EXEs inside known install roots;
- parent identity recovery and PID-reuse rejection;
- preservation of rich process history when later observations are partial;
- launcher-family promotion rules;
- explicit helper/ignored decisions winning before learned resolution while preserving launcher identity history;
- conservative candidate admission for generic graphical applications;
- candidate persistence, strongest-rationale retention and durable decisions;
- ignored/helper decisions removing or replacing contradictory primary mappings;
- confirmed-game decisions removing stale helper overrides;
- migration cleanup of legacy pending candidates without deleting prior decisions.

Still pending on a real Windows machine:

- representative GameConfigStore entries;
- graphics/window evidence across real games and normal GPU-accelerated applications;
- launcher -> helper -> game families, including launchers that exit quickly;
- candidate-center decisions across later launches;
- visual/usability testing of the desktop workflow.

## Future independent sources

Potential additions should remain independent evidence providers rather than weakening existing thresholds:

- Xbox / Microsoft Store / Game Pass;
- EA app;
- Ubisoft Connect;
- Battle.net;
- optional folder scanning;
- optional/community executable metadata.
