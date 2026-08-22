# Focused and active playtime

GameHours keeps **time executed** as the authoritative measured-session duration and derives two additional local metrics for sessions recorded after activity telemetry was introduced.

These metrics are intentionally separate. GameHours never shortens a measured `PlaySession` because the game lost focus or the user became idle.

```text
measured session / executed time
|--------------------------------------------------|

focused time
     |------------------|        |-----------|

active estimated time (when AFK filtering is enabled)
     |----------|                |------|
                ^ idle cutoff
```

## Metrics

### Executed time

The existing measured session duration. A game contributes this time while GameHours' process tracker considers at least one primary process for that game active.

This remains the authoritative Steam-like metric and keeps its existing timeline, checkpoint, crash-recovery and suspend/resume semantics.

### Focused time

A sample contributes focused time only when the Windows foreground window belongs to a PID that the existing GameHours session already maps to that game.

The Windows layer uses `GetForegroundWindow` and `GetWindowThreadProcessId`. It does **not** run a second process scanner for this feature.

### Active estimated time

When AFK filtering is enabled, a sample contributes active time when:

1. the game is focused; and
2. the current interactive user has not exceeded the configured idle threshold.

The supported local preferences are **Disabled, 2, 5, 10 and 15 minutes**, with five minutes as the recommended default. Keyboard/mouse inactivity comes from `GetLastInputInfo`. XInput-compatible controller activity is observed separately because controller input is not represented by `GetLastInputInfo`.

When AFK filtering is **Disabled**, GameHours does not call `GetLastInputInfo` or XInput for activity estimation. It still samples foreground ownership so focused time remains available. Internally the activity counter mirrors focused time to preserve the storage invariant, but persisted provenance explicitly marks that session as **not AFK-estimated** and aggregate statistics do not present it as estimated active playtime.

`active_duration <= focused_duration <= measured_session_duration` remains a storage invariant.

## Preference changes during a session

GameHours never mixes two AFK policies inside one measured session.

A preference change is saved immediately. If no game is active, the tracker is restarted gracefully and the new policy applies immediately. If a game is active, the current session keeps its existing threshold and the new policy is applied after that session finishes.

## Why this is an estimate

Active time is an engagement-oriented metric, not proof that the user was or was not playing.

Examples that may legitimately become idle despite the player still paying attention include long cutscenes, dialogue, reading, turn-based planning and passive in-game sequences. For that reason the UI calls this **active time** / **estimated active playtime**, never replaces measured playtime with it or labels it as an exact truth.

## Sampling and failure policy

The desktop tracker samples foreground/activity state once per second only while at least one game session is active.

A sampling interval contributes no telemetry when:

- the foreground process cannot be attributed to the active game;
- interaction observation fails;
- the elapsed gap is more than three sample intervals, which indicates a stall/suspend interval whose interior state is unknown.

Unknown gaps are not backfilled as focused or active time. A failure in this secondary telemetry must not stop or alter authoritative playtime tracking.

When AFK filtering is disabled, the same temporary loop measures only foreground ownership; it does not query idle-input APIs.

## Controllers

The controller implementation uses XInput 1.4 only as a coarse AFK signal while a game is active **and AFK filtering is enabled**. GameHours compares sequential `dwPacketNumber` values; a changed packet means that some controller state changed since the previous observation. GameHours does not interpret which control changed.

Although the native `XInputGetState` ABI writes the complete 16-byte `XINPUT_STATE`, the GameHours managed interop representation intentionally exposes only the four-byte packet number. The remaining 12 bytes are opaque padding: there are no managed button, trigger or stick fields for the activity provider to inspect.

Connected controller slots are sampled with the existing one-second activity cadence. Empty XInput slots are probed only every five seconds, following Microsoft's recommendation to avoid querying disconnected slots every frame.

A controller's first successful observation establishes a baseline and is not itself counted as interaction. A later packet-number change updates the controller activity clock. GameHours never persists packet numbers or controller state; they exist only in memory for the current process.

Controllers that are not exposed through XInput are not yet guaranteed to reset GameHours' controller-idle clock. Support for broader HID/Raw Input devices can be considered later only if real-machine validation demonstrates a meaningful gap; GameHours should not collect richer input data merely to improve this estimate.

## Privacy

The Core boundary receives only:

- foreground process ID;
- elapsed idle duration when AFK estimation is enabled.

For keyboard/mouse activity, GameHours uses only the elapsed time reported by `GetLastInputInfo`. For XInput controllers, it uses only whether the packet number changed. GameHours does not interpret or persist raw keys, typed text, mouse coordinates, controller buttons, trigger values, stick positions, packet numbers or input contents.

With AFK filtering disabled, the input-idle APIs are not queried at all.

Persistent storage contains only aggregate focused/active durations, the threshold used and whether AFK filtering was enabled for that session.

## Persistence and recovery

Schema v4 introduced `session_activity`, keyed by the measured session UUID. Schema v5 adds explicit AFK-policy provenance and makes a zero idle threshold a valid, unambiguous representation of a disabled AFK filter. Existing v4 rows migrate as AFK-enabled because all v4 rows necessarily had a positive threshold.

While a session is open, focused/active counters are checkpointed alongside the existing durable session checkpoint. When the session is finalized, its activity row is finalized as well.

If GameHours recovers an interrupted session through its last durable checkpoint, any available activity metrics are clamped to the recovered session boundary before being finalized. This preserves:

```text
active <= focused <= recovered measured duration
```

A full SQLite backup/restore includes these metrics automatically.

Portable JSON v1 deliberately remains unchanged and therefore does not currently transfer session-activity telemetry. Adding that data to portable interchange requires a versioned contract decision rather than silently changing the stable v1 format.

## Statistics and data quality

Lifetime statistics expose:

- focused duration only across measured sessions that actually have activity telemetry;
- estimated active duration only across sessions whose persisted provenance says AFK filtering was enabled;
- activity-telemetry coverage as a share of measured playtime;
- directly measured playtime as a share of all known playtime.

GameHours does not fabricate monthly active-time values by proportionally redistributing a session-level aggregate across calendar boundaries. If the stored data cannot support a claim exactly enough, the UI leaves that claim unavailable instead of inventing precision.

## Historical sessions

Sessions recorded before activity telemetry have no focused/active telemetry. They are shown as **unavailable**, not `0`, because zero would falsely claim that GameHours observed the user as inactive.

## Real-machine validation still required

Automated tests validate schema/persistence, migration, the pure activity policy and the managed XInput privacy boundary. Windows hardware validation remains pending for:

- focus switching between a game and another application;
- each configurable keyboard/mouse idle cutoff and resume;
- disabled AFK mode performing focus-only observation;
- XInput packet-change activity and idle recovery;
- multiprocess games whose foreground window belongs to a secondary tracked process;
- session lock and suspend/resume;
- old sessions remaining unavailable rather than becoming zero;
- preference changes being applied after an active session without mixing thresholds.

See `docs/REAL-MACHINE-VALIDATION.md` for the canonical hardware-validation backlog.
