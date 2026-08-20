# Sources and external references

GameHours distinguishes verified local behavior from external documentation.

## Windows artifacts

- Microsoft Windows SRUM / `SRUDB.dat`: historical evidence source investigated by the project.
- Eric Zimmerman's SrumECmd: used only as a reference parser during feasibility testing; the production design should not require modifying the live SRUM database.
- Windows UserAssist: secondary execution/focus evidence.

## Dependencies

- `Microsoft.Data.Sqlite` for local SQLite persistence.
- xUnit for tests.

Detailed measured findings live in `docs/VERIFIED-FINDINGS.md`.
