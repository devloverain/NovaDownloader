using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NovaDownloader.Core.Models;

namespace NovaDownloader.UI.Converters;

public class StatusDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 1 || values[0] is not DownloadStatus status)
            return string.Empty;

        var progress = values.Length > 1 && values[1] is double d ? d : 0.0;

        return status switch
        {
            DownloadStatus.Completed => "Complete",
            DownloadStatus.Downloading => $"{progress:F1}%",
            DownloadStatus.Paused => $"Paused ({progress:F1}%)",
            DownloadStatus.Pending => "Queued",
            DownloadStatus.Failed => "Error",
            DownloadStatus.Cancelled => "Cancelled",
            _ => status.ToString()
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class StatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CompleteBrush = CreateFrozen("#4ADE80");
    private static readonly SolidColorBrush PausedBrush = CreateFrozen("#FBBF24");
    private static readonly SolidColorBrush QueuedBrush = CreateFrozen("#94A3B8");
    private static readonly SolidColorBrush ErrorBrush = CreateFrozen("#F87171");
    private static readonly SolidColorBrush ActiveBrush = CreateFrozen("#38BDF8");

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DownloadStatus status)
            return QueuedBrush;

        return status switch
        {
            DownloadStatus.Completed => CompleteBrush,
            DownloadStatus.Paused => PausedBrush,
            DownloadStatus.Pending => QueuedBrush,
            DownloadStatus.Failed or DownloadStatus.Cancelled => ErrorBrush,
            DownloadStatus.Downloading => ActiveBrush,
            _ => QueuedBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
