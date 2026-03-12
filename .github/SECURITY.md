# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in C2, please report it responsibly.

**Do not** open a public issue for security vulnerabilities.

Instead, please email the maintainer directly or use GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) feature on the repository.

### What to Include

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### Response Timeline

- **Acknowledgment**: Within 48 hours
- **Initial assessment**: Within 1 week
- **Fix or mitigation**: Dependent on severity

## Security Considerations

C2 is a command center that manages remote agent connections over WebSocket. Key security areas:

- **WebSocket connections** are encrypted via TLS (`wss://`)
- **Agent pairing** is managed through the relay service with 1:1 Durable Object isolation
- **Browser storage** (IndexedDB, File System Access API) is subject to browser same-origin policy
- **No server-side state** — C2 runs entirely client-side as a Blazor WebAssembly app

## Supported Versions

Only the latest version on the `main` branch receives security updates.
