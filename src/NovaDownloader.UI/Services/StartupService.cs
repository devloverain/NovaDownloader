using System.IO;
using Microsoft.Win32;

namespace NovaDownloader.UI.Services;

/// <summary>
/// Manages Windows startup registration under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "NovaDownloader";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (!enabled)
            {
                if (key.GetValue(AppValueName) != null)
                {
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                }
                return;
            }

            var exePath = ResolveAppExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath)) return;

            var command = startMinimized
                ? $"\"{exePath}\" --minimized"
                : $"\"{exePath}\"";

            key.SetValue(AppValueName, command, RegistryValueKind.String);
        }
        catch
        {
            // Ignore registry permission issues
        }
    }

    private static string? ResolveAppExecutablePath()
    {
        var baseExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NovaDownloader.UI.exe");
        if (File.Exists(baseExe))
            return baseExe;

        return Environment.ProcessPath;
    }
}
