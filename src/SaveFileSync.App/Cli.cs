using SaveFileSync.Core.Config;
using SaveFileSync.Core.Recovery;
using SaveFileSync.Repository;
using SaveFileSync.Windows;

namespace SaveFileSync.App;

/// <summary>Read-only recovery commands: work without the sync engine (PLAN.md §4.1).</summary>
public static class Cli
{
    public static int Run(CommandLine cmd)
    {
        ConsoleAttach.AttachToParent();
        Console.WriteLine();
        try
        {
            return cmd.Command switch
            {
                "list" => List(cmd),
                "export" => Export(cmd),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            SaveFileSync — recovery commands (read-only, the sync engine is not started)

              SaveFileSync.exe list   [--repo <folder>]
              SaveFileSync.exe export <game name or unit id> <target folder> [--version <id>] [--repo <folder>]

            Without --repo, the repository configured on this PC is used.
            From cmd.exe, run as:  start /wait SaveFileSync.exe export ...
            """);
        return 0;
    }

    private static int List(CommandLine cmd)
    {
        var recovery = new RecoveryService(OpenRepository(cmd));
        var units = recovery.ListUnits();
        if (units.Count == 0)
        {
            Console.WriteLine("No games in this repository.");
            return 0;
        }

        foreach (var unit in units)
        {
            Console.WriteLine($"{unit.Name}  [{unit.UnitId}]{(unit.Removed ? "  (removed)" : "")}");
            foreach (var v in unit.Versions)
            {
                var flags = (v.IsTip ? " latest" : "")
                            + (v.IsArchived ? " ARCHIVED (files removed)" : v.IsComplete ? "" : " INCOMPLETE");
                Console.WriteLine($"    {v.VersionId}  {v.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {v.DeviceName,-16} {v.FileCount,4} files {FormatSize(v.TotalSize),10}  {v.Kind}{flags}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int Export(CommandLine cmd)
    {
        if (cmd.Positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: SaveFileSync.exe export <game name or unit id> <target folder> [--version <id>] [--repo <folder>]");
            return 2;
        }

        var recovery = new RecoveryService(OpenRepository(cmd));
        var unitId = recovery.FindUnit(cmd.Positional[0]);
        if (unitId is null)
        {
            Console.Error.WriteLine($"No single game matches '{cmd.Positional[0]}'. Use 'list' to see names and ids.");
            return 2;
        }

        var target = Path.GetFullPath(cmd.Positional[1]);
        var report = recovery.Export(unitId, target, cmd.Version);
        foreach (var folder in report.ExportedFolders)
        {
            Console.WriteLine($"Exported to {folder}");
        }

        Console.WriteLine($"{report.FilesWritten} file(s) written.");
        foreach (var problem in report.Problems)
        {
            Console.Error.WriteLine($"  problem: {problem}");
        }

        return report.Problems.Count == 0 ? 0 : 3;
    }

    private static FolderRemoteStore OpenRepository(CommandLine cmd)
    {
        var path = cmd.Repo;
        if (path is null)
        {
            var paths = AppPaths.ForProfile(cmd.Profile);
            path = File.Exists(paths.ConfigFile) ? new ConfigStore(paths.ConfigFile).Snapshot().RepositoryPath : null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("No repository configured on this PC: pass --repo <folder>.");
        }

        var store = new FolderRemoteStore(path);
        if (!store.IsAvailable())
        {
            throw new InvalidOperationException($"'{path}' is not a SaveFileSync repository (repo.json not found).");
        }

        return store;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };
}
