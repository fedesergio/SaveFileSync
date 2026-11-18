using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.Service;

public enum UnitState
{
    /// <summary>Not synced yet since the app started.</summary>
    Pending,
    Synced,
    /// <summary>Quiet period running, game just closed, or OneDrive still delivering files.</summary>
    Waiting,
    GameRunning,
    /// <summary>Game running and a newer save from another PC is waiting (PLAN.md §6.6).</summary>
    Stale,
    Syncing,
    Conflict,
    /// <summary>Needs a decision that is not a conflict (e.g. local saves disappeared).</summary>
    Attention,
    Error,
    Paused,
    NotSetUp,
    LocationMissing,
    RepositoryUnavailable,
}

public sealed record UnitStatus(
    string UnitId,
    string Name,
    UnitState State,
    string Detail,
    DateTime? LastSyncUtc,
    string? LastChangeDevice,
    DateTime? QuietUntilUtc,
    bool GameRunning,
    bool IsBound,
    ConflictInfo? Conflict);

public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

public enum ActivityKind
{
    Info,
    Uploaded,
    Downloaded,
    Merged,
    Conflict,
    Resolved,
    Warning,
    Error,
}

public sealed record ActivityEntry(DateTime TimeUtc, string UnitName, ActivityKind Kind, string Message);

public sealed record SyncNotification(string Title, string Message, NotificationLevel Level, string? UnitId);
