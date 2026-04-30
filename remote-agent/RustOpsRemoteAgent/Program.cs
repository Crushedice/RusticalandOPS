using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoreRCON;
using Microsoft.AspNetCore.Http.Json;

RustOpsEnv.LoadFromDefaultLocations();
using var sentry = RustOpsSentry.Initialize("rustops-remote-agent");

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.Configure<JsonOptions>(options =>
    {
        options.SerializerOptions.WriteIndented = true;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

    var bindUrl = Environment.GetEnvironmentVariable("RUSTOPS_REMOTE_AGENT_BIND") ?? "http://0.0.0.0:2088";
    var apiKey = RustOpsEnv.FirstNonEmptyEnvironment(
        "RUSTOPS_REMOTE_AGENT_API_KEY",
        "RUSTMGR_API_KEY",
        "RUSTOPS_API_KEY") ?? "changeme";
    var rustMgrPath = Environment.GetEnvironmentVariable("RUSTMGR_PATH") ?? "/opt/rust-manager/rustmgr.sh";
    var configDir = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config";
    var runtimeDir = Environment.GetEnvironmentVariable("RUSTMGR_RUNTIME") ?? "/opt/rust-manager/runtime";
    var tasksDir = Environment.GetEnvironmentVariable("RUSTMGR_TASKS_DIR") ?? "/opt/rust-manager/tasks";

    Directory.CreateDirectory(configDir);
    Directory.CreateDirectory(runtimeDir);
    Directory.CreateDirectory(tasksDir);

    var executor = new RustMgrExecutor(rustMgrPath);
    var app = builder.Build();
    app.Urls.Clear();
    app.Urls.Add(bindUrl);

    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path == "/")
        {
            await next();
            return;
        }

        var supplied = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.Equals(supplied, apiKey, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ApiError("unauthorized", "Invalid remote agent API key."));
            return;
        }

        await next();
    });

    app.MapGet("/", () => Results.Redirect("/health"));

    app.MapGet("/health", () => Results.Ok(new
    {
        ok = true,
        utc = DateTime.UtcNow,
        service = "rustops-remote-agent",
        bindUrl,
        rustMgrPath,
        configDir,
        runtimeDir,
        tasksDir,
        capabilities = new[]
        {
            "server-list",
            "server-status",
            "server-lifecycle",
            "server-config",
            "server-logs",
            "server-events",
            "rcon-command",
            "rcon-query",
            "moderation"
        }
    }));

    app.MapGet("/servers", async () =>
    {
        var servers = await ListServersAsync();
        return Results.Ok(servers.Select(name => new
        {
            name,
            configExists = File.Exists(GetConfigPath(name)),
            remoteAgent = true
        }));
    });

    app.MapGet("/servers/summary", async () =>
    {
        var servers = await ListServersAsync();
        var statuses = await Task.WhenAll(servers.Select(GetStatusObjectAsync));
        return Results.Ok(new { count = statuses.Length, servers = statuses });
    });

    app.MapGet("/servers/{server}/status", async (string server) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        return Results.Ok(await GetStatusObjectAsync(server));
    });

    app.MapGet("/servers/{server}/health", async (string server) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var status = await executor.GetStatusAsync(server);
        var logs = await executor.ExecuteAsync("logs", server);
        var recentErrors = (logs.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(200)
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("crash", StringComparison.OrdinalIgnoreCase))
            .TakeLast(20)
            .ToArray();

        return Results.Ok(new
        {
            name = server,
            status,
            recentErrors,
            remoteAgent = true,
            checkedAt = DateTime.UtcNow
        });
    });

    foreach (var operation in new[] { "start", "stop", "restart", "kill", "update", "umod", "sync-config", "wipe" })
    {
        app.MapPost($"/servers/{{server}}/{operation}", async (string server) =>
        {
            if (!await IsKnownServerAsync(server))
                return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

            var result = operation is "start" or "stop" or "restart"
                ? await executor.ExecuteLifecycleAsync(server, operation)
                : await executor.ExecuteAsync(operation, server);

            return ToCommandResult(server, operation, result);
        });
    }

    app.MapGet("/servers/{server}/config", async (string server) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var cfg = LoadServerConfig(server);
        return cfg is null
            ? Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."))
            : Results.Ok(cfg);
    });

    app.MapPut("/servers/{server}/config", async (string server, ServerConfig config) =>
    {
        if (!await IsKnownServerAsync(server) && File.Exists(GetConfigPath(server)))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        config.Name = server;
        var error = ValidateConfig(config);
        if (error is not null)
            return Results.BadRequest(new ApiError("invalid_config", error));

        File.WriteAllText(GetConfigPath(server), JsonSerializer.Serialize(config, RemoteAgentJson.Options));
        return Results.Ok(new { ok = true, server, restartRequired = true, config });
    });

    app.MapPost("/servers/{server}/config/validate", (string server, ServerConfig config) =>
    {
        config.Name = server;
        var error = ValidateConfig(config);
        return Results.Ok(new
        {
            valid = error is null,
            errors = error is null ? Array.Empty<string>() : new[] { error },
            normalized = config
        });
    });

    app.MapGet("/servers/{server}/console", async (string server, int? lines) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var result = await executor.ExecuteAsync("logs", server);
        if (!result.Ok)
            return Results.BadRequest(result);

        var count = Math.Clamp(lines ?? 120, 1, 1000);
        return Results.Ok(new { server, lines = count, content = TailLines(result.StdOut ?? string.Empty, count) });
    });

    app.MapGet("/servers/{server}/logs/tail", async (string server, int? lines, string? since, int? offset) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var result = await executor.ExecuteAsync("logs", server);
        if (!result.Ok)
            return Results.BadRequest(result);

        var count = Math.Clamp(lines ?? 200, 1, 1000);
        var entries = TailLines(result.StdOut ?? string.Empty, count)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((line, index) => new LogEntry(null, "log", line, index))
            .ToList();

        var skip = Math.Max(0, offset ?? 0);
        var total = entries.Count;
        if (skip > 0)
            entries = entries.Skip(skip).ToList();

        return Results.Ok(new { server, total, offset = skip, count = entries.Count, entries });
    });

    app.MapGet("/servers/{server}/logs/read", async (string server, long? offset, int? maxBytes) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var cfg = LoadServerConfig(server);
        if (cfg is null)
            return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

        var path = GetServerLogPath(cfg);
        var slice = ReadTextSlice(path, offset ?? 0, Math.Clamp(maxBytes ?? 64 * 1024, 1024, 512 * 1024));
        return Results.Ok(new
        {
            server,
            path,
            exists = slice.Exists,
            startOffset = slice.StartOffset,
            endOffset = slice.EndOffset,
            truncated = slice.Truncated,
            reset = slice.Reset,
            content = slice.Content
        });
    });

    app.MapGet("/servers/{server}/commands", async (string server, int? lines) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var count = Math.Clamp(lines ?? 80, 1, 1000);
        var result = await executor.ExecuteAsync("commands", server, count.ToString());
        return result.Ok
            ? Results.Ok(new { server, lines = count, content = result.StdOut ?? string.Empty })
            : Results.BadRequest(result);
    });

    app.MapGet("/servers/{server}/events", async (string server, int? lines) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var count = Math.Clamp(lines ?? 100, 1, 1000);
        var path = Path.Combine(runtimeDir, $"{server}.commands.log");
        if (!File.Exists(path))
            return Results.Ok(new { server, count = 0, events = Array.Empty<object>() });

        var events = File.ReadLines(path)
            .TakeLast(count)
            .Select(ParseTraceEvent)
            .Where(e => e is not null)
            .ToArray();

        return Results.Ok(new { server, count = events.Length, events });
    });

    app.MapPost("/servers/{server}/command", async (string server, ServerCommandRequest request) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var commandError = ValidateCommand(request.Command);
        if (commandError is not null)
            return Results.BadRequest(new ApiError("invalid_request", commandError));

        return await ExecuteRconCommandAsync(server, request.Command.Trim(), includeTransport: true);
    });

    app.MapPost("/servers/{server}/command/exec", async (string server, ServerCommandExecRequest request) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var commandError = ValidateCommand(request.Command);
        if (commandError is not null)
            return Results.BadRequest(new ApiError("invalid_request", commandError));

        var result = await SendRconAsync(server, request.Command.Trim());
        return Results.Ok(new
        {
            ok = true,
            server,
            command = request.Command.Trim(),
            waitMs = request.WaitMs,
            transport = "remote-agent-rcon-web",
            directReply = string.IsNullOrWhiteSpace(result.Reply) ? null : result.Reply.Trim(),
            output = Array.Empty<object>(),
            endpoint = new { result.Host, result.Port }
        });
    });

    app.MapGet("/servers/{server}/serverinfo", async (string server) => await QueryJsonRconAsync(server, "serverinfo"));
    app.MapGet("/servers/{server}/players", async (string server) => await QueryJsonRconAsync(server, "playerlist"));
    app.MapGet("/servers/{server}/bans", async (string server) => await QueryJsonRconAsync(server, "bans"));

    app.MapPost("/servers/{server}/kick", async (string server, ModerationRequest request) =>
        await ExecuteModerationAsync(server, $"kick {request.SteamId} \"{EscapeReason(request.Reason)}\""));
    app.MapPost("/servers/{server}/ban", async (string server, ModerationRequest request) =>
        await ExecuteModerationAsync(server, $"ban {request.SteamId} \"{EscapeReason(request.Reason)}\""));
    app.MapPost("/servers/{server}/unban", async (string server, ModerationRequest request) =>
        await ExecuteModerationAsync(server, $"unban {request.SteamId}"));

    await app.RunAsync();

    async Task<List<string>> ListServersAsync()
    {
        var result = await executor.ExecuteAsync("list");
        return (result.StdOut ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task<bool> IsKnownServerAsync(string server)
    {
        if (File.Exists(GetConfigPath(server)))
            return true;

        var servers = await ListServersAsync();
        return servers.Contains(server, StringComparer.OrdinalIgnoreCase);
    }

    async Task<object> GetStatusObjectAsync(string server)
    {
        var status = await executor.GetStatusAsync(server);
        return new
        {
            name = server,
            state = status?.State ?? "unknown",
            online = status?.Online ?? false,
            pid = status?.Pid,
            autoRestart = status?.AutoRestart ?? false,
            session = status?.Session ?? false,
            raw = status?.Raw ?? string.Empty,
            remoteAgent = true
        };
    }

    string GetConfigPath(string server) => Path.Combine(configDir, $"{server}.json");

    ServerConfig? LoadServerConfig(string server)
    {
        var path = GetConfigPath(server);
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), RemoteAgentJson.Options);
    }

    string? ValidateConfig(ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "name is required.";
        if (config.ServerPort is < 1 or > 65535)
            return "server.port must be between 1 and 65535.";
        if (config.RconPort is < 1 or > 65535)
            return "rcon.port must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(config.RconPassword))
            return "rcon.password is required.";
        if (string.IsNullOrWhiteSpace(config.ServerDir))
            return "serverDir is required.";
        return null;
    }

    string GetServerLogPath(ServerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.LogFile) && Path.IsPathRooted(config.LogFile))
            return config.LogFile;

        var fileName = string.IsNullOrWhiteSpace(config.LogFile) ? "Log.txt" : config.LogFile;
        return Path.Combine(config.ServerDir, fileName);
    }

    async Task<IResult> ExecuteRconCommandAsync(string server, string command, bool includeTransport)
    {
        try
        {
            var result = await SendRconAsync(server, command);
            return Results.Ok(new
            {
                ok = true,
                server,
                command,
                transport = includeTransport ? "remote-agent-rcon-web" : null,
                endpoint = new { result.Host, result.Port },
                reply = string.IsNullOrWhiteSpace(result.Reply) ? null : result.Reply.Trim()
            });
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "Remote agent RCON command failed.", "remote-agent.rcon");
            return Results.BadRequest(new ApiError("rcon_error", ex.Message));
        }
    }

    async Task<IResult> QueryJsonRconAsync(string server, string command)
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var commandResult = await SendRconAsync(server, command);
        var payload = TryExtractJson(commandResult.Reply);
        return payload is null
            ? Results.BadRequest(new ApiError("parse_error", $"Could not parse {command} response."))
            : Results.Content(payload, "application/json");
    }

    async Task<IResult> ExecuteModerationAsync(string server, string command)
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        return await ExecuteRconCommandAsync(server, command, includeTransport: true);
    }

    async Task<RconCommandResult> SendRconAsync(string server, string command)
    {
        var cfg = LoadServerConfig(server) ?? throw new InvalidOperationException($"No config found for '{server}'.");
        var endpoint = ResolveRconConnectionInfo(server, cfg);
        await using var rcon = new RustRcon(endpoint.Host, endpoint.Port, endpoint.Password);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);
        return new RconCommandResult(endpoint.Host, endpoint.Port, reply);
    }

    RconConnectionInfo ResolveRconConnectionInfo(string server, ServerConfig config)
    {
        var host = ReadRawConfigValue(server, "rcon.ip") ??
                   ReadArgValue(config.AdditionalArgs, "rcon.ip") ??
                   "127.0.0.1";
        var password = config.RconPassword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException($"rcon.password is empty for '{server}'.");

        return new RconConnectionInfo(host.Trim().Trim('"'), (ushort)Math.Clamp(config.RconPort, 1, 65535), password);
    }

    string? ReadRawConfigValue(string server, string key)
    {
        var path = GetConfigPath(server);
        if (!File.Exists(path))
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    }
}
catch (Exception ex)
{
    RustOpsSentry.CaptureException(ex, "rustops-remote-agent terminated unexpectedly.", "runtime");
    throw;
}
finally
{
    await RustOpsSentry.FlushAsync();
}

