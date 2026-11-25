using System;
using System.IO;

namespace NovaDownloader.ExtensionHost.Logging;

public static class HostLogger
{
    private static readonly object LockObj = new();
    private static readonly string LogDirectory;
    private static readonly string LogFilePath;

    static HostLogger()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LogDirectory = Path.Combine(localAppData, "NovaDownloader", "Logs");
            Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(LogDirectory, "native_host.log");
        }
        catch
        {
            LogDirectory = Path.GetTempPath();
            LogFilePath = Path.Combine(LogDirectory, "novadownloader_host.log");
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log("ERROR", msg);
    }
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        try
        {
            var formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

            // Chrome/Edge Native Messaging STDOUT is strictly binary protocol data.
            // NEVER write to Console.Out! Only write to Console.Error (stderr) or file.
            Console.Error.WriteLine(formatted);

            lock (LockObj)
            {
                // Simple file rotation if > 10MB
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > 10 * 1024 * 1024)
                {
                    var backup = Path.Combine(LogDirectory, $"native_host_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    File.Move(LogFilePath, backup, overwrite: true);
                }

                File.AppendAllText(LogFilePath, formatted + Environment.NewLine);
            }
        }
        catch
        {
            // Suppress logging errors to prevent breaking the native host process
        }
    }
}
