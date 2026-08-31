using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (command is not ("backup" or "export"))
        {
            PrintUsage();
            return 2;
        }

        var databasePath = ResolveDatabasePath(args);
        if (!File.Exists(databasePath))
        {
            Console.Error.WriteLine($"GameHours database not found: {databasePath}");
            return 2;
        }

        var destination = ResolveDestination(command, args, databasePath);
        var database = new GameHoursDatabase(databasePath);
        var portability = new GameHoursDataPortabilityService(database);

        try
        {
            if (command == "backup")
            {
                var result = await portability.CreateBackupAsync(destination);
                Console.WriteLine("GameHours backup created.");
                Console.WriteLine($"Source:      {databasePath}");
                Console.WriteLine($"Destination: {result.Path}");
                Console.WriteLine($"Size:        {result.SizeBytes:N0} bytes");
                return 0;
            }

            var export = await portability.ExportPortableJsonAsync(destination);
            Console.WriteLine("GameHours portable export created.");
            Console.WriteLine($"Source:      {databasePath}");
            Console.WriteLine($"Destination: {export.Path}");
            Console.WriteLine($"Format:      v{export.FormatVersion}");
            Console.WriteLine($"Games:       {export.GameCount}");
            Console.WriteLine($"Sessions:    {export.SessionCount}");
            Console.WriteLine($"Historical:  {export.HistoricalEvidenceCount}");
            Console.WriteLine($"Achievements:{export.AchievementCount,9}");
            Console.WriteLine($"Ach. evidence:{export.AchievementEvidenceCount,8}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{command} failed: {exception.Message}");
            return 1;
        }
    }

    private static string ResolveDatabasePath(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--database", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours",
            "gamehours.db");
    }

    private static string ResolveDestination(string command, string[] args, string databasePath)
    {
        var positional = args
            .Skip(1)
            .TakeWhile(argument => !string.Equals(argument, "--database", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(positional))
        {
            return Path.GetFullPath(positional);
        }

        var dataDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Could not determine the GameHours data directory.");
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return command == "backup"
            ? Path.Combine(dataDirectory, "backups", $"gamehours-{timestamp}.db")
            : Path.Combine(dataDirectory, "exports", $"gamehours-{timestamp}.json");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  GameHours.Portability backup [destination.db] [--database path]");
        Console.Error.WriteLine("  GameHours.Portability export [destination.json] [--database path]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Without a destination, files are written under the GameHours data directory.");
    }
}
