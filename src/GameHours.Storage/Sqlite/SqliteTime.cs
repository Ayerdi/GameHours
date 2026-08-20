namespace GameHours.Storage.Sqlite;

internal static class SqliteTime
{
    public static string Serialize(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

    public static DateTimeOffset Deserialize(string value) =>
        DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
}