static IResult ToCommandResult(string server, string operation, CommandExecutionResult result)
{
    var body = new
    {
        ok = result.Ok,
        operation,
        server,
        exitCode = result.ExitCode,
        stdout = result.StdOut,
        stderr = result.StdErr,
        message = result.Message,
        timedOut = result.TimedOut,
        remoteAgent = true
    };

    return result.Ok ? Results.Ok(body) : Results.BadRequest(body);
}

static string? ValidateCommand(string? command)
{
    if (string.IsNullOrWhiteSpace(command))
        return "Command is required.";
    if (command.Trim().Length > 256)
        return "Command length exceeds 256 characters.";
    if (command.Contains('\n') || command.Contains('\r'))
        return "Command must be a single line.";
    return null;
}

static string TailLines(string text, int lines)
{
    if (string.IsNullOrWhiteSpace(text))
        return string.Empty;

    return string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').TakeLast(lines));
}

static string? TryExtractJson(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return null;

    var trimmed = text.Trim();
    var firstObject = trimmed.IndexOf('{');
    var firstArray = trimmed.IndexOf('[');
    if (firstObject < 0 && firstArray < 0)
        return null;

    var start = firstObject < 0 ? firstArray : firstArray < 0 ? firstObject : Math.Min(firstObject, firstArray);
    var candidate = trimmed[start..].Trim();
    try
    {
        using var _ = JsonDocument.Parse(candidate);
        return candidate;
    }
    catch
    {
        return null;
    }
}

