using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Paths;

/// <summary>
/// Finds the save folders of a unit on this PC without asking the user
/// (PLAN.md §7): used to set up, on the second PC, games that the first PC added.
/// </summary>
public interface ILocationResolver
{
    /// <summary>
    /// Location id → absolute folder, when <b>every</b> location resolves to exactly one
    /// existing folder; otherwise <c>null</c> (the user has to choose).
    /// </summary>
    IReadOnlyDictionary<string, string>? TryResolve(UnitDefinition unit);
}

/// <summary>Finds the folder of a running program, for emulator detection.</summary>
public interface IProcessPaths
{
    /// <summary>Folder of the exe of a running process (name without ".exe"), or null.</summary>
    string? FindFolder(string processName);
}
