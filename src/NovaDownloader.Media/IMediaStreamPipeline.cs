using NovaDownloader.Media.Downloading;
using NovaDownloader.Media.Models;
using NovaDownloader.Media.Muxing;

namespace NovaDownloader.Media;

/// <summary>
/// Overall pipeline execution result for downloading and remuxing a complete media stream.
/// </summary>
public sealed class MediaPipelineResult
{
    public required bool Success { get; init; }
    public required string OutputPath { get; init; }
    public string? Error { get; init; }
    public long OutputSizeBytes { get; init; }
    public int TotalSegments { get; init; }
    public TimeSpan DownloadElapsed { get; init; }
    public TimeSpan MergeElapsed { get; init; }
    public TimeSpan TotalElapsed => DownloadElapsed + MergeElapsed;
    public bool UsedFfmpeg { get; init; }
    public string? SelectedQuality { get; init; }
}

/// <summary>
/// High-level orchestration pipeline for inspecting, downloading, and muxing media streams.
/// </summary>
public interface IMediaStreamPipeline
{
    /// <summary>
    /// Inspects a stream URL, parses master manifests if HLS/DASH, and builds a comprehensive MediaItem.
    /// </summary>
    Task<MediaItem> InspectMediaAsync(
        string url,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Complete end-to-end HLS download pipeline:
    /// Master playlist -> Quality selection -> Parallel segments download -> FFmpeg/Direct merge -> Clean output file.
    /// </summary>
    Task<MediaPipelineResult> DownloadHlsStreamAsync(
        string m3u8Url,
        string outputPath,
        string? targetQuality = null,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
