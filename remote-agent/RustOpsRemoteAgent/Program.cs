using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    var rconConnections = new PersistentRconConnections();
    app.Lifetime.ApplicationStopping.Register(() => _ = Task.Run(rconConnections.DisposeAsync));
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
            "server-meta",
            "rcon-command",
            "rcon-query",
            "moderation",
            "oxide-validate",
            "plugin-updates",
            "plugin-install",
            "process-stats"
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

    // -- Runtime meta -----------------------------------------------------------
    app.MapGet("/servers/{server}/meta", async (string server) =>
    {
        if (!await IsKnownServerAsync(server))
            return Results.NotFound(new ApiError("not_found", $"Unknown server '{server}'."));

        var metaPath = Path.Combine(runtimeDir, $"{server}.meta");
        if (!File.Exists(metaPath))
            return Results.NotFound(new ApiError("not_found", "Meta file not found. Run sync-config first."));

        var lines = await File.ReadAllLinesAsync(metaPath);
        var meta = lines
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(
                p => p[0].Trim().ToLowerInvariant(),
                p => p[1].Trim('"'));

        return Results.Ok(meta);
    });

    // -- Oxide / plugin validation ---------------------------------------------
    app.MapGet("/servers/{server}/oxide/validate", (string server) =>
    {
        var cfg = LoadServerConfig(server);
        if (cfg is null)
            return Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."));

        var normalized = NormalizeConfig(server, cfg);
        var oxideRoots = GetOxideRootCandidates(normalized);
        var configPaths = oxideRoots
            .Select(root => Path.Combine(root, "config"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pluginsPaths = oxideRoots
            .Select(root => Path.Combine(root, "plugins"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string[] jsonFiles;
        string[] pluginFiles;
        try
        {
            jsonFiles = configPaths
                .Where(Directory.Exists)
                .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            pluginFiles = pluginsPaths
                .Where(Directory.Exists)
                .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Ok(new
            {
                server,
                ok = false,
                error = "access_denied",
                note = ex.Message,
                searchedPaths = new { oxideRoots, configPaths, pluginsPaths }
            });
        }

        var jsonResults = jsonFiles.Select(ValidateJsonFile).ToList();
        var pluginResults = pluginFiles.Select(ValidateOxidePluginFile).ToList();

        return Results.Ok(new
        {
            server,
            ok = jsonResults.All(r => r.Ok) && pluginResults.All(r => r.Ok),
            searchedPaths = new { oxideRoots, configPaths, pluginsPaths },
            jsonConfigCount = jsonFiles.Length,
            pluginCount = pluginFiles.Length,
            jsonConfigs = jsonResults,
            plugins = pluginResults
        });
    });

    app.MapGet("/servers/{server}/plugins/updates", async (string server, CancellationToken cancellationToken) =>
    {
        var result = await CheckPluginUpdatesAsync(server, configDir, cancellationToken);
        return result is null
            ? Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."))
            : Results.Ok(result);
    });

    app.MapPost("/servers/{server}/plugins/install", async (string server, PluginInstallRequest request, CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.PluginName))
            return Results.BadRequest(new ApiError("invalid_request", "pluginName is required."));

        if (string.IsNullOrWhiteSpace(request.DownloadUrl) ||
            !Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme is not ("http" or "https"))
        {
            return Results.BadRequest(new ApiError("invalid_request", "downloadUrl must be an absolute HTTP(S) URL."));
        }

        try
        {
            var result = await InstallPluginAsync(server, request.PluginName, downloadUri, configDir, cancellationToken);
            return result is null
                ? Results.NotFound(new ApiError("not_found", $"No config found for '{server}'."))
                : Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    });

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
        double? memoryMb = null;
        int? uptimeSeconds = null;
        if (status?.Pid.HasValue == true)
        {
            try
            {
                var proc = Process.GetProcessById(status.Pid.Value);
                memoryMb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1);
                uptimeSeconds = (int)(DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds;
            }
            catch { /* process may have exited */ }
        }
        return new
        {
            name = server,
            state = status?.State ?? "unknown",
            online = status?.Online ?? false,
            pid = status?.Pid,
            autoRestart = status?.AutoRestart ?? false,
            session = status?.Session ?? false,
            raw = status?.Raw ?? string.Empty,
            memoryMb,
            uptimeSeconds,
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

        try
        {
            var commandResult = await SendRconAsync(server, command);
            var payload = TryExtractJson(commandResult.Reply);
            return payload is null
                ? Results.BadRequest(new ApiError("parse_error", $"Could not parse {command} response."))
                : Results.Content(payload, "application/json");
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, $"Remote agent RCON query '{command}' failed.", "remote-agent.rcon");
            return Results.BadRequest(new ApiError("rcon_error", ex.Message));
        }
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
        var reply = await rconConnections.SendAndReceiveAsync(endpoint.Host, endpoint.Port, endpoint.Password, command);
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

static ServerConfig NormalizeConfig(string server, ServerConfig cfg) => new()
{
    Name                 = server,
    ServerHostname       = cfg.ServerHostname?.Trim()       ?? string.Empty,
    ServerDescription    = cfg.ServerDescription?.Trim()    ?? string.Empty,
    ServerUrl            = cfg.ServerUrl?.Trim()            ?? string.Empty,
    ServerLogoImage      = cfg.ServerLogoImage?.Trim()      ?? string.Empty,
    ServerHeaderImage    = cfg.ServerHeaderImage?.Trim()    ?? string.Empty,
    ServerTags           = cfg.ServerTags?.Trim()           ?? string.Empty,
    ServerIdentity       = (cfg.ServerIdentity?.Trim() is { Length: > 0 } id) ? id : server,
    ServerPort           = cfg.ServerPort,
    RconPort             = cfg.RconPort,
    AppPort              = cfg.AppPort,
    ServerWorldSize      = cfg.ServerWorldSize,
    ServerSeed           = cfg.ServerSeed,
    ServerMaxPlayers     = cfg.ServerMaxPlayers <= 0 ? 100 : cfg.ServerMaxPlayers,
    ServerLevel          = string.IsNullOrWhiteSpace(cfg.ServerLevel) ? "Procedural Map" : cfg.ServerLevel.Trim(),
    ServerLevelUrl       = cfg.ServerLevelUrl?.Trim()       ?? string.Empty,
    RconPassword         = cfg.RconPassword?.Trim()         ?? string.Empty,
    ServerReportsServerEndpoint = cfg.ServerReportsServerEndpoint?.Trim() ?? string.Empty,
    LogFile              = string.IsNullOrWhiteSpace(cfg.LogFile) ? "Log.txt" : RustOpsEnv.NormalizePath(cfg.LogFile.Trim()),
    ServerEncryption     = string.IsNullOrWhiteSpace(cfg.ServerEncryption) ? "1" : cfg.ServerEncryption.Trim(),
    BoomboxServerUrlList = cfg.BoomboxServerUrlList?.Trim() ?? string.Empty,
    AdditionalArgs       = cfg.AdditionalArgs?.Trim()       ?? string.Empty,
    ServerDir            = string.IsNullOrWhiteSpace(cfg.ServerDir) ? $"/srv/rust/{server}" : RustOpsEnv.NormalizePath(cfg.ServerDir.Trim()),
    OxideDir             = string.IsNullOrWhiteSpace(cfg.OxideDir) ? string.Empty : RustOpsEnv.NormalizePath(cfg.OxideDir.Trim())
};

static List<string> GetOxideRootCandidates(ServerConfig normalized)
{
    if (!string.IsNullOrWhiteSpace(normalized.OxideDir))
        return new List<string> { normalized.OxideDir.TrimEnd('/', '\\') };

    var canonicalRoot = Environment.GetEnvironmentVariable("RUST_SERVER_ROOT") ?? "/srv/rust";
    return new[]
        {
            Path.Combine(canonicalRoot, normalized.Name, "oxide"),
            Path.Combine(normalized.ServerDir, "oxide"),
            Path.Combine(normalized.ServerDir, normalized.ServerIdentity, "oxide"),
            Path.Combine(normalized.ServerDir, "server", normalized.ServerIdentity, "oxide"),
            Path.Combine(canonicalRoot, normalized.ServerIdentity, "oxide")
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static ValidationResult ValidateJsonFile(string path)
{
    try
    {
        using var _ = JsonDocument.Parse(File.ReadAllText(path));
        return new ValidationResult { Path = path, Ok = true };
    }
    catch (Exception ex)
    {
        return new ValidationResult { Path = path, Ok = false, Message = ex.Message };
    }
}

static ValidationResult ValidateOxidePluginFile(string path)
{
    var text = File.ReadAllText(path);
    var infoMatch = Regex.Match(
        text,
        "\\[\\s*Info\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<author>[^\"]+)\"\\s*,\\s*\"(?<version>[^\"]+)\"\\s*\\)\\s*\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var pluginName    = infoMatch.Success ? infoMatch.Groups["name"].Value.Trim()    : null;
    var pluginAuthor  = infoMatch.Success ? infoMatch.Groups["author"].Value.Trim()  : null;
    var pluginVersion = infoMatch.Success ? infoMatch.Groups["version"].Value.Trim() : null;
    var pluginSlug    = !string.IsNullOrWhiteSpace(pluginName) ? ToPluginSlug(pluginName) : null;
    var sourceHash    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    var commands      = ExtractPluginCommands(text);
    var permissions   = ExtractPluginPermissions(text);
    var hooks         = ExtractPluginHooks(text);
    var configKeys    = ExtractPluginConfigKeys(text);

    var hasPluginBase =
        text.Contains(": RustPlugin",      StringComparison.Ordinal) ||
        text.Contains(": CovalencePlugin", StringComparison.Ordinal) ||
        text.Contains(": CSPlugin",        StringComparison.Ordinal);

    if (!hasPluginBase)
        return new ValidationResult { Path = path, Ok = false, Message = "Missing expected Oxide plugin base class.", PluginName = pluginName, PluginAuthor = pluginAuthor, PluginVersion = pluginVersion, PluginSlug = pluginSlug, SourceHash = sourceHash, Commands = commands, Permissions = permissions, Hooks = hooks, ConfigKeys = configKeys };

    var open  = text.Count(c => c == '{');
    var close = text.Count(c => c == '}');
    if (open != close)
        return new ValidationResult { Path = path, Ok = false, Message = $"Brace mismatch: {open} '{{' vs {close} '}}'.", PluginName = pluginName, PluginAuthor = pluginAuthor, PluginVersion = pluginVersion, PluginSlug = pluginSlug, SourceHash = sourceHash, Commands = commands, Permissions = permissions, Hooks = hooks, ConfigKeys = configKeys };

    return new ValidationResult { Path = path, Ok = true, PluginName = pluginName, PluginAuthor = pluginAuthor, PluginVersion = pluginVersion, PluginSlug = pluginSlug, SourceHash = sourceHash, Commands = commands, Permissions = permissions, Hooks = hooks, ConfigKeys = configKeys };
}

static PluginMetadata ParsePluginMetadata(string path)
{
    var text = File.ReadAllText(path);
    var infoMatch = Regex.Match(
        text,
        "\\[\\s*Info\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<author>[^\"]*)\"\\s*,\\s*\"(?<version>[^\"]+)\"\\s*\\)\\s*\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    return infoMatch.Success
        ? new PluginMetadata(infoMatch.Groups["name"].Value.Trim(), infoMatch.Groups["version"].Value.Trim())
        : new PluginMetadata(Path.GetFileNameWithoutExtension(path), null);
}

static async Task<object?> CheckPluginUpdatesAsync(string server, string configDir, CancellationToken cancellationToken)
{
    var cfgPath = Path.Combine(configDir, $"{server}.json");
    if (!File.Exists(cfgPath))
        return null;

    var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(cfgPath), RemoteAgentJson.Options);
    if (cfg is null)
        return null;

    var normalized = NormalizeConfig(server, cfg);
    var candidates = GetOxideRootCandidates(normalized)
        .Select(root => Path.Combine(root, "plugins"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    string? pluginsDir;
    try { pluginsDir = candidates.FirstOrDefault(Directory.Exists); }
    catch (UnauthorizedAccessException ex)
    {
        return new { server, updates = Array.Empty<object>(), error = "access_denied", note = ex.Message, triedPaths = candidates };
    }

    if (pluginsDir is null)
        return new { server, updates = Array.Empty<object>(), note = "plugins directory not found in any expected location", triedPaths = candidates };

    List<PluginMetadata> plugins;
    try
    {
        plugins = Directory.GetFiles(pluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(ParsePluginMetadata)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
    }
    catch (UnauthorizedAccessException ex)
    {
        return new { server, updates = Array.Empty<object>(), error = "access_denied", path = pluginsDir, note = $"Read permission denied for '{pluginsDir}'. Details: {ex.Message}", triedPaths = candidates };
    }

    var updates = new List<object>();
    using var http = new HttpClient();
    foreach (var plugin in plugins)
    {
        var searchName = Uri.EscapeDataString(plugin.Name!);
        var url = $"https://umod.org/plugins/search.json?query={searchName}&page=1&sort=title&sortdir=asc&filter=rust";
        string body;
        try { body = await http.GetStringAsync(url, cancellationToken); }
        catch (Exception ex)
        {
            updates.Add(new { plugin = plugin.Name, current = plugin.Version, state = "not_found", reason = $"umod lookup failed: {ex.Message}" });
            continue;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                updates.Add(new { plugin = plugin.Name, current = plugin.Version, state = "not_found" });
                continue;
            }
            var first = data[0];
            var latest = first.TryGetProperty("latest_release_version", out var latestNode) ? latestNode.GetString() : null;
            var current = plugin.Version;
            var needsUpdate = !string.IsNullOrWhiteSpace(latest) && !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
            var downloadUrl = needsUpdate && first.TryGetProperty("download_url", out var dlNode) ? dlNode.GetString() : null;
            updates.Add(new { plugin = plugin.Name, current, latest, downloadUrl, state = needsUpdate ? "update_available" : "current" });
        }
        catch (JsonException ex)
        {
            updates.Add(new { plugin = plugin.Name, current = plugin.Version, state = "not_found", reason = $"umod parse failed: {ex.Message}" });
        }
    }

    return new { server, pluginPath = pluginsDir, updates };
}

static async Task<object?> InstallPluginAsync(string server, string pluginName, Uri downloadUri, string configDir, CancellationToken cancellationToken)
{
    var cfgPath = Path.Combine(configDir, $"{server}.json");
    if (!File.Exists(cfgPath))
        return null;

    var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(cfgPath), RemoteAgentJson.Options);
    if (cfg is null)
        return null;

    var normalized = NormalizeConfig(server, cfg);
    var candidates = GetOxideRootCandidates(normalized)
        .Select(root => Path.Combine(root, "plugins"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var pluginsDir = candidates.FirstOrDefault(Directory.Exists)
        ?? Path.Combine(Environment.GetEnvironmentVariable("RUST_SERVER_ROOT") ?? "/srv/rust", normalized.Name, "oxide", "plugins");

    try { Directory.CreateDirectory(pluginsDir); }
    catch (UnauthorizedAccessException ex)
    {
        throw new UnauthorizedAccessException($"Write permission denied for '{pluginsDir}'. Details: {ex.Message}", ex);
    }

    var safeName = string.Concat(pluginName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
    if (string.IsNullOrWhiteSpace(safeName))
        safeName = "Plugin";

    var destPath = Path.Combine(pluginsDir, $"{safeName}.cs");
    try
    {
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(downloadUri, cancellationToken);
        await File.WriteAllBytesAsync(destPath, bytes, cancellationToken);
        return new { server, plugin = pluginName, installed = true, path = destPath, bytes = bytes.Length };
    }
    catch (UnauthorizedAccessException ex)
    {
        throw new UnauthorizedAccessException($"Write permission denied for '{destPath}'. Details: {ex.Message}", ex);
    }
}

static List<PluginCommandReferenceView> ExtractPluginCommands(string source)
{
    var commands = new List<PluginCommandReferenceView>();
    AddPluginAttributeCommands(commands, source, @"\[\s*ChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]",    "ChatCommand");
    AddPluginAttributeCommands(commands, source, @"\[\s*ConsoleCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "ConsoleCommand");
    AddPluginAttributeCommands(commands, source, @"\[\s*Command\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]",       "CovalenceCommand");
    foreach (Match match in Regex.Matches(source, @"cmd\.AddChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*,\s*this\s*,\s*(?:nameof\s*\(\s*)?""?(?<handler>[A-Za-z_][A-Za-z0-9_]*)""?", RegexOptions.IgnoreCase))
        commands.Add(new PluginCommandReferenceView(match.Groups["cmd"].Value.Trim(), "ChatCommand", match.Groups["handler"].Value.Trim()));
    foreach (Match match in Regex.Matches(source, @"AddCovalenceCommand\s*\(\s*""(?<cmd>[^""]+)""\s*,\s*(?:nameof\s*\(\s*)?""?(?<handler>[A-Za-z_][A-Za-z0-9_]*)""?", RegexOptions.IgnoreCase))
        commands.Add(new PluginCommandReferenceView(match.Groups["cmd"].Value.Trim(), "CovalenceCommand", match.Groups["handler"].Value.Trim()));
    return commands
        .GroupBy(c => $"{c.Type}:{c.Command}", StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .OrderBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static void AddPluginAttributeCommands(List<PluginCommandReferenceView> commands, string source, string pattern, string type)
{
    foreach (Match match in Regex.Matches(source, pattern, RegexOptions.IgnoreCase))
        commands.Add(new PluginCommandReferenceView(match.Groups["cmd"].Value.Trim(), type, FindPluginHandlerAfter(source, match.Index + match.Length)));
}

static string FindPluginHandlerAfter(string source, int index)
{
    var tail  = source[Math.Min(index, source.Length)..];
    var match = Regex.Match(tail, @"\b(?:private|public|protected|internal)?\s*(?:void|bool|object|string)\s+(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["handler"].Value.Trim() : string.Empty;
}

static List<string> ExtractPluginPermissions(string source)
{
    var patterns = new[]
    {
        @"permission\.RegisterPermission\s*\(\s*""(?<value>[^""]+)""",
        @"permission\.UserHasPermission\s*\([^,]+,\s*""(?<value>[^""]+)""",
        @"\.HasPermission\s*\(\s*""(?<value>[^""]+)"""
    };
    return patterns
        .SelectMany(p => Regex.Matches(source, p, RegexOptions.IgnoreCase).Select(m => m.Groups["value"].Value.Trim()))
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<string> ExtractPluginHooks(string source)
{
    var known = new[] { "OnServerInitialized", "Init", "Loaded", "Unload", "OnPlayerConnected", "OnPlayerDisconnected", "OnEntityDeath", "OnPlayerDeath", "OnUserChat", "CanBuild", "CanLootEntity" };
    return known
        .Where(hook => Regex.IsMatch(source, $@"\b(?:void|object|bool|string)\s+{Regex.Escape(hook)}\s*\(", RegexOptions.IgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<string> ExtractPluginConfigKeys(string source)
{
    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (Match m in Regex.Matches(source, @"Config\s*\[\s*""(?<key>[^""]+)""\s*\]", RegexOptions.IgnoreCase))
        keys.Add(m.Groups["key"].Value.Trim());
    foreach (Match m in Regex.Matches(source, @"GetConfig\s*\(\s*""(?<key>[^""]+)""", RegexOptions.IgnoreCase))
        keys.Add(m.Groups["key"].Value.Trim());
    foreach (Match m in Regex.Matches(source, @"JsonProperty\s*\(\s*""(?<key>[^""]+)""\s*\)", RegexOptions.IgnoreCase))
        keys.Add(m.Groups["key"].Value.Trim());
    foreach (Match m in Regex.Matches(source, @"configData\.(?<key>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase))
        keys.Add(m.Groups["key"].Value.Trim());
    return keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
}

static string ToPluginSlug(string input)
{
    var slug = Regex.Replace(input.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-");
    return slug.Trim('-');
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

internal sealed class PersistentRconConnections
{
    private readonly ConcurrentDictionary<RconConnectionKey, RconConnectionSlot> _connections = new();

    public async Task<string> SendAndReceiveAsync(
        string host,
        ushort port,
        string password,
        string command,
        CancellationToken cancellationToken = default)
    {
        var key = new RconConnectionKey(host.Trim(), port, password);
        var slot = _connections.GetOrAdd(key, _ => new RconConnectionSlot());

        await slot.Lock.WaitAsync(cancellationToken);
        try
        {
            slot.Client ??= new RustRcon(key.Host, key.Port, key.Password);
            if (!slot.Client.IsConnected)
                await slot.Client.ConnectAsync(cancellationToken);

            try
            {
                return await slot.Client.SendAndReceiveAsync(command, cancellationToken);
            }
            catch
            {
                await slot.DisposeClientAsync();
                slot.Client = new RustRcon(key.Host, key.Port, key.Password);
                await slot.Client.ConnectAsync(cancellationToken);
                return await slot.Client.SendAndReceiveAsync(command, cancellationToken);
            }
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var slot in _connections.Values)
            await slot.DisposeClientAsync();

        _connections.Clear();
    }

    private sealed record RconConnectionKey(string Host, ushort Port, string Password);

    private sealed class RconConnectionSlot
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public RustRcon? Client { get; set; }

        public async Task DisposeClientAsync()
        {
            if (Client is not null)
            {
                await Client.DisposeAsync();
                Client = null;
            }
        }
    }
}

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

internal sealed class PluginInstallRequest
{
    [JsonPropertyName("pluginName")]  public string PluginName  { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = string.Empty;
}

internal sealed record PluginMetadata(string? Name, string? Version);
internal sealed record PluginCommandReferenceView(string Command, string Type, string HandlerMethod);

internal sealed class ValidationResult
{
    public string Path              { get; set; } = string.Empty;
    public bool   Ok                { get; set; }
    public string? Message          { get; set; }
    public string? PluginName       { get; set; }
    public string? PluginAuthor     { get; set; }
    public string? PluginVersion    { get; set; }
    public string? PluginSlug       { get; set; }
    public string SourceHash        { get; set; } = string.Empty;
    public List<PluginCommandReferenceView> Commands    { get; set; } = new();
    public List<string>                    Permissions { get; set; } = new();
    public List<string>                    Hooks       { get; set; } = new();
    public List<string>                    ConfigKeys  { get; set; } = new();
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
    private ClientWebSocket? _ws;
    private Task? _receiveTask;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private int _nextId = 1;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public RustRcon(string host, ushort port, string password)
    {
        _host = host;
        _port = port;
        _password = password;
    }

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisposeSocketAsync();

        _ws = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var uri = new Uri($"ws://{_host}:{_port}/{Uri.EscapeDataString(_password)}");
        await _ws.ConnectAsync(uri, cancellationToken);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
    }

    public async Task<string> SendAndReceiveAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("RCON is not connected.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { Identifier = id, Message = command, Name = "WebRcon" });
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _ws!.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);
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
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var raw = Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrWhiteSpace(raw)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var id = doc.RootElement.TryGetProperty("Identifier", out var idEl) ? idEl.GetInt32() : -1;
                    var msg = doc.RootElement.TryGetProperty("Message", out var msgEl) ? msgEl.ToString() : raw;
                    if (id >= 0 && _pending.TryGetValue(id, out var tcs))
                        tcs.TrySetResult(msg);
                }
                catch { /* ignore unsolicited/unparseable messages */ }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(ex);
        }
    }

    private async Task DisposeSocketAsync()
    {
        _cts?.Cancel();
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch { }
            _receiveTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }

        _ws?.Dispose();
        _ws = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync();
        _sendLock.Dispose();
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
