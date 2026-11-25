/**
 * NovaDownloader - Content Script (Floating Video Grabber & Quality Selector)
 * Injects sleek IDM-like floating download button on top of HTML5 video players.
 */

(function () {
  if (window.__novaDownloaderInjected) return;
  window.__novaDownloaderInjected = true;

  const isPersianLocale =
    window.location.hostname.includes("aparat.com") ||
    window.location.hostname.includes("filimo.com") ||
    window.location.hostname.includes("telewebion.com") ||
    window.location.hostname.includes("namava.ir") ||
    window.location.hostname.includes("varzesh3.com") ||
    (document.documentElement.lang && document.documentElement.lang.startsWith("fa"));

  const TXT_DOWNLOAD = isPersianLocale ? "دانلود ویدیو" : "Download Video";
  const TXT_SENT = isPersianLocale ? "به نوا دانلودر ارسال شد! ✓" : "Sent to NovaDownloader! ✓";
  const TXT_QUALITIES = isPersianLocale ? "کیفیت‌های در دسترس" : "Available Streams";
  const TXT_DL_SELECTED = isPersianLocale ? "دانلود با نوا دانلودر" : "Download with NovaDownloader";

  // SVG Icons
  const SVG_NOVA = `
    <svg class="nova-icon" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
      <path d="M12 2L12 15M12 15L7 10M12 15L17 10" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="M4 17V19C4 20.1046 4.89543 21 6 21H18C19.1046 21 20 20.1046 20 19V17" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/>
    </svg>`;

  const SVG_ARROW = `
    <svg class="nova-arrow-icon" viewBox="0 0 10 6" fill="none" xmlns="http://www.w3.org/2000/svg">
      <path d="M1 1L5 5L9 1" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>`;

  // WeakMap to link HTMLVideoElement to its floating widget
  const videoWidgets = new WeakMap();

  /**
   * Determine best title for video
   */
  function getVideoTitle(video) {
    if (window.location.hostname.includes("youtube.com")) {
      if (window.location.pathname.startsWith("/shorts/")) {
        const shortsTitle = document.querySelector(
          "ytd-reel-video-renderer[is-active] h2.title, ytd-reel-video-renderer[is-active] #shorts-title, ytd-reel-player-header-renderer h2, #shorts-title"
        );
        if (shortsTitle && shortsTitle.textContent.trim()) {
          return shortsTitle.textContent.trim();
        }
      }

      const ytTitle = document.querySelector("h1.ytd-watch-metadata, #title h1, h1.title");
      if (ytTitle && ytTitle.textContent.trim()) {
        return ytTitle.textContent.trim();
      }
    }

    if (window.location.hostname.includes("aparat.com")) {
      const aparatTitle = document.querySelector(".single-details__title, h1.title, .video-title");
      if (aparatTitle && aparatTitle.textContent.trim()) {
        return aparatTitle.textContent.trim();
      }
    }

    // Generic checks
    if (video.getAttribute("title")) return video.getAttribute("title");
    if (video.getAttribute("aria-label")) return video.getAttribute("aria-label");

    const pageHeading = document.querySelector("h1");
    if (pageHeading && pageHeading.textContent.trim()) {
      return pageHeading.textContent.trim();
    }

    return document.title.replace(/ - YouTube$/, "").replace(/ - آپارات$/, "").trim() || "Video Download";
  }

  /**
   * Collect streams for a video element
   */
  function collectStreams(video) {
    const streams = [];
    const title = getVideoTitle(video);
    const pageUrl = window.location.href;

    // 1. YouTube specialized handling (quality options routed via yt-dlp)
    if (window.location.hostname.includes("youtube.com") || window.location.hostname.includes("youtu.be")) {
      const qualities = [
        { quality: "Best (4K/1080p)", qualityKey: "best", extension: "MP4" },
        { quality: "1080p FHD", qualityKey: "1080p", extension: "MP4" },
        { quality: "720p HD", qualityKey: "720p", extension: "MP4" },
        { quality: "480p SD", qualityKey: "480p", extension: "MP4" },
        { quality: "Audio Only", qualityKey: "mp3", extension: "MP3" }
      ];
      qualities.forEach((q) => {
        streams.push({
          url: pageUrl,
          title: title,
          quality: q.quality,
          qualityKey: q.qualityKey,
          extension: q.extension,
          source: "youtube_page",
          pageUrl: pageUrl
        });
      });
      return streams;
    }

    // 2. Aparat specialized handling
    if (window.location.hostname.includes("aparat.com")) {
      // Check for Aparat quality buttons or script config
      const qualityBtns = document.querySelectorAll(".menu-item[data-quality], .quality-item, [data-quality]");
      if (qualityBtns.length > 0) {
        qualityBtns.forEach((btn) => {
          const qText = btn.textContent.trim();
          const qUrl = btn.getAttribute("data-url") || btn.getAttribute("href") || video.currentSrc;
          if (qUrl) {
            streams.push({
              url: qUrl,
              title: `${title} - ${qText}`,
              quality: qText,
              extension: "MP4",
              source: "aparat_quality",
              pageUrl: pageUrl
            });
          }
        });
      }
    }

    // 3. Current active source
    const currentSrc = video.currentSrc || video.src;
    if (currentSrc && !currentSrc.startsWith("blob:")) {
      let quality = "Original";
      if (video.videoHeight >= 1080) quality = "1080p FHD";
      else if (video.videoHeight >= 720) quality = "720p HD";
      else if (video.videoHeight >= 480) quality = "480p SD";
      else if (video.videoHeight > 0) quality = `${video.videoHeight}p`;

      streams.push({
        url: currentSrc,
        title: title,
        quality: quality,
        extension: currentSrc.includes(".m3u8") ? "M3U8" : "MP4",
        source: "video_currentSrc",
        pageUrl: pageUrl
      });
    }

    // 4. Source child elements
    const sourceElements = video.querySelectorAll("source");
    sourceElements.forEach((s) => {
      const src = s.getAttribute("src");
      if (src && !streams.some((existing) => existing.url === src)) {
        const fullUrl = new URL(src, window.location.href).href;
        const res = s.getAttribute("size") || s.getAttribute("data-quality") || s.getAttribute("type") || "Standard";
        streams.push({
          url: fullUrl,
          title: `${title} (${res})`,
          quality: res,
          extension: fullUrl.includes(".m3u8") ? "M3U8" : "MP4",
          source: "source_tag",
          pageUrl: pageUrl
        });
      }
    });

    // If only blob src was available, still allow forwarding page URL for engine extraction
    if (streams.length === 0 && currentSrc) {
      streams.push({
        url: window.location.href,
        title: title,
        quality: "Web Stream",
        extension: "MP4",
        source: "page_stream",
        pageUrl: pageUrl
      });
    }

    return streams;
  }

  /**
   * Send download request to background script
   */
  function triggerDownload(stream, videoTitle, toastEl) {
    const payload = {
      action: "DOWNLOAD_URL",
      url: stream.url,
      filename: `${videoTitle || stream.title || "video"}.${(stream.extension || "mp4").toLowerCase()}`,
      quality: stream.qualityKey || stream.quality || null,
      pageTitle: videoTitle,
      pageUrl: window.location.href,
      referer: window.location.href,
      mimeType: stream.extension === "M3U8" ? "application/x-mpegurl" : "video/mp4"
    };

    chrome.runtime.sendMessage(payload, (response) => {
      if (toastEl) {
        toastEl.classList.add("nova-show");
        setTimeout(() => toastEl.classList.remove("nova-show"), 2800);
      }
    });
  }

  /**
   * Create and attach floating UI widget for a video element
   */
  function attachWidgetToVideo(video) {
    if (videoWidgets.has(video)) return;

    // Filter out invisible, zero-size or audio-only video elements
    const rect = video.getBoundingClientRect();
    if (video.offsetWidth < 120 || video.offsetHeight < 80) {
      return;
    }

    const bar = document.createElement("div");
    bar.className = "nova-floating-bar";

    const btnGroup = document.createElement("div");
    btnGroup.className = "nova-btn-group";

    // Main download button
    const mainBtn = document.createElement("button");
    mainBtn.className = "nova-download-btn";
    mainBtn.innerHTML = `${SVG_NOVA} <span>${TXT_DOWNLOAD}</span>`;

    // Dropdown toggle button
    const toggleBtn = document.createElement("button");
    toggleBtn.className = "nova-toggle-btn";
    toggleBtn.setAttribute("aria-label", "Toggle stream qualities");
    toggleBtn.innerHTML = SVG_ARROW;

    // Dropdown menu
    const dropdown = document.createElement("div");
    dropdown.className = "nova-dropdown-menu";

    // Toast feedback
    const toast = document.createElement("div");
    toast.className = "nova-toast";
    toast.textContent = TXT_SENT;

    btnGroup.appendChild(mainBtn);
    btnGroup.appendChild(toggleBtn);
    bar.appendChild(btnGroup);
    bar.appendChild(dropdown);
    bar.appendChild(toast);

    // Attach to document.body so fixed floating position is never clipped by player overflow
    document.body.appendChild(bar);

    videoWidgets.set(video, { bar, dropdown, toggleBtn });

    // Populate dropdown with available streams (merging DOM + background webRequest sniffer)
    function renderDropdownStreams(streams, title) {
      dropdown.innerHTML = "";

      const header = document.createElement("div");
      header.className = "nova-menu-header";
      header.textContent = TXT_QUALITIES;
      dropdown.appendChild(header);

      if (streams.length === 0) {
        const emptyItem = document.createElement("div");
        emptyItem.style.padding = "8px";
        emptyItem.style.color = "#94a3b8";
        emptyItem.style.fontSize = "11px";
        emptyItem.textContent = "Scanning stream formats...";
        dropdown.appendChild(emptyItem);
        return;
      }

      streams.forEach((st) => {
        const item = document.createElement("button");
        item.className = "nova-menu-item";

        let badgeClass = "nova-quality-badge";
        const qUpper = (st.quality || "").toUpperCase();
        if (qUpper.includes("1080") || qUpper.includes("FHD")) badgeClass += " fhd";
        if (qUpper.includes("4K") || qUpper.includes("2160")) badgeClass += " uhd";

        item.innerHTML = `
          <div class="nova-item-label">
            <span class="${badgeClass}">${st.quality || "Default"}</span>
            <span>${st.extension || "MP4"}</span>
          </div>
          <div class="nova-item-size">${st.formattedSize || ""}</div>
        `;

        item.addEventListener("click", (e) => {
          e.stopPropagation();
          dropdown.classList.remove("nova-show");
          toggleBtn.querySelector(".nova-arrow-icon")?.classList.remove("nova-open");
          triggerDownload(st, title, toast);
        });

        dropdown.appendChild(item);
      });
    }

    function updateDropdown() {
      const domStreams = collectStreams(video);
      const title = getVideoTitle(video);
      renderDropdownStreams(domStreams, title);

      // On non-YouTube sites, merge sniffed streams from background service worker (e.g. HLS .m3u8)
      if (!window.location.hostname.includes("youtube.com") && !window.location.hostname.includes("youtu.be")) {
        chrome.runtime.sendMessage({ action: "GET_MEDIA" }, (resp) => {
          if (chrome.runtime.lastError || !resp || !Array.isArray(resp.mediaList)) return;
          const merged = [...domStreams];
          for (const m of resp.mediaList) {
            if (m && m.url && !m.url.includes("googlevideo.com") && !merged.some((x) => x.url === m.url)) {
              merged.push(m);
            }
          }
          renderDropdownStreams(merged, title);
        });
      }

      // Also notify background script about newly found DOM media
      chrome.runtime.sendMessage({
        action: "MEDIA_FOUND",
        media: domStreams.map((s) => ({
          url: s.url,
          title: s.title || title,
          pageUrl: window.location.href,
          quality: s.quality,
          extension: s.extension,
          source: s.source
        }))
      });
    }

    // Primary button click: download highest available stream
    mainBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      const streams = collectStreams(video);
      const title = getVideoTitle(video);
      const chosen = streams[0] || {
        url: video.currentSrc || window.location.href,
        extension: "MP4"
      };
      triggerDownload(chosen, title, toast);
    });

    // Toggle dropdown
    toggleBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      const isShowing = dropdown.classList.contains("nova-show");
      if (isShowing) {
        dropdown.classList.remove("nova-show");
        toggleBtn.querySelector(".nova-arrow-icon")?.classList.remove("nova-open");
      } else {
        updateDropdown();
        dropdown.classList.add("nova-show");
        toggleBtn.querySelector(".nova-arrow-icon")?.classList.add("nova-open");
      }
    });

    // Close dropdown on outside click
    document.addEventListener("click", (e) => {
      if (!bar.contains(e.target)) {
        dropdown.classList.remove("nova-show");
        toggleBtn.querySelector(".nova-arrow-icon")?.classList.remove("nova-open");
      }
    });

    // Draggable floating offset
    let dragOffsetX = 0;
    let dragOffsetY = 0;
    let isDragging = false;
    let dragStartX = 0;
    let dragStartY = 0;

    btnGroup.addEventListener("mousedown", (e) => {
      if (e.button !== 0) return;
      isDragging = false;
      dragStartX = e.clientX;
      dragStartY = e.clientY;
      const startOffX = dragOffsetX;
      const startOffY = dragOffsetY;

      const onMove = (ev) => {
        const dx = ev.clientX - dragStartX;
        const dy = ev.clientY - dragStartY;
        if (Math.abs(dx) > 4 || Math.abs(dy) > 4) {
          isDragging = true;
          dragOffsetX = startOffX + dx;
          dragOffsetY = startOffY + dy;
          updatePosition();
        }
      };
      const onUp = () => {
        window.removeEventListener("mousemove", onMove);
        window.removeEventListener("mouseup", onUp);
        if (isDragging) {
          setTimeout(() => { isDragging = false; }, 50);
        }
      };
      window.addEventListener("mousemove", onMove);
      window.addEventListener("mouseup", onUp);
    });

    // Positioning loop (floats just above top-right of video)
    function updatePosition() {
      if (!video.isConnected) {
        bar.remove();
        return;
      }

      const vRect = video.getBoundingClientRect();
      if (vRect.width < 120 || vRect.height < 80 || vRect.bottom < 0 || vRect.top > window.innerHeight) {
        bar.classList.add("nova-hidden");
        return;
      }

      bar.classList.remove("nova-hidden");

      const barW = bar.offsetWidth || 160;
      const barH = bar.offsetHeight || 32;
      const isShorts = window.location.pathname.startsWith("/shorts/");
      const baseTop = isShorts
        ? vRect.top + 12
        : (vRect.top > (barH + 12) ? (vRect.top - barH - 6) : (vRect.top + 10));
      const baseLeft = vRect.right - barW - 10;

      bar.style.top = `${Math.max(6, baseTop + dragOffsetY)}px`;
      bar.style.left = `${Math.max(6, Math.min(window.innerWidth - barW - 6, baseLeft + dragOffsetX))}px`;
      bar.style.right = "auto";
    }

    updatePosition();
    window.addEventListener("scroll", updatePosition, { passive: true });
    window.addEventListener("resize", updatePosition, { passive: true });

    // Handle Fullscreen transitions
    document.addEventListener("fullscreenchange", () => {
      const fsEl = document.fullscreenElement;
      if (fsEl && (fsEl.contains(video) || fsEl === video)) {
        fsEl.appendChild(bar);
        updatePosition();
      } else if (!fsEl) {
        document.body.appendChild(bar);
        updatePosition();
      }
    });

    // Re-check streams when video starts playing or changes src
    video.addEventListener("play", () => updateDropdown());
    video.addEventListener("loadedmetadata", () => {
      updatePosition();
      updateDropdown();
    });
  }

  /**
   * Scan page for video elements
   */
  function scanVideos() {
    const videos = document.querySelectorAll("video");
    videos.forEach((v) => attachWidgetToVideo(v));
  }

  // Initial scan
  scanVideos();

  // Watch for dynamic video players added to DOM
  const observer = new MutationObserver((mutations) => {
    let hasNewVideo = false;
    for (const m of mutations) {
      if (m.addedNodes.length > 0) {
        for (const node of m.addedNodes) {
          if (node.nodeType === Node.ELEMENT_NODE) {
            if (node.tagName === "VIDEO" || node.querySelector("video")) {
              hasNewVideo = true;
              break;
            }
          }
        }
      }
      if (hasNewVideo) break;
    }

    if (hasNewVideo) {
      scanVideos();
    }
  });

  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });

  // Re-scan on YouTube / SPA navigations and Shorts scrolling
  window.addEventListener("yt-navigate-finish", () => setTimeout(scanVideos, 400));
  if (window.location.hostname.includes("youtube.com")) {
    setInterval(scanVideos, 1200);
  }
  document.addEventListener("play", (e) => {
    if (e.target && e.target.tagName === "VIDEO") {
      attachWidgetToVideo(e.target);
    }
  }, true);

  // =========================================================================
  // Selection Link Grabber (Floating "Download with NovaDownloader" Pill)
  // =========================================================================
  let selectionBar = null;
  let selectionLinks = [];
  let selectionTimer = null;

  function ensureSelectionBar() {
    if (selectionBar) return selectionBar;

    selectionBar = document.createElement("div");
    selectionBar.className = "nova-selection-bar nova-hidden";

    const dlBtn = document.createElement("button");
    dlBtn.className = "nova-sel-btn";
    dlBtn.type = "button";
    dlBtn.innerHTML = `${SVG_NOVA} <span class="nova-sel-label">${TXT_DL_SELECTED}</span>`;

    const closeBtn = document.createElement("button");
    closeBtn.className = "nova-sel-close";
    closeBtn.type = "button";
    closeBtn.title = "Dismiss";
    closeBtn.textContent = "✕";

    dlBtn.addEventListener("mousedown", (e) => e.preventDefault());
    dlBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (selectionLinks.length === 0) return;

      const linksToSend = [...selectionLinks];
      hideSelectionBar();

      if (typeof chrome === "undefined" || !chrome.runtime || !chrome.runtime.id) {
        showSelectionToast("NovaDownloader reloaded — please refresh this page (F5).", true);
        return;
      }

      try {
        chrome.runtime.sendMessage(
          {
            action: "BATCH_DOWNLOAD_LINKS",
            links: linksToSend,
            pageUrl: window.location.href,
            pageTitle: document.title,
            referer: window.location.href
          },
          (response) => {
            if (chrome.runtime.lastError) {
              showSelectionToast("NovaDownloader: " + chrome.runtime.lastError.message, true);
              return;
            }
            if (!response) {
              showSelectionToast("No response from background worker — reload extension in opera://extensions & refresh (F5).", true);
              return;
            }
            if (response.status === "ignored") {
              showSelectionToast("Extension background worker is outdated — remove & re-add NovaDownloader in opera://extensions.", true);
              return;
            }
            if (response.status === "error") {
              showSelectionToast(response.message || "Error sending links to NovaDownloader.", true);
              return;
            }
            showSelectionToast(`Sent ${linksToSend.length} link(s) to NovaDownloader ✓`, false);
          }
        );
      } catch (err) {
        showSelectionToast("NovaDownloader reloaded — please refresh this page (F5).", true);
      }
    });

    closeBtn.addEventListener("mousedown", (e) => e.preventDefault());
    closeBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      hideSelectionBar();
    });

    selectionBar.appendChild(dlBtn);
    selectionBar.appendChild(closeBtn);
    document.body.appendChild(selectionBar);
    return selectionBar;
  }

  function showSelectionToast(message, isError) {
    const toast = document.createElement("div");
    toast.style.cssText = `position:fixed;bottom:20px;right:20px;z-index:2147483647;padding:9px 14px;border-radius:8px;font-family:Segoe UI,sans-serif;font-size:12.5px;font-weight:600;color:#fff;box-shadow:0 8px 24px rgba(0,0,0,0.45);background:${isError ? "#dc2626" : "#0891b2"};`;
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 3500);
  }

  function hideSelectionBar() {
    if (selectionBar) {
      selectionBar.classList.add("nova-hidden");
    }
    selectionLinks = [];
  }

  function collectSelectedLinks() {
    const sel = window.getSelection();
    if (!sel || sel.isCollapsed || sel.rangeCount === 0) return [];

    const anchors = document.querySelectorAll("a[href]");
    const seen = new Set();
    const collected = [];

    for (let r = 0; r < sel.rangeCount; r++) {
      const range = sel.getRangeAt(r);
      for (const a of anchors) {
        if (!range.intersectsNode(a)) continue;

        const href = a.href;
        if (!href || (!href.startsWith("http://") && !href.startsWith("https://"))) continue;
        if (seen.has(href)) continue;

        seen.add(href);
        const linkText = (a.innerText || a.textContent || a.title || "").replace(/\s+/g, " ").trim();
        let fileName = "";
        try {
          const parsed = new URL(href);
          const lastSeg = decodeURIComponent(parsed.pathname.split("/").filter(Boolean).pop() || "");
          if (lastSeg.includes(".")) fileName = lastSeg;
        } catch { }

        collected.push({
          url: href,
          linkText: linkText,
          fileName: fileName
        });
      }
    }

    return collected;
  }

  function updateSelectionBar() {
    const links = collectSelectedLinks();
    if (links.length === 0) {
      hideSelectionBar();
      return;
    }

    // Require either 2+ links OR 1 link with a downloadable file extension
    const downloadableExtRe = /\.(mp4|mkv|avi|mov|webm|mp3|flac|zip|rar|7z|tar|gz|iso|exe|msi|pdf|apk|m3u8)(\?|$)/i;
    if (links.length === 1 && !downloadableExtRe.test(links[0].url)) {
      hideSelectionBar();
      return;
    }

    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) {
      hideSelectionBar();
      return;
    }

    const rect = sel.getRangeAt(0).getBoundingClientRect();
    if (!rect || (rect.width === 0 && rect.height === 0)) {
      hideSelectionBar();
      return;
    }

    selectionLinks = links;
    const bar = ensureSelectionBar();
    const labelEl = bar.querySelector(".nova-sel-label");
    if (labelEl) {
      labelEl.textContent = links.length > 1
        ? `${TXT_DL_SELECTED} (${links.length})`
        : TXT_DL_SELECTED;
    }

    bar.classList.remove("nova-hidden");
    const barW = bar.offsetWidth || 230;
    const barH = bar.offsetHeight || 34;

    let top = rect.top - barH - 8;
    if (top < 8) top = rect.bottom + 8;
    let left = Math.max(8, Math.min(window.innerWidth - barW - 8, rect.left));

    bar.style.top = `${top}px`;
    bar.style.left = `${left}px`;
  }

  function scheduleSelectionCheck() {
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(updateSelectionBar, 90);
  }

  document.addEventListener("mouseup", (e) => {
    if (selectionBar && selectionBar.contains(e.target)) return;
    scheduleSelectionCheck();
  });

  document.addEventListener("keyup", (e) => {
    if (e.shiftKey || e.key === "a" || e.key === "A") {
      scheduleSelectionCheck();
    }
  });

  document.addEventListener("mousedown", (e) => {
    if (selectionBar && !selectionBar.contains(e.target)) {
      hideSelectionBar();
    }
  });

  console.log("[NovaDownloader] Video detection & selection link grabber activated.");
})();
