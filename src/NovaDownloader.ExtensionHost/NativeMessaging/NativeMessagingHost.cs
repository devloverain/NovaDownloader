using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NovaDownloader.ExtensionHost.Ipc;
using NovaDownloader.ExtensionHost.Logging;
using NovaDownloader.ExtensionHost.Models;

namespace NovaDownloader.ExtensionHost.NativeMessaging;

public class NativeMessagingHost
{
    private readonly NativeMessageProtocol _protocol;
    private readonly NamedPipeForwarder _pipeForwarder;

    public NativeMessagingHost(Stream inputStream, Stream outputStream, string pipeName = "NovaDownloaderIpcPipe")
    {
        _protocol = new NativeMessageProtocol(inputStream, outputStream);
        _pipeForwarder = new NamedPipeForwarder(pipeName);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        HostLogger.Info("NovaDownloader Native Host loop started.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _protocol.ReadMessageAsync<ExtensionMessage>(cancellationToken);
                if (message == null)
                {
                    HostLogger.Info("Received null message (browser closed connection or stream ended). Terminating loop.");
                    break;
                }

                await ProcessMessageAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            HostLogger.Info("Host cancellation requested. Exiting loop.");
        }
        catch (Exception ex)
        {
            HostLogger.Error("Fatal error in Native Host message loop", ex);
        }
        finally
        {
            HostLogger.Info("NovaDownloader Native Host loop finished.");
        }
    }

    private async Task ProcessMessageAsync(ExtensionMessage message, CancellationToken cancellationToken)
    {
        var action = message.EffectiveAction.ToUpperInvariant();
        HostLogger.Info($"Processing message action: '{action}'");

        try
        {
            switch (action)
            {
                case "PING":
                    await HandlePingAsync(cancellationToken);
                    break;

                case "DOWNLOAD_URL":
                case "DOWNLOAD":
                    await HandleDownloadUrlAsync(message, cancellationToken);
                    break;

                case "BATCH_DOWNLOAD_LINKS":
                    await HandleBatchDownloadLinksAsync(message, cancellationToken);
                    break;

                case "SNIFFED_MEDIA":
                case "MEDIA_FOUND":
                    await HandleSniffedMediaAsync(message, cancellationToken);
                    break;

                case "GET_STATUS":
                case "STATUS":
                    await HandleGetStatusAsync(cancellationToken);
                    break;

                default:
                    HostLogger.Warn($"Unknown or unsupported action: '{message.EffectiveAction}'");
                    var unknownResp = HostResponse.Error(message.EffectiveAction, $"Unsupported action: {message.EffectiveAction}");
                    await _protocol.WriteMessageAsync(unknownResp, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            HostLogger.Error($"Error processing action '{action}'", ex);
            var errResp = HostResponse.Error(action, $"Internal host error: {ex.Message}");
            await _protocol.WriteMessageAsync(errResp, cancellationToken);
        }
    }

    private async Task HandlePingAsync(CancellationToken cancellationToken)
    {
        HostLogger.Debug("Received PING. Returning PONG.");
        await _protocol.WriteMessageAsync(HostResponse.Pong(), cancellationToken);
    }

    private async Task HandleDownloadUrlAsync(ExtensionMessage msg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(msg.Url))
        {
            HostLogger.Warn("DOWNLOAD_URL request missing 'url' parameter.");
            await _protocol.WriteMessageAsync(HostResponse.Error("DOWNLOAD_URL", "Missing required 'url' parameter."), cancellationToken);
            return;
        }

        HostLogger.Info($"Forwarding download request for URL: {msg.Url} (Filename: {msg.Filename ?? "<auto>"})");

        var ipcResult = await _pipeForwarder.ForwardAsync("DOWNLOAD_URL", msg, cancellationToken);

        var response = HostResponse.Ok(
            action: "DOWNLOAD_URL",
            message: ipcResult.Message,
            forwarded: ipcResult.Forwarded,
            data: new
            {
                pipeConnected = ipcResult.Connected,
                serverResponse = ipcResult.ServerResponse,
                url = msg.Url,
                filename = msg.Filename
            }
        );

        await _protocol.WriteMessageAsync(response, cancellationToken);
    }

    private async Task HandleBatchDownloadLinksAsync(ExtensionMessage msg, CancellationToken cancellationToken)
    {
        var count = msg.Links?.Count ?? 0;
        if (count == 0)
        {
            HostLogger.Warn("BATCH_DOWNLOAD_LINKS request had 0 links.");
            await _protocol.WriteMessageAsync(HostResponse.Error("BATCH_DOWNLOAD_LINKS", "No links provided."), cancellationToken);
            return;
        }

        HostLogger.Info($"Forwarding BATCH_DOWNLOAD_LINKS count: {count}");
        var ipcResult = await _pipeForwarder.ForwardAsync("BATCH_DOWNLOAD_LINKS", msg, cancellationToken);

        var response = HostResponse.Ok(
            action: "BATCH_DOWNLOAD_LINKS",
            message: ipcResult.Message,
            forwarded: ipcResult.Forwarded,
            data: new
            {
                pipeConnected = ipcResult.Connected,
                serverResponse = ipcResult.ServerResponse,
                linksCount = count
            }
        );

        await _protocol.WriteMessageAsync(response, cancellationToken);
    }

    private async Task HandleSniffedMediaAsync(ExtensionMessage msg, CancellationToken cancellationToken)
    {
        var count = msg.MediaList?.Count ?? (msg.Media != null ? 1 : 0);
        HostLogger.Info($"Forwarding SNIFFED_MEDIA item count: {count}");

        var ipcResult = await _pipeForwarder.ForwardAsync("SNIFFED_MEDIA", msg, cancellationToken);

        var response = HostResponse.Ok(
            action: "SNIFFED_MEDIA",
            message: ipcResult.Message,
            forwarded: ipcResult.Forwarded,
            data: new
            {
                pipeConnected = ipcResult.Connected,
                mediaCount = count
            }
        );

        await _protocol.WriteMessageAsync(response, cancellationToken);
    }

    private async Task HandleGetStatusAsync(CancellationToken cancellationToken)
    {
        var statusResp = HostResponse.Ok(
            action: "GET_STATUS",
            message: "NovaDownloader Native Host is operational.",
            forwarded: true,
            data: new
            {
                processId = Environment.ProcessId,
                framework = Environment.Version.ToString(),
                runtime = RuntimeInformationHelper.GetRuntimeDescription()
            }
        );

        await _protocol.WriteMessageAsync(statusResp, cancellationToken);
    }
}

internal static class RuntimeInformationHelper
{
    public static string GetRuntimeDescription()
    {
        return $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
    }
}
