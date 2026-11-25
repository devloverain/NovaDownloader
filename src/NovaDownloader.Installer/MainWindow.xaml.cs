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

            if (DownloadToolsCheckBox.IsChecked == true)
            {
                StatusText.Text = "Installing yt-dlp.exe & ffmpeg.exe (for YouTube / HLS)...";
                InstallProgress.Value = 78;
                await DownloadMediaToolsAsync(msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg));
            }

            StatusText.Text = "Creating shortcuts...";
            InstallProgress.Value = 90;
            var createDesktop = DesktopShortcutCheckBox.IsChecked == true;
            await Task.Run(() => CreateShortcuts(uiExe, targetDir, createDesktop));

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

    private static async Task DownloadMediaToolsAsync(Action<string> onStatus)
    {
        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NovaDownloader",
            "Tools");
        Directory.CreateDirectory(toolsDir);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaDownloader-Setup/1.2");

        var ytDlpPath = Path.Combine(toolsDir, "yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            try
            {
                onStatus("Downloading yt-dlp.exe...");
                using var resp = await client.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var tmp = ytDlpPath + ".tmp";
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await resp.Content.CopyToAsync(fs);
                }
                File.Move(tmp, ytDlpPath, overwrite: true);
            }
            catch { }
        }

        var ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
        {
            var tempZip = Path.Combine(toolsDir, $"ffmpeg_{Guid.NewGuid():N}.zip");
            try
            {
                onStatus("Downloading ffmpeg.exe...");
                using var resp = await client.GetAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                onStatus("Extracting ffmpeg.exe...");
                using var archive = ZipFile.OpenRead(tempZip);
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    var tmp = ffmpegPath + ".tmp";
                    entry.ExtractToFile(tmp, overwrite: true);
                    File.Move(tmp, ffmpegPath, overwrite: true);
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
