# C2

A fully serverless, file-less, zero-cost command and control platform. The [agent](https://github.com/mrzaxaryan/Position-Independent-Agent) runs as position-independent shellcode with zero disk footprint, the [relay](https://github.com/mrzaxaryan/relay) runs on cloud with no dedicated server, and this C2 dashboard runs entirely in the browser as a Blazor WebAssembly PWA — no backend required.

## Overview

**C2** connects to the [relay](https://github.com/mrzaxaryan/relay) service over WebSocket and provides a desktop-like interface for live agent monitoring, remote file management, screen capture, and search — all running in the browser with offline support.

### Design Principles

- **File-less agent** — the agent is position-independent shellcode that runs entirely in memory, with no files written to disk, no libraries loaded, and no dependency on libc or CRT
- **Serverless relay** — the relay runs in the cloud with no infrastructure to provision or maintain — free tier is sufficient
- **No backend C2** — this dashboard is a static Blazor WebAssembly app served from any CDN or `file://` — all state lives in the browser (IndexedDB + File System API)
- **Zero cost** — every component runs on free tiers: cloud workers for the relay, static hosting (or local) for the C2, and the agent needs no infrastructure at all

### Features

- **Real-time monitoring** — live agent/relay connection status via WebSocket events
- **Multi-window UI** — draggable, resizable floating windows per agent
- **Remote file browser** — navigate agent file systems through a binary WebSocket protocol
- **Download queue** — queue and track file downloads from agents with progress and auto-resume
- **Upload queue** — upload files to agents with pause/resume support
- **File search** — recursive search across agents with extension group filtering and auto-download
- **Screen capture** — live display enumeration and JPEG frame streaming
- **Remote shell** — interactive command execution on agents
- **Offline cache** — browser FileSystem API + IndexedDB for local file storage
- **Extension groups** — user-defined file type categories for filtering
- **Dark/light theme** — toggle with localStorage persistence
- **Setup wizard** — guided onboarding for first-time configuration
- **PWA** — installable with service worker for offline access

## Architecture

```
+---------------------------+         +---------------------------+
|  Position-Independent     |  <--->  |  relay                    |
|  Agent                    |   WS    |  Serverless cloud         |
|  File-less, in-memory     |         |  WebSocket relay          |
|  shellcode agent          |         |                           |
+---------------------------+         +------------+--------------+
                                                   |
                                                   v
                                      +---------------------------+
                                      |  C2 (this project)        |
                                      |  Blazor WebAssembly       |
                                      |  Static PWA, no backend   |
                                      +---------------------------+
```

| Component | Description | Repo |
|-----------|-------------|------|
| **Agent** | File-less, in-memory shellcode agent — runs with zero disk footprint across platforms | [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) |
| **Relay** | Serverless cloud WebSocket relay — pairs agents with C2 connections 1:1 | [relay](https://github.com/mrzaxaryan/relay) |
| **C2** | This project — static Blazor WebAssembly dashboard with no backend, all state in browser storage | [C2](https://github.com/mrzaxaryan/C2) |

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
│   │   ├── AgentsPanel.razor       # Agent list UI
│   │   ├── AgentInfoPanel.razor    # Agent detail view
│   │   └── AgentStore.cs           # Agent persistence (IndexedDB)
│   ├── Relay/                  # Relay server connectivity
│   │   ├── RelayPanel.razor             # Relay config UI
│   │   ├── RelayConnectionService.cs    # Central orchestrator + event publisher
│   │   ├── RelaySocket.cs              # Binary WebSocket protocol to agents
│   │   ├── RelayStore.cs               # Relay server persistence
│   │   ├── RelayModels.cs              # Agent/relay data models
│   │   └── AgentCommands.cs            # Binary protocol command codes
│   ├── FileSystem/             # Remote file browsing and cache
│   │   ├── FileSystem.razor         # Remote file browser UI
│   │   ├── FilePanel.razor          # File list panel
│   │   ├── FileActions.razor        # File context actions
│   │   ├── FileViewer.razor         # File content viewer
│   │   ├── VfsStore.cs              # Virtual filesystem metadata (IndexedDB)
│   │   ├── FileSystemPath.cs        # Path utilities
│   │   └── DirEntry.cs              # Binary directory response parser
│   ├── Transfers/              # File transfer management
│   │   ├── TransfersPanel.razor     # Transfer queue UI
│   │   ├── TransferService.cs       # Queue processor with auto-resume
│   │   └── TransferStore.cs         # Transfer queue + state persistence
│   ├── Scan/                   # File search across agents
│   │   ├── ScanService.cs          # Recursive search executor
│   │   └── ScanStore.cs            # Search queue + results persistence
│   ├── Screen/                 # Remote screen capture
│   │   ├── ScreenPanel.razor       # Screen capture UI
│   │   ├── ScreenService.cs        # Display enumeration + frame streaming
│   │   └── ScreenModels.cs         # Display and screenshot models
│   ├── Shell/                  # Remote shell
│   │   └── ShellPanel.razor        # Interactive shell UI
│   ├── Extensions/             # File type filtering
│   │   ├── ExtensionGroupsPanel.razor  # Group management UI
│   │   └── ExtensionGroupStore.cs      # Extension group definitions
│   ├── Storage/                # Cache and settings
│   │   ├── SettingsPanel.razor     # Settings and storage config UI
│   │   └── CacheManager.cs        # Browser FileSystem API wrapper
│   ├── Tools/                  # Utility tools
│   │   ├── PeParserPanel.razor     # PE file parser
│   │   ├── Base64Panel.razor       # Base64 encoder/decoder
│   │   └── LnkToolPanel.razor     # LNK file tool
│   ├── Loaders/                # Payload loaders
│   │   └── PythonLoaderPanel.razor # Python loader generator
│   ├── Workspace/              # Desktop window system
│   │   ├── MainLayout.razor        # Menu bar + desktop container
│   │   ├── FloatingWindows.razor   # Draggable/resizable window renderer
│   │   ├── SetupWizard.razor       # Onboarding flow
│   │   ├── WindowManager.cs        # Window state management
│   │   └── WindowState.cs          # Window data model
│   └── Notifications/          # Toast notification system
│       └── NotificationStore.cs
├── Shared/                     # Reusable UI components
│   ├── PanelLayout.razor       # Standard panel layout
│   ├── C2Btn.razor             # Button
│   ├── C2Card.razor            # Card container
│   ├── C2Dialog.razor          # Modal dialog
│   ├── C2Dropdown.razor        # Dropdown menu
│   ├── C2DropdownItem.razor    # Dropdown menu item
│   ├── C2Input.razor           # Text input
│   ├── C2Table.razor           # Table layout
│   ├── C2Tr.razor              # Table row
│   ├── C2Progress.razor        # Progress bar
│   ├── C2Spinner.razor         # Loading spinner
│   ├── C2Icon.razor            # Icon
│   ├── C2Badge.razor           # Badge/label
│   ├── C2ConfirmBtn.razor      # Confirmation button
│   └── C2Enums.cs              # Shared enums (Size, etc.)
├── Infrastructure/             # Cross-cutting services
│   ├── EventBus.cs             # Publish/subscribe event bus
│   ├── EventSubscriber.cs      # Base class for event-aware components
│   ├── Events.cs               # Event definitions
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
        ├── windowManager.js    # Window drag/resize/snap engine
        ├── screen.js           # Screen capture canvas renderer
        ├── interop.js          # C# ↔ JS interop helpers
        ├── filesystem.js       # File System Access API wrapper
        └── indexeddb.js        # IndexedDB wrapper for offline storage
```

## Tech Stack

- .NET 10.0 / Blazor WebAssembly
- Bootstrap 5
- IndexedDB + File System Access API for offline storage
- C# with nullable reference types
