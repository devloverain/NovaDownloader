using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaDownloader.ExtensionHost.Logging;

namespace NovaDownloader.ExtensionHost.NativeMessaging;

public class NativeMessageProtocol
{
    private const int MaxMessageSize = 10 * 1024 * 1024; // 10 MB limit
    private readonly Stream _inputStream;
    private readonly Stream _outputStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public NativeMessageProtocol(Stream inputStream, Stream outputStream)
    {
        _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
    }

    /// <summary>
    /// Reads a 4-byte length-prefixed JSON message from the input stream.
    /// Returns null if the stream is closed or EOF is reached.
    /// </summary>
    public async Task<string?> ReadRawMessageAsync(CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[4];
        var bytesRead = await ReadExactBytesAsync(_inputStream, lengthBuffer, 0, 4, cancellationToken);
        if (bytesRead < 4)
        {
            HostLogger.Info("Browser closed input pipe or reached EOF.");
            return null;
        }

        var messageLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
        if (messageLength == 0)
        {
            HostLogger.Warn("Received empty 0-byte message length header.");
            return string.Empty;
        }

        if (messageLength > MaxMessageSize)
        {
            HostLogger.Error($"Message length ({messageLength} bytes) exceeds maximum allowed size ({MaxMessageSize} bytes).");
            throw new InvalidOperationException($"Incoming message too large: {messageLength} bytes");
        }

        var messageBuffer = new byte[messageLength];
        var totalPayloadRead = await ReadExactBytesAsync(_inputStream, messageBuffer, 0, (int)messageLength, cancellationToken);
        if (totalPayloadRead < messageLength)
        {
            HostLogger.Error($"Incomplete message read: expected {messageLength} bytes, got {totalPayloadRead} bytes.");
            return null;
        }

        var jsonString = Encoding.UTF8.GetString(messageBuffer);
        return jsonString;
    }

    /// <summary>
    /// Reads and deserializes a JSON message from the input stream.
    /// </summary>
    public async Task<T?> ReadMessageAsync<T>(CancellationToken cancellationToken = default) where T : class
    {
        var rawJson = await ReadRawMessageAsync(cancellationToken);
        if (rawJson == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, SerializerOptions);
        }
        catch (Exception ex)
        {
            HostLogger.Error($"Failed to deserialize message: {rawJson}", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes a length-prefixed JSON message to the standard output stream.
    /// Thread-safe via semaphore.
    /// </summary>
    public async Task WriteMessageAsync<T>(T payload, CancellationToken cancellationToken = default)
    {
        if (payload == null) return;

        var jsonString = JsonSerializer.Serialize(payload, SerializerOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(jsonString);

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, (uint)payloadBytes.Length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _outputStream.WriteAsync(lengthBuffer, cancellationToken);
            await _outputStream.WriteAsync(payloadBytes, cancellationToken);
            await _outputStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<int> ReadExactBytesAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken);
            if (read == 0)
            {
                break; // End of stream
            }
            totalRead += read;
        }
        return totalRead;
    }
}
