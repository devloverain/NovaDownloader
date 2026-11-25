using System.Diagnostics;

namespace NovaDownloader.Media.Muxing;

/// <summary>
/// Direct stream merger that concatenates MPEG-TS or fMP4 segments at the binary stream level.
/// Used as a high-performance fallback when FFmpeg is not installed on the system.
/// </summary>
public static class DirectStreamMerger
{
    private const int BufferSize = 256 * 1024; // 256 KB buffer for high I/O throughput

    /// <summary>
    /// Concatenates segment files into a single destination file sequentially.
    /// </summary>
    public static async Task<MergeResult> ConcatenateAsync(
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
                Error = "No segment files provided for direct concatenation.",
                UsedFfmpeg = false
            };
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stopwatch = Stopwatch.StartNew();
        var buffer = new byte[BufferSize];

        try
        {
            await using (var outputStream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true))
            {
                // Write init segment first if present (for CMAF / fragmented MP4)
                if (!string.IsNullOrEmpty(initSegmentPath) && File.Exists(initSegmentPath))
                {
                    await AppendFileToStreamAsync(initSegmentPath, outputStream, buffer, cancellationToken).ConfigureAwait(false);
                }

                // Write all segments in exact sequence
                foreach (var segmentFile in segmentPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!File.Exists(segmentFile))
                        continue;

                    await AppendFileToStreamAsync(segmentFile, outputStream, buffer, cancellationToken).ConfigureAwait(false);
                }

                await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            var fileInfo = new FileInfo(outputPath);

            return new MergeResult
            {
                Success = true,
                OutputPath = outputPath,
                OutputSizeBytes = fileInfo.Length,
                Elapsed = stopwatch.Elapsed,
                UsedFfmpeg = false
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = $"Direct stream concatenation failed: {ex.Message}",
                Elapsed = stopwatch.Elapsed,
                UsedFfmpeg = false
            };
        }
    }

    private static async Task AppendFileToStreamAsync(
        string filePath,
        Stream destinationStream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        int bytesRead;
        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }
}
