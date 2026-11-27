using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaDownloader.ExtensionHost.Logging;
using NovaDownloader.ExtensionHost.Models;

namespace NovaDownloader.ExtensionHost.Ipc;

public record IpcForwardResult(bool Connected, bool Forwarded, string Message, string? ServerResponse = null);

public class NamedPipeForwarder
{
    private const string DefaultPipeName = "NovaDownloaderIpcPipe";
    private const int ConnectTimeoutMs = 1500;
    private readonly string _pipeName;

    public NamedPipeForwarder(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public async Task<IpcForwardResult> ForwardAsync(string action, object payload, CancellationToken cancellationToken = default)
    {
        HostLogger.Info($"Attempting to forward action '{action}' via Named Pipe '{_pipeName}'...");

        var envelope = new
        {
            Action = action,
            Timestamp = DateTime.UtcNow,
            Payload = payload
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ConnectTimeoutMs);

            using var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(cts.Token);

            HostLogger.Info($"Connected to pipe '{_pipeName}'. Sending {json.Length} bytes of JSON data.");

            var utf8Bytes = Encoding.UTF8.GetBytes(json);
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)utf8Bytes.Length);

            // Write 4-byte length prefix followed by utf8 json
            await pipeClient.WriteAsync(lenBytes, cancellationToken);
            await pipeClient.WriteAsync(utf8Bytes, cancellationToken);
            await pipeClient.FlushAsync(cancellationToken);

            // Read response (timeout 2 seconds)
            string? responseStr = null;
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(2000);

                var respLenBuffer = new byte[4];
                var readLen = await pipeClient.ReadAsync(respLenBuffer.AsMemory(0, 4), readCts.Token);
                if (readLen == 4)
                {
                    var respLength = BinaryPrimitives.ReadUInt32LittleEndian(respLenBuffer);
                    if (respLength > 0 && respLength < 1024 * 1024)
                    {
                        var respBytes = new byte[respLength];
                        var totalRead = 0;
                        while (totalRead < respLength)
                        {
                            var r = await pipeClient.ReadAsync(respBytes.AsMemory(totalRead, (int)respLength - totalRead), readCts.Token);
                            if (r == 0) break;
                            totalRead += r;
                        }
                        responseStr = Encoding.UTF8.GetString(respBytes, 0, totalRead);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HostLogger.Warn($"No explicit response read from pipe server (might be fire-and-forget): {ex.Message}");
            }

            HostLogger.Info($"Successfully forwarded '{action}' to NovaDownloader app.");
            return new IpcForwardResult(true, true, "Forwarded to NovaDownloader UI successfully.", responseStr);
        }
        catch (OperationCanceledException)
        {
            HostLogger.Warn($"Connection to Named Pipe '{_pipeName}' timed out after {ConnectTimeoutMs}ms. Desktop app might be closed.");
            QueueOfflineDownload(action, json);
            TryLaunchDesktopApp();
            return new IpcForwardResult(false, false, "NovaDownloader app is not running. Download queued and app launch requested.");
        }
        catch (Exception ex)
        {
            HostLogger.Warn($"Failed to connect to Named Pipe '{_pipeName}': {ex.Message}");
            QueueOfflineDownload(action, json);
            TryLaunchDesktopApp();
            return new IpcForwardResult(false, false, $"Named pipe connection failed: {ex.Message}. Download request saved to offline queue.");
        }
    }

    private static void QueueOfflineDownload(string action, string json)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var queueDir = Path.Combine(appData, "NovaDownloader", "Queue");
            Directory.CreateDirectory(queueDir);

            var queueFile = Path.Combine(queueDir, $"pending_{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(queueFile, json, Encoding.UTF8);
            HostLogger.Info($"Offline request queued to file: {queueFile}");
        }
        catch (Exception ex)
        {
            HostLogger.Error("Failed to queue offline download", ex);
        }
    }

    private static void TryLaunchDesktopApp()
    {
        try
        {
            // Look for NovaDownloader.UI.exe in adjacent directories
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(currentDir, "NovaDownloader.UI.exe"),
                Path.Combine(currentDir, "..", "NovaDownloader.UI", "NovaDownloader.UI.exe"),
                Path.Combine(currentDir, "..", "..", "NovaDownloader.UI", "bin", "Debug", "net10.0-windows", "NovaDownloader.UI.exe"),
                Path.Combine(currentDir, "..", "..", "..", "..", "NovaDownloader.UI", "bin", "Debug", "net10.0-windows", "NovaDownloader.UI.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NovaDownloader", "NovaDownloader.UI.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaDownloader", "NovaDownloader.UI.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NovaDownloader", "NovaDownloader.UI.exe")
            };

            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    HostLogger.Info($"Found NovaDownloader UI at '{fullPath}'. Launching...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(fullPath)
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            HostLogger.Warn($"Could not auto-launch desktop app: {ex.Message}");
        }
    }
}
