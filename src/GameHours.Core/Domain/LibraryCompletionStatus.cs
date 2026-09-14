namespace GameHours.Core.Domain;

public enum LibraryCompletionStatus
{
    Unspecified = 0,
    Backlog = 1,
    Playing = 2,
    Completed = 3,
    Abandoned = 4,
    // Keep this appended so v8 development databases that already stored 3/4 retain their
    // Completed/Abandoned meaning after Gestor de Juegos compatibility is added.
    Paused = 5
}
