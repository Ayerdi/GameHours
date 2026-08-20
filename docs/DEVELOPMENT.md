# Development

## Prerequisites

- Windows 10/11 for Windows-specific runtime tests;
- .NET 8 SDK.

## Build and test

```powershell
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release
dotnet test GameHours.sln -c Release
```

## Run the development host

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj
```

It creates `%LOCALAPPDATA%\GameHours\gamehours.db` and prints the current visible process count. It deliberately does **not** fix `tracking_started_at` or record playtime yet; the real tracker activation will own that cutover.

## Quality gate without GitHub Actions

Until CI is introduced, do not merge changes without a clean local Release build and test run on a Windows machine with .NET 8 installed.
