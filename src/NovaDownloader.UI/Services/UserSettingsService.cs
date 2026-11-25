using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDownloader.Core.Configuration;

namespace NovaDownloader.UI.Services;

public enum DuplicateFileBehavior
{
    AutoRename,
    Overwrite,
    Ask
}

public class UserSettings
{
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultSegments { get; set; } = 8;
    public bool RememberCategoryPaths { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = false;
    public bool StartMinimizedOnStartup { get; set; } = true;
    public bool ShowCompletedDialog { get; set; } = true;
    public bool PlaySoundOnComplete { get; set; } = true;
    public bool AutoExtractArchives { get; set; } = false;
    public DuplicateFileBehavior DuplicateBehavior { get; set; } = DuplicateFileBehavior.AutoRename;

    public bool SpeedLimitEnabled { get; set; } = false;
    public int SpeedLimitKBps { get; set; } = 2048;

    public bool ProxyEnabled { get; set; } = false;
    public string ProxyAddress { get; set; } = string.Empty;
    public string ProxyUsername { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;
    public string CustomUserAgent { get; set; } = "NovaDownloader/1.0 (+https://github.com/NovaDownloader)";

    public string CustomYtDlpPath { get; set; } = string.Empty;
    public string CustomFfmpegPath { get; set; } = string.Empty;

    public string DefaultDownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    public Dictionary<string, string> CategoryPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CategoryExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class UserSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaDownloader");

    public static string AppDataDirectory => SettingsDir;

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static UserSettings? _cached;

    public static IReadOnlyList<string> Categories { get; } =
    [
        "Video",
        "Music",
        "Compressed",
        "Documents",
        "Programs",
        "General"
    ];

    public static UserSettings Current => _cached ??= Load();

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.CategoryPaths = new Dictionary<string, string>(
                        loaded.CategoryPaths ?? [],
                        StringComparer.OrdinalIgnoreCase);
                    loaded.CategoryExtensions = new Dictionary<string, string>(
                        loaded.CategoryExtensions ?? [],
                        StringComparer.OrdinalIgnoreCase);
                    EnsureDefaults(loaded);
                    _cached = loaded;
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to defaults if corrupted
        }

        var defaults = new UserSettings();
        EnsureDefaults(defaults);
        _cached = defaults;
        return defaults;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore persistence errors
        }
    }

    public static void ApplyToDownloaderOptions(DownloaderOptions options)
    {
        var s = Current;
        options.MaxConcurrentTasks = Math.Clamp(s.MaxConcurrentDownloads, 1, 16);
        options.DefaultSegments = Math.Clamp(s.DefaultSegments, 1, 16);
        options.MaxDownloadSpeedBytesPerSec = s.SpeedLimitEnabled && s.SpeedLimitKBps > 0
            ? s.SpeedLimitKBps * 1024L
            : 0;
        options.ProxyEnabled = s.ProxyEnabled;
        options.ProxyAddress = s.ProxyAddress ?? string.Empty;
        options.ProxyUsername = s.ProxyUsername;
        options.ProxyPassword = s.ProxyPassword;
        if (!string.IsNullOrWhiteSpace(s.CustomUserAgent))
            options.UserAgent = s.CustomUserAgent.Trim();
    }

    public static string ResolveDuplicatePath(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !File.Exists(destinationPath))
            return destinationPath;

        if (Current.DuplicateBehavior == DuplicateFileBehavior.Overwrite)
            return destinationPath;

        var dir = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destinationPath);
        var ext = Path.GetExtension(destinationPath);

        int index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({index++}){ext}");
        }
        while (File.Exists(candidate));

        return candidate;
    }

    public static string GetPathForCategory(string category)
    {
        if (Current.CategoryPaths.TryGetValue(category, out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return GetDefaultPathForCategory(category);
    }

    public static void SetPathForCategory(string category, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(folderPath))
            return;

        Current.CategoryPaths[category] = folderPath.Trim();
        Save();
    }

    public static string GetExtensionsForCategory(string category)
    {
        if (Current.CategoryExtensions.TryGetValue(category, out var exts) && !string.IsNullOrWhiteSpace(exts))
            return exts;

        return GetDefaultExtensionsForCategory(category);
    }

    public static string DetectCategory(string? fileName, string? url, string? mimeType = null)
    {
        if (!string.IsNullOrWhiteSpace(url) && YtDlpService.IsSupportedVideoPage(url))
        {
            if (!string.IsNullOrWhiteSpace(fileName) &&
                fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                return "Music";
            return "Video";
        }

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ext = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(ext))
        {
            foreach (var cat in Categories)
            {
                if (cat == "General") continue;
                var list = GetExtensionsForCategory(cat)
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant());

                if (list.Contains(ext))
                    return cat;
            }
        }

        if (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return "Video";
        if (mimeType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true) return "Video";
        if (mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return "Music";

        return "General";
    }

    private static void EnsureDefaults(UserSettings settings)
    {
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16);
        settings.DefaultSegments = Math.Clamp(settings.DefaultSegments, 1, 16);

        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder))
        {
            settings.DefaultDownloadFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
        }

        foreach (var cat in Categories)
        {
            if (!settings.CategoryPaths.ContainsKey(cat) || string.IsNullOrWhiteSpace(settings.CategoryPaths[cat]))
            {
                settings.CategoryPaths[cat] = GetDefaultPathForCategory(cat, settings.DefaultDownloadFolder);
            }

            if (!settings.CategoryExtensions.ContainsKey(cat) || string.IsNullOrWhiteSpace(settings.CategoryExtensions[cat]))
            {
                settings.CategoryExtensions[cat] = GetDefaultExtensionsForCategory(cat);
            }
        }
    }

    public static string GetDefaultExtensionsForCategory(string category) => category switch
    {
        "Video" => ".mp4, .mkv, .webm, .ts, .m3u8, .avi, .mov, .flv, .wmv",
        "Music" => ".mp3, .aac, .flac, .m4a, .wav, .ogg, .opus",
        "Compressed" => ".zip, .rar, .7z, .tar, .gz, .iso",
        "Documents" => ".pdf, .docx, .doc, .xlsx, .pptx, .txt, .csv",
        "Programs" => ".exe, .msi, .bat, .cmd, .msix, .apk",
        _ => string.Empty
    };

    public static string GetDefaultPathForCategory(string category, string? rootFolder = null)
    {
        var baseDownloads = !string.IsNullOrWhiteSpace(rootFolder)
            ? rootFolder
            : (Current?.DefaultDownloadFolder is { Length: > 0 } root
                ? root
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        return category switch
        {
            "Video" => Path.Combine(baseDownloads, "Video"),
            "Music" => Path.Combine(baseDownloads, "Music"),
            "Compressed" => Path.Combine(baseDownloads, "Compressed"),
            "Documents" => Path.Combine(baseDownloads, "Documents"),
            "Programs" => Path.Combine(baseDownloads, "Programs"),
            _ => baseDownloads
        };
    }

    public static void ResetCategoryPathsToRoot(string rootFolder)
    {
        Current.DefaultDownloadFolder = rootFolder;
        foreach (var cat in Categories)
        {
            Current.CategoryPaths[cat] = GetDefaultPathForCategory(cat, rootFolder);
        }
        Save();
    }
}
