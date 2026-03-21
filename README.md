# C2

Real-time command and control center for monitoring and managing [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) connections via the [relay](https://github.com/mrzaxaryan/relay) service. Built with Blazor WebAssembly (.NET 10).

## Overview

**C2** connects to the [relay](https://github.com/mrzaxaryan/relay) service over WebSocket and provides a desktop-like interface for live agent monitoring, remote file management, and search — all running in the browser as a PWA with offline support.

### Features

- **Real-time monitoring** — live agent/relay connection status via WebSocket events
- **Multi-window UI** — draggable, resizable floating windows per agent
- **Remote file browser** — navigate agent file systems through a binary WebSocket protocol
- **Download queue** — queue and track file downloads from agents with progress and auto-resume
- **Upload queue** — upload files to agents with pause/resume support
- **File search** — recursive search across agents with extension group filtering and auto-download
- **Offline cache** — browser FileSystem API + IndexedDB for local file storage
- **Extension groups** — user-defined file type categories for filtering
- **Dark/light theme** — toggle with localStorage persistence
- **Setup wizard** — guided onboarding for first-time configuration
- **PWA** — installable with service worker for offline access

## Architecture

```
+---------------------------+         +---------------------------+
|  Position-Independent     |         |                           |
|  Runtime (PIR)            |         |  C2 (this project)        |
|  C++23 shellcode runtime  |         |  Blazor WebAssembly       |
|  Zero-dependency, cross-  |         |  monitoring dashboard     |
|  platform                 |         |                           |
+------------+--------------+         +------------+--------------+
             |                                     |
             v                                     v
+---------------------------+         +---------------------------+
|  Position-Independent     |  <--->  |  relay                    |
|  Agent                    |   WS    |  Cloudflare Workers +     |
|  Cross-platform remote    |         |  Durable Objects          |
|  agent built on PIR       |         |  WebSocket relay server   |
+---------------------------+         +---------------------------+
```

| Component | Description | Repo |
|-----------|-------------|------|
| **PIR** | C++23 position-independent runtime — cryptography, networking, TLS 1.3, all without libc or CRT | [Position-Independent-Runtime](https://github.com/mrzaxaryan/Position-Independent-Runtime) |
| **Agent** | Cross-platform remote agent built on PIR — file system, hashing, binary command protocol over WebSocket | [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) |
| **Relay** | WebSocket relay on Cloudflare Workers — pairs agents with relay connections 1:1 via Durable Objects | [relay](https://github.com/mrzaxaryan/relay) |
| **C2** | This project — Blazor WebAssembly command and control center with real-time WebSocket monitoring and remote file management | [C2](https://github.com/mrzaxaryan/C2) |

## Development

```bash
# Run
dotnet run

# Or with hot reload
dotnet watch
```

Starts on `http://localhost:5057` / `https://localhost:7104`.

## Project Structure

```
├── Program.cs                  # App startup and DI registration
├── App.razor                   # Root router component
├── Features/
│   ├── Agents/                 # Agent monitoring and metadata
│   │   ├── AgentsPanel.razor   # Agent list UI
│   │   └── AgentStore.cs       # Agent persistence (IndexedDB)
│   ├── Relay/                  # Relay server connectivity
│   │   ├── RelayPanel.razor    # Relay config UI
│   │   ├── RelayConnectionService.cs  # Central orchestrator + event publisher
│   │   ├── RelaySocket.cs      # Binary WebSocket protocol to agents
│   │   ├── RelayStore.cs       # Relay server persistence
│   │   └── RelayModels.cs      # Agent/relay data models
│   ├── FileManager/            # Remote file browsing and cache
│   │   ├── FileManager.razor   # Remote file browser UI
│   │   ├── SyncPanel.razor     # Upload queue panel
│   │   ├── VfsStore.cs         # Virtual filesystem metadata (IndexedDB)
│   │   ├── CacheManager.cs     # Browser FileSystem API wrapper
│   │   └── DirEntry.cs         # Binary directory response parser
│   ├── Downloads/              # File download management
│   │   ├── DownloadsPanel.razor    # Download progress UI
│   │   ├── DownloadService.cs      # Queue processor with auto-resume
│   │   └── DownloadStore.cs        # Download queue + state persistence
│   ├── Uploads/                # File upload management
│   │   └── UploadsPanel.razor      # Upload queue UI
│   ├── Search/                 # File search across agents
│   │   ├── FileSearchPanel.razor   # Per-agent search UI
│   │   ├── SearchJobsPanel.razor   # Global search job history
│   │   ├── SearchService.cs        # Recursive search executor
│   │   └── SearchStore.cs          # Search queue + results persistence
│   ├── Extensions/             # File type filtering
│   │   ├── ExtensionGroupsPanel.razor  # Group management UI
│   │   └── ExtensionGroupStore.cs      # Extension group definitions
│   ├── Storage/                # Cache and settings
│   │   ├── SettingsPanel.razor     # Settings and storage config UI
│   │   └── CacheManager.cs        # Browser FileSystem API wrapper
│   ├── Workspace/              # Desktop window system
│   │   ├── MainLayout.razor        # Menu bar + desktop container
│   │   ├── FloatingWindows.razor   # Draggable/resizable window renderer
│   │   ├── SetupWizard.razor       # Onboarding flow
│   │   ├── WindowManager.cs        # Window state management
│   │   └── WindowState.cs          # Window data model
│   └── Notifications/          # Toast notification system
│       └── NotificationStore.cs
├── Shared/                     # Reusable UI components
│   ├── C2Btn.razor             # Button
│   ├── C2Card.razor            # Card container
│   ├── C2Dialog.razor          # Modal dialog
│   ├── C2Dropdown.razor        # Dropdown menu
│   ├── C2Input.razor           # Text input
│   ├── C2Table.razor           # Table layout
│   ├── C2Tr.razor              # Table row
│   ├── C2Progress.razor        # Progress bar
│   ├── C2Spinner.razor         # Loading spinner
│   ├── C2Icon.razor            # Icon
│   ├── C2Badge.razor           # Badge/label
│   └── C2ConfirmBtn.razor      # Confirmation button
├── Infrastructure/             # Cross-cutting services
│   ├── ThemeService.cs         # Dark/light theme toggle
│   ├── LocalStorageService.cs  # Browser localStorage wrapper
│   ├── MessageService.cs       # Toast notification dispatcher
│   ├── ServiceStateStore.cs    # Pause/resume state (IndexedDB)
│   ├── CtsManager.cs           # Cancellation token lifecycle
│   └── Formatters.cs           # Display utilities (sizes, timestamps, etc.)
└── wwwroot/                    # Static assets
    ├── index.html              # Entry point + service worker registration
    ├── manifest.webmanifest    # PWA manifest
    ├── css/app.css             # Stylesheet with dark/light theme system
    └── js/
        ├── interop.js          # C# ↔ JS interop helpers
        ├── filesystem.js       # File System Access API wrapper
        └── indexeddb.js        # IndexedDB wrapper for offline storage
```

## Tech Stack

- .NET 10.0 / Blazor WebAssembly
- Bootstrap 5
- IndexedDB + File System Access API for offline storage
- C# with nullable reference types
