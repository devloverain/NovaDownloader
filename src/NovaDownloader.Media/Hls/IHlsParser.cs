namespace NovaDownloader.Media.Hls;

/// <summary>
/// Service interface for parsing HLS (.m3u8) playlists and manifests.
/// </summary>
public interface IHlsParser : IDisposable
{
    /// <summary>
    /// Checks whether the raw playlist text represents a master playlist (#EXT-X-STREAM-INF).
    /// </summary>
    bool IsMasterPlaylist(string playlistContent);

    /// <summary>
    /// Parses an HLS playlist string and returns either a master or media playlist result.
    /// </summary>
    HlsPlaylistResult Parse(string playlistContent, Uri? baseUri = null);

    /// <summary>
    /// Parses an HLS master playlist string.
    /// </summary>
    HlsMasterPlaylist ParseMasterPlaylist(string content, Uri? baseUri = null);

    /// <summary>
    /// Parses an HLS media playlist string with segment URIs, durations, keys, etc.
    /// </summary>
    HlsMediaPlaylist ParseMediaPlaylist(string content, Uri? baseUri = null);

    /// <summary>
    /// Fetches an m3u8 playlist from a URL and parses it asynchronously.
    /// </summary>
    Task<HlsPlaylistResult> FetchAndParseAsync(
        string url,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}
