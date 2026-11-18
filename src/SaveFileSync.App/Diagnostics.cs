using System.IO.Compression;
using System.Text;

namespace SaveFileSync.App;

/// <summary>
/// "Export diagnostics" (PLAN.md §8, item 7): a zip with logs, config and repository
/// listing, with the user profile path and user name replaced by placeholders.
/// </summary>
public static class Diagnostics
{
    public static async Task ExportAsync(AppHost host, string zipPath)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        string Redact(string text) => text
            .Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void AddText(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(Redact(content));
        }

        if (File.Exists(host.Paths.ConfigFile))
        {
            AddText("config.json", await File.ReadAllTextAsync(host.Paths.ConfigFile));
        }

        foreach (var log in Directory.EnumerateFiles(host.Paths.LogsDirectory, "*.log").OrderDescending().Take(7))
        {
            // Serilog keeps the current file open: read it with sharing.
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            AddText("logs/" + Path.GetFileName(log), await reader.ReadToEndAsync());
        }

        var summary = new StringBuilder();
        summary.AppendLine($"App version: {AppHost.AppVersion}");
        summary.AppendLine($"OS: {Environment.OSVersion}");
        summary.AppendLine($"Repository available: {host.Remote?.IsAvailable()}");
        if (host.Service is { } service)
        {
            foreach (var s in service.CurrentStatus)
            {
                summary.AppendLine($"{s.Name} [{s.UnitId}]: {s.State} — {s.Detail}");
            }

            var ignored = await service.FindIgnoredRepositoryFilesAsync();
            summary.AppendLine($"Ignored repository files (OneDrive conflict copies etc.): {ignored.Count}");
            foreach (var file in ignored)
            {
                summary.AppendLine("  " + file);
            }
        }

        AddText("summary.txt", summary.ToString());
    }
}
