# Contributing to C2

Thank you for your interest in contributing to C2. This guide covers everything you need to get started.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or higher
- Git
- A modern browser with WebSocket, IndexedDB, and File System Access API support (Chromium-based recommended)
- IDE: Visual Studio, VS Code (with C# Dev Kit), or JetBrains Rider

## Getting Started

```bash
# Clone the repository
git clone https://github.com/mrzaxaryan/C2.git
cd C2

# Restore dependencies
dotnet restore

# Run with hot reload
dotnet watch
```

The app starts on `http://localhost:5057` (HTTP) or `https://localhost:7104` (HTTPS).

## Project Structure

C2 follows a **feature-folder** layout. Each feature lives under `Features/{FeatureName}/` and contains its own models, services, stores, and Razor components.

```
Features/          Feature modules (Agents, Relay, FileManager, Downloads, Search, etc.)
Infrastructure/    Cross-cutting services (EventBus, ThemeService, Formatters, etc.)
Shared/            Reusable UI components (C2Btn, C2Card, C2Dialog, etc.)
wwwroot/           Static assets, JS interop, CSS, PWA manifest
```

See the [README](README.md) for a detailed file tree.

## Architecture

### Event-Driven Communication

Services and components communicate through an `IEventBus` (publish-subscribe). Components inherit from `EventSubscriber` to automatically refresh the UI when events fire. **Do not** couple components or services directly — always publish events for state changes.

### Store Pattern

Data persistence uses IndexedDB via JS interop. Every store follows this pattern:

1. **Load on demand** with `LoadAsync()` (idempotent, loads once)
2. **Cache in-memory** using a dictionary
3. **Persist** via JS interop (`window.c2XxxDb.put/get/remove`)
4. **Publish** a change event on every mutation

### Service Lifetimes

- **Singleton** — App-wide shared state: `IEventBus`, `MessageService`, `WindowManager`
- **Scoped** — Per-request (shared in component tree): stores, connection services, background processors

Register new services in [Program.cs](Program.cs).

## Code Style

### C# Conventions

- **Nullable reference types** are enabled — use `string?` for optional values, `string` for required
- **File-scoped namespaces** — `namespace C2.Features.Relay;`
- **Naming**: PascalCase for public members, `_camelCase` for private fields, `Async` suffix for async methods
- **XML doc comments** on public types and members
- **Record types** for events and immutable data

### Razor Components

- Inherit `EventSubscriber` for reactive updates
- Use `[Parameter]` for component inputs, `[Inject]` for DI
- Use shared `C2*` components (`C2Btn`, `C2Card`, `C2Input`, etc.) instead of raw HTML
- Create scoped CSS with `.razor.css` files alongside components

### Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: Add new feature description
fix: Correct specific bug description
refactor: Restructure code without behavior change
style: Formatting or branding changes
docs: Documentation updates
```

## Adding a New Feature

1. Create a folder under `Features/{FeatureName}/`
2. Define models and records
3. Create a store (if persistent data is needed) following the store pattern above
4. Create services for business logic
5. Register services in [Program.cs](Program.cs)
6. Create Razor components inheriting `EventSubscriber`
7. Add JS interop functions in `wwwroot/js/` if browser APIs are needed

## Building for Production

```bash
dotnet publish -c Release -o output
```

Or use the included build script:

```bash
./build.sh
```

## Code Review Checklist

- [ ] Nullable reference types respected (no `null` where non-nullable is expected)
- [ ] Events published for UI updates (no direct component coupling)
- [ ] Services registered in DI with correct lifetime
- [ ] `async`/`await` used consistently with `CancellationToken` propagation
- [ ] IndexedDB calls wrapped in try-catch
- [ ] Shared components used instead of duplicating markup
- [ ] Offline scenarios handled gracefully (agents may disconnect at any time)

## Related Repositories

| Component | Description | Repository |
|-----------|-------------|------------|
| **PIR** | C++23 position-independent runtime | [Position-Independent-Runtime](https://github.com/mrzaxaryan/Position-Independent-Runtime) |
| **Agent** | Cross-platform remote agent built on PIR | [Position-Independent-Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) |
| **Relay** | WebSocket relay on Cloudflare Workers | [relay](https://github.com/mrzaxaryan/relay) |

## Questions?

Open an issue on the [GitHub repository](https://github.com/mrzaxaryan/C2/issues).
