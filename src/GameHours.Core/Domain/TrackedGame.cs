namespace GameHours.Core.Domain;

public sealed record TrackedGame
{
    public Guid Id { get; }
    public string Title { get; }

    public TrackedGame(Guid id, string title)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Game title cannot be empty.", nameof(title));
        }

        Id = id;
        Title = title.Trim();
    }
}
