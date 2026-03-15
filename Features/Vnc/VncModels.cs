namespace C2.Features.Vnc;

/// <summary>Display device info returned by GetDisplays command.</summary>
public sealed class DisplayInfo
{
    public int Index { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public bool Primary { get; init; }

    public string Label => $"Display {Index + 1}{(Primary ? " (Primary)" : "")} — {Width}x{Height} @{Left},{Top}";
}

/// <summary>A single JPEG section within a screenshot response.</summary>
public readonly record struct ScreenSection(uint X, uint Y, byte[] JpegData);
