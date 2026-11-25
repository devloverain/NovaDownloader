namespace NovaDownloader.Media.Downloading;

/// <summary>
/// Configuration options for downloading an HLS media stream.
/// </summary>
public sealed class HlsDownloadOptions
{
    /// <summary>
    /// Maximum number of segments downloaded concurrently in parallel. Default is 8.
    /// </summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// Maximum number of retry attempts per segment upon transient network errors. Default is 4.
    /// </summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// Base delay between retries. Default is 1 second.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Timeout per individual HTTP segment request. Default is 30 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Temporary workspace directory where segments are downloaded.
    /// If null, a uniquely generated folder in Path.GetTempPath() is used.
    /// </summary>
    public string? TempDirectory { get; set; }

    /// <summary>
    /// Custom HTTP headers to include with segment requests (e.g., Referer, Authorization, Cookies).
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Automatically decrypt AES-128 encrypted segments using their #EXT-X-KEY definition.
    /// Default is true.
    /// </summary>
    public bool AutoDecrypt { get; set; } = true;

    /// <summary>
    /// If true, deletes intermediate segment chunks after successful concatenation/muxing.
    /// Default is true.
    /// </summary>
    public bool CleanupSegmentsAfterMerge { get; set; } = true;
}
