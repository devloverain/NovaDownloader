using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;
using NovaDownloader.Media;
using NovaDownloader.Media.Models;

namespace NovaDownloader.UI.Services;

public record ExtensionDownloadPayload(
    string Url,
    string? FileName,
    string? Cookies,
    string? UserAgent,
    string? Referer,
    Dictionary<string, string>? Headers,
    long? FileSize,
    string? MimeType,
    string? PageTitle,
    string? Quality = null);

public record ExtensionMediaPayload(
    string Url,
    string? Title,
    string? Format,
    string? MimeType,
    string? PageTitle,
    string? PageUrl,
    Dictionary<string, string>? Headers);

public record ExtensionEnvelope(
    string? Action,
    DateTime? Timestamp,
    JsonElement? Payload);

public record ExtensionBatchLinkItem(
    string Url,
    string? LinkText,
    string? FileName);

public record ExtensionBatchDownloadPayload(
    List<ExtensionBatchLinkItem> Links,
    string? PageUrl,
    string? PageTitle,
    string? Referer,
    string? UserAgent,
    string? Cookies);

public class IpcService : IDisposable
{
    private const string PipeName = "NovaDownloaderIpcPipe";
    private readonly IDownloadQueueManager _queueManager;
    private readonly IMediaStreamPipeline _mediaPipeline;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public event Action<ExtensionDownloadPayload>? OnDownloadRequested;
    public event Action<ExtensionBatchDownloadPayload>? OnBatchDownloadRequested;
    public event Action<ExtensionMediaPayload, MediaItem?>? OnMediaIntercepted;
    public event Action<string>? OnStatusMessage;
    public event Action? OnShowWindowRequested;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public IpcService(IDownloadQueueManager queueManager, IMediaStreamPipeline mediaPipeline)
    {
        _queueManager = queueManager;
        _mediaPipeline = mediaPipeline;
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        OnStatusMessage?.Invoke("IPC Pipe listener started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private NamedPipeServerStream CreateServerStream()
    {
        try
        {
            var pipeSecurity = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
            }

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 65536,
                outBufferSize: 65536,
                pipeSecurity: pipeSecurity);
        }
        catch
        {
            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(token);

                // Read 4-byte length prefix
                var lenBytes = new byte[4];
                var read = await server.ReadAsync(lenBytes, token);
                if (read < 4) continue;

                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
                if (payloadLength > 20 * 1024 * 1024) continue; // limit 20MB

                var buffer = new byte[payloadLength];
                var totalRead = 0;
                while (totalRead < payloadLength)
                {
                    var r = await server.ReadAsync(buffer.AsMemory(totalRead, (int)payloadLength - totalRead), token);
                    if (r == 0) break;
                    totalRead += r;
                }

                if (totalRead == payloadLength)
                {
                    var json = Encoding.UTF8.GetString(buffer);
                    var responseJson = await ProcessMessageAsync(json);

                    // Send response back
                    var respBytes = Encoding.UTF8.GetBytes(responseJson);
                    var respLenBytes = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(respLenBytes, (uint)respBytes.Length);
                    await server.WriteAsync(respLenBytes, token);
                    await server.WriteAsync(respBytes, token);
                    await server.FlushAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusMessage?.Invoke($"IPC Error: {ex.Message}");
                await Task.Delay(200, token);
            }
        }
    }

    private async Task<string> ProcessMessageAsync(string json)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var envelope = JsonSerializer.Deserialize<ExtensionEnvelope>(json, options);

            if (envelope == null)
            {
                return JsonSerializer.Serialize(new { status = "error", message = "Invalid envelope" });
            }

            var action = envelope.Action?.ToUpperInvariant();

            if (action == "PING")
            {
                var activeCount = _queueManager.Tasks.Count(t => t.Status == DownloadStatus.Downloading);
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    action = "PONG",
                    activeDownloads = activeCount,
                    totalTasks = _queueManager.Tasks.Count
                });
            }

            if (action == "SHOW_WINDOW")
            {
                Application.Current?.Dispatcher.BeginInvoke(() => OnShowWindowRequested?.Invoke());
                return JsonSerializer.Serialize(new { status = "ok", action = "WINDOW_SHOWN" });
            }

            if (action == "DOWNLOAD_URL" && envelope.Payload.HasValue)
            {
                var payload = envelope.Payload.Value.Deserialize<ExtensionDownloadPayload>(options);
                if (payload != null && !string.IsNullOrWhiteSpace(payload.Url))
                {
                    if (OnDownloadRequested != null)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            OnDownloadRequested.Invoke(payload);
                        });

                        return JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            message = "Download info dialog opened in NovaDownloader",
                            fileName = payload.FileName
                        });
                    }

                    var rawName = payload.FileName ?? "download_" + DateTime.Now.Ticks;
                    var defaultDest = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads",
                        rawName);
                    var task = await _queueManager.AddTaskAsync(payload.Url, defaultDest, payload.FileName);

                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        message = "Download queued successfully",
                        taskId = task.Id,
                        fileName = task.FileName
                    });
                }
            }

            if (action == "BATCH_DOWNLOAD_LINKS" && envelope.Payload.HasValue)
            {
                var payload = envelope.Payload.Value.Deserialize<ExtensionBatchDownloadPayload>(options);
                if (payload?.Links is { Count: > 0 })
                {
                    if (OnBatchDownloadRequested != null)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            OnBatchDownloadRequested.Invoke(payload);
                        });

                        return JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            message = $"Batch download dialog opened for {payload.Links.Count} links"
                        });
                    }
                }
            }

            if (action == "SNIFFED_MEDIA" && envelope.Payload.HasValue)
            {
                var payload = envelope.Payload.Value.Deserialize<ExtensionMediaPayload>(options);
                if (payload != null && !string.IsNullOrWhiteSpace(payload.Url))
                {
                    MediaItem? mediaItem = null;
                    try
                    {
                        mediaItem = await _mediaPipeline.InspectMediaAsync(payload.Url, payload.Headers);
                    }
                    catch
                    {
                        // Fallback to basic
                    }

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        OnMediaIntercepted?.Invoke(payload, mediaItem);
                    });

                    return JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        message = "Media received by NovaDownloader",
                        qualitiesCount = mediaItem?.AvailableQualities.Count ?? 0
                    });
                }
            }

            return JsonSerializer.Serialize(new { status = "ok", message = "Action processed" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
