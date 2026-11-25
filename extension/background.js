/**
 * NovaDownloader Browser Extension - Background Service Worker (Manifest V3)
 * Manages media sniffing, cookie extraction, and Native Messaging communication.
 */

const NATIVE_HOST_NAME = "com.novadownloader.host";

// Supported extensions and MIME patterns
const MEDIA_EXTENSIONS = [
  ".mp4", ".m4v", ".webm", ".mkv", ".m3u8", ".mpd", ".ts",
  ".flv", ".avi", ".mov", ".wmv", ".mp3", ".m4a", ".aac",
  ".wav", ".ogg", ".opus", ".flac"
];

const STREAM_MANIFEST_EXTENSIONS = [".m3u8", ".mpd"];

const MEDIA_MIME_PREFIXES = [
  "video/",
  "audio/",
  "application/x-mpegurl",
  "application/vnd.apple.mpegurl",
  "application/dash+xml",
  "application/vnd.ms-sstr+xml",
  "application/octet-stream" // checked along with file extension
];

// In-memory cache of detected media per tab: tabId -> Map(url -> mediaItem)
const tabMediaStore = new Map();

// Load persistent state from session storage on worker startup
chrome.storage.session.get(null).then((items) => {
  for (const [key, list] of Object.entries(items)) {
    if (key.startsWith("tab_media_") && Array.isArray(list)) {
      const tabId = parseInt(key.replace("tab_media_", ""), 10);
      if (!isNaN(tabId)) {
        const map = new Map();
        list.forEach((item) => map.set(item.url, item));
        tabMediaStore.set(tabId, map);
      }
    }
  }
}).catch(() => {
  // session storage might not be supported in older Chrome builds, fallback to memory
});

/**
 * Format raw byte size to human readable string (e.g. 142.5 MB)
 */
function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return null;
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
}

/**
 * Extract filename from URL or Content-Disposition
 */
function extractFilename(url, contentDisposition) {
  if (contentDisposition) {
    const filenameMatch = contentDisposition.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)["']?/i);
    if (filenameMatch && filenameMatch[1]) {
      try {
        return decodeURIComponent(filenameMatch[1].trim());
      } catch (e) {
        return filenameMatch[1].trim();
      }
    }
  }

  try {
    const parsed = new URL(url);
    const pathname = parsed.pathname;
    const segments = pathname.split("/").filter(Boolean);
    if (segments.length > 0) {
      const last = decodeURIComponent(segments[segments.length - 1]);
      if (last.includes(".") && !last.endsWith(".")) {
        return last;
      }
    }
  } catch (e) {
    // ignore
  }

  return "video_" + Date.now() + ".mp4";
}

/**
 * Infer quality label (e.g. 1080p, 720p, 4K, HLS)
 */
function inferQuality(url, width, height) {
  if (height) {
    if (height >= 2160) return "4K 2160p";
    if (height >= 1440) return "2K 1440p";
    if (height >= 1080) return "1080p FHD";
    if (height >= 720) return "720p HD";
    if (height >= 480) return "480p SD";
    return height + "p";
  }

  const lower = url.toLowerCase();
  if (lower.includes("2160p") || lower.includes("4k")) return "4K 2160p";
  if (lower.includes("1440p") || lower.includes("2k")) return "2K 1440p";
  if (lower.includes("1080p") || lower.includes("1080")) return "1080p FHD";
  if (lower.includes("720p") || lower.includes("720")) return "720p HD";
  if (lower.includes("480p") || lower.includes("480")) return "480p SD";
  if (lower.includes("360p") || lower.includes("360")) return "360p";
  if (lower.includes("240p")) return "240p";
  if (lower.endsWith(".m3u8") || lower.includes(".m3u8?")) return "HLS Stream";
  if (lower.endsWith(".mpd") || lower.includes(".mpd?")) return "DASH Stream";
  return "Standard";
}

/**
 * Infer file extension from URL or MIME
 */
function inferExtension(url, mimeType) {
  const lowerUrl = url.toLowerCase();
  for (const ext of MEDIA_EXTENSIONS) {
    if (lowerUrl.includes(ext)) return ext.replace(".", "").toUpperCase();
  }
  if (mimeType) {
    if (mimeType.includes("mp4")) return "MP4";
    if (mimeType.includes("webm")) return "WEBM";
    if (mimeType.includes("mpegurl")) return "M3U8";
    if (mimeType.includes("dash")) return "MPD";
    if (mimeType.includes("matroska")) return "MKV";
    if (mimeType.includes("audio/mpeg") || mimeType.includes("mp3")) return "MP3";
  }
  return "MP4";
}

/**
 * Update tab badge counter
 */
function updateBadge(tabId) {
  const map = tabMediaStore.get(tabId);
  const count = map ? map.size : 0;
  if (count > 0) {
    chrome.action.setBadgeText({ text: count.toString(), tabId });
    chrome.action.setBadgeBackgroundColor({ color: "#6366f1", tabId });
  } else {
    chrome.action.setBadgeText({ text: "", tabId });
  }

  // Persist to session storage
  if (map && map.size > 0) {
    const list = Array.from(map.values());
    chrome.storage.session.set({ ["tab_media_" + tabId]: list }).catch(() => {});
  } else {
    chrome.storage.session.remove("tab_media_" + tabId).catch(() => {});
  }
}

