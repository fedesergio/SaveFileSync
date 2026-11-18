using CommunityToolkit.Mvvm.ComponentModel;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.App.Views;

/// <summary>Editable row for one save location in the add/details windows.</summary>
public sealed partial class LocationRow : ObservableObject
{
    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string _includes = "";

    [ObservableProperty]
    private string _excludes = "";

    public string Id { get; set; } = "";

    public string? Label { get; set; }

    public static IReadOnlyList<string> Split(string text) =>
        text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public LocationDefinition ToDefinition() => new()
    {
        Id = Id,
        Label = Label,
        Includes = Split(Includes) is { Count: > 0 } inc ? inc : ["**"],
        Excludes = Split(Excludes),
    };

    /// <summary>Files and total size that would be synced with the current filters.</summary>
    public (int Files, long Bytes) Preview()
    {
        if (!Directory.Exists(Path))
        {
            return (0, 0);
        }

        var definition = ToDefinition();
        var filter = new PathFilter(definition.Includes, definition.Excludes);
        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var relative = System.IO.Path.GetRelativePath(Path, file.FullName).Replace('\\', '/');
            if (filter.IsIncluded(relative))
            {
                files++;
                bytes += file.Length;
            }
        }

        return (files, bytes);
    }

    /// <summary>A short, stable location id derived from the folder name ("savedata", "saves-2").</summary>
    public static string MakeId(string folder, IEnumerable<string> taken)
    {
        var name = new string(System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (name.Length == 0)
        {
            name = "saves";
        }

        if (name.Length > 24)
        {
            name = name[..24];
        }

        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = name;
        for (var i = 2; set.Contains(id); i++)
        {
            id = $"{name}-{i}";
        }

        return id;
    }
}
