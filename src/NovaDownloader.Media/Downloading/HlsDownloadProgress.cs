namespace NovaDownloader.Media.Downloading;

/// <summary>
/// Real-time progress report emitted during HLS stream downloading.
/// </summary>
public sealed record HlsDownloadProgress
{
    /// <summary>
    /// Total number of segments successfully downloaded so far.
    /// </summary>
    public required int SegmentsCompleted { get; init; }

    /// <summary>
    /// Total number of segments in the media playlist.
    /// </summary>
    public required int TotalSegments { get; init; }

    /// <summary>
    /// Cumulative raw bytes downloaded so far.
    /// </summary>
    public required long BytesDownloaded { get; init; }

    /// <summary>
    /// Instantaneous or moving average download speed in bytes per second.
    /// </summary>
    public double SpeedBytesPerSecond { get; init; }

    /// <summary>
    /// Estimated time remaining to complete all segments.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; init; }

    /// <summary>
    /// Human-readable status message (e.g., "Downloading segment 45/180").
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Percentage completed (0.0 to 100.0).
    /// </summary>
    public double PercentComplete =>
        TotalSegments > 0 ? Math.Min(100.0, (double)SegmentsCompleted / TotalSegments * 100.0) : 0.0;

    /// <summary>
    /// User-friendly formatted speed (e.g. "12.4 MB/s").
    /// </summary>
    public string FormattedSpeed => SpeedBytesPerSecond switch
    {
        >= 1_048_576 => $"{(SpeedBytesPerSecond / 1_048_576.0):0.0} MB/s",
        >= 1024 => $"{(SpeedBytesPerSecond / 1024.0):0.0} KB/s",
        > 0 => $"{SpeedBytesPerSecond:0} B/s",
        _ => "0 B/s"
    };

    /// <summary>
    /// User-friendly formatted downloaded size (e.g. "45.2 MB").
    /// </summary>
    public string FormattedBytes => BytesDownloaded switch
    {
        >= 1_073_741_824 => $"{(BytesDownloaded / 1_073_741_824.0):0.00} GB",
        >= 1_048_576 => $"{(BytesDownloaded / 1_048_576.0):0.0} MB",
        >= 1024 => $"{(BytesDownloaded / 1024.0):0} KB",
        _ => $"{BytesDownloaded} B"
    };

    public override string ToString() =>
        $"[{PercentComplete:0.0}%] {SegmentsCompleted}/{TotalSegments} segments ({FormattedBytes}) @ {FormattedSpeed}";
}
