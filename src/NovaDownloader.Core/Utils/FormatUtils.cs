namespace NovaDownloader.Core.Utils;

public static class FormatUtils
{
    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes == 0) return "0 B";

        int mag = (int)Math.Log(bytes, 1024);
        if (mag >= SizeSuffixes.Length)
            mag = SizeSuffixes.Length - 1;

        decimal adjustedSize = (decimal)bytes / (1L << (mag * 10));
        return mag switch
        {
            0 => $"{adjustedSize:0} {SizeSuffixes[mag]}",
            1 => $"{adjustedSize:0.0} {SizeSuffixes[mag]}",
            _ => $"{adjustedSize:0.00} {SizeSuffixes[mag]}"
        };
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        return $"{FormatBytes((long)bytesPerSec)}/s";
    }

    public static string FormatTimeSpan(TimeSpan? timeSpan)
    {
        if (!timeSpan.HasValue || timeSpan.Value < TimeSpan.Zero || timeSpan.Value == TimeSpan.MaxValue)
            return "--:--:--";

        var ts = timeSpan.Value;
        if (ts.TotalHours >= 24)
            return $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
