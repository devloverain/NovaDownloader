using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NovaDownloader.ExtensionHost.Logging;

namespace NovaDownloader.ExtensionHost.Registry;

public static class HostInstaller
{
    public const string HostName = "com.novadownloader.host";
    public const string HostDescription = "NovaDownloader Native Messaging Host for Chrome & Edge";

    // Deterministic NovaDownloader extension ID (from manifest.json "key") + legacy dev IDs
    private static readonly string[] DefaultAllowedOrigins =
    {
        "chrome-extension://maninekbhekackeejegfbnelaognjaob/",
        "chrome-extension://knldjmfmddgdpolphpmdmbfpdocgmlon/",
        "chrome-extension://eafggjebnkkjllflmchmccfgckpogbhk/",
        "chrome-extension://nljpmlghmepbblfllljgeffcghklnmef/",
        "chrome-extension://pbgjfpjhhblfkjhffgacghffflnlnffg/"
    };

    public static string GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NovaDownloader.ExtensionHost.exe");
        }
        return Path.GetFullPath(exePath);
    }

    public static string GetManifestPath(string? targetDir = null)
    {
        var dir = targetDir ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(dir, $"{HostName}.json");
    }

    public static string GenerateManifestFile(string? targetDirectory = null, string[]? allowedOrigins = null)
    {
        var exePath = GetExecutablePath();
        var manifestPath = GetManifestPath(targetDirectory);

        var manifestData = new
        {
            name = HostName,
            description = HostDescription,
            path = exePath,
            type = "stdio",
            allowed_origins = allowedOrigins ?? DefaultAllowedOrigins
        };

        var json = JsonSerializer.Serialize(manifestData, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(manifestPath, json);
        HostLogger.Info($"Manifest written to: {manifestPath}");
        return manifestPath;
    }

    public static bool InstallRegistry(string manifestPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            HostLogger.Warn("Native messaging host registry install is only supported on Windows.");
            return false;
        }

        try
        {
            var targets = new[]
            {
                @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
                @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\Opera Stable\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\" + HostName,
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
                @"Software\Vivaldi\NativeMessagingHosts\" + HostName
            };

            foreach (var subKeyPath in targets)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                key.SetValue(null, manifestPath); // Default value is manifest full path
                HostLogger.Info($"Registered registry key: HKCU\\{subKeyPath} -> {manifestPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            HostLogger.Error("Failed to register native messaging host in Windows Registry", ex);
            return false;
        }
    }

    public static bool UninstallRegistry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            var targets = new[]
            {
                @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
                @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\Opera Stable\NativeMessagingHosts\" + HostName,
                @"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\" + HostName,
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
                @"Software\Vivaldi\NativeMessagingHosts\" + HostName
            };

            foreach (var subKeyPath in targets)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
                HostLogger.Info($"Removed registry key: HKCU\\{subKeyPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            HostLogger.Error("Failed to remove native messaging host from Windows Registry", ex);
            return false;
        }
    }

    public static void GenerateBatchScripts(string outputDirectory)
    {
        var manifestPath = Path.Combine(outputDirectory, $"{HostName}.json");
        var exePath = Path.Combine(outputDirectory, "NovaDownloader.ExtensionHost.exe");

        // Install batch script
        var installBat = $@"@echo off
setlocal enabledelayedexpansion
title NovaDownloader - Install Native Messaging Host

echo =======================================================
echo   NovaDownloader Chrome / Edge Native Host Installer
echo =======================================================
echo.

set ""HOST_DIR=%~dp0""
set ""HOST_EXE=%~dp0NovaDownloader.ExtensionHost.exe""
set ""MANIFEST_PATH=%~dp0{HostName}.json""

echo Target Executable: ""!HOST_EXE!""
echo Target Manifest:   ""!MANIFEST_PATH!""
echo.

if not exist ""!HOST_EXE!"" (
    echo [ERROR] Native Host binary not found at ""!HOST_EXE!""
    echo Please build the project first using: dotnet build
    pause
    exit /b 1
)

:: Run host with --install to create manifest and register keys
""!HOST_EXE!"" --install

:: Also ensure direct registry registration for Chrome and Edge
echo.
echo Registering Native Messaging Host for Google Chrome...
reg add ""HKCU\Software\Google\Chrome\NativeMessagingHosts\{HostName}"" /ve /t REG_SZ /d ""!MANIFEST_PATH!"" /f >nul
if %ERRORLEVEL% equ 0 (
    echo [OK] Chrome registered successfully.
) else (
    echo [WARN] Failed to write Chrome registry key.
)

echo Registering Native Messaging Host for Microsoft Edge...
reg add ""HKCU\Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"" /ve /t REG_SZ /d ""!MANIFEST_PATHExternal:!"" /f >nul
reg add ""HKCU\Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"" /ve /t REG_SZ /d ""!MANIFEST_PATH!"" /f >nul
if %ERRORLEVEL% equ 0 (
    echo [OK] Edge registered successfully.
) else (
    echo [WARN] Failed to write Edge registry key.
)

echo Registering Native Messaging Host for Brave Browser...
reg add ""HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{HostName}"" /ve /t REG_SZ /d ""!MANIFEST_PATH!"" /f >nul

echo.
echo =======================================================
echo [SUCCESS] NovaDownloader Native Messaging Host installed!
echo You can now reload the Chrome/Edge extension.
echo =======================================================
echo.
pause
";

        // Uninstall batch script
        var uninstallBat = $@"@echo off
setlocal
title NovaDownloader - Uninstall Native Messaging Host

echo =======================================================
echo   NovaDownloader Chrome / Edge Native Host Uninstaller
echo =======================================================
echo.

set ""HOST_EXE=%~dp0NovaDownloader.ExtensionHost.exe""

if exist ""%HOST_EXE%"" (
    ""%HOST_EXE%"" --uninstall
)

echo Removing Chrome registration...
reg delete ""HKCU\Software\Google\Chrome\NativeMessagingHosts\{HostName}"" /f >nul 2>&1
echo Removing Edge registration...
reg delete ""HKCU\Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"" /f >nul 2>&1
echo Removing Brave registration...
reg delete ""HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{HostName}"" /f >nul 2>&1

echo.
echo =======================================================
echo [SUCCESS] NovaDownloader Native Host uninstalled.
echo =======================================================
echo.
pause
";

        File.WriteAllText(Path.Combine(outputDirectory, "install_host.bat"), installBat);
        File.WriteAllText(Path.Combine(outputDirectory, "uninstall_host.bat"), uninstallBat);
        HostLogger.Info($"Batch scripts generated in: {outputDirectory}");
    }
}
