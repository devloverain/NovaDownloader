namespace NovaDownloader.Media.Muxing;

/// <summary>
/// Unified stream merger coordinating FFmpeg remuxing with automatic fallback to direct stream concatenation.
/// </summary>
public sealed class StreamMerger : IStreamMerger
{
    private readonly FfmpegMuxer _ffmpegMuxer;

    public StreamMerger(string? customFfmpegPath = null)
    {
        _ffmpegMuxer = new FfmpegMuxer(customFfmpegPath);
    }

    public StreamMerger(FfmpegMuxer ffmpegMuxer)
    {
        _ffmpegMuxer = ffmpegMuxer ?? throw new ArgumentNullException(nameof(ffmpegMuxer));
    }

    /// <inheritdoc />
    public bool IsFfmpegAvailable => _ffmpegMuxer.IsAvailable;

    /// <inheritdoc />
    public Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken = default) =>
        _ffmpegMuxer.GetVersionAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<MergeResult> MergeSegmentsAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        string? initSegmentPath = null,
        CancellationToken cancellationToken = default)
    {
        if (segmentPaths == null || segmentPaths.Count == 0)
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = "No segment files provided to merge.",
                UsedFfmpeg = false
            };
        }

        // Prefer FFmpeg if available for clean container remuxing and seekable +faststart MP4/MKV
        if (_ffmpegMuxer.IsAvailable)
        {
            var ffmpegResult = await _ffmpegMuxer.ConcatSegmentsAsync(
                segmentPaths,
                outputPath,
                initSegmentPath,
                cancellationToken).ConfigureAwait(false);

            if (ffmpegResult.Success)
                return ffmpegResult;

            // If FFmpeg encountered an error, fall back to direct concatenation
        }

        // Fallback: Direct binary stream concatenation
        return await DirectStreamMerger.ConcatenateAsync(
            segmentPaths,
            outputPath,
            initSegmentPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MergeResult> MuxAudioVideoAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!_ffmpegMuxer.IsAvailable)
        {
            return Task.FromResult(new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = "FFmpeg is required to mux separate audio and video tracks into a unified container.",
                UsedFfmpeg = false
            });
        }

        return _ffmpegMuxer.MuxAudioVideoAsync(videoPath, audioPath, outputPath, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MergeResult> ExtractAudioAsync(
        string inputVideoPath,
        string outputAudioPath,
        AudioExtractionFormat format = AudioExtractionFormat.Copy,
        CancellationToken cancellationToken = default)
    {
        if (!_ffmpegMuxer.IsAvailable)
        {
            return Task.FromResult(new MergeResult
            {
                Success = false,
                OutputPath = outputAudioPath,
                Error = "FFmpeg is required to extract or transcode audio tracks from video.",
                UsedFfmpeg = false
            });
        }

        return _ffmpegMuxer.ExtractAudioAsync(inputVideoPath, outputAudioPath, format, cancellationToken);
    }
}
