# Playtime timeline

The central GameHours invariant is that time must have provenance and temporal coverage.

## Why counters are not enough

Suppose a game has:

- Steam: 126 h cumulative;
- SRUM: 119 h foreground;
- GameHours: 8 h measured after installation.

`126 + 119 + 8` is invalid because the sources overlap.

## Cutover

On first tracker activation, GameHours stores an immutable `tracking_started_at` value.

Historical baseline evidence may cover only time before that cutover:

```text
[ historical baseline ] | [ measured GameHours sessions ... ]
                        ^
               tracking_started_at
```

For a game without a better external baseline, SRUM can provide an estimated foreground-time baseline.

## After cutover

GameHours sessions are primary for intervals the tracker covers.

SRUM is not continuously added on top. It is consulted again only if there is a known tracking gap, for example when GameHours was not running.

A `gap_recovery` item:

- starts at or after `tracking_started_at`;
- represents an explicitly uncovered period;
- is rejected if its covered period overlaps a stored GameHours session.

## Half-open interval semantics

Overlap checks use `[start, end)` semantics:

- 18:00–19:00 and 18:30–20:00 overlap;
- 18:00–19:00 and 19:00–20:00 do not.

## Confidence vocabulary

### Measured sessions

- `Exact`: both boundaries came from a sufficiently precise authoritative event/clock path.
- `High`: one or more boundaries were reconstructed by the reconciliation interval.

### Historical evidence

- `High`: unusually strong reconstructed evidence, but still not a measured GameHours process lifetime.
- `Estimated`: reconstructed/focus evidence such as SRUM/UserAssist.

Historical evidence must never claim `Exact`.
