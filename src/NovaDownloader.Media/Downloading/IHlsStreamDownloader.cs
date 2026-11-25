using NovaDownloader.Media.Hls;

namespace NovaDownloader.Media.Downloading;

/// <summary>
/// Service interface for downloading HLS streams and segments.
/// </summary>
public interface IHlsStreamDownloader
{
    /// <summary>
    /// Downloads all segments in the given media playlist concurrently according to options.
    /// </summary>
    Task<HlsDownloadResult> DownloadAsync(
        HlsMediaPlaylist playlist,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the media playlist from the URL and downloads all its segments.
    /// </summary>
    Task<HlsDownloadResult> DownloadAsync(
        string mediaPlaylistUrl,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