/**
 * Add or update a media item for a tab
 */
function addMediaItem(tabId, item) {
  if (!tabId || tabId < 0 || !item || !item.url) return;

  // Ignore blob: or chrome: or data: urls for network downloads
  if (item.url.startsWith("blob:") && !item.originalUrl) {
    // If blob, keep for player reference if it has title
    if (!item.title) return;
  }
  if (item.url.startsWith("data:") || item.url.startsWith("chrome://")) return;

  if (!tabMediaStore.has(tabId)) {
    tabMediaStore.set(tabId, new Map());
  }

  const map = tabMediaStore.get(tabId);
  if (!map.has(item.url)) {
    map.set(item.url, item);
    updateBadge(tabId);
    console.log(`[NovaDownloader] Detected media on tab ${tabId}: ${item.title || item.url}`);
  }
}

/**
 * Native Messaging sender with Promise wrapper
 */
function sendNativeMessageAsync(payload) {
  return new Promise((resolve, reject) => {
    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
        if (chrome.runtime.lastError) {
          console.warn("[NovaDownloader] Native message error:", chrome.runtime.lastError.message);
          resolve({
            status: "error",
            connected: false,
            message: chrome.runtime.lastError.message
          });
        } else {
          resolve(response || { status: "ok", connected: true });
        }
      });
    } catch (err) {
      console.error("[NovaDownloader] Native messaging exception:", err);
      resolve({ status: "error", connected: false, message: err.message });
    }
  });
}

/**
 * WebRequest Sniffer: Inspect response headers for media files
 */
chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    // Only inspect main frame and sub frames, ignore chrome internal requests
    if (details.tabId < 0) return;

    const url = details.url;
    const lowerUrl = url.toLowerCase();

    // Ignore YouTube/MSE raw byte-range playback chunks
    if (lowerUrl.includes("googlevideo.com/videoplayback")) return;

    // Check extension
    const hasMediaExt = MEDIA_EXTENSIONS.some((ext) => lowerUrl.includes(ext));

    let contentType = "";
    let contentLength = 0;
    let contentDisposition = "";

    if (details.responseHeaders) {
      for (const h of details.responseHeaders) {
        const name = h.name.toLowerCase();
        if (name === "content-type") contentType = (h.value || "").toLowerCase();
        if (name === "content-length") contentLength = parseInt(h.value || "0", 10);
        if (name === "content-disposition") contentDisposition = h.value || "";
      }
    }

    const hasMediaMime = MEDIA_MIME_PREFIXES.some((mime) => contentType.includes(mime));
    const isManifest = STREAM_MANIFEST_EXTENSIONS.some((ext) => lowerUrl.includes(ext));

    // Filter out tiny chunks (< 150KB) unless it's an HLS/DASH manifest
    if (!hasMediaExt && !hasMediaMime) return;
    if (contentLength > 0 && contentLength < 150 * 1024 && !isManifest) return;

    const filename = extractFilename(url, contentDisposition);
    const extension = inferExtension(url, contentType);
    const quality = inferQuality(url);
    const formattedSize = formatBytes(contentLength);

    const mediaItem = {
      url: url,
      filename: filename,
      title: filename,
      pageUrl: details.initiator || details.documentUrl || "",
      quality: quality,
      extension: extension,
      mimeType: contentType,
      size: contentLength,
      formattedSize: formattedSize,
      source: isManifest ? "manifest_stream" : "webrequest",
      timestamp: Date.now()
    };

    addMediaItem(details.tabId, mediaItem);
  },
  { urls: ["<all_urls>"] },
  ["responseHeaders"]
);

/**
 * Handle incoming messages from popup and content scripts
 */
chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  const action = (request.action || request.type || "").toUpperCase();
  const tabId = request.tabId || (sender.tab ? sender.tab.id : null);

  switch (action) {
    case "GET_MEDIA": {
      const targetTabId = request.tabId || (sender.tab ? sender.tab.id : null);
      const map = tabMediaStore.get(targetTabId);
      const rank = (q) => {
        const s = (q || "").toLowerCase();
        if (s.includes("2160") || s.includes("4k")) return 100;
        if (s.includes("1440") || s.includes("2k")) return 90;
        if (s.includes("1080")) return 80;
        if (s.includes("720")) return 70;
        if (s.includes("480")) return 50;
        if (s.includes("hls") || s.includes("dash")) return 40;
        return 10;
      };
      const mediaList = map
        ? Array.from(map.values()).sort((a, b) => rank(b.quality) - rank(a.quality))
        : [];
      sendResponse({ mediaList: mediaList });
      return true;
    }

    case "MEDIA_FOUND": {
      if (tabId && request.media) {
        if (Array.isArray(request.media)) {
          request.media.forEach((m) => addMediaItem(tabId, m));
        } else {
          addMediaItem(tabId, request.media);
        }
      }
      sendResponse({ status: "ok" });
      return true;
    }

    case "CLEAR_MEDIA": {
      if (tabId && tabMediaStore.has(tabId)) {
        tabMediaStore.delete(tabId);
        updateBadge(tabId);
      }
      sendResponse({ status: "ok" });
      return true;
    }

    case "CHECK_HOST": {
      sendNativeMessageAsync({ action: "PING" }).then((resp) => {
        sendResponse(resp);
      });
      return true; // Keep channel open for async response
    }

    case "DOWNLOAD_URL": {
      const targetUrl = request.url;
      if (!targetUrl) {
        sendResponse({ status: "error", message: "Missing URL" });
        return true;
      }

      // Fetch cookies for target URL so authenticated downloads work seamlessly
      chrome.cookies.getAll({ url: targetUrl }, (cookies) => {
        const cookieString = (cookies || [])
          .map((c) => `${c.name}=${c.value}`)
          .join("; ");

        const payload = {
          action: "DOWNLOAD_URL",
          url: targetUrl,
          filename: request.filename || extractFilename(targetUrl),
          cookies: cookieString,
          userAgent: navigator.userAgent,
          referer: request.referer || (sender.tab ? sender.tab.url : ""),
          headers: request.headers || {},
          fileSize: request.fileSize || request.size || 0,
          mimeType: request.mimeType || "",
          pageTitle: request.pageTitle || (sender.tab ? sender.tab.title : ""),
          pageUrl: request.pageUrl || (sender.tab ? sender.tab.url : ""),
          quality: request.quality || null,
          downloadDirectory: request.downloadDirectory || null
        };

        console.log("[NovaDownloader] Forwarding download to native host:", payload.url);

        sendNativeMessageAsync(payload).then((resp) => {
          sendResponse(resp);
        });
      });

      return true; // Keep message channel open for async response
    }

    case "BATCH_DOWNLOAD_LINKS": {
      const links = Array.isArray(request.links) ? request.links : [];
      if (links.length === 0) {
        sendResponse({ status: "error", message: "No links selected" });
        return true;
      }

      const refUrl = request.pageUrl || (sender.tab ? sender.tab.url : "") || links[0].url;
      (async () => {
        let cookieString = "";
        try {
          const cookies = await chrome.cookies.getAll({ url: refUrl });
          cookieString = (cookies || []).map((c) => `${c.name}=${c.value}`).join("; ");
        } catch { }

        const payload = {
          action: "BATCH_DOWNLOAD_LINKS",
          links: links,
          pageUrl: request.pageUrl || (sender.tab ? sender.tab.url : ""),
          pageTitle: request.pageTitle || (sender.tab ? sender.tab.title : ""),
          referer: request.referer || (sender.tab ? sender.tab.url : ""),
          userAgent: navigator.userAgent,
          cookies: cookieString
        };

        console.log(`[NovaDownloader] Forwarding ${links.length} selected links to desktop app.`);
        const resp = await sendNativeMessageAsync(payload);
        sendResponse(resp);
      })();

      return true;
    }

    default:
      sendResponse({ status: "ignored" });
      return false;
  }
});

/**
 * Tab cleanup listeners
 */
chrome.tabs.onRemoved.addListener((tabId) => {
  tabMediaStore.delete(tabId);
  chrome.storage.session.remove("tab_media_" + tabId).catch(() => {});
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    // Page navigated to a new URL, clear media for tab
    tabMediaStore.delete(tabId);
    updateBadge(tabId);
  }
});

/**
 * Intercept standard browser downloads and hand off to NovaDownloader when enabled & online
 */
chrome.downloads.onCreated.addListener(async (downloadItem) => {
  try {
    const url = downloadItem.finalUrl || downloadItem.url;
    if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) return;

    const settings = await chrome.storage.local.get({ autoIntercept: true });
    if (!settings.autoIntercept) return;

    const ping = await sendNativeMessageAsync({ action: "PING" });
    if (!ping || ping.status === "error") return;

    // Cancel browser download and forward to NovaDownloader
    await chrome.downloads.cancel(downloadItem.id).catch(() => {});
    await chrome.downloads.erase({ id: downloadItem.id }).catch(() => {});

    const cookies = await chrome.cookies.getAll({ url }).catch(() => []);
    const cookieString = (cookies || []).map((c) => `${c.name}=${c.value}`).join("; ");

    await sendNativeMessageAsync({
      action: "DOWNLOAD_URL",
      url,
      filename: downloadItem.filename ? downloadItem.filename.split(/[\\/]/).pop() : extractFilename(url),
      cookies: cookieString,
      userAgent: navigator.userAgent,
      referer: downloadItem.referrer || "",
      fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : 0,
      mimeType: downloadItem.mime || ""
    });
  } catch (err) {
    console.warn("[NovaDownloader] Download intercept skipped:", err);
  }
});

console.log("[NovaDownloader] Background service worker initialized.");
