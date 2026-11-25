using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;

namespace NovaDownloader.UI.Services;

public static class YtDlpService
{
    private static readonly ConcurrentDictionary<Guid, (Process Process, CancellationTokenSource Cts)> ActiveProcesses = new();

    public static bool IsSupportedVideoPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com") || host.Contains("youtu.be")
            || host.Contains("aparat.com") || host.Contains("vimeo.com")
            || host.Contains("tiktok.com") || host.Contains("instagram.com")
            || host.Contains("twitter.com") || host.Contains("x.com");
    }

    public static string? FindYtDlpExe()
    {
        var custom = UserSettingsService.Current.CustomYtDlpPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(baseDir, "Tools", "yt-dlp.exe");
        if (File.Exists(local)) return local;

        var winGet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "yt-dlp.exe");
        if (File.Exists(winGet)) return winGet;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir.Trim(), "yt-dlp.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindFfmpegExe()
    {
        var custom = UserSettingsService.Current.CustomFfmpegPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(baseDir, "Tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static DownloadTask? StartDownload(
        IDownloadQueueManager queueManager,
        string url,
        string destinationPath,
        string? title,
        string? quality,
        Action<string>? onStatus = null,
        string? description = null,
        string? category = null,
        bool startImmediately = true)
    {
        var ytDlpExe = FindYtDlpExe();
        if (ytDlpExe == null)
        {
            MessageBox.Show("yt-dlp.exe was not found on PATH or in Tools\\.", "yt-dlp Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var q = (quality ?? "best").Trim().ToLowerInvariant();
        var isAudio = q.Contains("mp3") || q.Contains("audio");
        var ext = isAudio ? ".mp3" : ".mp4";

        var rawName = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(destinationPath)
            : title;
        var sanitizedTitle = SanitizeFileName(rawName);
        if (string.IsNullOrWhiteSpace(sanitizedTitle))
            sanitizedTitle = "video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var folder = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(folder))
            folder = UserSettingsService.GetPathForCategory(category ?? (isAudio ? "Music" : "Video"));

        Directory.CreateDirectory(folder);
        var finalPath = Path.Combine(folder, sanitizedTitle + ext);

        var task = new DownloadTask
        {
            Url = url,
            DestinationPath = finalPath,
            FileName = Path.GetFileName(finalPath),
            Status = startImmediately ? DownloadStatus.Downloading : DownloadStatus.Paused,
            IsExternalManaged = true,
            Quality = quality ?? "Best",
            Category = category ?? (isAudio ? "Music" : "Video"),
            Description = string.IsNullOrWhiteSpace(description) ? $"YouTube ({quality ?? "Best"})" : description
        };

        queueManager.AddTask(task);

        if (startImmediately)
        {
            LaunchYtDlpProcess(ytDlpExe, queueManager, task, onStatus);
        }
        else
        {
            onStatus?.Invoke($"Saved for later: {task.FileName}");
        }

        return task;
    }

    public static bool PauseDownload(Guid taskId)
    {
        if (!ActiveProcesses.TryRemove(taskId, out var entry))
            return false;

        try
        {
            entry.Cts.Cancel();
            if (!entry.Process.HasExited)
            {
                entry.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore process termination races
        }
        finally
        {
            entry.Cts.Dispose();
        }

        return true;
    }

    public static void ResumeDownload(
        IDownloadQueueManager queueManager,
        DownloadTask task,
        Action<string>? onStatus = null)
    {
        if (ActiveProcesses.ContainsKey(task.Id))
            return;

        var ytDlpExe = FindYtDlpExe();
        if (ytDlpExe == null)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = "yt-dlp.exe was not found on PATH or in Tools\\.";
            onStatus?.Invoke(task.ErrorMessage);
            return;
        }

        task.ErrorMessage = null;
        task.Status = DownloadStatus.Downloading;
        task.SpeedBytesPerSec = 0;
        task.EstimatedTimeRemaining = null;

        LaunchYtDlpProcess(ytDlpExe, queueManager, task, onStatus);
    }

    private static void LaunchYtDlpProcess(
        string ytDlpExe,
        IDownloadQueueManager queueManager,
        DownloadTask task,
        Action<string>? onStatus)
    {
        var folder = Path.GetDirectoryName(task.DestinationPath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);

        var baseTitle = Path.GetFileNameWithoutExtension(task.DestinationPath);
        var q = (task.Quality ?? "best").Trim().ToLowerInvariant();
        var cts = new CancellationTokenSource();

        onStatus?.Invoke($"Starting yt-dlp download: {task.FileName}");

        _ = Task.Run(async () =>
        {
            Process? proc = null;
            try
            {
                var formatArgs = q switch
                {
                    "mp3" or "audio" => "-x --audio-format mp3 --audio-quality 0",
                    "1080p" or "1080" => "-f \"bv*[height<=1080]+ba/b[height<=1080]/b\" --merge-output-format mp4",
                    "720p" or "720" => "-f \"bv*[height<=720]+ba/b[height<=720]/b\" --merge-output-format mp4",
                    "480p" or "480" => "-f \"bv*[height<=480]+ba/b[height<=480]/b\" --merge-output-format mp4",
                    _ => "-f \"bv*+ba/b\" --merge-output-format mp4"
                };

                var outputTemplate = Path.Combine(folder, baseTitle + ".%(ext)s");
                var args = $"{formatArgs} --continue --no-playlist --newline --progress-template \"download:NOVA|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s\" -o \"{outputTemplate}\" \"{task.Url}\"";

                var psi = new ProcessStartInfo(ytDlpExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                proc = new Process { StartInfo = psi };
                var errorLines = new List<string>();
                long accumulatedDlBytes = 0;
                long currentStreamLastDl = 0;
                long currentStreamTotal = 0;
                long highestTotalBytes = 0;
                double smoothedSpeed = 0;
                double smoothedEtaSec = -1;
                long lastUiUpdateTick = 0;
                var uiThrottleTicks = (long)(Stopwatch.Frequency * 0.45); // 450ms calm readout cadence

                proc.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    if (!e.Data.StartsWith("NOVA|", StringComparison.Ordinal)) return;

                    var parts = e.Data.Split('|');
                    if (parts.Length < 6) return;

                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var dlBytesRaw);
                    double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var totalBytesRaw);
                    double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var estBytesRaw);
                    double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var rawSpeed);
                    double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var rawEtaSec);

                    var dlBytes = (long)Math.Max(0, dlBytesRaw);
                    var streamTotal = totalBytesRaw > 0 ? (long)totalBytesRaw : (long)Math.Max(0, estBytesRaw);

                    // Detect when yt-dlp finishes one stream (e.g. video) and starts the next (e.g. audio)
                    if (currentStreamLastDl > 65536 && dlBytes < currentStreamLastDl / 2)
                    {
                        accumulatedDlBytes += Math.Max(currentStreamLastDl, currentStreamTotal);
                        currentStreamLastDl = 0;
                        currentStreamTotal = 0;
                    }

                    currentStreamLastDl = Math.Max(currentStreamLastDl, dlBytes);
                    if (streamTotal > 0)
                    {
                        currentStreamTotal = Math.Max(currentStreamTotal, streamTotal);
                    }

                    var cumulativeDl = accumulatedDlBytes + currentStreamLastDl;
                    var cumulativeTotal = accumulatedDlBytes + Math.Max(currentStreamTotal, currentStreamLastDl);
                    if (cumulativeTotal > highestTotalBytes)
                        highestTotalBytes = cumulativeTotal;

                    // EMA speed smoothing (alpha = 0.20) to eliminate wild instantaneous spikes
                    if (rawSpeed > 0)
                    {
                        smoothedSpeed = smoothedSpeed <= 0
                            ? rawSpeed
                            : (0.20 * rawSpeed) + (0.80 * smoothedSpeed);
                    }

                    // Derive stabilized ETA from remaining bytes / smoothed speed
                    double candidateEta = -1;
                    if (smoothedSpeed > 100 && highestTotalBytes > cumulativeDl)
                    {
                        candidateEta = (highestTotalBytes - cumulativeDl) / smoothedSpeed;
                    }
                    else if (rawEtaSec > 0)
                    {
                        candidateEta = rawEtaSec;
                    }

                    if (candidateEta >= 0)
                    {
                        smoothedEtaSec = smoothedEtaSec < 0
                            ? candidateEta
                            : (0.25 * candidateEta) + (0.75 * smoothedEtaSec);
                    }

                    // Throttle UI numeric text updates to ~450ms so numbers stay calm & readable
                    var nowTick = Stopwatch.GetTimestamp();
                    if (nowTick - lastUiUpdateTick < uiThrottleTicks && cumulativeDl < highestTotalBytes)
                        return;
                    lastUiUpdateTick = nowTick;

                    var snapTotal = highestTotalBytes;
                    var snapDl = cumulativeDl;
                    var snapSpeed = smoothedSpeed;
                    var snapEta = smoothedEtaSec;

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (cts.IsCancellationRequested || task.Status == DownloadStatus.Paused) return;
                        if (snapTotal > task.TotalBytes)
                            task.TotalBytes = snapTotal;
                        if (snapDl > task.DownloadedBytes)
                            task.DownloadedBytes = snapDl;
                        if (task.TotalBytes < task.DownloadedBytes)
                            task.TotalBytes = task.DownloadedBytes;
                        task.SpeedBytesPerSec = snapSpeed > 0 ? snapSpeed : 0;
                        task.EstimatedTimeRemaining = snapEta >= 0 ? TimeSpan.FromSeconds(Math.Round(snapEta)) : null;
                    });
                };

                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        lock (errorLines)
                        {
                            errorLines.Add(e.Data.Trim());
                        }
                    }
                };

                proc.Start();
                ActiveProcesses[task.Id] = (proc, cts);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(cts.Token);

                ActiveProcesses.TryRemove(task.Id, out _);

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested || task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Cancelled)
                    {
                        task.SpeedBytesPerSec = 0;
                        task.EstimatedTimeRemaining = null;
                        _ = queueManager.SaveStateAsync();
                        return;
                    }

                    if (proc.ExitCode == 0)
                    {
                        if (File.Exists(task.DestinationPath))
                        {
                            var len = new FileInfo(task.DestinationPath).Length;
                            task.TotalBytes = len;
                            task.DownloadedBytes = len;
                        }
                        else if (task.TotalBytes > 0)
                        {
                            task.DownloadedBytes = task.TotalBytes;
                        }
                        task.SpeedBytesPerSec = 0;
                        task.EstimatedTimeRemaining = TimeSpan.Zero;
                        task.CompletedAt = DateTimeOffset.UtcNow;
                        task.Status = DownloadStatus.Completed;
                        onStatus?.Invoke($"Completed: {task.FileName}");
                    }
                    else
                    {
                        string? bestError;
                        lock (errorLines)
                        {
                            bestError = errorLines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                                ?? errorLines.LastOrDefault();
                        }

                        task.SpeedBytesPerSec = 0;
                        task.EstimatedTimeRemaining = null;
                        task.Status = DownloadStatus.Failed;
                        task.ErrorMessage = bestError ?? $"yt-dlp exited with code {proc.ExitCode}";
                        onStatus?.Invoke($"Failed: {task.FileName} — {task.ErrorMessage}");
                    }

                    _ = queueManager.SaveStateAsync();
                });
            }
            catch (OperationCanceledException)
            {
                ActiveProcesses.TryRemove(task.Id, out _);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    task.SpeedBytesPerSec = 0;
                    task.EstimatedTimeRemaining = null;
                    _ = queueManager.SaveStateAsync();
                });
            }
            catch (Exception ex)
            {
                ActiveProcesses.TryRemove(task.Id, out _);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested || task.Status == DownloadStatus.Paused)
                    {
                        task.SpeedBytesPerSec = 0;
                        return;
                    }

                    task.SpeedBytesPerSec = 0;
                    task.Status = DownloadStatus.Failed;
                    task.ErrorMessage = ex.Message;
                    onStatus?.Invoke($"Error: {ex.Message}");
                    _ = queueManager.SaveStateAsync();
                });
            }
            finally
            {
                proc?.Dispose();
                cts.Dispose();
            }
        });
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
