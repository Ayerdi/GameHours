namespace GameHours.Windows.Srum;

public sealed record SrumApplicationUsage(
    int AppId,
    string Application,
    string? UserSid,
    DateTimeOffset RecordedAtUtc,
    TimeSpan FaceTime);
