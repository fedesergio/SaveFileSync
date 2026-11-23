using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest.Tests;

/// <summary>A temporary folder tree that stands for one PC's disk; deleted on dispose.</summary>
public sealed class FakePc : IDisposable
{
    public FakePc()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfs-man-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Home = Dir("Users/tester");
        AppData = Dir("Users/tester/AppData/Roaming");
        LocalAppData = Dir("Users/tester/AppData/Local");
        LocalLow = Dir("Users/tester/AppData/LocalLow");
        Documents = Dir("Users/tester/Documents");
    }

    public string Root { get; }

    public string Home { get; }

    public string AppData { get; }

    public string LocalAppData { get; }

    public string LocalLow { get; }

    public string Documents { get; }

    public KnownFolderSet Folders => new()
    {
        Home = Home,
        AppData = AppData,
        LocalAppData = LocalAppData,
        LocalAppDataLow = LocalLow,
        Documents = Documents,
        Public = Dir("Users/Public"),
        ProgramData = Dir("ProgramData"),
        WinDir = Dir("Windows"),
        UserName = "tester",
    };

    public ResolveContext Context(IReadOnlyList<string>? roots = null, IReadOnlyList<string>? installDirs = null) => new()
    {
        Folders = Folders,
        LibraryRoots = roots ?? [],
        InstallDirs = installDirs ?? [],
    };

    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content = "data")
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Games
{
    public static GameRecord Game(string name, params PathRule[] paths) => new()
    {
        Name = name,
        Paths = paths,
        InstallDirs = [name],
    };

    public static PathRule Save(string template) => new(PathTemplate.Normalize(template), Save: true, Config: false, SteamOnly: false);

    public static PathRule Config(string template) => new(PathTemplate.Normalize(template), Save: false, Config: true, SteamOnly: false);
}
