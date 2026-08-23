# Development

## Prerequisites

- Windows 10/11 for the desktop app and Windows-specific runtime tests;
- .NET 8 SDK;
- PowerShell 7 or Windows PowerShell 5.1 for repository scripts.

## Restore, build and test

Use the same core sequence as CI:

```powershell
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release --no-restore
dotnet test GameHours.sln -c Release --no-build --logger "console;verbosity=normal"
```

Do not hard-code an expected test count in scripts or documentation. The authoritative gate is that every test discovered by the current solution passes; any unexpected drop in discovered tests should be investigated rather than accepted silently.

## Run the desktop application

The primary development application is the WPF desktop project:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj
```

Start directly in the notification area:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj -- --background
```

GameHours stores its normal local state under `%LOCALAPPDATA%\GameHours`. Do not commit local databases, logs, machine-specific paths or generated artifacts.

## Diagnostic host

`GameHours.App` remains available for focused diagnostic commands; it is not the normal desktop entry point.

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- scan
dotnet run --project src/GameHours.App/GameHours.App.csproj -- diagnose
dotnet run --project src/GameHours.App/GameHours.App.csproj -- track
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
```

## CI

`.github/workflows/ci.yml` runs on pull requests, direct pushes to `main` and manual dispatches. It restores, builds, runs the complete test suite and smoke-publishes `GameHours.Desktop` on Windows.

Velopack packaging is intentionally skipped for draft pull-request synchronizations. The packaging smoke runs when the pull request is ready for review, on `main`, or through a manual workflow dispatch. Superseded runs for the same ref are cancelled.

Third-party Actions are pinned to full commit SHAs. Keep the human-readable major version in the adjacent comment and update the SHA deliberately when upgrading the Action.

## Local packaging smoke

Restore the repository-pinned Velopack CLI, then use the shared packaging script:

```powershell
dotnet tool restore
.\scripts\package-windows.ps1 -Version 0.0.1-local -Channel beta
```

The script validates the generated release before reporting success. Production distribution additionally requires the signing/update-origin work tracked in `docs/DISTRIBUTION.md`.

## Validation policy

A change is not considered verified merely because it compiles. Before merge, require a green CI run for the exact head under review. Changes that depend on real Windows behavior must also complete the relevant checks in `docs/REAL-MACHINE-VALIDATION.md` before those behaviors are claimed as validated.

If GitHub-hosted CI is unavailable, a clean local Release build/test can provide useful evidence, but the hosted CI requirement remains pending rather than silently treated as satisfied.
