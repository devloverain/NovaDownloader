/**
 * NovaDownloader - Popup Controller
 * Manages media display, connection status, and one-click downloads.
 */

document.addEventListener("DOMContentLoaded", async () => {
  const statusBadge = document.getElementById("statusBadge");
  const statusText = document.getElementById("statusText");
  const tabTitleEl = document.getElementById("tabTitle");
  const mediaCountEl = document.getElementById("mediaCount");
  const mediaContainer = document.getElementById("mediaContainer");
  const emptyState = document.getElementById("emptyState");
  const btnDownloadAll = document.getElementById("btnDownloadAll");
  const btnRescan = document.getElementById("btnRescan");
  const btnRefresh = document.getElementById("btnRefresh");
  const btnClear = document.getElementById("btnClear");
  const chkAutoIntercept = document.getElementById("chkAutoIntercept");

  if (chkAutoIntercept) {
    chrome.storage.local.get({ autoIntercept: true }, (res) => {
      chkAutoIntercept.checked = !!res.autoIntercept;
    });
    chkAutoIntercept.addEventListener("change", () => {
      chrome.storage.local.set({ autoIntercept: chkAutoIntercept.checked });
    });
  }

  let currentTab = null;
  let detectedMedia = [];

  // SVG Icons
  const SVG_DOWNLOAD = `
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
      <path d="M12 2v13M12 15l-4-4M12 15l4-4M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2"></path>
    </svg>`;

  const SVG_CHECK = `
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
      <polyline points="20 6 9 17 4 12"></polyline>
    </svg>`;

  const SVG_VIDEO_ICON = `
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <polygon points="23 7 16 12 23 17 23 7"></polygon>
      <rect x="1" y="5" width="15" height="14" rx="2" ry="2"></rect>
    </svg>`;

  const SVG_AUDIO_ICON = `
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M9 18V5l12-2v13"></path>
      <circle cx="6" cy="18" r="3"></circle>
      <circle cx="18" cy="16" r="3"></circle>
    </svg>`;

  /**
   * Check connection to NovaDownloader Native Host
   */
  async function checkHostConnection() {
    try {
      chrome.runtime.sendMessage({ action: "CHECK_HOST" }, (response) => {
        if (chrome.runtime.lastError || !response || response.status === "error") {
          statusBadge.classList.add("offline");
          statusText.textContent = "App Offline (Queuing)";
        } else {
          statusBadge.classList.remove("offline");
          statusText.textContent = "Connected";
        }
      });
    } catch (e) {
      statusBadge.classList.add("offline");
      statusText.textContent = "App Offline";
    }
  }

  /**
   * Get Active Tab
   */
  async function getActiveTab() {
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    return tabs[0] || null;
  }

  /**
   * Load media items for the current tab
   */
  async function loadTabMedia() {
    currentTab = await getActiveTab();
    if (!currentTab) return;

    tabTitleEl.textContent = currentTab.title || currentTab.url || "Current Page";

    chrome.runtime.sendMessage({ action: "GET_MEDIA", tabId: currentTab.id }, (response) => {
      detectedMedia = (response && response.mediaList) ? response.mediaList : [];
      renderMediaList(detectedMedia);
    });
  }

  /**
   * Render media cards
   */
  function renderMediaList(items) {
    mediaContainer.innerHTML = "";
    const count = items ? items.length : 0;
    mediaCountEl.textContent = `${count} Stream${count === 1 ? "" : "s"}`;

    if (count === 0) {
      emptyState.style.display = "flex";
      mediaContainer.style.display = "none";
      btnDownloadAll.style.display = "none";
      return;
    }

    emptyState.style.display = "none";
    mediaContainer.style.display = "flex";
    btnDownloadAll.style.display = count > 1 ? "inline-block" : "none";

    items.forEach((item, index) => {
      const card = document.createElement("div");
      card.className = "media-card";

      const isAudio = item.extension === "MP3" || item.extension === "M4A" || (item.mimeType && item.mimeType.startsWith("audio"));
      const iconSvg = isAudio ? SVG_AUDIO_ICON : SVG_VIDEO_ICON;

      const title = item.title || item.filename || `Stream #${index + 1}`;
      const ext = item.extension || "MP4";
      const quality = item.quality || "Standard";
      const sizeText = item.formattedSize || (item.size ? `${(item.size / 1024 / 1024).toFixed(1)} MB` : "");

      card.innerHTML = `
        <div class="card-top">
          <div class="card-icon">${iconSvg}</div>
          <div class="card-details">
            <div class="card-title" title="${escapeHtml(title)}">${escapeHtml(title)}</div>
            <div class="card-badges">
              <span class="badge badge-quality">${escapeHtml(quality)}</span>
              <span class="badge badge-format">${escapeHtml(ext)}</span>
              ${sizeText ? `<span class="badge badge-size">${escapeHtml(sizeText)}</span>` : ""}
            </div>
          </div>
        </div>
        <div class="card-actions">
          <button class="btn-download" data-index="${index}">
            ${SVG_DOWNLOAD}
            <span>Download</span>
          </button>
          <button class="btn-copy" data-url="${escapeHtml(item.url)}" title="Copy stream URL">
            Copy Link
          </button>
        </div>
      `;

      // Download button action
      const btnDownload = card.querySelector(".btn-download");
      btnDownload.addEventListener("click", () => {
        downloadItem(item, btnDownload);
      });

      // Copy link action
      const btnCopy = card.querySelector(".btn-copy");
      btnCopy.addEventListener("click", () => {
        navigator.clipboard.writeText(item.url);
        btnCopy.textContent = "Copied!";
        setTimeout(() => (btnCopy.textContent = "Copy Link"), 1500);
      });

      mediaContainer.appendChild(card);
    });
  }

  /**
   * Trigger download for a media item
   */
  function downloadItem(item, buttonEl) {
    if (buttonEl) {
      buttonEl.innerHTML = `${SVG_CHECK} <span>Sending...</span>`;
      buttonEl.style.background = "#10b981";
    }

    const payload = {
      action: "DOWNLOAD_URL",
      url: item.url,
      filename: item.filename || item.title || "video.mp4",
      fileSize: item.size || 0,
      mimeType: item.mimeType || "",
      pageTitle: currentTab ? currentTab.title : "",
      pageUrl: currentTab ? currentTab.url : "",
      referer: currentTab ? currentTab.url : ""
    };

    chrome.runtime.sendMessage(payload, (resp) => {
      if (buttonEl) {
        buttonEl.innerHTML = `${SVG_CHECK} <span>Sent to App!</span>`;
        setTimeout(() => {
          buttonEl.innerHTML = `${SVG_DOWNLOAD} <span>Download</span>`;
          buttonEl.style.background = "";
        }, 2500);
      }
    });
  }

  /**
   * Download All detected items
   */
  btnDownloadAll.addEventListener("click", () => {
    btnDownloadAll.textContent = "Sending All...";
    detectedMedia.forEach((item, idx) => {
      setTimeout(() => {
        chrome.runtime.sendMessage({
          action: "DOWNLOAD_URL",
          url: item.url,
          filename: item.filename || `video_${idx + 1}.mp4`,
          pageTitle: currentTab ? currentTab.title : "",
          pageUrl: currentTab ? currentTab.url : "",
          referer: currentTab ? currentTab.url : ""
        });
      }, idx * 250);
    });

    setTimeout(() => {
      btnDownloadAll.textContent = "Sent All! ✓";
      setTimeout(() => (btnDownloadAll.textContent = "Download All"), 2500);
    }, detectedMedia.length * 250 + 200);
  });

  /**
   * Rescan current page
   */
  btnRescan.addEventListener("click", async () => {
    btnRescan.textContent = "Scanning...";
    if (currentTab && currentTab.id) {
      try {
        await chrome.scripting.executeScript({
          target: { tabId: currentTab.id },
          func: () => {
            const videos = document.querySelectorAll("video");
            videos.forEach((v) => {
              v.dispatchEvent(new Event("loadedmetadata"));
            });
          }
        });
      } catch (e) {
        // ignore
      }
    }
    setTimeout(() => {
      loadTabMedia();
      btnRescan.textContent = "Rescan Page";
    }, 600);
  });

  btnRefresh.addEventListener("click", () => {
    checkHostConnection();
    loadTabMedia();
  });

  btnClear.addEventListener("click", () => {
    if (currentTab && currentTab.id) {
      chrome.runtime.sendMessage({ action: "CLEAR_MEDIA", tabId: currentTab.id }, () => {
        loadTabMedia();
      });
    }
  });

  function escapeHtml(str) {
    if (!str) return "";
    return str
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  // Initial load
  await checkHostConnection();
  await loadTabMedia();
});
