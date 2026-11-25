namespace NovaDownloader.Core.Models;

public record ServerProbeResult(
    bool Success,
    long? ContentLength,
    bool SupportsRange,
    string? SuggestedFileName,
    string? ContentType,
    string? ErrorMessage = null
);
