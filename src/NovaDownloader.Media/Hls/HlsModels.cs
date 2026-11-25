using NovaDownloader.Media.Models;

namespace NovaDownloader.Media.Hls;

/// <summary>
/// Supported encryption algorithms in HLS playlists.
/// </summary>
public enum HlsEncryptionMethod
{
    None = 0,
    Aes128,
    SampleAes,
    Other
}

/// <summary>
/// Byte range specification for sub-resource downloading (#EXT-X-BYTERANGE).
/// </summary>
public sealed record HlsByteRange(long Length, long? Offset = null)
{
    public override string ToString() => Offset.HasValue ? $"{Length}@{Offset.Value}" : $"{Length}";
}

/// <summary>
/// Encryption key metadata extracted from #EXT-X-KEY tag.
/// </summary>
public sealed class HlsKey
{
    public HlsEncryptionMethod Method { get; init; } = HlsEncryptionMethod.None;
    public Uri? Uri { get; init; }
    public byte[]? Iv { get; init; }
    public string? KeyFormat { get; init; }
    public string? KeyFormatVersions { get; init; }

    /// <summary>
    /// Gets the effective 16-byte initialization vector (IV) for AES-128 decryption.
    /// If IV was not specified in the tag, RFC 8216 Section 5.2 defines IV as the 
    /// media sequence number formatted as a 128-bit big-endian integer.
    /// </summary>
    public byte[] GetEffectiveIv(long sequenceNumber)
    {
        if (Iv is { Length: 16 })
            return Iv;

        var derived = new byte[16];
        // Big-endian sequence number in lower 8 bytes
        var seqBytes = BitConverter.GetBytes(sequenceNumber);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(seqBytes);

        Buffer.BlockCopy(seqBytes, 0, derived, 16 - seqBytes.Length, seqBytes.Length);
        return derived;
    }
}

/// <summary>
/// Initialization segment metadata (e.g. #EXT-X-MAP for fragmented MP4 / CMAF).
/// </summary>
public sealed record HlsInitSegment(Uri Uri, HlsByteRange? ByteRange = null);

/// <summary>
/// A media segment (#EXTINF) representing a chunk of audio or video.
/// </summary>
public sealed class HlsSegment
{
    public required int Index { get; init; }
    public required long SequenceNumber { get; init; }
    public required double Duration { get; init; }
    public string? Title { get; init; }
    public required Uri Uri { get; init; }
    public HlsByteRange? ByteRange { get; init; }
    public HlsKey? Key { get; init; }
    public bool HasDiscontinuity { get; init; }
    public HlsInitSegment? InitSegment { get; init; }

    public override string ToString() =>
        $"Segment #{Index} [Seq={SequenceNumber}, Dur={Duration:0.00}s, Encrypted={Key != null && Key.Method != HlsEncryptionMethod.None}]: {Uri}";
}

/// <summary>
/// Audio or alternate rendition specified in master playlist (#EXT-X-MEDIA).
/// </summary>
public sealed record HlsAudioRendition
{
    public required string GroupId { get; init; }
    public required string Name { get; init; }
    public string? Language { get; init; }
    public bool IsDefault { get; init; }
    public bool IsAutoSelect { get; init; }
    public Uri? Uri { get; init; }
    public string? Channels { get; init; }
}

/// <summary>
/// Variant stream specification from master playlist (#EXT-X-STREAM-INF).
/// </summary>
public sealed class HlsStreamVariant
{
    public long Bandwidth { get; init; }
    public long? AverageBandwidth { get; init; }
    public string? Resolution { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codecs { get; init; }
    public double? FrameRate { get; init; }
    public required Uri Uri { get; init; }
    public string? AudioGroupId { get; init; }
    public string? SubtitlesGroupId { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// Derives standard quality label such as "1080p", "720p", "480p", etc.
    /// </summary>
    public string GetQualityLabel()
    {
        if (!string.IsNullOrEmpty(Name))
            return Name;

        if (Height.HasValue && Height.Value > 0)
        {
            var p = $"{Height.Value}p";
            if (FrameRate.HasValue && FrameRate.Value > 45)
                p += $"{Math.Round(FrameRate.Value)}";
            return p;
        }

        if (Bandwidth > 0)
        {
            return Bandwidth switch
            {
                >= 8_000_000 => "1080p (HQ)",
                >= 4_500_000 => "1080p",
                >= 2_500_000 => "720p",
                >= 1_200_000 => "480p",
                >= 700_000 => "360p",
                >= 300_000 => "240p",
                _ => $"Stream ({(Bandwidth / 1000)}k)"
            };
        }

        return "Auto";
    }

    public MediaQuality ToMediaQuality(string? audioTrack = null) => new()
    {
        Quality = GetQualityLabel(),
        Bitrate = Bandwidth,
        Resolution = Resolution,
        Width = Width,
        Height = Height,
        Codecs = Codecs,
        FrameRate = FrameRate,
        Url = Uri.ToString(),
        AudioTrack = audioTrack
    };

    public override string ToString() =>
        $"{GetQualityLabel()} ({Resolution ?? "Adaptive"}, Bandwidth={Bandwidth} bps) -> {Uri}";
}

/// <summary>
/// Master playlist representing multiple variant streams and renditions.
/// </summary>
public sealed class HlsMasterPlaylist
{
    public Uri? BaseUri { get; init; }
    public List<HlsStreamVariant> Streams { get; init; } = [];
    public List<HlsAudioRendition> AudioRenditions { get; init; } = [];

    /// <summary>
    /// Converts variants to MediaQuality list for user selection.
    /// </summary>
    public List<MediaQuality> ToMediaQualities()
    {
        return Streams
            .OrderByDescending(s => s.Height ?? 0)
            .ThenByDescending(s => s.Bandwidth)
            .Select(s =>
            {
                var audio = AudioRenditions.FirstOrDefault(a => a.GroupId == s.AudioGroupId);
                return s.ToMediaQuality(audio?.Name);
            })
            .ToList();
    }
}

/// <summary>
/// Media playlist containing individual downloadable audio/video segments.
/// </summary>
public sealed class HlsMediaPlaylist
{
    public Uri? BaseUri { get; init; }
    public double TargetDuration { get; init; }
    public long MediaSequence { get; init; }
    public bool IsEndList { get; init; }
    public string? PlaylistType { get; init; } // "VOD" or "EVENT"
    public HlsInitSegment? InitSegment { get; init; }
    public List<HlsSegment> Segments { get; init; } = [];

    public TimeSpan TotalDuration => TimeSpan.FromSeconds(Segments.Sum(s => s.Duration));
    public bool IsLive => !IsEndList && !string.Equals(PlaylistType, "VOD", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"MediaPlaylist [{Segments.Count} segments, Total={TotalDuration:hh\\:mm\\:ss}, TargetDur={TargetDuration:0.0}s, EndList={IsEndList}]";
}

/// <summary>
/// Unified result of parsing an HLS playlist (can be master or media playlist).
/// </summary>
public sealed class HlsPlaylistResult
{
    public bool IsMaster => MasterPlaylist != null;
    public HlsMasterPlaylist? MasterPlaylist { get; init; }
    public HlsMediaPlaylist? MediaPlaylist { get; init; }
}
