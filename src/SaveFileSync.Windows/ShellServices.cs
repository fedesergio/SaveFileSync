using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SaveFileSync.Core.Paths;

namespace SaveFileSync.Windows;

/// <summary>
/// Known folders through <c>SHGetKnownFolderPath</c>, not environment variables: a Documents
/// folder moved to OneDrive or another drive, or a localized name, resolves correctly (PLAN.md §7.1).
/// </summary>
public static partial class ShellKnownFolders
{
    private static readonly Guid Profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    private static readonly Guid RoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    private static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid Public = new("DFDF76A2-C82A-4D63-906A-5644AC457385");
    private static readonly Guid ProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    private static readonly Guid Windows = new("F38BF404-1D43-42F2-9305-67DE0B28FC23");
    private static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");

    public static KnownFolderSet Get()
    {
        var fallback = KnownFolderSet.FromEnvironment();
        return new KnownFolderSet
        {
            Home = Query(Profile) ?? fallback.Home,
            AppData = Query(RoamingAppData) ?? fallback.AppData,
            LocalAppData = Query(LocalAppData) ?? fallback.LocalAppData,
            LocalAppDataLow = Query(LocalAppDataLow) ?? fallback.LocalAppDataLow,
            Documents = Query(Documents) ?? fallback.Documents,
            Public = Query(Public) ?? fallback.Public,
            ProgramData = Query(ProgramData) ?? fallback.ProgramData,
            WinDir = Query(Windows) ?? fallback.WinDir,
            UserName = Environment.UserName,
        };
    }

    /// <summary>Folders that Windows Security's "Controlled folder access" protects by default.</summary>
    internal static IReadOnlyList<string> GetProtectedFolders() =>
        new[] { Documents, Pictures, Videos, Music, Desktop }
            .Select(Query)
            .OfType<string>()
            .ToList();

    private static string? Query(Guid id)
    {
        var hr = SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pointer);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>Folder of the exe of a running process; needs no admin rights for the user's own processes.</summary>
public sealed class WindowsProcessPaths : IProcessPaths
{
    public string? FindFolder(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file)
                    {
                        return Path.GetDirectoryName(file);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Elevated or already exited: skip.
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Windows Security "Controlled folder access" can block unknown apps from writing to Documents,
/// Pictures and similar folders (PLAN.md §7.2). We only detect and explain: allowing the app is the user's call.
/// </summary>
public static class ControlledFolderAccess
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";

    /// <summary>True when the feature is on (blocking). Unknown or audit-only counts as off.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(KeyPath);
            return key?.GetValue("EnableControlledFolderAccess") is int value && value == 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>A warning for a save folder that Controlled folder access would protect, or null.</summary>
    public static string? WarningFor(string folder)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var protectedRoot = ShellKnownFolders.GetProtectedFolders()
            .FirstOrDefault(p => full.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return protectedRoot is null
            ? null
            : $"'{folder}' is in a folder protected by Windows Security (Controlled folder access). " +
              "If saves cannot be written, allow SaveFileSync in Windows Security → Virus & threat protection → " +
              "Ransomware protection → Allow an app through Controlled folder access.";
    }
}
