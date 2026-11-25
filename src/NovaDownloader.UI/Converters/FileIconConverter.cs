using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace NovaDownloader.UI.Converters;

public class FileIconConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fileName = value as string ?? string.Empty;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        var iconName = ext switch
        {
            ".iso" or ".img" or ".vhd" => "file_iso",
            ".mp4" or ".mkv" or ".webm" or ".ts" or ".m3u8" or ".avi" or ".mov" => "file_video",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "file_archive",
            ".exe" or ".msi" or ".bat" or ".cmd" => "file_exe",
            ".pdf" or ".docx" or ".doc" or ".xlsx" or ".pptx" or ".txt" => "file_pdf",
            ".mp3" or ".aac" or ".flac" or ".m4a" or ".wav" or ".ogg" => "file_audio",
            _ => "file_pdf"
        };

        if (!Cache.TryGetValue(iconName, out var image))
        {
            image = new BitmapImage(new Uri($"pack://application:,,,/Assets/Icons/{iconName}.png", UriKind.Absolute));
            image.Freeze();
            Cache[iconName] = image;
        }

        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
