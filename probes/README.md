# Feasibility probes

PowerShell probes were used before the .NET implementation to answer specific unknowns:

- parse UserAssist v5 focus evidence;
- copy and inspect SRUM safely;
- verify SRUM `FaceTime` usefulness;
- verify external game start/stop detection;
- verify reconciliation fallback when WMI events are missed.

The measured outcomes are preserved in `docs/VERIFIED-FINDINGS.md`.

The probes are not the production architecture and should not become runtime dependencies of the desktop client.
