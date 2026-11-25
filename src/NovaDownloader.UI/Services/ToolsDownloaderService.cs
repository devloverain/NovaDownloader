using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace NovaDownloader.UI.Services;

/// <summary>
/// Downloads and installs yt-dlp.exe and ffmpeg.exe into %LocalAppData%\NovaDownloader\Tools\
/// so YouTube, social media, and HLS downloads work out of the box without manual setup.
/// </summary>
public static class ToolsDownloaderService
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string ToolsDirectory => Path.Combine(UserSettingsService.AppDataDirectory, "Tools");

    public static async Task EnsureAllToolsAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        if (YtDlpService.FindYtDlpExe() == null)
        {
            await DownloadYtDlpAsync(onProgress, ct);
        }

        if (YtDlpService.FindFfmpegExe() == null)
        {
            await DownloadFfmpegAsync(onProgress, ct);
        }
    }

    public static async Task<string> DownloadYtDlpAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var targetPath = Path.Combine(ToolsDirectory, "yt-dlp.exe");
        var tempPath = targetPath + ".tmp";

        onProgress?.Invoke("Downloading yt-dlp.exe...");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaDownloader/1.2");

        using var resp = await client.GetAsync(YtDlpUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await resp.Content.CopyToAsync(fs, ct);
        }

        File.Move(tempPath, targetPath, overwrite: true);
        onProgress?.Invoke("yt-dlp.exe installed.");
        return targetPath;
    }

    public static async Task<string> DownloadFfmpegAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var targetPath = Path.Combine(ToolsDirectory, "ffmpeg.exe");
        var tempZip = Path.Combine(ToolsDirectory, $"ffmpeg_{Guid.NewGuid():N}.zip");

        onProgress?.Invoke("Downloading ffmpeg.exe (this may take a moment)...");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NovaDownloader/1.2");

            using var resp = await client.GetAsync(FfmpegZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            onProgress?.Invoke("Extracting ffmpeg.exe...");

            using var archive = ZipFile.OpenRead(tempZip);
            var ffmpegEntry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("ffmpeg.exe not found inside downloaded archive.");

            var tempExe = targetPath + ".tmp";
            ffmpegEntry.ExtractToFile(tempExe, overwrite: true);
            File.Move(tempExe, targetPath, overwrite: true);

            onProgress?.Invoke("ffmpeg.exe installed.");
            return targetPath;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
