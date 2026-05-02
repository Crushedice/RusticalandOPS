using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sentry;

namespace RustOpsAgent.Domains.Rust.Rcon;

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
        await _socket.ConnectAsync(uri, cancellationToken);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

        // Rust WebRCON commonly authenticates via ws://host:port/{password}.
        // If no password path is present, fall back to explicit auth message.
        var usesPathAuth = !string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/";
        if (usesPathAuth || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var authReply = await SendInternalAsync(password, identifier: 0, cancellationToken);
        if (!authReply.Contains("auth", StringComparison.OrdinalIgnoreCase) &&
            !authReply.Contains("true", StringComparison.OrdinalIgnoreCase) &&
            !authReply.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RCON authentication did not return a success indicator.");
        }
    }

    public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        return SendInternalAsync(command, id, cancellationToken);
    }

    private async Task<string> SendInternalAsync(string message, int identifier, CancellationToken cancellationToken)
    {
        // Check if the receive loop has failed; if so, propagate the exception instead of timing out.
        if (_receiveTask?.IsFaulted == true)
        {
            throw new InvalidOperationException(
                "RCON receive loop has failed. Cannot process command.",
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
        using var registration = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(identifier, out var _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var frameStream = new MemoryStream();
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (WebSocketException ex)
                {
                    RustOpsSentry.CaptureException(
                        ex,
                        "WebSocket error during RCON receive loop.",
                        "agent.rcon",
                        extras: new Dictionary<string, object?> { ["socketState"] = _socket.State.ToString() });
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is expected during shutdown or timeout
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                frameStream.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(frameStream.ToArray());
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("Identifier", out var idNode) && idNode.ValueKind == JsonValueKind.Number
                        ? idNode.GetInt32()
                        : -1;
                    var message = root.TryGetProperty("Message", out var messageNode)
                        ? messageNode.ToString()
                        : payload;

                    if (id >= 0 && _pending.TryGetValue(id, out var tcs))
                    {
                        tcs.TrySetResult(message);
                    }
                    else
                    {
                        UnsolicitedMessage?.Invoke(message);
                    }
                }
                catch (Exception ex)
                {
                    RustOpsSentry.CaptureMessage(
                        "Failed to parse RCON payload as JSON. Forwarding raw message as unsolicited output.",
                        "agent.rcon",
                        SentryLevel.Warning,
                        extras: new Dictionary<string, object?>
                        {
                            ["payloadPreview"] = payload.Length > 500 ? payload[..500] : payload,
                            ["exception"] = ex.Message
                        });
                    UnsolicitedMessage?.Invoke(payload);
                }
            }
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(
                ex,
                "RCON receive loop terminated with exception.",
                "agent.rcon");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel all in-flight commands immediately rather than waiting for their 10s timeouts.
        foreach (var kv in _pending)
        {
            kv.Value.TrySetCanceled();
        }
        _pending.Clear();

        try
        {
            _receiveCts?.Cancel();
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "Failed to cancel RCON receive loop.", "agent.rcon");
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureMessage(
                    "RCON receive loop ended with an exception during dispose.",
                    "agent.rcon",
                    SentryLevel.Warning,
                    extras: new Dictionary<string, object?> { ["exception"] = ex.Message });
            }
        }

        _sendLock.Dispose();
        _receiveCts?.Dispose();

        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureMessage(
                    "RCON websocket close failed during dispose.",
                    "agent.rcon",
                    SentryLevel.Warning,
                    extras: new Dictionary<string, object?> { ["exception"] = ex.Message });
            }
        }

        _socket.Dispose();
    }
}