static string? ReadArgValue(string? additionalArgs, string key)
{
    if (string.IsNullOrWhiteSpace(additionalArgs))
        return null;

    var pattern = $@"(?:^|\s)\+{Regex.Escape(key)}\s+(?:""(?<v>[^""]+)""|(?<v>\S+))";
    var match = Regex.Match(additionalArgs, pattern, RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["v"].Value : null;
}

static object? ParseTraceEvent(string line)
{
    if (string.IsNullOrWhiteSpace(line))
        return null;

    DateTime? timestamp = null;
    var body = line.Trim();
    if (body.StartsWith('['))
    {
        var close = body.IndexOf(']');
        if (close > 1 && DateTime.TryParse(body[1..close], out var parsed))
        {
            timestamp = parsed.ToUniversalTime();
            body = body[(close + 1)..].Trim();
        }
    }

    var colon = body.IndexOf(':');
    return new
    {
        timestamp,
        kind = colon > 0 ? body[..colon].Trim() : "event",
        detail = colon > 0 ? body[(colon + 1)..].Trim() : body
    };
}

static TextSlice ReadTextSlice(string path, long offset, int maxBytes)
{
    if (!File.Exists(path))
        return new TextSlice(false, 0, 0, false, false, string.Empty);

    var length = new FileInfo(path).Length;
    var start = Math.Clamp(offset, 0, length);
    var reset = offset > length;
    var readBytes = (int)Math.Min(maxBytes, length - start);
    var buffer = new byte[readBytes];
    using var stream = File.OpenRead(path);
    stream.Seek(start, SeekOrigin.Begin);
    var bytesRead = stream.Read(buffer, 0, readBytes);
    return new TextSlice(
        true,
        start,
        start + bytesRead,
        start + bytesRead < length,
        reset,
        System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
}

static string EscapeReason(string? reason) =>
    string.IsNullOrWhiteSpace(reason)
        ? string.Empty
        : reason.Replace("\\", "\\\\").Replace("\"", "\\\"");

internal sealed record ApiError(string Code, string Message);
internal sealed record RconConnectionInfo(string Host, ushort Port, string Password);
internal sealed record RconCommandResult(string Host, ushort Port, string Reply);
internal sealed record LogEntry(DateTime? Timestamp, string Level, string Message, int Index);
internal sealed record TextSlice(bool Exists, long StartOffset, long EndOffset, bool Truncated, bool Reset, string Content);

internal sealed class ServerCommandRequest
{
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
}

internal sealed class ServerCommandExecRequest
{
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
    [JsonPropertyName("waitMs")] public int WaitMs { get; set; } = 2500;
    [JsonPropertyName("maxBytes")] public int MaxBytes { get; set; } = 128 * 1024;
    [JsonPropertyName("maxLines")] public int MaxLines { get; set; } = 120;
}

internal sealed class ModerationRequest
{
    [JsonPropertyName("steamId")] public string SteamId { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class ServerConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("server.hostname")] public string ServerHostname { get; set; } = string.Empty;
    [JsonPropertyName("server.description")] public string ServerDescription { get; set; } = string.Empty;
    [JsonPropertyName("server.url")] public string ServerUrl { get; set; } = string.Empty;
    [JsonPropertyName("server.logoimage")] public string ServerLogoImage { get; set; } = string.Empty;
    [JsonPropertyName("server.headerimage")] public string ServerHeaderImage { get; set; } = string.Empty;
    [JsonPropertyName("server.tags")] public string ServerTags { get; set; } = string.Empty;
    [JsonPropertyName("server.identity")] public string ServerIdentity { get; set; } = string.Empty;
    [JsonPropertyName("server.port")] public int ServerPort { get; set; }
    [JsonPropertyName("rcon.port")] public int RconPort { get; set; }
    [JsonPropertyName("app.port")] public int AppPort { get; set; }
    [JsonPropertyName("server.worldsize")] public int ServerWorldSize { get; set; }
    [JsonPropertyName("server.seed")] public int ServerSeed { get; set; }
    [JsonPropertyName("server.maxplayers")] public int ServerMaxPlayers { get; set; }
    [JsonPropertyName("server.level")] public string ServerLevel { get; set; } = "Procedural Map";
    [JsonPropertyName("server.levelurl")] public string ServerLevelUrl { get; set; } = string.Empty;
    [JsonPropertyName("rcon.password")] public string RconPassword { get; set; } = string.Empty;
    [JsonPropertyName("server.reportsserverendpoint")] public string ServerReportsServerEndpoint { get; set; } = string.Empty;
    [JsonPropertyName("logFile")] public string LogFile { get; set; } = "Log.txt";
    [JsonPropertyName("server.encryption")] public string ServerEncryption { get; set; } = string.Empty;
    [JsonPropertyName("boombox.serverurllist")] public string BoomboxServerUrlList { get; set; } = string.Empty;
    [JsonPropertyName("additionalArgs")] public string AdditionalArgs { get; set; } = string.Empty;
    [JsonPropertyName("serverDir")] public string ServerDir { get; set; } = string.Empty;
    [JsonPropertyName("oxideDir")] public string OxideDir { get; set; } = string.Empty;
}

internal sealed class RustRcon : IAsyncDisposable
{
    private readonly string _host;
    private readonly ushort _port;
    private readonly string _password;
    private RCON? _rcon;

    public RustRcon(string host, ushort port, string password)
    {
        _host = host;
        _port = port;
        _password = password;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var address = await ResolveAddressAsync(_host, cancellationToken);
        _rcon = new RCON(address, _port, _password);
        await _rcon.ConnectAsync();
    }

    public async Task<string> SendAndReceiveAsync(string command)
    {
        if (_rcon is null)
            throw new InvalidOperationException("RCON is not connected.");

        return await _rcon.SendCommandAsync(command);
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
            return address;

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not resolve host '{host}'.");
    }

    public ValueTask DisposeAsync()
    {
        _rcon?.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class RemoteAgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
