namespace NovaDownloader.Media.Downloading;

/// <summary>
/// Result of an HLS stream download operation.
/// </summary>
public sealed class HlsDownloadResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Sequential list of paths to downloaded segment files ordered from segment 0 to N-1.
    /// </summary>
    public IReadOnlyList<string> SegmentFiles { get; init; } = [];

    /// <summary>
    /// Path to initialization segment file (fMP4 / #EXT-X-MAP) if present.
    /// </summary>
    public string? InitSegmentFile { get; init; }

    /// <summary>
    /// Total bytes downloaded across all segments and headers.
    /// </summary>
    public long TotalBytesDownloaded { get; init; }

    /// <summary>
    /// Total elapsed wall clock time for the download.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Working directory where segments are stored.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Indicates whether segments are fragmented MP4/CMAF (.m4s) or MPEG-TS (.ts).
    /// </summary>
    public bool IsFragmentedMp4 { get; init; }
}
