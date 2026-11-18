using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SaveFileSync.Core;

namespace SaveFileSync.Windows;

/// <summary>Process polling: no admin rights needed (PLAN.md §6.5).</summary>
public sealed class WindowsProcessMonitor : IProcessMonitor
{
    public IReadOnlySet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName.ToLowerInvariant());
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating.
                }
            }
        }

        return names;
    }
}

/// <summary>Start with Windows through <c>HKCU\...\Run</c>; no admin rights needed.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ValueName(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? "SaveFileSync" : $"SaveFileSync ({profile})";

    public static bool IsEnabled(string? profile)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName(profile)) is string;
    }

    public static void Enable(string exePath, string? profile)
    {
        var args = string.IsNullOrWhiteSpace(profile) ? "--background" : $"--background --profile \"{profile}\"";
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName(profile), $"\"{exePath}\" {args}");
    }

    public static void Disable(string? profile)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName(profile), throwOnMissingValue: false);
    }
}

public static class MachineFingerprint
{
    /// <summary>Hash of the Windows MachineGuid (readable without admin rights).</summary>
    public static string? Get()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is not string guid)
            {
                return null;
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("SaveFileSync:" + guid)))[..16];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Copies the running exe to %LOCALAPPDATA%\Programs\SaveFileSync (PLAN.md §2.4).</summary>
public static class Installer
{
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SaveFileSync");

    public static string InstalledExePath => Path.Combine(InstallDirectory, "SaveFileSync.exe");

    public static bool IsRunningFromInstallDirectory(string exePath) =>
        string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase);

    public static string Install(string exePath)
    {
        Directory.CreateDirectory(InstallDirectory);
        File.Copy(exePath, InstalledExePath, overwrite: true);
        return InstalledExePath;
    }
}

/// <summary>Lets the WPF (WinExe) app print to the console it was started from.</summary>
public static partial class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    public static bool AttachToParent() => AttachConsole(AttachParentProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
