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

Fetches from [`https://relay.nostdlib.workers.dev/`](https://relay.nostdlib.workers.dev/) which returns:

```json
{
  "clients": {
    "count": 1,
    "connections": [{ "id": "...", "ip": "...", "location": "...", "connectedAt": 0, "messageCount": 0, "relayed": false }]
  },
  "relays": {
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
├── Program.cs              # App startup and HttpClient registration
├── App.razor               # Root router component
├── Pages/
│   ├── Home.razor          # Welcome page
│   ├── Agents.razor        # Agent monitoring dashboard
│   ├── Command.razor       # Connect to agent via relay, send commands
│   └── NotFound.razor      # 404 page
├── Layout/
│   ├── MainLayout.razor    # Sidebar + content layout
│   └── NavMenu.razor       # Navigation menu
└── wwwroot/                # Static assets (Bootstrap 5, index.html)
```

## Tech Stack

- .NET 10.0 / Blazor WebAssembly
- Bootstrap 5
- C# with nullable reference types
