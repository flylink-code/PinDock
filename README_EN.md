# PinDock

[简体中文](README.md) | [English](README_EN.md)

A standalone floating dock and quick launcher for Windows. Designed to coexist peacefully with the native Windows taskbar—providing fast access to apps, workspace folders, custom groups, and multi-level directory previews.

## Key Features (v0.1.7)

- **Hybrid Architecture (C# .NET 10 + WebView2 HTML/CSS)**
  - Native C# core for global hotkeys, 48×48 high-DPI shell icon extraction, fullscreen app avoidance, Explorer context menu integration, startup management, and single-instance IPC;
  - Pre-warmed hardware-accelerated WebView2 UI ([Assets/ui.html](src/PinDock/Assets/ui.html)) for the *All Bookmarks*, *Settings*, and configuration modals.
- **Floating Dock & Flexible Layout**
  - **4-Edge Docking & 3 Display Modes**: Always Visible, Hotkey Summon (customizable shortcut or disable), and Edge Auto-Hide (automatically wakes when dragging files/folders to the screen edge);
  - **Icon-Only & Icon + Label Modes**: Switch between macOS-style pure icons and mobile home-screen style icons with aligned bottom labels via Settings or the Dock right-click menu;
  - **Drag-to-Reorder & Custom Separators**: Drag icons, groups, or separators directly on the Dock to reorder; hold `Alt` or drag the Dock border to move the Dock along the screen edge; insert/remove custom separators or auto-separate by item type (Apps / Folders / Groups);
  - **Collapsible Tool Drawer (`‹ / ›`)**: One-click toggle at the end of the Dock to expand or collapse the built-in *Library*, *Add*, and *Settings* buttons.
- **Rich Custom Icons (Apps, Folders & Groups)**
  - Customize icons for any individual bookmark or group: **Default Shell / Folder Icon**, **Built-in Vector Presets** (Drive, Project Folder, Code, Hardware Chip, Terminal, Work, Database, Web, Design, Game, Star), **Letter & Number Badges** (`A–Z`, `0–9`, or any custom 1–3 character badge), or **Local Image / App Icon Files** (`.png`, `.ico`, `.jpg`, `.exe`, `.lnk`); Groups also support a **2×2 Member Thumbnail Grid**.
- **App Recent Items Preview & Hover Directory Browser**
  - **Universal App Recent Preview**: Hover over **VS Code**, **Cursor**, **Windsurf**, **Kiro**, **Trae**, **JetBrains IDEs (CLion / IDEA / PyCharm, etc.)**, **Visual Studio**, **Sublime Text**, **Obsidian**, **Notepad++**, **Typora**, **Office / WPS**, or any shortcut app on the Dock to preview recently opened `.code-workspace` workspaces, project folders, SSH remote workspaces, and recent documents—powered by native app storage parsers, Windows Jump Lists (`CustomDestinations` / `AutomaticDestinations`), file associations, and custom drag-and-drop project pinning;
  - **Multi-Level Directory Preview & Inline Drill-Down**: Hover over any pinned folder or drive to browse subfolders and files, or click `Expand ▸` on any app's recent project to drill into its directory tree right inside the popup;
  - **Scroll-Aware Dwell Detail Callouts**: Clean single-line list rows suppress tooltips during scrolling and only reveal the full multi-line name, full path, file size, and timestamp callout after the cursor rests on a row for ~0.6s, with 1-click *Copy Full Name* / *Copy Full Path*;
  - **Silky-Smooth Multi-Folder Switching**: Powered by first-screen incremental batch rendering, single-pass directory caching, and extension-level icon caching for zero-lag switching across pinned folders;
  - **5 Sorting Modes with Per-Folder Memory**: Sort by Name (`A→Z` / `Z→A` with natural numeric order), Date Modified (Newest / Oldest), or Date Created (Newest); remembers your preferred sort order independently for each folder and displays timestamps when sorting by time;
  - **Hide & Restore Subfolders**: Hover or right-click any subfolder in the preview popup to hide it from view, and toggle the footer `Hidden (N)` badge anytime to inspect or restore hidden folders.
- **System Tray Left-Click Quick Flyout**
  - Left-clicking the system tray icon opens a compact flyout near the tray featuring instant search (press `Enter` to launch), 1-click Light/Dark theme and `中/EN` language toggles, Dock status controls, Recent items, Pinned & Group tabs, and drag-and-drop bookmarking.
- **Bilingual UI & Quick Theme Switching**
  - Instant runtime switching between `简体中文 (zh-CN)` and `English (en-US)`;
  - 5 curated color palettes (*Forest*, *Ocean*, *Sand*, *Clay*, *Graphite*) × Light / Dark / System modes, with 1-click `☀ / 🌙` theme toggles in the panel header, tray flyout, and Dock context menu.
- **Windows 11 Context Menu & Online Auto-Updater**
  - Right-click any file, application, folder, folder background, or drive (`Add to PinDock`) or use `Send to -> PinDock`, with a **Windows 11 Direct Primary Context Menu** switch;
  - Launch at Windows startup, configurable panel close behavior (minimize to tray vs. exit), and 1-click GitHub Releases update checking and silent upgrade (with optional GitHub mirror acceleration).

## Build, Test & Package Installer

Requires Windows 10/11 and .NET 10 SDK:

```powershell
# Build solution and run automated tests
dotnet build PinDock.sln
dotnet run --project tests/PinDock.Tests

# Run PinDock from source
dotnet run --project src/PinDock

# Build standalone installer (outputs dist\PinDock-Setup-0.1.7.exe)
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

Bookmarks and settings are stored locally at `%LocalAppData%\PinDock\library.json` (removing a bookmark never deletes the target file on disk). See [CHANGELOG.md](CHANGELOG.md) for release history.

## License

[MIT License](LICENSE)