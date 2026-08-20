namespace GameHours.Windows.Srum;

public sealed record SrumTableSchema(
    string Name,
    IReadOnlyList<string> Columns);
