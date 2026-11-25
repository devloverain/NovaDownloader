using System.Text.RegularExpressions;

namespace NovaDownloader.Core.Utils;

public static partial class FileUtils
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return $"download_{Guid.NewGuid():N}";

        // Strip invalid characters
        var cleaned = string.Join("_", fileName.Split(InvalidChars, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return $"download_{Guid.NewGuid():N}";

        // Limit maximum filename length
        if (cleaned.Length > 200)
        {
            var ext = Path.GetExtension(cleaned);
            var baseName = Path.GetFileNameWithoutExtension(cleaned);
            cleaned = baseName[..Math.Min(190, baseName.Length)] + ext;
        }

        return cleaned;
    }

    public static string GetUniqueFilePath(string directory, string fileName)
    {
        var sanitized = SanitizeFileName(fileName);
        var fullPath = Path.Combine(directory, sanitized);

        if (!File.Exists(fullPath))
            return fullPath;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
        var ext = Path.GetExtension(sanitized);
        int counter = 1;

        while (true)
        {
            var candidate = Path.Combine(directory, $"{nameWithoutExt} ({counter}){ext}");
            if (!File.Exists(candidate))
                return candidate;
            counter++;
        }
    }
}
