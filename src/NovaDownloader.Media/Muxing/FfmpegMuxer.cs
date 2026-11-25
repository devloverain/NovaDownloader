using System.Diagnostics;
using System.Text;

namespace NovaDownloader.Media.Muxing;

/// <summary>
/// High-performance FFmpeg process wrapper for stream concatenation, audio/video muxing,
/// and audio extraction without re-encoding (-c copy).
/// </summary>
public sealed class FfmpegMuxer
{
    private readonly string? _customFfmpegPath;
    private string? _resolvedFfmpegPath;
    private bool? _isAvailable;

    public FfmpegMuxer(string? customFfmpegPath = null)
    {
        _customFfmpegPath = customFfmpegPath;
    }

    /// <summary>
    /// Indicates whether FFmpeg binary was detected and is ready for use.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!_isAvailable.HasValue)
            {
                _resolvedFfmpegPath = ResolveFfmpegPath();
                _isAvailable = !string.IsNullOrEmpty(_resolvedFfmpegPath);
            }
            return _isAvailable.Value;
        }
    }

    /// <summary>
    /// Returns the resolved absolute path to the FFmpeg executable.
    /// </summary>
    public string? FfmpegExecutablePath => IsAvailable ? _resolvedFfmpegPath : null;

    /// <summary>
    /// Queries the FFmpeg CLI version string.
    /// </summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return null;

        var result = await RunProcessAsync("-version", cancellationToken).ConfigureAwait(false);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            var firstLine = result.Output.Split('\n')[0].Trim();
            return firstLine;
        }

        return null;
    }

    /// <summary>
    /// Concatenates segment files into a single container (.mp4, .mkv, .ts) using FFmpeg concat demuxer.
    /// Uses stream copy (-c copy) for instantaneous processing.
    /// </summary>
    public async Task<MergeResult> ConcatSegmentsAsync(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        string? initSegmentPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = "FFmpeg binary is not available on this system.",
                UsedFfmpeg = false
            };
        }

        if (segmentPaths == null || segmentPaths.Count == 0)
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = "No segment files provided for concatenation.",
                UsedFfmpeg = true
            };
        }

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        // Generate temporary concat manifest file
        var concatListFile = Path.Combine(
            Path.GetDirectoryName(segmentPaths[0]) ?? Path.GetTempPath(),
            $"concat_{Guid.NewGuid():N}.txt");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(initSegmentPath) && File.Exists(initSegmentPath))
            {
                var escapedInit = initSegmentPath.Replace('\\', '/').Replace("'", "'\\''");
                sb.AppendLine($"file '{escapedInit}'");
            }

            foreach (var segment in segmentPaths)
            {
                if (File.Exists(segment))
                {
                    var escaped = segment.Replace('\\', '/').Replace("'", "'\\''");
                    sb.AppendLine($"file '{escaped}'");
                }
            }

            await File.WriteAllTextAsync(concatListFile, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            var ext = Path.GetExtension(outputPath).ToLowerInvariant();

            // First attempt: try with AAC bitstream filter for MP4 to ensure AAC compatibility
            var arguments = ext == ".mp4"
                ? $"-y -f concat -safe 0 -i \"{concatListFile}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{outputPath}\""
                : $"-y -f concat -safe 0 -i \"{concatListFile}\" -c copy \"{outputPath}\"";

            var runResult = await RunProcessAsync(arguments, cancellationToken).ConfigureAwait(false);

            // If bitstream filter failed (e.g. input is not AAC or no audio track), retry without bsf
            if (!runResult.Success && ext == ".mp4" && (runResult.Error?.Contains("aac_adtstoasc", StringComparison.OrdinalIgnoreCase) == true ||
                                                        runResult.Error?.Contains("bitstream filter", StringComparison.OrdinalIgnoreCase) == true))
            {
                var fallbackArgs = $"-y -f concat -safe 0 -i \"{concatListFile}\" -c copy -movflags +faststart \"{outputPath}\"";
                runResult = await RunProcessAsync(fallbackArgs, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();

            if (!runResult.Success || !File.Exists(outputPath))
            {
                return new MergeResult
                {
                    Success = false,
                    OutputPath = outputPath,
                    Error = $"FFmpeg concat execution failed: {runResult.Error}",
                    Elapsed = stopwatch.Elapsed,
                    UsedFfmpeg = true
                };
            }

            var fileInfo = new FileInfo(outputPath);
            return new MergeResult
            {
                Success = true,
                OutputPath = outputPath,
                OutputSizeBytes = fileInfo.Length,
                Elapsed = stopwatch.Elapsed,
                UsedFfmpeg = true
            };
        }
        finally
        {
            try
            {
                if (File.Exists(concatListFile))
                    File.Delete(concatListFile);
            }
            catch
            {
                // ignore temp cleanup failure
            }
        }
    }

    /// <summary>
    /// Muxes separate video and audio streams into a single unified container without re-encoding (-c copy).
    /// </summary>
    public async Task<MergeResult> MuxAudioVideoAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = "FFmpeg binary is not available on this system.",
                UsedFfmpeg = false
            };
        }

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var stopwatch = Stopwatch.StartNew();
        var arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c copy -movflags +faststart \"{outputPath}\"";

        var runResult = await RunProcessAsync(arguments, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!runResult.Success || !File.Exists(outputPath))
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputPath,
                Error = $"FFmpeg muxing failed: {runResult.Error}",
                Elapsed = stopwatch.Elapsed,
                UsedFfmpeg = true
            };
        }

        return new MergeResult
        {
            Success = true,
            OutputPath = outputPath,
            OutputSizeBytes = new FileInfo(outputPath).Length,
            Elapsed = stopwatch.Elapsed,
            UsedFfmpeg = true
        };
    }

    /// <summary>
    /// Extracts the audio track from a video file without re-encoding or transcoded to specified format.
    /// </summary>
    public async Task<MergeResult> ExtractAudioAsync(
        string inputVideoPath,
        string outputAudioPath,
        AudioExtractionFormat format = AudioExtractionFormat.Copy,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputAudioPath,
                Error = "FFmpeg binary is not available on this system.",
                UsedFfmpeg = false
            };
        }

        var outDir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var codecArgs = format switch
        {
            AudioExtractionFormat.Copy => "-c:a copy",
            AudioExtractionFormat.Mp3 => "-c:a libmp3lame -q:a 2",
            AudioExtractionFormat.Aac => "-c:a aac -b:a 256k",
            AudioExtractionFormat.Flac => "-c:a flac",
            AudioExtractionFormat.Wav => "-c:a pcm_s16le",
            _ => "-c:a copy"
        };

        var arguments = $"-y -i \"{inputVideoPath}\" -vn {codecArgs} \"{outputAudioPath}\"";

        var stopwatch = Stopwatch.StartNew();
        var runResult = await RunProcessAsync(arguments, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!runResult.Success || !File.Exists(outputAudioPath))
        {
            return new MergeResult
            {
                Success = false,
                OutputPath = outputAudioPath,
                Error = $"Audio extraction failed: {runResult.Error}",
                Elapsed = stopwatch.Elapsed,
                UsedFfmpeg = true
            };
        }

        return new MergeResult
        {
            Success = true,
            OutputPath = outputAudioPath,
            OutputSizeBytes = new FileInfo(outputAudioPath).Length,
            Elapsed = stopwatch.Elapsed,
            UsedFfmpeg = true
        };
    }

    private async Task<(bool Success, string Output, string Error)> RunProcessAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var exePath = _resolvedFfmpegPath ?? "ffmpeg";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore kill error
            }
            throw;
        }
        catch (Exception ex)
        {
            return (false, outputBuilder.ToString(), $"Process error: {ex.Message}");
        }
    }

    private string? ResolveFfmpegPath()
    {
        // 1. Explicit path
        if (!string.IsNullOrWhiteSpace(_customFfmpegPath) && File.Exists(_customFfmpegPath))
            return _customFfmpegPath;

        // 2. NovaDownloader AppData Tools & WinGet links directory on Windows
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var novaTool = Path.Combine(localAppData, "NovaDownloader", "Tools", "ffmpeg.exe");
            if (File.Exists(novaTool))
                return novaTool;

            var wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(wingetLink))
                return wingetLink;
        }

        // 3. User profile and program files locations
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\tools\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 4. Probing PATH environment variable
        var isWindows = OperatingSystem.IsWindows();
        var binaryName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var fullPath = Path.Combine(segment.Trim(), binaryName);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch
                {
                    // ignore invalid path segment
                }
            }
        }

        return null;
    }
}
