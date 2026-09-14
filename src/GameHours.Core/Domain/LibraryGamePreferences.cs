namespace GameHours.Core.Domain;

public sealed record LibraryGamePreferences(
    Guid GameId,
    bool IsFavorite = false,
    bool IsHidden = false,
    LibraryCompletionStatus CompletionStatus = LibraryCompletionStatus.Unspecified)
{
    public bool IsDefault =>
        !IsFavorite &&
        !IsHidden &&
        CompletionStatus == LibraryCompletionStatus.Unspecified;
}
