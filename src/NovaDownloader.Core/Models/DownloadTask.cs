using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using NovaDownloader.Core.Utils;

namespace NovaDownloader.Core.Models;

public class DownloadTask : INotifyPropertyChanged
{
    private Guid _id = Guid.NewGuid();
    private string _url = string.Empty;
    private string _destinationPath = string.Empty;
    private string _fileName = string.Empty;
    private long _totalBytes;
    private long _downloadedBytes;
    private DownloadStatus _status = DownloadStatus.Pending;
    private List<DownloadSegment> _segments = [];
    private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _completedAt;
    private double _speedBytesPerSec;
    private TimeSpan? _estimatedTimeRemaining;
    private string? _errorMessage;
    private bool _supportsRange;
    private string? _description;
    private string? _quality;
    private string? _category;
    private string _queueId = "main";

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetField(ref _destinationPath, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetField(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(FormattedTotalBytes));
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetField(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(FormattedDownloadedBytes));
            }
        }
    }

    public DownloadStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public List<DownloadSegment> Segments
    {
        get => _segments;
        set => SetField(ref _segments, value);
    }

    public DateTimeOffset CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    public double SpeedBytesPerSec
    {
        get => _speedBytesPerSec;
        set
        {
            if (SetField(ref _speedBytesPerSec, value))
            {
                OnPropertyChanged(nameof(FormattedSpeed));
            }
        }
    }

    public TimeSpan? EstimatedTimeRemaining
    {
        get => _estimatedTimeRemaining;
        set
        {
            if (SetField(ref _estimatedTimeRemaining, value))
            {
                OnPropertyChanged(nameof(FormattedRemainingTime));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public bool SupportsRange
    {
        get => _supportsRange;
        set => SetField(ref _supportsRange, value);
    }

    public string? Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public string? Quality
    {
        get => _quality;
        set => SetField(ref _quality, value);
    }

    public string? Category
    {
        get => _category;
        set => SetField(ref _category, value);
    }

    public string QueueId
    {
        get => string.IsNullOrWhiteSpace(_queueId) ? "main" : _queueId;
        set => SetField(ref _queueId, string.IsNullOrWhiteSpace(value) ? "main" : value);
    }

    [JsonIgnore]
    public double ProgressPercentage => TotalBytes > 0 
        ? Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0.0, 100.0) 
        : 0.0;

    [JsonIgnore]
    public string FormattedTotalBytes => FormatUtils.FormatBytes(TotalBytes);

    [JsonIgnore]
    public string FormattedDownloadedBytes => FormatUtils.FormatBytes(DownloadedBytes);

    [JsonIgnore]
    public string FormattedSpeed => FormatUtils.FormatSpeed(SpeedBytesPerSec);

    [JsonIgnore]
    public string FormattedRemainingTime => FormatUtils.FormatTimeSpan(EstimatedTimeRemaining);

    [JsonIgnore]
    public string FormattedLastTry => CreatedAt.ToLocalTime().ToString("h:mm tt");

    public bool IsExternalManaged { get; set; }

    [JsonIgnore]
    public string IncompleteFilePath => DestinationPath + ".novapart";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void RecalculateDownloadedBytes()
    {
        if (Segments.Count > 0)
        {
            DownloadedBytes = Segments.Sum(s => s.DownloadedBytes);
        }
    }
}
