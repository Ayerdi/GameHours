namespace GameHours.Core.Abstractions;

/// <summary>
/// Provides privacy-minimal interaction state for active-play estimation.
/// Implementations expose only the foreground process and elapsed idle time;
/// raw keys, buttons, pointer positions and input contents never cross this boundary.
/// </summary>
public interface IUserInteractionStateProvider
{
    ValueTask<UserInteractionState> GetStateAsync(
        CancellationToken cancellationToken = default);
}

public readonly record struct UserInteractionState(
    int? ForegroundProcessId,
    TimeSpan IdleDuration);
