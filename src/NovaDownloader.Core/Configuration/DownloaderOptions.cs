namespace NovaDownloader.Core.Configuration;

public class DownloaderOptions
{
    public int MaxConcurrentTasks { get; set; } = 3;
    public int MinSegments { get; set; } = 4;
    public int MaxSegments { get; set; } = 16;
    public int DefaultSegments { get; set; } = 8;
    public int BufferSize { get; set; } = 64 * 1024; // 64 KB
    public long MinChunkSizeBytes { get; set; } = 1024 * 1024; // 1 MB
    public int ProgressUpdateIntervalMs { get; set; } = 250; // Throttle events to 4x / sec
    public double SpeedRollingWindowSeconds { get; set; } = 3.0;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int MaxConnectionsPerServer { get; set; } = 64;
    public string UserAgent { get; set; } = "NovaDownloader/1.0 (+https://github.com/NovaDownloader)";
    public string StateFilePath { get; set; } = "downloads.json";
    public long MaxDownloadSpeedBytesPerSec { get; set; } = 0; // 0 = Unlimited
    public bool ProxyEnabled { get; set; } = false;
    public string ProxyAddress { get; set; } = string.Empty;
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
}
