using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NovaDownloader.ExtensionHost.Models;

public class ExtensionMessage
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("referer")]
    public string? Referer { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("fileSize")]
    public long? FileSize { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("pageTitle")]
    public string? PageTitle { get; set; }

    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; set; }

    [JsonPropertyName("downloadDirectory")]
    public string? DownloadDirectory { get; set; }

    [JsonPropertyName("mediaList")]
    public List<SniffedMediaItem>? MediaList { get; set; }

    [JsonPropertyName("media")]
    public SniffedMediaItem? Media { get; set; }

    [JsonPropertyName("links")]
    public List<BatchLinkItem>? Links { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    public string EffectiveAction => !string.IsNullOrWhiteSpace(Action) ? Action : (Type ?? string.Empty);
}

public class BatchLinkItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("linkText")]
    public string? LinkText { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}

public class SniffedMediaItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("extension")]
    public string? Extension { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("formattedSize")]
    public string? FormattedSize { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("referer")]
    public string? Referer { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class HostResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("forwarded")]
    public bool Forwarded { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    public static HostResponse Ok(string? action, string? message = null, bool forwarded = true, object? data = null)
    {
        return new HostResponse
        {
            Status = "ok",
            Action = action,
            Type = action,
            Message = message ?? "Success",
            Forwarded = forwarded,
            Data = data
        };
    }

    public static HostResponse Error(string? action, string message, object? data = null)
    {
        return new HostResponse
        {
            Status = "error",
            Action = action,
            Type = action,
            Message = message,
            Forwarded = false,
            Data = data
        };
    }

    public static HostResponse Pong()
    {
        return new HostResponse
        {
            Status = "ok",
            Action = "PONG",
            Type = "PONG",
            Message = "NovaDownloader Native Host is alive",
            Forwarded = true
        };
    }
}
