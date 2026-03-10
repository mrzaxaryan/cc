# cc

Real-time dashboard for monitoring [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) connections. Built with Blazor WebAssembly (.NET 10).

## Overview

**cc** is a command center that connects to the [relay](https://github.com/mrzaxaryan/relay) service and displays live status of connected agents and relay pairings. Data auto-refreshes every 5 seconds.

### Features

- Live client and relay connection counts
- Client details: ID, IP, location, connection time, message count, relay status
- Relay details: ID, paired client ID, connection time
- Auto-refresh with loading and error states

## Architecture

```
+---------------------------+         +---------------------------+
|  Position-Independent     |         |                           |
|  Runtime (PIR)            |         |  cc (this project)        |
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
| **PIR** | C++23 position-independent runtime -- cryptography, networking, TLS 1.3, all without libc or CRT | [Position-Independent-Runtime](https://github.com/mrzaxaryan/Position-Independent-Runtime) |
| **Agent** | Cross-platform remote agent built on PIR -- file system, hashing, binary command protocol over WebSocket | [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) |
| **Relay** | WebSocket relay on Cloudflare Workers -- pairs agents with relay connections 1:1 via Durable Objects | [relay](https://github.com/mrzaxaryan/relay) |
| **cc** | This project -- Blazor WebAssembly dashboard that polls the relay for live connection data | [cc](https://github.com/mrzaxaryan/cc) |

## Data Source

Fetches from [`https://relay.nostdlib.workers.dev/status`](https://relay.nostdlib.workers.dev/status) which returns:

```json
{
  "agents": {
    "count": 1,
    "connections": [{ "id": "...", "connectedAt": 0, "paired": false, "pairedRelayId": null, "messagesForwarded": 0, "lastActiveAt": 0, "ip": "...", "country": "...", "city": "..." }]
  },
  "relays": {
    "count": 0,
    "connections": []
  },
  "eventListeners": {
    "count": 0,
    "connections": []
  }
}
```

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
│   │   └── AgentStore.cs       # Agent persistence
│   ├── Relay/                  # Relay server connectivity
│   │   ├── RelayPanel.razor    # Relay config UI
│   │   ├── RelayConnectionService.cs  # Central orchestrator
│   │   ├── RelaySocket.cs      # Binary WebSocket protocol
│   │   ├── RelayStore.cs       # Relay server persistence
│   │   └── RelayModels.cs      # Agent/relay data models
│   ├── FileManager/            # Remote file browsing, downloads, cache
│   │   ├── FileManager.razor   # Remote file browser
│   │   ├── SyncPanel.razor     # Upload queue
│   │   ├── DownloadManagerPanel.razor  # Download progress
│   │   ├── CacheManagerPanel.razor     # Storage setup
│   │   ├── DownloadStore.cs    # Download queue + state
│   │   ├── VfsStore.cs         # Virtual filesystem metadata
│   │   ├── CacheManager.cs     # Browser FileSystem API
│   │   └── DirEntry.cs         # Binary directory parser
│   ├── Search/                 # File search across agents
│   │   ├── SearchGlobalPanel.razor   # Global search UI
│   │   ├── SearchConfigPanel.razor   # Per-agent search
│   │   └── SearchStore.cs      # Search queue + state
│   ├── Extensions/             # File type filtering
│   │   ├── ExtensionGroupPanel.razor  # Group management UI
│   │   └── ExtensionGroupStore.cs     # Extension groups
│   ├── Shell/                  # Desktop window system
│   │   ├── FloatingWindows.razor      # Multi-window shell
│   │   ├── SetupWizard.razor   # Onboarding flow
│   │   ├── MainLayout.razor    # Menu bar + content layout
│   │   ├── WindowManager.cs    # Window state management
│   │   └── WindowState.cs      # Window data model
│   └── Notifications/          # Toast + notification history
│       └── NotificationStore.cs
├── Shared/                     # Reusable UI components (CcBtn, CcCard, ...)
├── Infrastructure/             # Cross-cutting services
│   ├── ThemeService.cs         # Dark/light theme
│   ├── LocalStorageService.cs  # Browser localStorage
│   ├── MessageService.cs       # Toast pub/sub
│   ├── ServiceStateStore.cs    # Pause/resume states
│   ├── CtsManager.cs           # Cancellation tokens
│   └── Formatters.cs           # Display utilities
└── wwwroot/                    # Static assets (CSS, index.html)
```

## Tech Stack

- .NET 10.0 / Blazor WebAssembly
- Bootstrap 5
- C# with nullable reference types
