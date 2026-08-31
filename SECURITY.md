# Security and privacy

GameHours observes local process and Windows usage metadata. That makes privacy boundaries part of the security model, not an optional UI feature.

## Rules

- Never commit API tokens, device tokens, database credentials or machine-specific private data.
- Never upload a raw `SRUDB.dat`, registry hive, Windows SID, username, PID history or full local executable path by default.
- Never repair or mutate the live Windows SRUM database; operate on copies when historical import requires it.
- Device/backend credentials must be stored using OS-protected storage in the desktop client once sync is implemented.
- Backend sync must use TLS and per-device revocable credentials.

## Reporting

For now, report security issues privately to the repository owner rather than opening a public issue.
