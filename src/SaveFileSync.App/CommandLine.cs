namespace SaveFileSync.App;

/// <summary>
/// <c>SaveFileSync.exe [--background] [--profile name]</c> starts the app;
/// <c>list</c>, <c>export</c> and <c>help</c> run the recovery CLI (PLAN.md §4.1).
/// </summary>
public sealed class CommandLine
{
    public string? Command { get; private init; }

    public IReadOnlyList<string> Positional { get; private init; } = [];

    public string? Profile { get; private init; }

    public string? Repo { get; private init; }

    public string? Version { get; private init; }

    public bool Background { get; private init; }

    public bool IsCli => Command is "list" or "export" or "help" or "--help" or "-h" or "/?";

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        string? profile = null, repo = null, version = null, command = null;
        var background = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--profile" when i + 1 < args.Count:
                    profile = args[++i];
                    break;
                case "--repo" when i + 1 < args.Count:
                    repo = args[++i];
                    break;
                case "--version" when i + 1 < args.Count:
                    version = args[++i];
                    break;
                case "--background":
                    background = true;
                    break;
                default:
                    if (command is null)
                    {
                        command = a.ToLowerInvariant();
                    }
                    else
                    {
                        positional.Add(a);
                    }

                    break;
            }
        }

        return new CommandLine
        {
            Command = command,
            Positional = positional,
            Profile = profile,
            Repo = repo,
            Version = version,
            Background = background,
        };
    }
}
