using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NovaDownloader.ExtensionHost.Logging;
using NovaDownloader.ExtensionHost.NativeMessaging;
using NovaDownloader.ExtensionHost.Registry;

namespace NovaDownloader.ExtensionHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Check CLI arguments for installation / configuration tasks
        if (args.Length > 0)
        {
            var command = args[0].ToLowerInvariant();
            if (command is "--install" or "-i" or "/install")
            {
                return HandleInstall();
            }
            if (command is "--uninstall" or "-u" or "/uninstall")
            {
                return HandleUninstall();
            }
            if (command is "--generate-manifest" or "-g")
            {
                return HandleGenerateManifest();
            }
            if (command is "--help" or "-h" or "/?")
            {
                ShowHelp();
                return 0;
            }
        }

        // 2. Normal Native Messaging Mode (invoked by Chrome / Edge)
        // CRITICAL: Console.Out must never be polluted by text writes.
        // Chrome interprets standard output as 4-byte length-prefixed JSON.
        // Redirect standard Console.Out to TextWriter.Null to protect binary stream.
        Console.SetOut(TextWriter.Null);

        HostLogger.Info($"NovaDownloader Extension Host launched (PID: {Environment.ProcessId}). Args: [{string.Join(", ", args)}]");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            HostLogger.Info("CancelKeyPress received. Requesting host shutdown...");
            cts.Cancel();
        };

        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var host = new NativeMessagingHost(stdin, stdout);
            await host.RunAsync(cts.Token);
            return 0;
        }
        catch (Exception ex)
        {
            HostLogger.Error("Unhandled fatal exception in Extension Host", ex);
            return 1;
        }
    }

    private static int HandleInstall()
    {
        Console.WriteLine("Installing NovaDownloader Native Messaging Host...");
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var manifestPath = HostInstaller.GenerateManifestFile(baseDir);
            Console.WriteLine($"[OK] Manifest created at: {manifestPath}");

            var registered = HostInstaller.InstallRegistry(manifestPath);
            if (registered)
            {
                Console.WriteLine("[OK] Registry entries created for Chrome, Edge, Brave, and Vivaldi.");
            }
            else
            {
                Console.WriteLine("[WARN] Failed to write one or more registry keys.");
            }

            HostInstaller.GenerateBatchScripts(baseDir);
            Console.WriteLine("[OK] Generated install_host.bat and uninstall_host.bat.");

            // Also copy scripts and manifest to the project source folder if running from build output
            TryCopyArtifactsToSourceFolder(baseDir, manifestPath);

            Console.WriteLine("\n[SUCCESS] Installation completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Installation failed: {ex.Message}");
            return 1;
        }
    }

    private static int HandleUninstall()
    {
        Console.WriteLine("Uninstalling NovaDownloader Native Messaging Host...");
        try
        {
            var removed = HostInstaller.UninstallRegistry();
            if (removed)
            {
                Console.WriteLine("[OK] Registry entries removed.");
            }
            else
            {
                Console.WriteLine("[WARN] Failed to remove some registry keys.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Uninstall failed: {ex.Message}");
            return 1;
        }
    }

    private static int HandleGenerateManifest()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var manifestPath = HostInstaller.GenerateManifestFile(baseDir);
        Console.WriteLine($"Manifest created: {manifestPath}");
        return 0;
    }

    private static void TryCopyArtifactsToSourceFolder(string baseDir, string manifestPath)
    {
        try
        {
            var projectDirCandidates = new[]
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")), // from bin/Debug/net10.0 to project root
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "NovaDownloader.ExtensionHost"))
            };

            foreach (var projDir in projectDirCandidates)
            {
                if (Directory.Exists(projDir) && File.Exists(Path.Combine(projDir, "NovaDownloader.ExtensionHost.csproj")))
                {
                    var targetManifest = Path.Combine(projDir, Path.GetFileName(manifestPath));
                    File.Copy(manifestPath, targetManifest, overwrite: true);

                    var srcInstallBat = Path.Combine(baseDir, "install_host.bat");
                    if (File.Exists(srcInstallBat))
                    {
                        File.Copy(srcInstallBat, Path.Combine(projDir, "install_host.bat"), overwrite: true);
                    }

                    var srcUninstallBat = Path.Combine(baseDir, "uninstall_host.bat");
                    if (File.Exists(srcUninstallBat))
                    {
                        File.Copy(srcUninstallBat, Path.Combine(projDir, "uninstall_host.bat"), overwrite: true);
                    }
                    break;
                }
            }
        }
        catch
        {
            // Non-critical, ignore
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("NovaDownloader Extension Host (.NET 10)");
        Console.WriteLine("Usage:");
        Console.WriteLine("  NovaDownloader.ExtensionHost.exe               Run in Native Messaging mode (standard I/O)");
        Console.WriteLine("  NovaDownloader.ExtensionHost.exe --install     Generate manifest & register in Windows Registry");
        Console.WriteLine("  NovaDownloader.ExtensionHost.exe --uninstall   Remove registry entries");
        Console.WriteLine("  NovaDownloader.ExtensionHost.exe --generate-manifest Generate JSON manifest");
    }
}
