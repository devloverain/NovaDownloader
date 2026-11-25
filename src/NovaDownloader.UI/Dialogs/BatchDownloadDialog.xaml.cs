using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NovaDownloader.Core.Engine;
using NovaDownloader.Core.Utils;
using NovaDownloader.UI.Common;
using NovaDownloader.UI.Models;
using NovaDownloader.UI.Services;

namespace NovaDownloader.UI.Dialogs;

public class BatchLinkItemViewModel : ViewModelBase
{
    private bool _isSelected = true;
    private string _fileName = string.Empty;
    private string _extension = "FILE";
    private long? _sizeBytes;
    private string _formattedSize = "Checking...";
    private string _destinationPath = string.Empty;

    public string Url { get; init; } = string.Empty;
    public string LinkText { get; init; } = string.Empty;
    public string Category { get; set; } = "General";
    public bool IsWebpage { get; init; }
    public bool IsImage { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetProperty(ref _fileName, value))
            {
                var ext = Path.GetExtension(value).TrimStart('.').ToUpperInvariant();
                Extension = string.IsNullOrWhiteSpace(ext) ? "FILE" : (ext.Length > 5 ? ext[..5] : ext);
            }
        }
    }

    public string Extension
    {
        get => _extension;
        set => SetProperty(ref _extension, value);
    }

    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                FormattedSize = value is > 0 ? FormatUtils.FormatBytes(value.Value) : "—";
            }
        }
    }

    public string FormattedSize
    {
        get => _formattedSize;
        set => SetProperty(ref _formattedSize, value);
    }

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetProperty(ref _destinationPath, value);
    }
}

public partial class BatchDownloadDialog : Window
{
    private static readonly HashSet<string> WebpageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".php", ".asp", ".aspx", ".jsp" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico" };

    private readonly QueueSchedulerService _schedulerService;
    private readonly NovaDownloader.Core.Queue.IDownloadQueueManager _queueManager;
    private readonly List<BatchLinkItemViewModel> _allItems = [];
    private readonly ObservableCollection<BatchLinkItemViewModel> _visibleItems = [];
    private readonly CancellationTokenSource _probeCts = new();
    private bool _isInitializing = true;

    public IReadOnlyList<BatchLinkItemViewModel> SelectedItems =>
        _visibleItems.Where(i => i.IsSelected).ToList();

    public string SelectedQueueId => (QueueComboBox.SelectedItem as ScheduleQueue)?.Id ?? ScheduleQueue.MainQueueId;
    public bool UseLinkTextAsDescription => UseLinkTextDescCheckBox.IsChecked == true;
    public bool DownloadLater { get; private set; }

    public BatchDownloadDialog(
        ExtensionBatchDownloadPayload payload,
        QueueSchedulerService schedulerService,
        NovaDownloader.Core.Queue.IDownloadQueueManager queueManager,
        string? preferredQueueId = null)
    {
        InitializeComponent();
        _schedulerService = schedulerService;
        _queueManager = queueManager;

        CustomFolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        PopulateQueues(preferredQueueId);
        BuildItems(payload);

        LinksGrid.ItemsSource = _visibleItems;
        _isInitializing = false;

        ApplyFilters();
        UpdateSelectionCounts();
        _ = ProbeSizesAsync();
    }

    private void PopulateQueues(string? selectQueueId = null)
    {
        var queues = _schedulerService.Queues.ToList();
        QueueComboBox.ItemsSource = queues;
        var match = !string.IsNullOrWhiteSpace(selectQueueId)
            ? queues.FirstOrDefault(q => string.Equals(q.Id, selectQueueId, StringComparison.OrdinalIgnoreCase))
            : null;
        QueueComboBox.SelectedItem = match ?? queues.FirstOrDefault();
    }

    private void BuildItems(ExtensionBatchDownloadPayload payload)
    {
        foreach (var raw in payload.Links)
        {
            if (string.IsNullOrWhiteSpace(raw.Url)) continue;

            var fileName = ResolveInitialFileName(raw.Url, raw.FileName);
            var ext = Path.GetExtension(fileName);
            var isWebpage = WebpageExtensions.Contains(ext) || string.IsNullOrEmpty(ext);
            var isImage = ImageExtensions.Contains(ext);
            var category = UserSettingsService.DetectCategory(fileName, raw.Url);

            var vm = new BatchLinkItemViewModel
            {
                Url = raw.Url,
                LinkText = string.IsNullOrWhiteSpace(raw.LinkText) ? fileName : raw.LinkText.Trim(),
                FileName = fileName,
                Category = category,
                IsWebpage = isWebpage,
                IsImage = isImage,
                IsSelected = !isWebpage && !isImage,
                DestinationPath = Path.Combine(UserSettingsService.GetPathForCategory(category), fileName)
            };

            _allItems.Add(vm);
        }
    }

