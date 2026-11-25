using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace NovaDownloader.UI.Dialogs;

public partial class BrowserIntegrationDialog : Window
{
    private const string HostName = "com.novadownloader.host";
    private readonly string? _hostExePath;
    private readonly string? _extensionDirPath;

    public BrowserIntegrationDialog()
    {
        InitializeComponent();
        _hostExePath = ResolveHostExePath();
        _extensionDirPath = ResolveExtensionDirPath();
        RefreshStatus();
    }

    private static string? ResolveHostExePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(baseDir, "NovaDownloader.ExtensionHost.exe");
        if (File.Exists(local)) return local;

        var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "NovaDownloader.ExtensionHost", "bin", "Debug", "net10.0", "NovaDownloader.ExtensionHost.exe"));
        return File.Exists(dev) ? dev : null;
    }

    private static string? ResolveExtensionDirPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(baseDir, "extension");
        if (Directory.Exists(local) && File.Exists(Path.Combine(local, "manifest.json"))) return local;

        var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "extension"));
        return Directory.Exists(dev) && File.Exists(Path.Combine(dev, "manifest.json")) ? dev : null;
    }

    private static string? FindChromeExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindEdgeExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOperaExe()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "Opera", "opera.exe"),
            Path.Combine(local, "Programs", "Opera GX", "opera.exe"),
            Path.Combine(pf, "Opera", "opera.exe"),
            Path.Combine(pfx86, "Opera", "opera.exe"),
            Path.Combine(local, "Programs", "Opera", "launcher.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindBraveExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsRegistryKeyRegistered(string subKey)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(null) is string p && File.Exists(p);
    }

    private void RefreshStatus()
    {
        var green = (Brush)new BrushConverter().ConvertFromString("#4ADE80")!;
        var amber = (Brush)new BrushConverter().ConvertFromString("#FBBF24")!;
        var red = (Brush)new BrushConverter().ConvertFromString("#F87171")!;

        if (_hostExePath != null)
        {
            HostStatusText.Text = "Found";
            HostStatusText.Foreground = green;
        }
        else
        {
            HostStatusText.Text = "Missing (Build NovaDownloader.ExtensionHost)";
            HostStatusText.Foreground = red;
        }

        var regBrowsers = new List<string>();
        if (IsRegistryKeyRegistered($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}")) regBrowsers.Add("Chrome");
        if (IsRegistryKeyRegistered($@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}")) regBrowsers.Add("Edge");
        if (IsRegistryKeyRegistered($@"Software\Opera Software\NativeMessagingHosts\{HostName}") ||
            IsRegistryKeyRegistered($@"Software\Opera Software\Opera Stable\NativeMessagingHosts\{HostName}") ||
            IsRegistryKeyRegistered($@"Software\Opera Software\Opera GX Stable\NativeMessagingHosts\{HostName}")) regBrowsers.Add("Opera");
        if (IsRegistryKeyRegistered($@"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{HostName}")) regBrowsers.Add("Brave");
        if (IsRegistryKeyRegistered($@"Software\Vivaldi\NativeMessagingHosts\{HostName}")) regBrowsers.Add("Vivaldi");

        if (regBrowsers.Count > 0)
        {
            RegistryStatusText.Text = $"Registered ({string.Join(" / ", regBrowsers)})";
            RegistryStatusText.Foreground = green;
        }
        else
        {
            RegistryStatusText.Text = "Not Registered — Click Step 1";
            RegistryStatusText.Foreground = amber;
        }

        var detected = new List<string>();
        if (FindChromeExe() != null) detected.Add("Chrome");
        if (FindOperaExe() != null) detected.Add("Opera");
        if (FindEdgeExe() != null) detected.Add("Edge");
        if (FindBraveExe() != null) detected.Add("Brave");
        DetectedBrowsersText.Text = detected.Count > 0 ? string.Join(", ", detected) : "None found in default paths";

        ExtensionPathText.Text = _extensionDirPath ?? "Missing extension folder";
        ExtensionPathText.ToolTip = _extensionDirPath;
    }

    private void RegisterHost_Click(object sender, RoutedEventArgs e)
    {
        if (_hostExePath == null)
        {
            MessageBox.Show("NovaDownloader.ExtensionHost.exe was not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(_hostExePath, "--install")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            RefreshStatus();
            FeedbackText.Text = "Native Messaging Host registered successfully!";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to register host: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_extensionDirPath))
        {
            Clipboard.SetText(_extensionDirPath);
            FeedbackText.Text = "Extension folder path copied to clipboard!";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_extensionDirPath) && Directory.Exists(_extensionDirPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _extensionDirPath) { UseShellExecute = true });
        }
    }

    private void OpenChrome_Click(object sender, RoutedEventArgs e)
    {
        CopyExtensionPathSilently();
        LaunchBrowser(FindChromeExe() ?? "chrome.exe", "chrome://extensions");
        FeedbackText.Text = "Path copied! In Chrome: Developer Mode -> Load unpacked -> Ctrl+V.";
    }

    private void OpenOpera_Click(object sender, RoutedEventArgs e)
    {
        CopyExtensionPathSilently();
        LaunchBrowser(FindOperaExe() ?? "opera.exe", "opera://extensions");
        FeedbackText.Text = "Path copied! In Opera: Developer Mode -> Load unpacked -> Ctrl+V.";
    }

    private void OpenEdge_Click(object sender, RoutedEventArgs e)
    {
        CopyExtensionPathSilently();
        LaunchBrowser(FindEdgeExe() ?? "msedge.exe", "edge://extensions");
        FeedbackText.Text = "Path copied! In Edge: Developer Mode -> Load unpacked -> Ctrl+V.";
    }

    private void LaunchChromeWithExt_Click(object sender, RoutedEventArgs e)
        => LaunchWithExtension("chrome", FindChromeExe() ?? "chrome.exe", "Google Chrome");

    private void LaunchOperaWithExt_Click(object sender, RoutedEventArgs e)
        => LaunchWithExtension("opera", FindOperaExe() ?? "opera.exe", "Opera");

    private void CopyExtensionPathSilently()
    {
        if (!string.IsNullOrEmpty(_extensionDirPath))
        {
            Clipboard.SetText(_extensionDirPath);
        }
    }

    private void LaunchWithExtension(string processName, string targetExe, string displayName)
    {
        if (string.IsNullOrEmpty(_extensionDirPath)) return;

        var running = Process.GetProcessesByName(processName);
        if (running.Length > 0)
        {
            var answer = MessageBox.Show(
                $"{displayName} is currently running.\n\n" +
                $"Chromium browsers ignore '--load-extension' while already open.\n" +
                $"Close {displayName} and relaunch it with NovaDownloader extension loaded?",
                $"Relaunch {displayName}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            foreach (var proc in running)
            {
                try
                {
                    if (!proc.CloseMainWindow())
                        proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch
                {
                    // ignore individual helper processes
                }
            }

            // Ensure any leftover background processes exit before relaunch
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try { proc.Kill(); proc.WaitForExit(1500); } catch { }
            }
        }

        LaunchBrowser(targetExe, $"--load-extension=\"{_extensionDirPath}\"");
        FeedbackText.Text = $"Launched {displayName} with NovaDownloader extension loaded!";
    }

    private static void LaunchBrowser(string targetExe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(targetExe, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not launch {Path.GetFileName(targetExe)}: {ex.Message}", "Browser Launch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
