using System.Text.RegularExpressions;

namespace NovaDownloader.Media.Models;

/// <summary>
/// Data payload representing an intercepted network request/response captured by
/// browser extensions, proxy hooks, or internal HTTP sniffers.
/// </summary>
public sealed record SniffedNetworkMessage
{
    public required string RequestUrl { get; init; }
    public string? Method { get; init; } = "GET";
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public Dictionary<string, string> RequestHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? PageUrl { get; init; }
    public string? PageTitle { get; init; }
    public int? StatusCode { get; init; }
}

/// <summary>
/// Detector engine that analyzes network activity to identify video streams,
/// adaptive playlists (HLS / DASH), direct media containers, and metadata.
/// </summary>
public static class MediaSnifferDetector
{
    private static readonly Regex HlsPattern = new(@"\.m3u8(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DashPattern = new(@"\.mpd(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DirectMp4Pattern = new(@"\.(mp4|m4v)(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WebmPattern = new(@"\.webm(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AudioPattern = new(@"\.(mp3|aac|m4a|ogg|opus|flac|wav)(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TsPattern = new(@"\.ts(\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evaluates if the URL or content-type corresponds to an interceptable media stream.
    /// </summary>
    public static bool IsMediaStream(string url, string? contentType = null)
    {
        if (DetectFormat(url, contentType) != MediaFormat.Unknown)
            return true;

        if (url.Contains("/hls/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("master.m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("index.m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/dash/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("manifest.mpd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Identifies the media format from URL path and/or HTTP Content-Type.
    /// </summary>
    public static MediaFormat DetectFormat(string url, string? contentType = null)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.Trim().ToLowerInvariant();
            if (ct.Contains("application/vnd.apple.mpegurl") ||
                ct.Contains("application/x-mpegurl") ||
                ct.Contains("audio/x-mpegurl"))
            {
                return MediaFormat.HLS;
            }

            if (ct.Contains("application/dash+xml"))
                return MediaFormat.DASH;

            if (ct.Contains("video/mp4") || ct.Contains("video/x-m4v"))
                return MediaFormat.DirectMp4;

            if (ct.Contains("video/webm"))
                return MediaFormat.WebM;

            if (ct.Contains("audio/"))
                return MediaFormat.DirectAudio;
        }

        var cleanPath = GetUriPath(url);

        if (HlsPattern.IsMatch(cleanPath))
            return MediaFormat.HLS;

        if (DashPattern.IsMatch(cleanPath))
            return MediaFormat.DASH;

        if (DirectMp4Pattern.IsMatch(cleanPath))
            return MediaFormat.DirectMp4;

        if (WebmPattern.IsMatch(cleanPath))
            return MediaFormat.WebM;

        if (AudioPattern.IsMatch(cleanPath))
            return MediaFormat.DirectAudio;

        if (TsPattern.IsMatch(cleanPath))
            return MediaFormat.Other;

        return MediaFormat.Unknown;
    }

    /// <summary>
    /// Creates a populated MediaItem from a sniffed network message.
    /// </summary>
    public static MediaItem CreateMediaItem(SniffedNetworkMessage message)
    {
        var format = DetectFormat(message.RequestUrl, message.ContentType);
        var title = GenerateTitle(message);
        var streamType = format switch
        {
            MediaFormat.DirectAudio => MediaStreamType.Audio,
            _ => MediaStreamType.Muxed
        };

        var item = new MediaItem
        {
            Title = title,
            Url = message.RequestUrl,
            Format = format,
            StreamType = streamType,
            SourcePageUrl = message.PageUrl,
            CapturedAt = DateTimeOffset.UtcNow
        };

        // Forward essential headers (User-Agent, Referer, Cookie, Authorization)
        foreach (var header in message.RequestHeaders)
        {
            if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                item.Headers[header.Key] = header.Value;
            }
        }

        // Add a default quality entry if direct file
        if (format is MediaFormat.DirectMp4 or MediaFormat.WebM or MediaFormat.DirectAudio)
        {
            item.AvailableQualities.Add(new MediaQuality
            {
                Quality = "Original",
                Bitrate = message.ContentLength ?? 0,
                Url = message.RequestUrl
            });
        }

        return item;
    }

    private static string GenerateTitle(SniffedNetworkMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.PageTitle))
            return SanitizeTitle(message.PageTitle);

        try
        {
            var uri = new Uri(message.RequestUrl);
            var fileName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Length > 2)
            {
                return SanitizeTitle(fileName);
            }
        }
        catch
        {
            // fallback
        }

        return $"Stream_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
    }

    private static string SanitizeTitle(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = title.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim(' ', '_');
    }

    private static string GetUriPath(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.AbsolutePath;
        }
        catch
        {
            // ignore
        }
        return url;
    }
}