    private static string ResolveInitialFileName(string url, string? hint)
    {
        var name = hint?.Trim();
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            name = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "page.html";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }

    private async Task ProbeSizesAsync()
    {
        using var engine = new MultiSegmentDownloader();
        using var sem = new SemaphoreSlim(4);
        var itemsToProbe = _allItems.Where(i => !i.IsWebpage && !i.IsImage).ToList();

        if (itemsToProbe.Count == 0)
        {
            Dispatcher.Invoke(() => ProbeStatusText.Text = "Ready");
            return;
        }

        int done = 0;
        var tasks = itemsToProbe.Select(async item =>
        {
            await sem.WaitAsync(_probeCts.Token);
            try
            {
                var probe = await engine.ProbeAsync(item.Url, _probeCts.Token);
                Dispatcher.Invoke(() =>
                {
                    if (probe.Success)
                    {
                        item.SizeBytes = probe.ContentLength;
                        if (!string.IsNullOrWhiteSpace(probe.SuggestedFileName))
                        {
                            item.FileName = FileUtils.SanitizeFileName(probe.SuggestedFileName);
                            item.Category = UserSettingsService.DetectCategory(item.FileName, item.Url, probe.ContentType);
                            UpdateItemDestination(item);
                        }
                    }
                    else
                    {
                        item.FormattedSize = "—";
                    }

                    done++;
                    ProbeStatusText.Text = done >= itemsToProbe.Count
                        ? "Ready"
                        : $"Checking sizes ({done}/{itemsToProbe.Count})...";
                });
            }
            catch
            {
                Dispatcher.Invoke(() => item.FormattedSize = "—");
            }
            finally
            {
                sem.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyFilters()
    {
        if (_isInitializing) return;

        var hideWeb = HideWebpagesCheckBox.IsChecked == true;
        var hideImg = HideImagesCheckBox.IsChecked == true;
        var hideDup = HideDuplicatesCheckBox.IsChecked == true;
        var search = FilterSearchBox.Text.Trim();

        _visibleItems.Clear();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _allItems)
        {
            if (hideWeb && item.IsWebpage) continue;
            if (hideImg && item.IsImage) continue;
            if (hideDup && !seenUrls.Add(item.Url)) continue;

            if (!string.IsNullOrWhiteSpace(search)
                && !item.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.LinkText.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !item.Url.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _visibleItems.Add(item);
        }

        UpdateSelectionCounts();
    }

    private void UpdateSelectionCounts()
    {
        var count = _visibleItems.Count(i => i.IsSelected);
        SelectionBadgeText.Text = $"{count} Selected";
        DownloadSelectedBtn.Content = $"Download Selected ({count})";
        DownloadSelectedBtn.IsEnabled = count > 0;
    }

    private void UpdateItemDestination(BatchLinkItemViewModel item)
    {
        var folder = SaveSingleFolderRadio.IsChecked == true
            ? CustomFolderBox.Text.Trim()
            : UserSettingsService.GetPathForCategory(item.Category);

        if (string.IsNullOrWhiteSpace(folder))
            folder = UserSettingsService.GetPathForCategory(item.Category);

        item.DestinationPath = Path.Combine(folder, item.FileName);
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilters();

    private void ItemCheckChanged(object sender, RoutedEventArgs e) => UpdateSelectionCounts();

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleItems) item.IsSelected = true;
        UpdateSelectionCounts();
    }

    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleItems) item.IsSelected = false;
        UpdateSelectionCounts();
    }

    private void SaveModeChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var single = SaveSingleFolderRadio.IsChecked == true;
        CustomFolderBox.IsEnabled = single;
        BrowseFolderBtn.IsEnabled = single;

        foreach (var item in _allItems)
        {
            UpdateItemDestination(item);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select destination folder for all selected downloads",
            InitialDirectory = Directory.Exists(CustomFolderBox.Text)
                ? CustomFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dlg.ShowDialog() == true)
        {
            CustomFolderBox.Text = dlg.FolderName;
            foreach (var item in _allItems)
            {
                UpdateItemDestination(item);
            }
        }
    }

    private void QuickCreateQueue_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SchedulerDialog(_schedulerService, _queueManager, SelectedQueueId)
        {
            Owner = this
        };
        dlg.ShowDialog();
        PopulateQueues(SelectedQueueId);
    }

    private void AddToSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems.Count == 0) return;
        DownloadLater = true;
        DialogResult = true;
        Close();
    }

    private void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItems.Count == 0) return;
        DownloadLater = false;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeCts.Cancel();
        _probeCts.Dispose();
        base.OnClosed(e);
    }
}
