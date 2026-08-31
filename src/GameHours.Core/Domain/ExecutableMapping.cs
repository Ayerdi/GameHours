namespace GameHours.Core.Domain;

public sealed record ExecutableMapping
{
    public Guid GameId { get; }
    public string ExecutablePath { get; }
    public string ExecutableName { get; }
    public bool IsHelper { get; }

    public ExecutableMapping(Guid gameId, string executablePath, bool isHelper)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));
        }

        GameId = gameId;
        ExecutablePath = Path.GetFullPath(executablePath);
        ExecutableName = Path.GetFileName(ExecutablePath);
        IsHelper = isHelper;
    }
}
