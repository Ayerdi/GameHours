using System.Text.Json;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Reads only public global achievement names from Steam. The endpoint is keyless and is used
/// solely to seed emulator definitions; user unlock state always remains local.
/// </summary>
internal sealed class SteamGlobalAchievementNameClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    internal async Task<IReadOnlyList<string>?> FetchAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit))
        {
            return null;
        }

        var uri = new Uri(
            $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={Uri.EscapeDataString(appId)}",
            UriKind.Absolute);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SharedHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseNames(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> ParseNames(JsonElement root)
    {
        if (!TryGetProperty(root, "achievementpercentages", out var percentages) ||
            !TryGetProperty(percentages, "achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return achievements
            .EnumerateArray()
            .Select(item => TryGetProperty(item, "name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
