using System.Text.Json.Serialization;
using NovaDownloader.UI.Common;

namespace NovaDownloader.UI.Models;

public enum ScheduleFrequency
{
    Daily,
    OneTime,
    SelectedDays
}

public class ScheduleQueue : ViewModelBase
{
    private string _id = Guid.NewGuid().ToString("N")[..8];
    private string _name = "New Schedule";
    private int _maxConcurrentDownloads = 1;
    private bool _isScheduleEnabled;
    private ScheduleFrequency _frequency = ScheduleFrequency.Daily;
    private TimeSpan _startTime = new(2, 0, 0);
    private DateTime? _oneTimeDate = DateTime.Today;
    private List<DayOfWeek> _daysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private bool _hasStopTime;
    private TimeSpan _stopTime = new(7, 0, 0);
    private bool _isRunning;

    public const string MainQueueId = "main";

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set => SetProperty(ref _maxConcurrentDownloads, Math.Clamp(value, 1, 10));
    }

    public bool IsScheduleEnabled
    {
        get => _isScheduleEnabled;
        set
        {
            if (SetProperty(ref _isScheduleEnabled, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public ScheduleFrequency Frequency
    {
        get => _frequency;
        set
        {
            if (SetProperty(ref _frequency, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public TimeSpan StartTime
    {
        get => _startTime;
        set
        {
            if (SetProperty(ref _startTime, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public DateTime? OneTimeDate
    {
        get => _oneTimeDate;
        set
        {
            if (SetProperty(ref _oneTimeDate, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public List<DayOfWeek> DaysOfWeek
    {
        get => _daysOfWeek;
        set
        {
            if (SetProperty(ref _daysOfWeek, value ?? []))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public bool HasStopTime
    {
        get => _hasStopTime;
        set
        {
            if (SetProperty(ref _hasStopTime, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public TimeSpan StopTime
    {
        get => _stopTime;
        set
        {
            if (SetProperty(ref _stopTime, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
                OnPropertyChanged(nameof(StatusLabel));
        }
    }

    [JsonIgnore]
    public bool IsDefault => string.Equals(Id, MainQueueId, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string StatusLabel => IsRunning
        ? "Running"
        : (IsScheduleEnabled ? $"Scheduled {StartTime:hh\\:mm}" : "Idle");

    [JsonIgnore]
    public string ScheduleSummary
    {
        get
        {
            if (!IsScheduleEnabled)
                return $"Manual • {MaxConcurrentDownloads} concurrent";

            var timeStr = StartTime.ToString(@"hh\:mm");
            var stopStr = HasStopTime ? $"–{StopTime:hh\\:mm}" : string.Empty;

            return Frequency switch
            {
                ScheduleFrequency.OneTime => $"Once ({(OneTimeDate ?? DateTime.Today):MMM dd}) at {timeStr}{stopStr} • {MaxConcurrentDownloads} concurrent",
                ScheduleFrequency.SelectedDays => $"{DaysOfWeek.Count} days at {timeStr}{stopStr} • {MaxConcurrentDownloads} concurrent",
                _ => $"Daily at {timeStr}{stopStr} • {MaxConcurrentDownloads} concurrent"
            };
        }
    }

    [JsonIgnore]
    internal string? LastStartTriggerKey { get; set; }

    [JsonIgnore]
    internal string? LastStopTriggerKey { get; set; }

    public override string ToString() => Name;
}
