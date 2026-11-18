using System.Diagnostics;
using Microsoft.Win32;

namespace SaveFileSync.Windows;

/// <summary>Detection of OneDrive folders and "Always keep on this device" pinning.</summary>
public static class OneDrive
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>Local roots of the OneDrive accounts signed in for this user.</summary>
    public static IReadOnlyList<string> GetRoots()
    {
        var roots = new List<string>();
        using (var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
        {
            if (accounts is not null)
            {
                foreach (var name in accounts.GetSubKeyNames())
                {
                    using var account = accounts.OpenSubKey(name);
                    if (account?.GetValue("UserFolder") is string folder && !string.IsNullOrWhiteSpace(folder))
                    {
                        roots.Add(folder);
                    }
                }
            }
        }

        foreach (var variable in new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                roots.Add(value);
            }
        }

        return roots
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The personal OneDrive root, if any (preferred for the repository).</summary>
    public static string? GetPersonalRoot()
    {
        using var personal = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
        if (personal?.GetValue("UserFolder") is string folder && Directory.Exists(folder))
        {
            return folder;
        }

        return Environment.GetEnvironmentVariable("OneDriveConsumer") is { Length: > 0 } consumer && Directory.Exists(consumer)
            ? consumer
            : GetRoots().FirstOrDefault();
    }

    public static bool IsInsideOneDrive(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return GetRoots().Any(r => full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPinned(string folder) =>
        Directory.Exists(folder) && (File.GetAttributes(folder) & Pinned) != 0;

    /// <summary>
    /// Marks a folder and its contents "Always keep on this device" (<c>attrib +P -U</c>),
    /// so blobs are never cloud-only (PLAN.md §4, §9). New files inherit the pin.
    /// </summary>
    public static bool TryPin(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            RunAttrib($"+P -U \"{folder}\"");
            RunAttrib($"+P -U \"{Path.Combine(folder, "*")}\" /S /D");
            return IsPinned(folder) || (File.GetAttributes(folder) & Unpinned) == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void RunAttrib(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "attrib.exe"),
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process?.WaitForExit(60_000);
    }
}
