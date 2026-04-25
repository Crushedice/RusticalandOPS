using System.Text.Json;
using rustmgrapi.Api.Models;

namespace rustmgrapi.Api.Services;

internal sealed class RustManagerService
{
    private readonly RustMgrExecutor _executor;
    private readonly RuntimePaths _paths;

    public RustManagerService(RustMgrExecutor executor, RuntimePaths paths)
    {
        _executor = executor;
        _paths = paths;
    }

    public async Task<List<string>> ListServersAsync()
    {
        var result = await _executor.ExecuteAsync("list");
        if (!result.Ok && string.IsNullOrWhiteSpace(result.StdOut))
        {
            return new List<string>();
        }

        return (result.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsKnownServer(string server)
    {
        var path = Path.Combine(_paths.ConfigDir, $"{server}.json");
        return File.Exists(path);
    }

    public ServerConfig? LoadConfig(string server)
    {
        var path = Path.Combine(_paths.ConfigDir, $"{server}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
    }

    public async Task<object> GetStatusAsync(string server)
    {
        var status = await _executor.GetStatusAsync(server);
        return new
        {
            name = server,
            state = status?.State ?? "unknown",
            online = status?.Online ?? false,
            pid = status?.Pid,
            autoRestart = status?.AutoRestart ?? false,
            session = status?.Session ?? false,
            raw = status?.Raw ?? string.Empty
        };
    }

    public async Task<object> LifecycleAsync(string server, string operation)
    {
        var result = await _executor.ExecuteLifecycleAsync(server, operation);
        return new
        {
            ok = result.Ok,
            operation,
            server,
            exitCode = result.ExitCode,
            stdout = result.StdOut,
            stderr = result.StdErr,
            message = result.Message
        };
    }

    public async Task<object> KillAsync(string server)
    {
        var result = await _executor.ExecuteAsync("kill", server);
        return new { ok = result.Ok, server, operation = "kill", result.ExitCode, result.StdOut, result.StdErr };
    }

    public async Task<object> UpdateAsync(string server)
    {
        var result = await _executor.ExecuteAsync("update", server);
        return new { ok = result.Ok, server, operation = "update", result.ExitCode, result.StdOut, result.StdErr };
    }

    public async Task<object> ReadLogsTailAsync(string server, int lines)
    {
        var result = await _executor.ExecuteAsync("logs", server);
        var all = (result.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(Math.Clamp(lines, 1, 1000))
            .ToArray();

        return new
        {
            server,
            count = all.Length,
            lines = all
        };
    }

    public async Task<object> ReadHealthAsync(string server)
    {
        var status = await _executor.GetStatusAsync(server);
        var logs = await _executor.ExecuteAsync("logs", server);

        var recentErrors = (logs.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(160)
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .TakeLast(20)
            .ToArray();

        return new
        {
            server,
            state = status?.State ?? "unknown",
            online = status?.Online ?? false,
            pid = status?.Pid,
            recentErrors
        };
    }
}
