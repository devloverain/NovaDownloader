using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NovaDownloader.Media.Hls;

namespace NovaDownloader.Media.Downloading;

/// <summary>
/// High-throughput parallel HLS segment downloader with retry support and AES-128 decryption.
/// </summary>
public sealed class HlsStreamDownloader : IHlsStreamDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IHlsParser _parser;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();

    public HlsStreamDownloader(HttpClient? httpClient = null, IHlsParser? parser = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _parser = parser ?? new HlsParser(_httpClient);
    }

    /// <inheritdoc />
    public async Task<HlsDownloadResult> DownloadAsync(
        string mediaPlaylistUrl,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HlsDownloadOptions();
        var playlistResult = await _parser.FetchAndParseAsync(mediaPlaylistUrl, options.Headers, cancellationToken).ConfigureAwait(false);

        if (playlistResult.MediaPlaylist == null)
        {
            if (playlistResult.MasterPlaylist != null)
            {
                // If caller gave master playlist, pick best variant
                var bestVariant = playlistResult.MasterPlaylist.Streams
                    .OrderByDescending(s => s.Height ?? 0)
                    .ThenByDescending(s => s.Bandwidth)
                    .FirstOrDefault();

                if (bestVariant == null)
                    throw new InvalidOperationException("Master playlist does not contain any stream variants.");

                return await DownloadAsync(bestVariant.Uri.ToString(), options, progress, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Parsed playlist is not a valid media playlist.");
        }

        return await DownloadAsync(playlistResult.MediaPlaylist, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HlsDownloadResult> DownloadAsync(
        HlsMediaPlaylist playlist,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        options ??= new HlsDownloadOptions();

        if (playlist.Segments.Count == 0)
        {
            return new HlsDownloadResult
            {
                Success = false,
                Error = "Playlist has no segments to download.",
                WorkingDirectory = string.Empty,
                Elapsed = TimeSpan.Zero
            };
        }

        var workDir = options.TempDirectory ?? Path.Combine(Path.GetTempPath(), "NovaDownloader", "hls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var stopwatch = Stopwatch.StartNew();
        var totalSegments = playlist.Segments.Count;
        var completedSegments = 0;
        long totalBytes = 0;

        string? initSegmentPath = null;
        var isFragmentedMp4 = false;

        // 1. Download Init Segment (#EXT-X-MAP) if present
        if (playlist.InitSegment != null)
        {
            isFragmentedMp4 = true;
            initSegmentPath = Path.Combine(workDir, "init.mp4");
            var initBytes = await DownloadWithRetryAsync(
                playlist.InitSegment.Uri,
                playlist.InitSegment.ByteRange,
                options,
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(initSegmentPath, initBytes, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref totalBytes, initBytes.Length);
        }

        // Determine segment file extension
        var firstSegUri = playlist.Segments[0].Uri.AbsolutePath;
        var ext = firstSegUri.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
                  firstSegUri.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ".m4s"
            : ".ts";

        if (ext == ".m4s")
            isFragmentedMp4 = true;

        var segmentPaths = new string[totalSegments];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
            CancellationToken = cancellationToken
        };

        // 2. Download segments in parallel
        try
        {
            await Parallel.ForEachAsync(playlist.Segments, parallelOptions, async (segment, ct) =>
            {
                var segmentData = await DownloadSegmentAsync(segment, options, ct).ConfigureAwait(false);

                var fileName = $"segment_{segment.Index:D6}{ext}";
                var filePath = Path.Combine(workDir, fileName);
                await File.WriteAllBytesAsync(filePath, segmentData, ct).ConfigureAwait(false);

                segmentPaths[segment.Index] = filePath;

                var completed = Interlocked.Increment(ref completedSegments);
                var bytes = Interlocked.Add(ref totalBytes, segmentData.Length);

                if (progress != null)
                {
                    var elapsedSec = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                    var speed = bytes / elapsedSec;
                    var remainingSegs = totalSegments - completed;
                    var avgSecPerSeg = elapsedSec / completed;
                    var eta = TimeSpan.FromSeconds(avgSecPerSeg * remainingSegs);

                    progress.Report(new HlsDownloadProgress
                    {
                        SegmentsCompleted = completed,
                        TotalSegments = totalSegments,
                        BytesDownloaded = bytes,
                        SpeedBytesPerSecond = speed,
                        EstimatedTimeRemaining = eta,
                        StatusMessage = $"Downloaded segment {completed}/{totalSegments}"
                    });
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HlsDownloadResult
            {
                Success = false,
                Error = $"Download failed: {ex.Message}",
                WorkingDirectory = workDir,
                TotalBytesDownloaded = totalBytes,
                Elapsed = stopwatch.Elapsed,
                SegmentFiles = segmentPaths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList(),
                InitSegmentFile = initSegmentPath,
                IsFragmentedMp4 = isFragmentedMp4
            };
        }

        stopwatch.Stop();

        return new HlsDownloadResult
        {
            Success = true,
            WorkingDirectory = workDir,
            SegmentFiles = segmentPaths.ToList(),
            InitSegmentFile = initSegmentPath,
            TotalBytesDownloaded = totalBytes,
            Elapsed = stopwatch.Elapsed,
            IsFragmentedMp4 = isFragmentedMp4
        };
    }

    private async Task<byte[]> DownloadSegmentAsync(
        HlsSegment segment,
        HlsDownloadOptions options,
        CancellationToken cancellationToken)
    {
        var rawBytes = await DownloadWithRetryAsync(segment.Uri, segment.ByteRange, options, cancellationToken).ConfigureAwait(false);

        // Handle AES-128 Decryption
        if (options.AutoDecrypt && segment.Key != null && segment.Key.Method == HlsEncryptionMethod.Aes128)
        {
            return await DecryptAes128SegmentAsync(rawBytes, segment, options, cancellationToken).ConfigureAwait(false);
        }

        return rawBytes;
    }

    private async Task<byte[]> DecryptAes128SegmentAsync(
        byte[] encryptedData,
        HlsSegment segment,
        HlsDownloadOptions options,
        CancellationToken cancellationToken)
    {
        if (segment.Key?.Uri == null)
            return encryptedData;

        var keyUri = segment.Key.Uri.ToString();
        if (!_keyCache.TryGetValue(keyUri, out var keyBytes))
        {
            keyBytes = await DownloadWithRetryAsync(segment.Key.Uri, null, options, cancellationToken).ConfigureAwait(false);
            _keyCache[keyUri] = keyBytes;
        }

        var iv = segment.Key.GetEffectiveIv(segment.SequenceNumber);

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;

        try
        {
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        }
        catch (CryptographicException)
        {
            // Some non-standard HLS streams use unpadded AES-128
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        }
    }

    private async Task<byte[]> DownloadWithRetryAsync(
        Uri uri,
        HlsByteRange? byteRange,
        HlsDownloadOptions options,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        var maxAttempts = Math.Max(1, options.MaxRetries);

        while (true)
        {
            attempts++;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(options.RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                // Add headers
                foreach (var header in options.Headers)
                {
                    if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.UserAgent.Clear();
                        request.Headers.TryAddWithoutValidation("User-Agent", header.Value);
                    }
                    else if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Referrer = new Uri(header.Value);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                if (byteRange != null)
                {
                    var from = byteRange.Offset ?? 0;
                    var to = from + byteRange.Length - 1;
                    request.Headers.Range = new RangeHeaderValue(from, to);
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cts.Token).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (attempts < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(1.5, attempts - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
