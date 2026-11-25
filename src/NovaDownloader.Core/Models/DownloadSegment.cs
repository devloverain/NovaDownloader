using System.Text.Json.Serialization;

namespace NovaDownloader.Core.Models;

public class DownloadSegment
{
    public int Index { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public long CurrentOffset { get; set; }
    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;
    public string? TempFilePath { get; set; }

    [JsonIgnore]
    public long DownloadedBytes => Math.Max(0, CurrentOffset - StartOffset);

    [JsonIgnore]
    public long TotalBytes => EndOffset >= StartOffset ? (EndOffset - StartOffset + 1) : 0;

    [JsonIgnore]
    public double ProgressPercentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100.0 : 0.0;

    [JsonIgnore]
    public bool IsComplete => EndOffset >= StartOffset && CurrentOffset > EndOffset;

    public void Reset()
    {
        CurrentOffset = StartOffset;
        Status = SegmentStatus.Pending;
    }
}
