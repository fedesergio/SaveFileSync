namespace SaveFileSync.Core.Model;

/// <summary>How finely local and remote changes are compared (PLAN.md §6.4).</summary>
public enum MergeGranularity
{
    /// <summary>The whole unit is one group: any change on both sides is a conflict.</summary>
    Unit,
    /// <summary>Each first-level folder under a location is a group (phase 1b).</summary>
    Folder,
    /// <summary>Each file is a group (phase 1b).</summary>
    File,
}

public enum UnitMode
{
    /// <summary>Two-way sync.</summary>
    Sync,
    /// <summary>History only; never pulled automatically (phase 5, Steam).</summary>
    BackupOnly,
}

public enum VersionKind
{
    Normal,
    Merge,
    Resolve,
    Restore,
    Initial,
}
