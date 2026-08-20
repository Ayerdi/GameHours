using System.Security.Cryptography;
using System.Text;

namespace GameHours.Core.Discovery;

public static class DeterministicGameId
{
    public static Guid Create(string source, string externalId)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Source cannot be empty.", nameof(source));
        if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("External id cannot be empty.", nameof(externalId));

        var normalized = $"{source.Trim().ToLowerInvariant()}:{externalId.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash.AsSpan(0, 16));
    }
}
