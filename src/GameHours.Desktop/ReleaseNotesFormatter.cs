using System.Text;
using System.Text.RegularExpressions;

namespace GameHours.Desktop;

internal static partial class ReleaseNotesFormatter
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "No hay notas de versión disponibles para esta actualización.";
        }

        var output = new StringBuilder();
        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (output.Length > 0 && output[^1] != '\n')
                {
                    output.AppendLine();
                }

                continue;
            }

            var trimmed = line.TrimStart();
            while (trimmed.StartsWith('#'))
            {
                trimmed = trimmed[1..].TrimStart();
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                trimmed = "• " + trimmed[2..].TrimStart();
            }

            trimmed = MarkdownLinkRegex().Replace(trimmed, "$1");
            trimmed = trimmed
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal);

            output.AppendLine(trimmed);
        }

        return output.ToString().Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkRegex();
}
