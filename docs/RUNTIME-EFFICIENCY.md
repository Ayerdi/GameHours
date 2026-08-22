# Runtime efficiency

GameHours is intended to stay open while games are running, so background observation must remain secondary to the game itself. The design rule is simple: prefer an operating-system notification over frequent polling, keep fallback polling bounded, and never trade tracking reliability for a small theoretical saving without measuring it.

## Process tracking

Process starts use the Windows `Win32_ProcessStartTrace` WMI event as the primary notification path. When an event arrives, GameHours enriches only that PID instead of enumerating every process on the machine.

Process exits continue to use the per-process `Process.Exited` notification once a process is known.

Events are not treated as infallible. When the WMI event source is healthy, GameHours performs a complete process reconciliation every five seconds as a safety net for missed events. A process recovered by reconciliation uses its actual Windows start time when that timestamp falls inside the interval since the previous complete snapshot, so the slower safety scan does not automatically become a five-second timing error.

If WMI cannot start or stops unexpectedly, GameHours immediately falls back to the previous one-second full reconciliation cadence. Reliability therefore degrades to the older behavior rather than silently losing process tracking.

Suspend/resume detection is independent from full process reconciliation. A cheap system-uptime sample remains at one second so lowering the frequency of global process enumeration does not cause suspended wall-clock time to be counted as gameplay.

## Achievement observation

Achievement observation only runs for a measured active game.

Once the concrete achievement state file is known, GameHours watches that exact file with `FileSystemWatcher`. The watcher is narrowly filtered, does not recurse into subdirectories and keeps the platform's default buffer size. Duplicate filesystem notifications are coalesced into one wake-up.

Filesystem notifications can be lost, so the watcher is not the sole source of truth. A full achievement observation runs at least every 30 seconds while a known state file is being watched. If no concrete state file is known yet, source discovery retries every five seconds until one is found.

GameHours also performs the existing bounded reconciliation when a game exits because some emulators flush achievement state only during shutdown.

## Focused / active playtime

Foreground/idle sampling runs once per second only while at least one measured game is active. The sample consists of the foreground PID, Windows keyboard/mouse idle duration and XInput packet-number changes for controller activity. No raw keyboard, mouse or controller contents are persisted.

When no game is active, the activity loop sleeps without periodic input polling.

## Persistence

Durable session checkpoints have intentionally not been relaxed as part of this optimization. Their write interval protects against playtime loss after a crash. Any change to that interval should be based on measured disk/CPU impact on real hardware and an explicit decision about the larger possible recovery gap.

## Validation standard

CI can prove compilation, unit policy, watcher filtering and packaging, but it cannot prove negligible impact on a real gaming PC. Real-machine validation should compare idle and in-game CPU usage, memory, disk activity and process-scan frequency, and should exercise WMI failure/fallback, missed-event reconciliation, achievement notifications and suspend/resume before this work is described as performance-verified.
