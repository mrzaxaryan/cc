namespace C2.Features.Relay;

/// <summary>Agent binary protocol command codes.</summary>
public static class AgentCommands
{
    /// <summary>Query agent system info (ping).</summary>
    public const byte SystemInfo = 0x00;

    /// <summary>List directory contents.</summary>
    public const byte ListDirectory = 0x01;

    /// <summary>Read file content (paginated).</summary>
    public const byte ReadFile = 0x02;

    /// <summary>Compute SHA-256 hash of a file.</summary>
    public const byte HashFile = 0x03;

    /// <summary>Write input to agent shell.</summary>
    public const byte WriteShell = 0x04;

    /// <summary>Read output from agent shell.</summary>
    public const byte ReadShell = 0x05;

    /// <summary>Enumerate connected display devices.</summary>
    public const byte GetDisplays = 0x06;

    /// <summary>Capture a JPEG screenshot of a display.</summary>
    public const byte GetScreenshot = 0x07;
}
