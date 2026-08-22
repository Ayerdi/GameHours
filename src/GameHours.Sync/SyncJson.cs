using System.Text.Json;

namespace GameHours.Sync;

internal static class SyncJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
