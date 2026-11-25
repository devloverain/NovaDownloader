using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;
using NovaDownloader.UI.Models;
using NovaDownloader.UI.Services;

namespace NovaDownloader.UI.Dialogs;

public partial class SchedulerDialog : Window
{
    private readonly QueueSchedulerService _schedulerService;
    private readonly IDownloadQueueManager _queueManager;
    private readonly IReadOnlyList<DownloadTask> _highlightedTasks;
    private ScheduleQueue? _selectedQueue;
    private bool _isLoadingUi;
    private bool _isRenaming;

    public SchedulerDialog(
        QueueSchedulerService schedulerService,
        IDownloadQueueManager queueManager,
        string? initialQueueId = null,
        IReadOnlyList<DownloadTask>? highlightedTasks = null)
    {
        InitializeComponent();
        _schedulerService = schedulerService;
        _queueManager = queueManager;
        _highlightedTasks = highlightedTasks?.Where(t => t.Status != DownloadStatus.Completed).ToList() ?? [];

        ConcurrencyComboBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        RefreshQueueList(initialQueueId ?? ScheduleQueue.MainQueueId);
    }

    private void RefreshQueueList(string? selectQueueId = null)
    {
        var items = _schedulerService.Queues.ToList();
        QueuesListBox.ItemsSource = items;

        var target = !string.IsNullOrWhiteSpace(selectQueueId)
            ? items.FirstOrDefault(q => string.Equals(q.Id, selectQueueId, StringComparison.OrdinalIgnoreCase))
            : null;
        QueuesListBox.SelectedItem = target ?? items.FirstOrDefault();
    }

