using NovaDownloader.Media.Downloading;
using NovaDownloader.Media.Hls;
using NovaDownloader.Media.Models;
using NovaDownloader.Media.Muxing;

namespace NovaDownloader.Media;

/// <summary>
/// End-to-end media pipeline coordinating sniffer models, HLS parsing, parallel downloading, and FFmpeg muxing.
/// </summary>
public sealed class MediaStreamPipeline : IMediaStreamPipeline
{
    private readonly IHlsParser _hlsParser;
    private readonly IHlsStreamDownloader _downloader;
    private readonly IStreamMerger _streamMerger;

    public MediaStreamPipeline(
        IHlsParser? hlsParser = null,
        IHlsStreamDownloader? downloader = null,
        IStreamMerger? streamMerger = null)
    {
        _hlsParser = hlsParser ?? new HlsParser();
        _downloader = downloader ?? new HlsStreamDownloader(null, _hlsParser);
        _streamMerger = streamMerger ?? new StreamMerger();
    }

    /// <inheritdoc />
    public async Task<MediaItem> InspectMediaAsync(
        string url,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var format = MediaSnifferDetector.DetectFormat(url);
        var item = new MediaItem
        {
            Title = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath),
            Url = url,
            Format = format,
            StreamType = MediaStreamType.Muxed
        };

        if (headers != null)
        {
            foreach (var kvp in headers)
                item.Headers[kvp.Key] = kvp.Value;
        }

        if (format == MediaFormat.HLS)
        {
            try
            {
                var parseResult = await _hlsParser.FetchAndParseAsync(url, headers, cancellationToken).ConfigureAwait(false);
                if (parseResult.IsMaster && parseResult.MasterPlaylist != null)
                {
                    item.AvailableQualities.AddRange(parseResult.MasterPlaylist.ToMediaQualities());
                }
                else if (parseResult.MediaPlaylist != null)
                {
                    item.Duration = parseResult.MediaPlaylist.TotalDuration;
                    item.AvailableQualities.Add(new MediaQuality
                    {
                        Quality = "Default",
                        Bitrate = 0,
                        Url = url
                    });
                }
            }
            catch
            {
                // Return basic MediaItem on network/parse failure
            }
        }

        return item;
    }

    /// <inheritdoc />
    public async Task<MediaPipelineResult> DownloadHlsStreamAsync(
        string m3u8Url,
        string outputPath,
        string? targetQuality = null,
        HlsDownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HlsDownloadOptions();

        // 1. Fetch and parse playlist
        var playlistResult = await _hlsParser.FetchAndParseAsync(m3u8Url, options.Headers, cancellationToken).ConfigureAwait(false);

        string effectiveMediaUrl = m3u8Url;
        string? selectedQualityName = targetQuality;

        if (playlistResult.IsMaster && playlistResult.MasterPlaylist != null)
        {
            var variants = playlistResult.MasterPlaylist.Streams;
            if (variants.Count == 0)
            {
                return new MediaPipelineResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    Error = "Master playlist contains no stream variants."
                };
            }

            // Select quality match or default to best
            HlsStreamVariant? selected = null;
            if (!string.IsNullOrWhiteSpace(targetQuality))
            {
                selected = variants.FirstOrDefault(v =>
                    string.Equals(v.GetQualityLabel(), targetQuality, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.Resolution, targetQuality, StringComparison.OrdinalIgnoreCase) ||
                    v.Name?.Equals(targetQuality, StringComparison.OrdinalIgnoreCase) == true);
            }

            selected ??= variants
                .OrderByDescending(v => v.Height ?? 0)
                .ThenByDescending(v => v.Bandwidth)
                .First();

            effectiveMediaUrl = selected.Uri.ToString();
            selectedQualityName = selected.GetQualityLabel();
        }

        // 2. Download all segments in parallel
        var downloadResult = await _downloader.DownloadAsync(
            effectiveMediaUrl,
            options,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!downloadResult.Success)
        {
            return new MediaPipelineResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = downloadResult.Error,
                DownloadElapsed = downloadResult.Elapsed
            };
        }

        // 3. Merge / Remux segments into single container
        var mergeResult = await _streamMerger.MergeSegmentsAsync(
            downloadResult.SegmentFiles,
            outputPath,
            downloadResult.InitSegmentFile,
            cancellationToken).ConfigureAwait(false);

        // 4. Cleanup temporary segments if requested
        if (options.CleanupSegmentsAfterMerge && !string.IsNullOrEmpty(downloadResult.WorkingDirectory))
        {
            try
            {
                if (Directory.Exists(downloadResult.WorkingDirectory))
                    Directory.Delete(downloadResult.WorkingDirectory, recursive: true);
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        if (!mergeResult.Success)
        {
            return new MediaPipelineResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = mergeResult.Error,
                TotalSegments = downloadResult.SegmentFiles.Count,
                DownloadElapsed = downloadResult.Elapsed,
                MergeElapsed = mergeResult.Elapsed,
                UsedFfmpeg = mergeResult.UsedFfmpeg,
                SelectedQuality = selectedQualityName
            };
        }

        return new MediaPipelineResult
        {
            Success = true,
            OutputPath = outputPath,
            OutputSizeBytes = mergeResult.OutputSizeBytes,
            TotalSegments = downloadResult.SegmentFiles.Count,
            DownloadElapsed = downloadResult.Elapsed,
            MergeElapsed = mergeResult.Elapsed,
            UsedFfmpeg = mergeResult.UsedFfmpeg,
            SelectedQuality = selectedQualityName
        };
    }
}
