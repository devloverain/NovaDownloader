namespace NovaDownloader.Media.Models;

/// <summary>
/// Represents the streaming or container format of an intercepted media source.
/// </summary>
public enum MediaFormat
{
    Unknown = 0,
    HLS,        // HTTP Live Streaming (.m3u8)
    DASH,       // Dynamic Adaptive Streaming over HTTP (.mpd)
    DirectMp4,  // Standalone MP4 container (.mp4)
    WebM,       // WebM video/audio (.webm)
    DirectAudio,// Direct audio files (.mp3, .aac, .m4a, .ogg, .flac)
    Other       // Other streams (.ts, .flv, .mkv, etc.)
}

/// <summary>
/// Represents the role and content type of the media stream.
/// </summary>
public enum MediaStreamType
{
    Video,      // Video only
    Audio,      // Audio only
    Muxed,      // Muxed Video + Audio
    LiveStream, // Live streaming broadcast
    VOD         // Video On Demand
}

/// <summary>
/// Represents a specific stream quality or variant rendition available for a media item.
/// </summary>
public sealed record MediaQuality
{
    /// <summary>
    /// User-friendly quality label (e.g. "1080p", "720p", "480p", "360p", "Audio Only").
    /// </summary>
    public required string Quality { get; init; }

    /// <summary>
    /// Stream bitrate in bits per second.
    /// </summary>
    public long Bitrate { get; init; }

    /// <summary>
    /// Display resolution formatted as "WIDTHxHEIGHT" (e.g. "1920x1080").
    /// </summary>
    public string? Resolution { get; init; }

    /// <summary>
    /// Direct URL or playlist URL for this specific quality stream.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Horizontal pixel resolution.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Vertical pixel resolution.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    /// Codec string (e.g., "avc1.640028,mp4a.40.2" or "vp9,opus").
    /// </summary>
    public string? Codecs { get; init; }

    /// <summary>
    /// Frame rate in frames per second (fps).
    /// </summary>
    public double? FrameRate { get; init; }

    /// <summary>
    /// Identifier or name of the associated audio track if separated.
    /// </summary>
    public string? AudioTrack { get; init; }

    /// <summary>
    /// Formatted bitrate for UI display (e.g., "4.5 Mbps" or "800 kbps").
    /// </summary>
    public string FormattedBitrate => Bitrate switch
    {
        >= 1_000_000 => $"{(Bitrate / 1_000_000.0):0.0} Mbps",
        >= 1_000 => $"{(Bitrate / 1_000.0):0} kbps",
        > 0 => $"{Bitrate} bps",
        _ => "Unknown"
    };

    public override string ToString() =>
        !string.IsNullOrEmpty(Resolution)
            ? $"{Quality} ({Resolution}, {FormattedBitrate})"
            : $"{Quality} ({FormattedBitrate})";
}

/// <summary>
/// Represents an intercepted media resource captured by the sniffer or browser extension.
/// </summary>
public sealed class MediaItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// User-friendly media title (e.g. from web page title, playlist title, or file name).
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Primary entry URL (master playlist URL, direct media URL, or MPD manifest URL).
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Intercepted media format (HLS, DASH, DirectMp4, WebM, etc.).
    /// </summary>
    public MediaFormat Format { get; set; }

    /// <summary>
    /// Type of media (Video, Audio, Muxed, LiveStream, VOD).
    /// </summary>
    public MediaStreamType StreamType { get; set; } = MediaStreamType.Muxed;

    /// <summary>
    /// Available qualities and adaptive stream renditions.
    /// </summary>
    public List<MediaQuality> AvailableQualities { get; init; } = [];

    /// <summary>
    /// Custom HTTP request headers required to fetch the stream (e.g., Referer, User-Agent, Cookie, Authorization).
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Estimated or parsed total duration of the media.
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Optional thumbnail image URL.
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Web page URL where this media stream was intercepted.
    /// </summary>
    public string? SourcePageUrl { get; set; }

    /// <summary>
    /// Timestamp when this media item was intercepted.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// File extension suggested for the final saved file (e.g. ".mp4", ".mkv", ".mp3").
    /// </summary>
    public string SuggestedExtension => Format switch
    {
        MediaFormat.HLS => ".mp4",
        MediaFormat.DASH => ".mp4",
        MediaFormat.DirectMp4 => ".mp4",
        MediaFormat.WebM => ".webm",
        MediaFormat.DirectAudio => ".m4a",
        _ => ".mp4"
    };

    /// <summary>
    /// Returns the best available quality based on height/bitrate.
    /// </summary>
    public MediaQuality? GetBestQuality() =>
        AvailableQualities
            .OrderByDescending(q => q.Height ?? 0)
            .ThenByDescending(q => q.Bitrate)
            .FirstOrDefault();

    /// <summary>
    /// Returns the quality closest to the requested height (e.g., 1080, 720).
    /// </summary>
    public MediaQuality? GetQualityByHeight(int targetHeight) =>
        AvailableQualities
            .OrderBy(q => Math.Abs((q.Height ?? 0) - targetHeight))
            .FirstOrDefault() ?? GetBestQuality();
}
