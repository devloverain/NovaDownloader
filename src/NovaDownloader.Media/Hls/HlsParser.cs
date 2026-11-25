using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaDownloader.Media.Hls;

/// <summary>
/// Robust, standard-compliant HLS (RFC 8216) playlist parser.
/// </summary>
public sealed class HlsParser : IHlsParser
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public HlsParser(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _disposeClient = true;
        }
    }

    /// <inheritdoc />
    public bool IsMasterPlaylist(string playlistContent)
    {
        if (string.IsNullOrWhiteSpace(playlistContent))
            return false;

        return playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public HlsPlaylistResult Parse(string playlistContent, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(playlistContent))
            throw new ArgumentException("Playlist content cannot be null or empty.", nameof(playlistContent));

        if (IsMasterPlaylist(playlistContent))
        {
            return new HlsPlaylistResult
            {
                MasterPlaylist = ParseMasterPlaylist(playlistContent, baseUri)
            };
        }

        return new HlsPlaylistResult
        {
            MediaPlaylist = ParseMediaPlaylist(playlistContent, baseUri)
        };
    }

    /// <inheritdoc />
    public async Task<HlsPlaylistResult> FetchAndParseAsync(
        string url,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty.", nameof(url));

        var targetUri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);

        if (headers != null)
        {
            foreach (var kvp in headers)
            {
                if (kvp.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.TryAddWithoutValidation("User-Agent", kvp.Value);
                }
                else if (kvp.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Referrer = new Uri(kvp.Value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var effectiveBaseUri = response.RequestMessage?.RequestUri ?? targetUri;

        return Parse(content, effectiveBaseUri);
    }

    /// <inheritdoc />
    public HlsMasterPlaylist ParseMasterPlaylist(string content, Uri? baseUri = null)
    {
        var lines = SplitLines(content);
        var streams = new List<HlsStreamVariant>();
        var audioRenditions = new List<HlsAudioRendition>();

        HlsStreamVariantPending? pendingVariant = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var attrString = line["#EXT-X-STREAM-INF:".Length..];
                var attrs = ParseAttributeList(attrString);
                pendingVariant = ParseStreamInfAttributes(attrs);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                var attrString = line["#EXT-X-MEDIA:".Length..];
                var attrs = ParseAttributeList(attrString);
                var rendition = ParseMediaRendition(attrs, baseUri);
                if (rendition != null)
                {
                    audioRenditions.Add(rendition);
                }
            }
            else if (!line.StartsWith('#') && pendingVariant != null)
            {
                // URI for the preceding #EXT-X-STREAM-INF
                var resolvedUri = ResolveUri(baseUri, line);
                streams.Add(new HlsStreamVariant
                {
                    Uri = resolvedUri,
                    Bandwidth = pendingVariant.Bandwidth,
                    AverageBandwidth = pendingVariant.AverageBandwidth,
                    Resolution = pendingVariant.Resolution,
                    Width = pendingVariant.Width,
                    Height = pendingVariant.Height,
                    Codecs = pendingVariant.Codecs,
                    FrameRate = pendingVariant.FrameRate,
                    AudioGroupId = pendingVariant.AudioGroupId,
                    SubtitlesGroupId = pendingVariant.SubtitlesGroupId,
                    Name = pendingVariant.Name
                });

                pendingVariant = null;
            }
        }

        return new HlsMasterPlaylist
        {
            BaseUri = baseUri,
            Streams = streams,
            AudioRenditions = audioRenditions
        };
    }

    /// <inheritdoc />
    public HlsMediaPlaylist ParseMediaPlaylist(string content, Uri? baseUri = null)
    {
        var lines = SplitLines(content);
        var segments = new List<HlsSegment>();

        double targetDuration = 0;
        long mediaSequence = 0;
        bool isEndList = false;
        string? playlistType = null;

        HlsKey? currentKey = null;
        HlsInitSegment? currentInitSegment = null;
        bool currentDiscontinuity = false;
        HlsByteRange? pendingByteRange = null;

        double? pendingExtInfDuration = null;
        string? pendingExtInfTitle = null;

        var segmentIndex = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line["#EXT-X-TARGETDURATION:".Length..].Trim();
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var td))
                    targetDuration = td;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim();
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                    mediaSequence = seq;
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.OrdinalIgnoreCase))
            {
                playlistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim();
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                isEndList = true;
            }
            else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
            {
                currentDiscontinuity = true;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var attrString = line["#EXT-X-KEY:".Length..];
                var attrs = ParseAttributeList(attrString);
                currentKey = ParseKey(attrs, baseUri);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var attrString = line["#EXT-X-MAP:".Length..];
                var attrs = ParseAttributeList(attrString);
                currentInitSegment = ParseInitMap(attrs, baseUri);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line["#EXT-X-BYTERANGE:".Length..].Trim();
                pendingByteRange = ParseByteRange(val);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var info = line["#EXTINF:".Length..].Trim();
                var commaIdx = info.IndexOf(',');
                if (commaIdx >= 0)
                {
                    var durStr = info[..commaIdx].Trim();
                    if (double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                        pendingExtInfDuration = dur;

                    var titleStr = info[(commaIdx + 1)..].Trim();
                    pendingExtInfTitle = string.IsNullOrEmpty(titleStr) ? null : titleStr;
                }
                else
                {
                    if (double.TryParse(info, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                        pendingExtInfDuration = dur;
                }
            }
            else if (!line.StartsWith('#'))
            {
                // Media segment URI line
                var resolvedUri = ResolveUri(baseUri, line);
                var duration = pendingExtInfDuration ?? targetDuration;
                var seqNum = mediaSequence + segmentIndex;

                segments.Add(new HlsSegment
                {
                    Index = segmentIndex,
                    SequenceNumber = seqNum,
                    Duration = duration,
                    Title = pendingExtInfTitle,
                    Uri = resolvedUri,
                    ByteRange = pendingByteRange,
                    Key = currentKey,
                    HasDiscontinuity = currentDiscontinuity,
                    InitSegment = currentInitSegment
                });

                segmentIndex++;
                pendingExtInfDuration = null;
                pendingExtInfTitle = null;
                pendingByteRange = null;
                currentDiscontinuity = false;
            }
        }

        return new HlsMediaPlaylist
        {
            BaseUri = baseUri,
            TargetDuration = targetDuration,
            MediaSequence = mediaSequence,
            IsEndList = isEndList,
            PlaylistType = playlistType,
            InitSegment = currentInitSegment,
            Segments = segments
        };
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    #region Parsing Helpers

    private static HlsStreamVariantPending ParseStreamInfAttributes(Dictionary<string, string> attrs)
    {
        long bandwidth = 0;
        if (attrs.TryGetValue("BANDWIDTH", out var bwStr) &&
            long.TryParse(bwStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBw))
        {
            bandwidth = parsedBw;
        }

        long? avgBandwidth = null;
        if (attrs.TryGetValue("AVERAGE-BANDWIDTH", out var avgBwStr) &&
            long.TryParse(avgBwStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAvg))
        {
            avgBandwidth = parsedAvg;
        }

        string? resolution = null;
        int? width = null;
        int? height = null;
        if (attrs.TryGetValue("RESOLUTION", out var resStr))
        {
            resolution = resStr;
            var parts = resStr.Split('x', 'X');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                width = w;
                height = h;
            }
        }

        double? frameRate = null;
        if (attrs.TryGetValue("FRAME-RATE", out var frStr) &&
            double.TryParse(frStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFr))
        {
            frameRate = parsedFr;
        }

        attrs.TryGetValue("CODECS", out var codecs);
        attrs.TryGetValue("AUDIO", out var audioGroup);
        attrs.TryGetValue("SUBTITLES", out var subsGroup);
        attrs.TryGetValue("NAME", out var name);

        return new HlsStreamVariantPending(
            bandwidth, avgBandwidth, resolution, width, height, codecs, frameRate, audioGroup, subsGroup, name);
    }

    private static HlsAudioRendition? ParseMediaRendition(Dictionary<string, string> attrs, Uri? baseUri)
    {
        attrs.TryGetValue("TYPE", out var type);
        if (!string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
            return null; // For now focused on audio renditions

        attrs.TryGetValue("GROUP-ID", out var groupId);
        attrs.TryGetValue("NAME", out var name);

        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(name))
            return null;

        attrs.TryGetValue("LANGUAGE", out var lang);
        attrs.TryGetValue("DEFAULT", out var isDefStr);
        attrs.TryGetValue("AUTOSELECT", out var isAutoStr);
        attrs.TryGetValue("CHANNELS", out var channels);

        Uri? uri = null;
        if (attrs.TryGetValue("URI", out var uriStr))
        {
            uri = ResolveUri(baseUri, uriStr);
        }

        return new HlsAudioRendition
        {
            GroupId = groupId,
            Name = name,
            Language = lang,
            IsDefault = string.Equals(isDefStr, "YES", StringComparison.OrdinalIgnoreCase),
            IsAutoSelect = string.Equals(isAutoStr, "YES", StringComparison.OrdinalIgnoreCase),
            Uri = uri,
            Channels = channels
        };
    }

    private static HlsKey? ParseKey(Dictionary<string, string> attrs, Uri? baseUri)
    {
        if (!attrs.TryGetValue("METHOD", out var methodStr))
            return null;

        var method = methodStr.ToUpperInvariant() switch
        {
            "NONE" => HlsEncryptionMethod.None,
            "AES-128" => HlsEncryptionMethod.Aes128,
            "SAMPLE-AES" => HlsEncryptionMethod.SampleAes,
            _ => HlsEncryptionMethod.Other
        };

        if (method == HlsEncryptionMethod.None)
            return null;

        Uri? keyUri = null;
        if (attrs.TryGetValue("URI", out var uriStr))
        {
            keyUri = ResolveUri(baseUri, uriStr);
        }

        byte[]? iv = null;
        if (attrs.TryGetValue("IV", out var ivStr))
        {
            iv = ParseHexBytes(ivStr);
        }

        attrs.TryGetValue("KEYFORMAT", out var keyFormat);
        attrs.TryGetValue("KEYFORMATVERSIONS", out var keyVersions);

        return new HlsKey
        {
            Method = method,
            Uri = keyUri,
            Iv = iv,
            KeyFormat = keyFormat,
            KeyFormatVersions = keyVersions
        };
    }

    private static HlsInitSegment? ParseInitMap(Dictionary<string, string> attrs, Uri? baseUri)
    {
        if (!attrs.TryGetValue("URI", out var uriStr))
            return null;

        var uri = ResolveUri(baseUri, uriStr);
        HlsByteRange? byteRange = null;

        if (attrs.TryGetValue("BYTERANGE", out var brStr))
        {
            byteRange = ParseByteRange(brStr);
        }

        return new HlsInitSegment(uri, byteRange);
    }

    private static HlsByteRange ParseByteRange(string val)
    {
        var atIdx = val.IndexOf('@');
        if (atIdx >= 0)
        {
            var lenStr = val[..atIdx].Trim();
            var offStr = val[(atIdx + 1)..].Trim();
            long.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);
            long.TryParse(offStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset);
            return new HlsByteRange(length, offset);
        }

        long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var len);
        return new HlsByteRange(len);
    }

    /// <summary>
    /// Parses an RFC 8216 attribute list (key=value or key="quoted-string"),
    /// correctly handling commas enclosed within quotes.
    /// </summary>
    public static Dictionary<string, string> ParseAttributeList(string attributeList)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(attributeList))
            return result;

        var span = attributeList.AsSpan();
        var i = 0;

        while (i < span.Length)
        {
            // Skip leading whitespace or commas
            while (i < span.Length && (char.IsWhiteSpace(span[i]) || span[i] == ','))
                i++;

            if (i >= span.Length)
                break;

            // Find '='
            var eqIdx = span[i..].IndexOf('=');
            if (eqIdx == -1)
                break;

            var key = span.Slice(i, eqIdx).Trim().ToString();
            i += eqIdx + 1;

            if (i >= span.Length)
            {
                result[key] = string.Empty;
                break;
            }

            string value;
            if (span[i] == '"')
            {
                // Quoted value
                i++; // skip opening quote
                var closeQuoteIdx = span[i..].IndexOf('"');
                if (closeQuoteIdx == -1)
                {
                    value = span[i..].ToString();
                    i = span.Length;
                }
                else
                {
                    value = span.Slice(i, closeQuoteIdx).ToString();
                    i += closeQuoteIdx + 1; // skip closing quote
                }
            }
            else
            {
                // Unquoted value up to next comma or end
                var commaIdx = span[i..].IndexOf(',');
                if (commaIdx == -1)
                {
                    value = span[i..].Trim().ToString();
                    i = span.Length;
                }
                else
                {
                    value = span.Slice(i, commaIdx).Trim().ToString();
                    i += commaIdx + 1;
                }
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Resolves a potentially relative segment or playlist URL against a base URI.
    /// </summary>
    public static Uri ResolveUri(Uri? baseUri, string relativeOrAbsolute)
    {
        if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absoluteUri))
            return absoluteUri;

        if (baseUri == null)
            return new Uri(relativeOrAbsolute, UriKind.RelativeOrAbsolute);

        return new Uri(baseUri, relativeOrAbsolute);
    }

    private static byte[]? ParseHexBytes(string hex)
    {
        var clean = hex.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            clean = clean[2..];

        if (clean.Length % 2 != 0)
            clean = "0" + clean;

        try
        {
            return Convert.FromHexString(clean);
        }
        catch
        {
            return null;
        }
    }

    private static string[] SplitLines(string text)
    {
        return text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    }

    private sealed record HlsStreamVariantPending(
        long Bandwidth,
        long? AverageBandwidth,
        string? Resolution,
        int? Width,
        int? Height,
        string? Codecs,
        double? FrameRate,
        string? AudioGroupId,
        string? SubtitlesGroupId,
        string? Name);

    #endregion
}
