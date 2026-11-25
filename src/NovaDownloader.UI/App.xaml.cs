using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace NovaDownloader.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_crash.log");
            File.WriteAllText(log, args.ExceptionObject?.ToString());
        };

        DispatcherUnhandledException += (s, args) =>
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dispatcher_crash.log");
            File.WriteAllText(log, args.Exception?.ToString());
        };

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\NovaDownloader_SingleInstance_Mutex", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            if (!startMinimized)
            {
                TrySignalExistingInstanceShowWindow();
            }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static void TrySignalExistingInstanceShowWindow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", "NovaDownloaderIpcPipe", PipeDirection.InOut);
            client.Connect(600);

            var payloadBytes = Encoding.UTF8.GetBytes("{\"Action\":\"SHOW_WINDOW\"}");
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)payloadBytes.Length);
            client.Write(lenBytes);
            client.Write(payloadBytes);
            client.Flush();
        }
        catch
        {
            // Existing instance unavailable or busy
        }
    }
}
