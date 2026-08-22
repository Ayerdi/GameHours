# Focused and active playtime

GameHours keeps **time executed** as the authoritative measured-session duration and derives two additional local metrics for sessions recorded after activity telemetry was introduced.

These metrics are intentionally separate. GameHours never shortens a measured `PlaySession` because the game lost focus or the user became idle.

```text
measured session / executed time
|--------------------------------------------------|

focused time
     |------------------|        |-----------|

active estimated time
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

### Active time

A sample contributes active time when:

1. the game is focused; and
2. the current interactive user has not exceeded the idle threshold.

The first implementation uses a five-minute idle threshold. Keyboard/mouse inactivity comes from `GetLastInputInfo`. XInput-compatible controller activity is observed separately because controller input is not represented by `GetLastInputInfo`.

`active_duration <= focused_duration <= measured_session_duration` is a storage invariant.

## Why this is an estimate

Active time is an engagement-oriented metric, not proof that the user was or was not playing.

Examples that may legitimately become idle despite the player still paying attention include long cutscenes, dialogue, reading, turn-based planning and passive in-game sequences. For that reason the UI must call this **active time** / **estimated active playtime**, never replace measured playtime with it or label it as an exact truth.

## Sampling and failure policy

The desktop tracker samples interaction state once per second only while at least one game session is active.

A sampling interval contributes no telemetry when:

- the foreground process cannot be attributed to the active game;
- interaction observation fails;
- the elapsed gap is more than three sample intervals, which indicates a stall/suspend interval whose interior state is unknown.

Unknown gaps are not backfilled as focused or active time. A failure in this secondary telemetry must not stop or alter authoritative playtime tracking.

## Controllers

The initial controller implementation uses XInput 1.4 and checks the four XInput user slots while a game is active. GameHours records only that recent controller interaction occurred; it does not persist button identities, trigger values, stick positions or an input log.

Controllers that are not exposed through XInput are not yet guaranteed to reset GameHours' controller-idle clock. Support for broader HID/Raw Input devices can be added later if real-machine validation demonstrates a meaningful gap.

## Privacy

The Core boundary receives only:

- foreground process ID;
- elapsed idle duration.

GameHours does not persist raw keys, typed text, mouse coordinates, individual controller buttons or input contents. Persistent storage contains only aggregate durations per session plus the idle threshold used to derive them.

## Persistence and recovery

Schema v4 adds `session_activity`, keyed by the measured session UUID. While a session is open, its focused/active counters are checkpointed alongside the existing durable session checkpoint. When the session is finalized, its activity row is finalized as well.

If GameHours recovers an interrupted session through its last durable checkpoint, any available activity metrics are clamped to the recovered session boundary before being finalized. This preserves:

```text
active <= focused <= recovered measured duration
```

A full SQLite backup/restore includes these metrics automatically.

Portable JSON v1 deliberately remains unchanged and therefore does not currently transfer session-activity telemetry. Adding that data to portable interchange requires a versioned contract decision rather than silently changing the stable v1 format.

## Historical sessions

Sessions recorded before schema v4 have no focused/active telemetry. They are shown as **unavailable**, not `0`, because zero would falsely claim that GameHours observed the user as inactive.

## Real-machine validation still required

Automated tests validate schema/persistence and the pure activity policy. Windows hardware validation remains pending for:

- focus switching between a game and another application;
- five-minute keyboard/mouse idle cutoff and resume;
- XInput controller activity and idle recovery;
- multiprocess games whose foreground window belongs to a secondary tracked process;
- session lock and suspend/resume;
- old sessions remaining unavailable rather than becoming zero.

See `docs/REAL-MACHINE-VALIDATION.md` for the canonical hardware-validation backlog.