    private void QueuesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedQueue != null && !_isLoadingUi)
        {
            ApplyUiToQueue(_selectedQueue);
        }

        _selectedQueue = QueuesListBox.SelectedItem as ScheduleQueue;
        LoadQueueIntoUi(_selectedQueue);
    }

    private void LoadQueueIntoUi(ScheduleQueue? queue)
    {
        if (queue == null) return;
        _isLoadingUi = true;

        RenameQueueBtn.IsEnabled = !queue.IsDefault;
        DeleteQueueBtn.IsEnabled = !queue.IsDefault;

        EnableScheduleCheckBox.IsChecked = queue.IsScheduleEnabled;
        ConcurrencyComboBox.SelectedItem = Math.Clamp(queue.MaxConcurrentDownloads, 1, 10);

        FreqDailyRadio.IsChecked = queue.Frequency == ScheduleFrequency.Daily;
        FreqOneTimeRadio.IsChecked = queue.Frequency == ScheduleFrequency.OneTime;
        FreqDaysRadio.IsChecked = queue.Frequency == ScheduleFrequency.SelectedDays;

        StartTimeBox.Text = queue.StartTime.ToString(@"hh\:mm");
        HasStopTimeCheckBox.IsChecked = queue.HasStopTime;
        StopTimeBox.Text = queue.StopTime.ToString(@"hh\:mm");
        OneTimeDateBox.Text = (queue.OneTimeDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        DayMon.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Monday);
        DayTue.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Tuesday);
        DayWed.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Wednesday);
        DayThu.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Thursday);
        DayFri.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Friday);
        DaySat.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Saturday);
        DaySun.IsChecked = queue.DaysOfWeek.Contains(DayOfWeek.Sunday);

        UpdateScheduleControlsVisibility();
        RefreshQueueFiles(queue);

        _isLoadingUi = false;
    }

    private void RefreshQueueFiles(ScheduleQueue queue)
    {
        var files = _queueManager.Tasks
            .Where(t => string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)
                     && t.Status != DownloadStatus.Completed)
            .ToList();

        QueueFilesListBox.ItemsSource = files;
        FilesHeaderText.Text = $"Pending in \"{queue.Name}\" ({files.Count})";

        var movableHighlighted = _highlightedTasks
            .Where(t => !string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (movableHighlighted.Count > 0)
        {
            AddHighlightedBtn.Content = $"+ Add Highlighted ({movableHighlighted.Count})";
            AddHighlightedBtn.Visibility = Visibility.Visible;
        }
        else
        {
            AddHighlightedBtn.Visibility = Visibility.Collapsed;
        }
    }

    private bool ApplyUiToQueue(ScheduleQueue queue)
    {
        if (!TimeSpan.TryParseExact(StartTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var start))
        {
            if (!TimeSpan.TryParse(StartTimeBox.Text.Trim(), CultureInfo.InvariantCulture, out start))
            {
                MessageBox.Show("Please enter a valid Start Time in HH:mm format (e.g., 02:30).", "Invalid Time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        var stop = queue.StopTime;
        if (HasStopTimeCheckBox.IsChecked == true)
        {
            if (!TimeSpan.TryParseExact(StopTimeBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out stop)
                && !TimeSpan.TryParse(StopTimeBox.Text.Trim(), CultureInfo.InvariantCulture, out stop))
            {
                MessageBox.Show("Please enter a valid Stop Time in HH:mm format (e.g., 07:00).", "Invalid Time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        DateTime? oneTimeDate = queue.OneTimeDate ?? DateTime.Today;
        if (FreqOneTimeRadio.IsChecked == true)
        {
            if (DateTime.TryParseExact(OneTimeDateBox.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                oneTimeDate = parsedDate;
            }
            else
            {
                MessageBox.Show("Please enter a valid date in YYYY-MM-DD format.", "Invalid Date", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        queue.IsScheduleEnabled = EnableScheduleCheckBox.IsChecked == true;
        queue.MaxConcurrentDownloads = ConcurrencyComboBox.SelectedItem is int c ? c : 2;
        queue.Frequency = FreqOneTimeRadio.IsChecked == true
            ? ScheduleFrequency.OneTime
            : (FreqDaysRadio.IsChecked == true ? ScheduleFrequency.SelectedDays : ScheduleFrequency.Daily);

        queue.StartTime = new TimeSpan(start.Hours, start.Minutes, 0);
        queue.HasStopTime = HasStopTimeCheckBox.IsChecked == true;
        queue.StopTime = new TimeSpan(stop.Hours, stop.Minutes, 0);
        queue.OneTimeDate = oneTimeDate;

        var days = new List<DayOfWeek>();
        if (DayMon.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTue.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWed.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThu.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFri.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySat.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySun.IsChecked == true) days.Add(DayOfWeek.Sunday);
        queue.DaysOfWeek = days;

        _schedulerService.Save();
        return true;
    }

    private void ScheduleOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        UpdateScheduleControlsVisibility();
    }

    private void FrequencyChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        UpdateScheduleControlsVisibility();
    }

    private void UpdateScheduleControlsVisibility()
    {
        var enabled = EnableScheduleCheckBox.IsChecked == true;
        ScheduleTimeGrid.IsEnabled = enabled;
        StopTimeBox.IsEnabled = enabled && HasStopTimeCheckBox.IsChecked == true;
        OneTimeDateRow.Visibility = FreqOneTimeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DaysOfWeekRow.Visibility = FreqDaysRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetToday_Click(object sender, RoutedEventArgs e)
        => OneTimeDateBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void SetTomorrow_Click(object sender, RoutedEventArgs e)
        => OneTimeDateBox.Text = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        _isRenaming = false;
        QueueNameInputBox.Text = "Games";
        QueueNameEditPanel.Visibility = Visibility.Visible;
        QueueNameInputBox.Focus();
        QueueNameInputBox.SelectAll();
    }

    private void RenameQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null || _selectedQueue.IsDefault) return;
        _isRenaming = true;
        QueueNameInputBox.Text = _selectedQueue.Name;
        QueueNameEditPanel.Visibility = Visibility.Visible;
        QueueNameInputBox.Focus();
        QueueNameInputBox.SelectAll();
    }

    private void QueueNameInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmQueueName_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelQueueName_Click(sender, e);
        }
    }

    private void ConfirmQueueName_Click(object sender, RoutedEventArgs e)
    {
        var name = QueueNameInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_isRenaming && _selectedQueue != null && !_selectedQueue.IsDefault)
        {
            _selectedQueue.Name = name;
            _schedulerService.Save();
            RefreshQueueList(_selectedQueue.Id);
        }
        else
        {
            var created = _schedulerService.CreateQueue(name, maxConcurrent: 1);
            RefreshQueueList(created.Id);
        }

        QueueNameEditPanel.Visibility = Visibility.Collapsed;
    }

    private void CancelQueueName_Click(object sender, RoutedEventArgs e)
    {
        QueueNameEditPanel.Visibility = Visibility.Collapsed;
    }

    private void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null || _selectedQueue.IsDefault) return;
        var res = MessageBox.Show(
            $"Delete schedule \"{_selectedQueue.Name}\"? Any files in it will return to Main Queue.",
            "Delete Schedule",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            _schedulerService.DeleteQueue(_selectedQueue.Id);
            RefreshQueueList(ScheduleQueue.MainQueueId);
        }
    }

    private void AddHighlightedToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null) return;
        var movable = _highlightedTasks
            .Where(t => !string.Equals(t.QueueId, _selectedQueue.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (movable.Count == 0) return;

        _schedulerService.AssignTasksToQueue(movable, _selectedQueue.Id);
        RefreshQueueFiles(_selectedQueue);
    }

    private void AddUnfinishedToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null) return;

        var candidates = _queueManager.Tasks
            .Where(t => !string.Equals(t.QueueId, _selectedQueue.Id, StringComparison.OrdinalIgnoreCase)
                     && t.Status is DownloadStatus.Paused or DownloadStatus.Pending or DownloadStatus.Failed)
            .ToList();

        if (candidates.Count == 0)
        {
            MessageBox.Show("No paused or unfinished downloads in other queues to move here.\nYou can also Ctrl/Shift-select items in the main list and right-click → Move to Schedule.",
                "Add to Schedule", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _schedulerService.AssignTasksToQueue(candidates, _selectedQueue.Id);
        RefreshQueueFiles(_selectedQueue);
    }

    private void RemoveSelectedFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null) return;
        var selected = QueueFilesListBox.SelectedItems.Cast<DownloadTask>().ToList();
        if (selected.Count == 0) return;

        _schedulerService.AssignTasksToQueue(selected, ScheduleQueue.MainQueueId);
        RefreshQueueFiles(_selectedQueue);
    }

    private async void StartQueueNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null) return;
        if (!ApplyUiToQueue(_selectedQueue)) return;
        await _schedulerService.StartQueueAsync(_selectedQueue.Id);
        RefreshQueueFiles(_selectedQueue);
    }

    private async void StopQueueNow_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue == null) return;
        await _schedulerService.StopQueueAsync(_selectedQueue.Id);
        RefreshQueueFiles(_selectedQueue);
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedQueue != null && !ApplyUiToQueue(_selectedQueue))
            return;

        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
