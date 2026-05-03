using System.Collections.Concurrent;
using Sentry;

namespace RustOpsAgent.Domains.Rust.Rcon;

/// <summary>
/// Manages persistent RCON sessions for local and remote Rust servers.
/// Reuses connections instead of creating new ones for each command.
/// </summary>
internal sealed class RconSessionPool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PersistentRconSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public RconSessionPool()
    {
    }

    /// <summary>
    /// Register a server's RCON connection in the pool.
    /// </summary>
    public void RegisterServer(string serverName, Uri rconUri, string rconPassword)
    {
        if (string.IsNullOrWhiteSpace(rconPassword))
        {
            RustOpsSentry.AddBreadcrumb($"Cannot register server '{serverName}': RCON password is empty", "rcon");
            return;
        }

        // Remove old session if exists
        if (_sessions.TryRemove(serverName, out var oldSession))
        {
            RustOpsSentry.AddBreadcrumb($"Replacing existing RCON session for '{serverName}'", "rcon");
            _ = oldSession.DisposeAsync();
        }

        var session = new PersistentRconSession(rconUri, rconPassword);
        _sessions[serverName] = session;
        RustOpsSentry.AddBreadcrumb($"Registered RCON session for '{serverName}' at {rconUri}", "rcon");
    }

    /// <summary>
    /// Initialize persistent sessions for all known servers.
    /// Called on agent startup.
    /// </summary>
    public async Task InitializeAsync(IReadOnlyList<(string Name, Uri RconUri, string Password)> servers)
    {
        RustOpsSentry.AddBreadcrumb($"Initializing persistent RCON sessions for {servers.Count} servers", "rcon");

        foreach (var (name, uri, password) in servers)
        {
            RegisterServer(name, uri, password);
        }

        // Warm up connections (optional - can be done on first use)
        var warmupTasks = _sessions.Values
            .Select(s => WarmupSessionAsync(s))
            .ToList();

        if (warmupTasks.Count > 0)
        {
            await Task.WhenAll(warmupTasks);
        }
    }

    private async Task WarmupSessionAsync(PersistentRconSession session)
    {
        try
        {
            // Establish connection early so it's ready
            await session.SendCommandAsync("serverinfo", CancellationToken.None);
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "Failed to warm up RCON session", "rcon");
            // Don't fail init if warmup fails - it will reconnect on first real command
        }
    }

    /// <summary>
    /// Get or create a session for a server.
    /// </summary>
    public PersistentRconSession? GetSession(string serverName)
    {
        return _sessions.TryGetValue(serverName, out var session) ? session : null;
    }

    /// <summary>
    /// Get all registered servers.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredServers()
    {
        return _sessions.Keys.ToList();
    }

    /// <summary>
    /// Dispose all sessions.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        RustOpsSentry.AddBreadcrumb($"Disposing {_sessions.Count} RCON sessions", "rcon");

        var disposeTasks = _sessions.Values.Select(s => s.DisposeAsync().AsTask()).ToList();
        if (disposeTasks.Count > 0)
        {
            await Task.WhenAll(disposeTasks);
        }
        _sessions.Clear();
    }
}
