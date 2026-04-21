using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

internal sealed class RustRconClient : IRconClient
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private CancellationTokenSource? _receiveLoopCts;
    private int _identifier;

    public event Action<string>? UnsolicitedMessage;

    public async Task ConnectAsync(Uri uri, string password, CancellationToken cancellationToken = default)
    {
        await _socket.ConnectAsync(uri, cancellationToken);
        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token), _receiveLoopCts.Token);

        // WebRCON auth frame.
        var authPayload = JsonSerializer.Serialize(new { Identifier = -1, Message = password, Name = "WebRcon" });
        await SendRawAsync(authPayload, cancellationToken);
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _identifier);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { Identifier = id, Message = command, Name = "WebRcon" });
        await SendRawAsync(payload, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        using var _ = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

        return await tcs.Task;
    }

    private async Task SendRawAsync(string message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                var segment = new ArraySegment<byte>(buffer);
                result = await _socket.ReceiveAsync(segment, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var message = builder.ToString();
            if (TryResolvePending(message))
                continue;

            UnsolicitedMessage?.Invoke(message);
        }
    }

    private bool TryResolvePending(string rawMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawMessage);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Identifier", out var idNode) || idNode.ValueKind != JsonValueKind.Number)
                return false;

            var id = idNode.GetInt32();
            if (!_pending.TryRemove(id, out var tcs))
                return false;

            var message = root.TryGetProperty("Message", out var messageNode) && messageNode.ValueKind == JsonValueKind.String
                ? messageNode.GetString() ?? string.Empty
                : root.ToString();

            tcs.TrySetResult(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveLoopCts?.Cancel();

        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None);

        _socket.Dispose();
    }
}
