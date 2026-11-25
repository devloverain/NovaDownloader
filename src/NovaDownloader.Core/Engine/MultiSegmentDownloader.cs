using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using NovaDownloader.Core.Configuration;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Utils;

namespace NovaDownloader.Core.Engine;

public class MultiSegmentDownloader : IDownloadEngine
{
    private HttpClient _httpClient;
    private readonly DownloaderOptions _options;
    private readonly bool _ownsHttpClient;
    private readonly Lock _throttleLock = new();
    private long _windowStartTicks = Stopwatch.GetTimestamp();
    private long _windowBytes;

    public event EventHandler<DownloadProgressEventArgs>? ProgressUpdated;
    public event EventHandler<DownloadTask>? TaskCompleted;
    public event EventHandler<(DownloadTask Task, Exception Error)>? TaskFailed;

    public MultiSegmentDownloader(DownloaderOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new DownloaderOptions();
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var proxy = SocketsHttpHandlerFactory.CreateProxy(
                _options.ProxyEnabled,
                _options.ProxyAddress,
                _options.ProxyUsername,
                _options.ProxyPassword);
            _httpClient = SocketsHttpHandlerFactory.CreateClient(
                maxConnectionsPerServer: _options.MaxConnectionsPerServer,
                userAgent: _options.UserAgent,
                timeout: TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
                proxy: proxy);
            _ownsHttpClient = true;
        }
    }

    public void RefreshHttpClient()
    {
        if (!_ownsHttpClient) return;
        var old = _httpClient;
        var proxy = SocketsHttpHandlerFactory.CreateProxy(
            _options.ProxyEnabled,
            _options.ProxyAddress,
            _options.ProxyUsername,
            _options.ProxyPassword);
        _httpClient = SocketsHttpHandlerFactory.CreateClient(
            maxConnectionsPerServer: _options.MaxConnectionsPerServer,
            userAgent: _options.UserAgent,
            timeout: TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
            proxy: proxy);
        old.Dispose();
    }

    public async Task<ServerProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Try HEAD request first
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResponse.IsSuccessStatusCode)
            {
                var supportsRange = headResponse.Headers.AcceptRanges.Any(r => string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
                var contentLength = headResponse.Content.Headers.ContentLength;
                var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                var fileName = ExtractFileName(headResponse, url);

                // If HEAD returned ContentLength, we have our answer
                if (contentLength.HasValue && contentLength.Value > 0)
                {
                    return new ServerProbeResult(
                        Success: true,
                        ContentLength: contentLength.Value,
                        SupportsRange: supportsRange,
                        SuggestedFileName: fileName,
                        ContentType: contentType);
                }
            }

            // 2. Fallback to GET with Range 0-0 (useful when HEAD is blocked or omits Content-Length)
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Range = new RangeHeaderValue(0, 0);

            using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (getResponse.IsSuccessStatusCode || getResponse.StatusCode == HttpStatusCode.PartialContent)
            {
                var fileName = ExtractFileName(getResponse, url);
                var contentType = getResponse.Content.Headers.ContentType?.MediaType;

                if (getResponse.StatusCode == HttpStatusCode.PartialContent)
                {
                    var totalLength = getResponse.Content.Headers.ContentRange?.Length;
                    return new ServerProbeResult(
                        Success: true,
                        ContentLength: totalLength,
                        SupportsRange: true,
                        SuggestedFileName: fileName,
                        ContentType: contentType);
                }
                else
                {
                    // Server returned 200 OK instead of 206 Partial Content: doesn't support ranges
                    var totalLength = getResponse.Content.Headers.ContentLength;
                    return new ServerProbeResult(
                        Success: true,
                        ContentLength: totalLength,
                        SupportsRange: false,
                        SuggestedFileName: fileName,
                        ContentType: contentType);
                }
            }

            return new ServerProbeResult(
                Success: false,
                ContentLength: null,
                SupportsRange: false,
                SuggestedFileName: ExtractFileName(null, url),
                ContentType: null,
                ErrorMessage: $"Server returned HTTP {(int)headResponse.StatusCode} {headResponse.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new ServerProbeResult(
                Success: false,
                ContentLength: null,
                SupportsRange: false,
                SuggestedFileName: ExtractFileName(null, url),
                ContentType: null,
                ErrorMessage: ex.Message);
        }
    }

    public async Task InitializeTaskAsync(DownloadTask task, CancellationToken cancellationToken = default)
    {
        // Probe if total bytes or support range is uninitialized
        if (task.TotalBytes <= 0)
        {
            var probe = await ProbeAsync(task.Url, cancellationToken);
            if (probe.Success && probe.ContentLength.HasValue)
            {
                task.TotalBytes = probe.ContentLength.Value;
                task.SupportsRange = probe.SupportsRange;
            }

            if (string.IsNullOrWhiteSpace(task.FileName) && !string.IsNullOrWhiteSpace(probe.SuggestedFileName))
            {
                task.FileName = probe.SuggestedFileName;
            }
        }

        if (string.IsNullOrWhiteSpace(task.FileName))
        {
            task.FileName = ExtractFileName(null, task.Url);
        }

        task.FileName = FileUtils.SanitizeFileName(task.FileName);

        // Resolve DestinationPath if it's a directory
        if (string.IsNullOrWhiteSpace(task.DestinationPath))
        {
            task.DestinationPath = Path.Combine(Environment.CurrentDirectory, task.FileName);
        }
        else if (Directory.Exists(task.DestinationPath) || Path.EndsInDirectorySeparator(task.DestinationPath))
        {
            task.DestinationPath = Path.Combine(task.DestinationPath, task.FileName);
        }

        // Initialize segments if not already initialized
        if (task.Segments.Count == 0)
        {
            if (task.SupportsRange && task.TotalBytes > 0)
            {
                int segmentCount = CalculateOptimalSegments(task.TotalBytes);
                task.Segments = CreateSegments(task.TotalBytes, segmentCount, task.IncompleteFilePath);
            }
            else
            {
                // Single-stream segment
                task.Segments =
                [
                    new DownloadSegment
                    {
                        Index = 0,
                        StartOffset = 0,
                        EndOffset = task.TotalBytes > 0 ? task.TotalBytes - 1 : -1,
                        CurrentOffset = 0,
                        Status = SegmentStatus.Pending,
                        TempFilePath = task.IncompleteFilePath
                    }
                ];
            }
        }

        task.RecalculateDownloadedBytes();
    }

    public async Task DownloadAsync(DownloadTask task, CancellationToken cancellationToken = default)
    {
        if (task.Status == DownloadStatus.Completed && File.Exists(task.DestinationPath))
        {
            return;
        }

        await InitializeTaskAsync(task, cancellationToken);

        var destinationDir = Path.GetDirectoryName(Path.GetFullPath(task.DestinationPath));
        if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        var speedCalculator = new SpeedCalculator(_options.SpeedRollingWindowSeconds);
        task.Status = DownloadStatus.Downloading;
        task.ErrorMessage = null;

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressTask = RunProgressReportingLoopAsync(task, speedCalculator, progressCts.Token);

        try
        {
            if (task.SupportsRange && task.TotalBytes > 0 && task.Segments.Count > 1)
            {
                await DownloadSegmentedAsync(task, speedCalculator, cancellationToken);
            }
            else
            {
                await DownloadSingleStreamAsync(task, speedCalculator, cancellationToken);
            }

            // Successfully downloaded
            await progressCts.CancelAsync();
            try { await progressTask; } catch (OperationCanceledException) { }

            // Final file move
            if (File.Exists(task.IncompleteFilePath))
            {
                if (File.Exists(task.DestinationPath))
                {
                    File.Delete(task.DestinationPath);
                }
                File.Move(task.IncompleteFilePath, task.DestinationPath);
            }

            task.DownloadedBytes = task.TotalBytes > 0 ? task.TotalBytes : task.DownloadedBytes;
            task.SpeedBytesPerSec = 0;
            task.EstimatedTimeRemaining = TimeSpan.Zero;
            task.Status = DownloadStatus.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow;

            ProgressUpdated?.Invoke(this, new DownloadProgressEventArgs
            {
                TaskId = task.Id,
                DownloadedBytes = task.DownloadedBytes,
                TotalBytes = task.TotalBytes,
                ProgressPercentage = task.ProgressPercentage,
                SpeedBytesPerSec = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
                ActiveSegmentsCount = 0
            });

            TaskCompleted?.Invoke(this, task);
        }
        catch (OperationCanceledException)
        {
            await progressCts.CancelAsync();
            try { await progressTask; } catch (OperationCanceledException) { }

            task.SpeedBytesPerSec = 0;
            task.EstimatedTimeRemaining = null;
            task.Status = DownloadStatus.Paused;
            foreach (var segment in task.Segments.Where(s => s.Status == SegmentStatus.Downloading))
            {
                segment.Status = SegmentStatus.Paused;
            }
            task.RecalculateDownloadedBytes();
        }
        catch (Exception ex)
        {
            await progressCts.CancelAsync();
            try { await progressTask; } catch (OperationCanceledException) { }

            task.SpeedBytesPerSec = 0;
            task.EstimatedTimeRemaining = null;
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            foreach (var segment in task.Segments.Where(s => s.Status == SegmentStatus.Downloading))
            {
                segment.Status = SegmentStatus.Failed;
            }

            TaskFailed?.Invoke(this, (task, ex));
            throw;
        }
    }

    private async Task DownloadSegmentedAsync(DownloadTask task, SpeedCalculator speedCalculator, CancellationToken cancellationToken)
    {
        // Open file handle with RandomAccess
        using var fileHandle = File.OpenHandle(
            task.IncompleteFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        // Pre-allocate file on disk to avoid fragmentation
        if (task.TotalBytes > 0)
        {
            var length = RandomAccess.GetLength(fileHandle);
            if (length < task.TotalBytes)
            {
                RandomAccess.SetLength(fileHandle, task.TotalBytes);
            }
        }

        var incompleteSegments = task.Segments.Where(s => !s.IsComplete).ToList();
        if (incompleteSegments.Count == 0)
        {
            return;
        }

        var downloadTasks = incompleteSegments.Select(segment =>
            DownloadSingleSegmentAsync(task, segment, fileHandle, speedCalculator, cancellationToken));

        await Task.WhenAll(downloadTasks);

        RandomAccess.FlushToDisk(fileHandle);
    }

    private async Task DownloadSingleSegmentAsync(
        DownloadTask task,
        DownloadSegment segment,
        Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
        SpeedCalculator speedCalculator,
        CancellationToken cancellationToken)
    {
        if (segment.IsComplete)
        {
            segment.Status = SegmentStatus.Completed;
            return;
        }

        segment.Status = SegmentStatus.Downloading;

        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        request.Headers.Range = new RangeHeaderValue(segment.CurrentOffset, segment.EndOffset);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            while (segment.CurrentOffset <= segment.EndOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(rentedBuffer.Length, segment.EndOffset - segment.CurrentOffset + 1);
                int bytesRead = await stream.ReadAsync(rentedBuffer.AsMemory(0, bytesToRead), cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(fileHandle, rentedBuffer.AsMemory(0, bytesRead), segment.CurrentOffset, cancellationToken);

                segment.CurrentOffset += bytesRead;
                speedCalculator.AddSample(task.DownloadedBytes);
                await ThrottleIfLimitedAsync(bytesRead, cancellationToken);
            }

            if (segment.CurrentOffset > segment.EndOffset)
            {
                segment.Status = SegmentStatus.Completed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private async Task DownloadSingleStreamAsync(DownloadTask task, SpeedCalculator speedCalculator, CancellationToken cancellationToken)
    {
        long startingOffset = 0;
        var partPath = task.IncompleteFilePath;

        var segment = task.Segments.FirstOrDefault() ?? new DownloadSegment
        {
            Index = 0,
            StartOffset = 0,
            EndOffset = task.TotalBytes > 0 ? task.TotalBytes - 1 : -1,
            CurrentOffset = 0,
            Status = SegmentStatus.Pending,
            TempFilePath = partPath
        };

        if (task.Segments.Count == 0)
        {
            task.Segments.Add(segment);
        }

        FileMode fileMode = FileMode.Create;
        if (File.Exists(partPath) && segment.CurrentOffset > 0 && task.SupportsRange)
        {
            startingOffset = segment.CurrentOffset;
            fileMode = FileMode.Open;
        }

        await using var fileStream = new FileStream(
            partPath,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: _options.BufferSize,
            useAsync: true);

        if (startingOffset > 0)
        {
            fileStream.Seek(startingOffset, SeekOrigin.Begin);
        }

        segment.Status = SegmentStatus.Downloading;

        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        if (startingOffset > 0 && task.SupportsRange)
        {
            request.Headers.Range = new RangeHeaderValue(startingOffset, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = await stream.ReadAsync(rentedBuffer.AsMemory(0, rentedBuffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(rentedBuffer.AsMemory(0, bytesRead), cancellationToken);
                segment.CurrentOffset += bytesRead;
                task.DownloadedBytes = segment.CurrentOffset;

                speedCalculator.AddSample(task.DownloadedBytes);
                await ThrottleIfLimitedAsync(bytesRead, cancellationToken);
            }

            await fileStream.FlushAsync(cancellationToken);
            segment.Status = SegmentStatus.Completed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private async Task RunProgressReportingLoopAsync(DownloadTask task, SpeedCalculator speedCalculator, CancellationToken cancellationToken)
    {
        var intervalMs = Math.Max(450, _options.ProgressUpdateIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        double smoothedSpeed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            task.RecalculateDownloadedBytes();
            speedCalculator.AddSample(task.DownloadedBytes);

            var rawSpeed = speedCalculator.CalculateSpeedBytesPerSec();
            smoothedSpeed = smoothedSpeed <= 0
                ? rawSpeed
                : (0.25 * rawSpeed) + (0.75 * smoothedSpeed);
            var eta = speedCalculator.CalculateEta(task.TotalBytes, task.DownloadedBytes, smoothedSpeed);

            task.SpeedBytesPerSec = smoothedSpeed;
            task.EstimatedTimeRemaining = eta;

            int activeSegments = task.Segments.Count(s => s.Status == SegmentStatus.Downloading);

            var eventArgs = new DownloadProgressEventArgs
            {
                TaskId = task.Id,
                DownloadedBytes = task.DownloadedBytes,
                TotalBytes = task.TotalBytes,
                ProgressPercentage = task.ProgressPercentage,
                SpeedBytesPerSec = smoothedSpeed,
                EstimatedTimeRemaining = eta,
                ActiveSegmentsCount = activeSegments
            };

            ProgressUpdated?.Invoke(this, eventArgs);
        }
    }

    private async Task ThrottleIfLimitedAsync(int bytesRead, CancellationToken cancellationToken)
    {
        var limit = _options.MaxDownloadSpeedBytesPerSec;
        if (limit <= 0) return;

        int delayMs = 0;
        lock (_throttleLock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= 1.0)
            {
                _windowStartTicks = now;
                _windowBytes = bytesRead;
                return;
            }

            _windowBytes += bytesRead;
            if (_windowBytes > limit)
            {
                var remainingSec = 1.0 - elapsedSec;
                delayMs = Math.Clamp((int)(remainingSec * 1000.0), 10, 1000);
            }
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    public int CalculateOptimalSegments(long totalBytes)
    {
        if (totalBytes <= 0) return 1;

        var targetMax = Math.Clamp(_options.DefaultSegments, 1, _options.MaxSegments);
        var targetMin = Math.Min(_options.MinSegments, targetMax);

        long minChunk = _options.MinChunkSizeBytes;
        if (totalBytes < minChunk) return 1;

        int chunks = (int)(totalBytes / minChunk);
        return Math.Clamp(chunks, targetMin, targetMax);
    }

    public static List<DownloadSegment> CreateSegments(long totalBytes, int segmentCount, string? tempFilePath = null)
    {
        var segments = new List<DownloadSegment>(segmentCount);
        long chunkSize = totalBytes / segmentCount;

        for (int i = 0; i < segmentCount; i++)
        {
            long start = i * chunkSize;
            long end = (i == segmentCount - 1) ? totalBytes - 1 : (i + 1) * chunkSize - 1;

            segments.Add(new DownloadSegment
            {
                Index = i,
                StartOffset = start,
                EndOffset = end,
                CurrentOffset = start,
                Status = SegmentStatus.Pending,
                TempFilePath = tempFilePath
            });
        }

        return segments;
    }

    private static string ExtractFileName(HttpResponseMessage? response, string url)
    {
        if (response != null && response.Content.Headers.ContentDisposition != null)
        {
            var cd = response.Content.Headers.ContentDisposition;
            if (!string.IsNullOrWhiteSpace(cd.FileNameStar))
            {
                return cd.FileNameStar.Trim('\"', '\'');
            }
            if (!string.IsNullOrWhiteSpace(cd.FileName))
            {
                return cd.FileName.Trim('\"', '\'');
            }
        }

        try
        {
            var uri = new Uri(url);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
            // Ignore Uri parse failure
        }

        return $"download_{Guid.NewGuid():N}.bin";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
