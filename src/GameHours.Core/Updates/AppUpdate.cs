namespace GameHours.Core.Updates;

public sealed record AppUpdate(
    string Version,
    string? ReleaseNotesMarkdown,
    long FullPackageSizeBytes,
    int DeltaCount,
    bool IsDowngrade);
