using Microsoft.Win32;

namespace SaveFileSync.Windows;

/// <summary>Where Steam is installed, from the current user's registry (read-only).</summary>
public static class SteamRegistry
{
    public static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
