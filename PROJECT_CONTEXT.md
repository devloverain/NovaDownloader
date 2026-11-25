# NovaDownloader — Project Structure & AI Handoff Context

> Use this file as context when starting a new AI chat so it immediately understands the architecture, recent changes, conventions, and build/test setup.

---

## 1. Overview & Tech Stack
- **Solution:** `NovaDownloader.slnx`
- **Target Frameworks:** `.NET 10` (`net10.0` for Core/Media/ExtensionHost, `net10.0-windows` WPF + WinForms `NotifyIcon`/`Screen` for UI)
- **Browser Extension:** Chrome / Opera / Edge / Brave Manifest V3 (`extension/`, v1.2.0)
- **AppData Persistence (`%LocalAppData%\NovaDownloader\`):**
  - `downloads.json` (task list, migrated automatically from legacy relative paths)
  - `settings.json` (`UserSettingsService`)
  - `schedules.json` (`QueueSchedulerService`)
  - `Logs\native_host.log` (`HostLogger`)
- **External Tools (optional on `PATH`, `Tools\`, or custom path in Options):** `yt-dlp.exe`, `ffmpeg.exe`

---

## 2. Repository Structure

```text
NovaDownloader/
├── NovaDownloader.slnx
├── PROJECT_CONTEXT.md
├── README.md
├── run_app.bat
├── install_browser_extension.bat
├── extension/                         # Manifest V3 browser extension (copied to UI output)
│   ├── manifest.json                  # v1.2.0, fixed RSA "key" -> ID: maninekbhekackeejegfbnelaognjaob
│   ├── background.js                  # webRequest sniffer, downloads.onCreated, DOWNLOAD_URL & BATCH_DOWNLOAD_LINKS
│   ├── content.js & content.css       # Floating video bar (incl. YouTube Shorts) + selection link grabber pill
│   ├── popup.html & popup.js
│   └── icons/
└── src/
    ├── NovaDownloader.Core/
    │   ├── Configuration/DownloaderOptions.cs   # Speed limit, segments, proxy, User-Agent, StateFilePath
    │   ├── Engine/
    │   │   ├── MultiSegmentDownloader.cs        # Parallel segments, RandomAccess, token-window speed throttle
    │   │   └── SocketsHttpHandlerFactory.cs     # Custom User-Agent & HTTP/SOCKS proxy support
    │   ├── Models/DownloadTask.cs               # Includes QueueId, Category, Description, IsExternalManaged
    │   └── Queue/DownloadQueueManager.cs        # RemoveTasksAsync with crash-safe TryDeleteFileSafe
    ├── NovaDownloader.Media/                    # HLS .m3u8 parser, parallel segment downloader, FFmpeg muxer
    ├── NovaDownloader.ExtensionHost/            # Native Messaging stdio <-> Named Pipe bridge & Registry installer
    │   ├── Models/ExtensionMessages.cs          # Includes BatchLinkItem & Links for BATCH_DOWNLOAD_LINKS
    │   └── NativeMessaging/NativeMessagingHost.cs
    └── NovaDownloader.UI/
        ├── App.xaml & App.xaml.cs               # Single-instance Mutex, --minimized tray boot, SHOW_WINDOW IPC
        ├── MainWindow.xaml & .cs                # Extended multi-select, ContextMenu icons, Start/Stop Queue dropdowns, Tray menus
        ├── Themes/IdmDarkTheme.xaml             # Dark Menu, ContextMenu, TreeViewItem, DataGrid, ComboBox, ScrollBar, CheckBox, RadioButton
        ├── ViewModels/MainViewModel.cs          # Multi-select ops, completion dialog routing, batch & scheduler integration
        ├── Services/
        │   ├── IpcService.cs                    # Named Pipe "NovaDownloaderIpcPipe" (DOWNLOAD_URL, BATCH_DOWNLOAD_LINKS, SHOW_WINDOW)
        │   ├── UserSettingsService.cs           # Persistent settings, category folders/extensions, duplicate resolution
        │   ├── QueueSchedulerService.cs         # Named queues, 1s schedule timer, per-queue concurrency dispatch
        │   ├── StartupService.cs                # HKCU Run key registration with --minimized
        │   ├── WindowPositionHelper.cs          # Multi-monitor DPI-aware centering on active browser screen
        │   └── YtDlpService.cs                  # yt-dlp.exe runner with custom path & pause/resume
        └── Dialogs/
            ├── DownloadFileInfoDialog.xaml/.cs  # Compact Start Download dialog with Schedule selector
            ├── DownloadProgressDialog.xaml/.cs  # Compact During Download dialog (auto-closes on completion)
            ├── DownloadCompletedDialog.xaml/.cs # Compact Finish Download dialog (Open, Open With, Open Folder)
            ├── BatchDownloadDialog.xaml/.cs     # Batch link grabber with live size probing & filters
            ├── DeleteConfirmDialog.xaml/.cs     # 3-button safe delete (List / File & List / Cancel)
            ├── SchedulerDialog.xaml/.cs         # Resizable two-pane Named Schedules & Queues manager
            ├── OptionsDialog.xaml/.cs           # Tabbed settings (General, Folders & Types, Speed & Network, Browser & Tools)
            └── BrowserIntegrationDialog.xaml/.cs
```

---

## 3. Build & Run Commands

```powershell
# Build entire solution (outputs UI + ExtensionHost + extension/ to src\NovaDownloader.UI\bin\Debug\net10.0-windows)
dotnet build "C:\Users\Alireza\.gemini\antigravity\scratch\NovaDownloader\NovaDownloader.slnx" -c Debug

# Run desktop app
dotnet run --project "C:\Users\Alireza\.gemini\antigravity\scratch\NovaDownloader\src\NovaDownloader.UI\NovaDownloader.UI.csproj"

# Register Native Messaging Host manually (also available in-app via Options -> Browser & Tools -> Setup Extension)
dotnet run --project "C:\Users\Alireza\.gemini\antigravity\scratch\NovaDownloader\src\NovaDownloader.ExtensionHost\NovaDownloader.ExtensionHost.csproj" -- --install
```
