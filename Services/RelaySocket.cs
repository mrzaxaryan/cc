using System.Net.WebSockets;
using System.Text;

namespace cc.Services;

public class RelaySocket
{
    private ClientWebSocket? _ws;

    public ClientWebSocket? WebSocket => _ws;
    public bool IsConnected => _ws is not null && _ws.State == WebSocketState.Open;
    public string? ConnectedAgentId { get; private set; }

    public async Task Connect(string agentId, CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"wss://relay.nostdlib.workers.dev/relay/{agentId}"), ct);
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

    // --- Binary protocol command builders ---

    public static byte[] BuildPathCommand(byte cmd, string path)
    {
        var pathBytes = Encoding.Unicode.GetBytes(path + "\0");
        var payload = new byte[1 + pathBytes.Length];
        payload[0] = cmd;
        pathBytes.CopyTo(payload, 1);
        return payload;
    }

    public static byte[] BuildFileCommand(byte cmd, string path, long size, long offset)
    {
        var pathBytes = Encoding.Unicode.GetBytes(path + "\0");
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

    public static string ReadHash(byte[] response) => Convert.ToHexString(response.AsSpan(4, 32));

    public static (ulong bytesRead, byte[] data) ReadFileContent(byte[] response)
    {
        var bytesRead = BitConverter.ToUInt64(response, 4);
        var data = response.AsSpan(12, (int)Math.Min((int)bytesRead, response.Length - 12));
        return (bytesRead, data.ToArray());
    }

    public static ulong ReadEntryCount(byte[] response) => BitConverter.ToUInt64(response, 4);
}
