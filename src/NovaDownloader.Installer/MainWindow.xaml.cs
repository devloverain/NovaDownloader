using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace NovaDownloader.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "NovaDownloader");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose NovaDownloader installation folder",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog() == true)
        {
            InstallPathBox.Text = Path.Combine(dlg.FolderName, "NovaDownloader");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var targetDir = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show("Please specify a valid install path.", "Installer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstallBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        try
        {
            StatusText.Text = "Closing any running NovaDownloader instances...";
            InstallProgress.Value = 15;
            await Task.Run(StopRunningInstances);

            StatusText.Text = "Extracting NovaDownloader files...";
            InstallProgress.Value = 35;
            await Task.Run(() => ExtractEmbeddedPayload(targetDir));

            var uiExe = Path.Combine(targetDir, "NovaDownloader.UI.exe");
            var hostExe = Path.Combine(targetDir, "NovaDownloader.ExtensionHost.exe");

            if (RegisterHostCheckBox.IsChecked == true && File.Exists(hostExe))
            {
                StatusText.Text = "Registering Browser Native Messaging Host...";
                InstallProgress.Value = 65;
                await Task.Run(() =>
                {
                    using var p = Process.Start(new ProcessStartInfo(hostExe, "--install")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = targetDir
                    });
                    p?.WaitForExit(5000);
                });
            }

            StatusText.Text = "Creating shortcuts & uninstaller...";
            InstallProgress.Value = 85;
            var createDesktop = DesktopShortcutCheckBox.IsChecked == true;
            await Task.Run(() =>
            {
                CreateShortcuts(uiExe, targetDir, createDesktop);
                RegisterUninstaller(targetDir, uiExe, hostExe);
            });

            if (StartupCheckBox.IsChecked == true)
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.SetValue("NovaDownloader", $"\"{uiExe}\" --minimized", RegistryValueKind.String);
                }
                catch { }
            }

            InstallProgress.Value = 100;
            StatusText.Text = "Installation complete!";

            if (LaunchNowCheckBox.IsChecked == true && File.Exists(uiExe))
            {
                Process.Start(new ProcessStartInfo(uiExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = targetDir
                });
            }

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
    }

    private static void StopRunningInstances()
    {
        foreach (var name in new[] { "NovaDownloader.UI", "NovaDownloader.ExtensionHost" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch { }
            }
        }
    }

    private static void ExtractEmbeddedPayload(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded installer payload not found.");

        using var stream = asm.GetManifestResourceStream(resName)!;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(targetDir, overwriteFiles: true);
    }

    private static void CreateShortcuts(string targetExe, string workingDir, bool includeDesktop)
    {
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        CreateShortcut(Path.Combine(startMenuDir, "NovaDownloader.lnk"), targetExe, workingDir);

        if (includeDesktop)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktopDir, "NovaDownloader.lnk"), targetExe, workingDir);
        }
    }

    private static void CreateShortcut(string lnkPath, string targetExe, string workingDir)
    {
        try
        {
            var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnkPath.Replace("'", "''")}');" +
                     $"$s.TargetPath='{targetExe.Replace("'", "''")}';" +
                     $"$s.WorkingDirectory='{workingDir.Replace("'", "''")}';" +
                     $"$s.Save()";
            using var proc = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"{ps}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            proc?.WaitForExit(4000);
        }
        catch { }
    }

    private static void RegisterUninstaller(string targetDir, string uiExe, string hostExe)
    {
        try
        {
            var uninstallScriptPath = Path.Combine(targetDir, "uninstall.ps1");
            var script = $$"""
                Add-Type -AssemblyName System.Windows.Forms
                $res = [System.Windows.Forms.MessageBox]::Show(
                    "Are you sure you want to uninstall NovaDownloader?",
                    "Uninstall NovaDownloader",
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Question)
                if ($res -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }

                foreach ($p in @("NovaDownloader.UI", "NovaDownloader.ExtensionHost")) {
                    Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                }
                Start-Sleep -Milliseconds 400

                $hostExe = "{{hostExe.Replace("'", "''")}}"
                if (Test-Path $hostExe) {
                    Start-Process -FilePath $hostExe -ArgumentList "--uninstall" -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
                }

                Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "NovaDownloader" -ErrorAction SilentlyContinue
                Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaDownloader" -Recurse -Force -ErrorAction SilentlyContinue

                $desktopLnk = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "NovaDownloader.lnk"
                $startLnk = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\NovaDownloader.lnk"
                Remove-Item $desktopLnk -Force -ErrorAction SilentlyContinue
                Remove-Item $startLnk -Force -ErrorAction SilentlyContinue

                $dir = "{{targetDir.Replace("'", "''")}}"
                Start-Process -FilePath "cmd.exe" -ArgumentList "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q `"$dir`"" -WindowStyle Hidden

                [System.Windows.Forms.MessageBox]::Show(
                    "NovaDownloader has been uninstalled.",
                    "Uninstall Complete",
                    [System.Windows.Forms.MessageBoxButtons]::OK,
                    [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
                """;

            File.WriteAllText(uninstallScriptPath, script);

            long totalBytes = 0;
            foreach (var f in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                try { totalBytes += new FileInfo(f).Length; } catch { }
            }
            int estimatedKb = (int)Math.Max(1, totalBytes / 1024);

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NovaDownloader", true);
            if (key != null)
            {
                key.SetValue("DisplayName", "NovaDownloader", RegistryValueKind.String);
                key.SetValue("DisplayVersion", "1.2.0", RegistryValueKind.String);
                key.SetValue("Publisher", "devloverain", RegistryValueKind.String);
                key.SetValue("InstallLocation", targetDir, RegistryValueKind.String);
                key.SetValue("DisplayIcon", uiExe, RegistryValueKind.String);
                key.SetValue("UninstallString", $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{uninstallScriptPath}\"", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
