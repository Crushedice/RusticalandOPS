namespace RustOpsAgent.Domains.Rust.Rcon;

internal sealed class PersistentRconSession : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly string _password;
    private readonly RconRollingLogMonitor _monitor = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRconClient? _client;

    public PersistentRconSession(Uri uri, string password)
    {
        _uri = uri;
        _password = password;
    }

    public string ConnectionEndpoint => $"{_uri.Host}:{_uri.Port}";

    /// <summary>
    /// Fires for every unsolicited message received on this RCON connection.
    /// Used by chat monitoring to capture player chat without polling logs.
    /// </summary>
    public event Action<string>? UnsolicitedMessageReceived;

    public bool Matches(Uri uri, string password) =>
        Uri.Compare(_uri, uri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
        string.Equals(_password, password, StringComparison.Ordinal);

    public IReadOnlyList<string> Snapshot() => _monitor.Snapshot();

    /// <summary>True if the WebSocket has been opened and not torn down.</summary>
    public bool IsConnected => _client is not null;

    /// <summary>
    /// Force the session to connect now (instead of waiting for the first command).
    /// Required for chat monitoring — we need the WebSocket open to receive chat events.
    /// Honours the supplied cancellation token so the caller can impose a timeout.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync_NoLock(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync_NoLock(cancellationToken);

            try
            {
                return await _client!.SendCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                RustOpsSentry.AddBreadcrumb(
                    $"Persistent RCON command failed, reconnecting and retrying once. uri={_uri} command={command} error={ex.Message}",
                    "agent.rcon");
                await ResetClientAsync();
                await EnsureConnectedAsync_NoLock(cancellationToken);
                return await _client!.SendCommandAsync(command, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ResetClientAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    // Caller must hold _gate. Renamed (was EnsureConnectedAsync) to make the locking
    // contract explicit — taking the gate while already holding it would deadlock.
    private async Task EnsureConnectedAsync_NoLock(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return;
        }

        var client = new RustRconClient();
        _monitor.Attach(client);
        client.UnsolicitedMessage += ForwardUnsolicited;

        try
        {
            await client.ConnectAsync(_uri, _password, cancellationToken);
            _client = client;
        }
        catch
        {
            _monitor.Detach(client);
            client.UnsolicitedMessage -= ForwardUnsolicited;
            await client.DisposeAsync();
            // No Sentry capture here — connection failures are expected for unreachable
            // remote servers. The caller (WarmupAsync / SendCommandAsync) decides how to
            // surface the error to the operator.
            throw;
        }
    }

    private async Task ResetClientAsync()
    {
        if (_client is null)
        {
            return;
        }

        var client = _client;
        _client = null;
        _monitor.Detach(client);
        client.UnsolicitedMessage -= ForwardUnsolicited;
        await client.DisposeAsync();
    }

    private void ForwardUnsolicited(string message)
    {
        try
        {
            UnsolicitedMessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            // Subscriber faults must not break the receive loop.
            RustOpsSentry.CaptureException(ex, "UnsolicitedMessageReceived subscriber faulted.", "agent.rcon");
        }
    }
}
