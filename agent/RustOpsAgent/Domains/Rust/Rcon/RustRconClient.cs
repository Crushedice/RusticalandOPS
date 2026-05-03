using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sentry;

namespace RustOpsAgent.Domains.Rust.Rcon;

/// <summary>
/// Rust WebRCON client. Rust uses WebSocket-based RCON (rcon.web 1), not the
/// Source Engine binary protocol. Authentication happens via the password in the
/// WebSocket URL path: ws://host:port/{password}
/// </summary>
internal sealed class RustRconClient : IRconClient
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _nextId = 10;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public event Action<string>? UnsolicitedMessage;

    public async Task ConnectAsync(Uri uri, string password, CancellationToken cancellationToken)
    {
        // Rust WebRCON authenticates via the password in the URL path: ws://host:port/{password}
        // Build the correct WebSocket URI with password in path if not already present.
        var connectUri = uri;
        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
        {
            connectUri = new Uri($"ws://{uri.Host}:{uri.Port}/{Uri.EscapeDataString(password)}");
        }

        await _socket.ConnectAsync(connectUri, cancellationToken);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);

        RustOpsSentry.AddBreadcrumb($"WebRCON connected to {uri.Host}:{uri.Port}", "agent.rcon");
    }

    public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        return SendInternalAsync(command, id, cancellationToken);
    }

    private async Task<string> SendInternalAsync(string message, int identifier, CancellationToken cancellationToken)
    {
        if (_receiveTask?.IsFaulted == true)
        {
            throw new InvalidOperationException(
                "WebRCON receive loop has failed.",
                _receiveTask.Exception?.InnerException ?? _receiveTask.Exception);
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[identifier] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            Identifier = identifier,
            Message = message,
            Name = "WebRcon"
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(identifier, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (WebSocketException ex)
                {
                    RustOpsSentry.CaptureException(ex, "WebRCON socket error in receive loop.", "agent.rcon");
                    throw;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                frame.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var raw = Encoding.UTF8.GetString(frame.ToArray());
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("Identifier", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt32() : -1;
                    var msg = root.TryGetProperty("Message", out var msgEl) ? msgEl.ToString() : raw;

                    if (id >= 0 && _pending.TryGetValue(id, out var tcs))
                        tcs.TrySetResult(msg);
                    else
                        UnsolicitedMessage?.Invoke(msg);
                }
                catch
                {
                    UnsolicitedMessage?.Invoke(raw);
                }
            }
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "WebRCON receive loop terminated.", "agent.rcon");
            foreach (var kv in _pending)
                kv.Value.TrySetException(ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _pending)
            kv.Value.TrySetCanceled();
        _pending.Clear();

        _receiveCts?.Cancel();

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { /* expected on cancel */ }
        }

        _receiveCts?.Dispose();
        _sendLock.Dispose();

        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); }
            catch { /* best effort */ }
        }

        _socket.Dispose();
    }
}
