namespace NovaDownloader.Media.Muxing;

/// <summary>
/// Audio extraction format options.
/// </summary>
public enum AudioExtractionFormat
{
    Copy,       // Stream copy without re-encoding (.m4a / source format)
    Mp3,        // MP3 encoding (libmp3lame)
    Aac,        // AAC encoding
    Flac,       // Lossless FLAC
    Wav         // Uncompressed PCM WAV
}

/// <summary>
/// Result of a stream merge or remuxing operation.
/// </summary>
public sealed class MergeResult
{
    public required bool Success { get; init; }
    public required string OutputPath { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long OutputSizeBytes { get; init; }
    public bool UsedFfmpeg { get; init; }
}

/// <summary>
/// Service interface for merging media segments and muxing audio/video streams.
/// </summary>
public interface IStreamMerger
{
    /// <summary>
    /// Indicates whether FFmpeg CLI binary is detected and operational.
    /// </summary>
    bool IsFfmpegAvailable { get; }

    /// <summary>
    /// Gets the detected FFmpeg version string if available.
    /// </summary>
    Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Concatenates a sequential list of segment files into a single output container (.mp4, .mkv, .ts).
    /// Uses FFmpeg when available for fast seekable output, or falls back to direct stream concatenation.
    /// </summary>
    Task<MergeResult> MergeSegmentsAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        string? initSegmentPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Muxes separate video and audio files into a single unified container without re-encoding (-c copy).
    /// </summary>
    Task<MergeResult> MuxAudioVideoAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the audio stream from a video file without re-encoding or transcoded to specified format.
    /// </summary>
    Task<MergeResult> ExtractAudioAsync(
        string inputVideoPath,
        string outputAudioPath,
        AudioExtractionFormat format = AudioExtractionFormat.Copy,
        CancellationToken cancellationToken = default);
}
