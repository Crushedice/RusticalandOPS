using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustFileEditToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly IGitOpsService _gitOps;
    private readonly GitOpsSettings _gitOpsSettings;

    private static readonly string[] AllowedExtensions = { ".cfg", ".json", ".txt", ".ini", ".env" };

    // Canonical rustmgr JSON config file keys — these live in the config JSON file, not runtime RCON.
    internal static readonly Dictionary<string, string> ServerConfigKeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["worldsize"] = "server.worldsize",
        ["world size"] = "server.worldsize",
        ["seed"] = "server.seed",
        ["maxplayers"] = "server.maxplayers",
        ["max players"] = "server.maxplayers",
        ["hostname"] = "server.hostname",
        ["server name"] = "server.hostname",
        ["identity"] = "server.identity",
        ["rcon port"] = "rcon.port",
        ["rcon password"] = "rcon.password",
        ["app port"] = "app.port",
        ["server port"] = "server.port",
        ["serverdir"] = "serverDir",
        ["server dir"] = "serverDir",
        ["logfile"] = "logFile",
        ["log file"] = "logFile",
        ["additionalargs"] = "additionalArgs",
        ["additional args"] = "additionalArgs"
    };

    private static readonly HashSet<string> ServerNameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "your", "the", "this", "that", "my", "our", "their", "its",
        "a", "an", "any", "some", "server", "servers", "config", "file",
        "plugin", "oxide"
    };

    public RustFileEditToolHandler(RustOpsApiClient api, IGitOpsService gitOps, GitOpsSettings gitOpsSettings)
    {
        _api = api;
        _gitOps = gitOps;
        _gitOpsSettings = gitOpsSettings;
    }

    public string Name => "rust.file.edit";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.FileEdit };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var lowered = context.Message.ToLowerInvariant();

        // Plugin config must be checked first to prevent the server config alias matcher from
        // stealing messages like "show plugin config for <server>" that contain alias keywords.
        if (LooksLikePluginConfigRequest(lowered))
            return await HandlePluginConfigAsync(context, cancellationToken);

        if (LooksLikeServerConfigRequest(lowered) || LooksLikeServerConfigValueQuery(lowered, isPluginRequest: false))
            return await HandleServerConfigAsync(context, cancellationToken);

        var filePath = ExtractFilePath(context.Message);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ToolExecutionResult(false,
                "Which file should I open? Mention the config file name, for example: show server.cfg, or edit oxide/config/MyPlugin.json.",
                null, false, "clarification_required");
        }

        if (!IsSafeExtension(filePath))
        {
            return new ToolExecutionResult(false,
                $"Only {string.Join(", ", AllowedExtensions)} files can be read or edited for safety.",
                null, false, "not_allowed");
        }

        var fullPath = ResolveSafePath(_gitOpsSettings.RepoPath, filePath);
        if (fullPath is null)
        {
            return new ToolExecutionResult(false,
                "That path resolves outside the repository root. Only files inside the repo can be accessed.",
                null, false, "path_traversal");
        }

        var isReadRequest = IsReadRequest(context.Message);
        if (isReadRequest)
            return await ReadFileAsync(fullPath, filePath, cancellationToken);

        if (!_gitOpsSettings.Enabled)
            return new ToolExecutionResult(false, "File editing requires GitOps to be enabled (gitOps.enabled=true in config).", null, false, "not_configured");

        var editContent = ExtractEditContent(context.Message);
        if (string.IsNullOrWhiteSpace(editContent))
        {
            if (!File.Exists(fullPath))
                return new ToolExecutionResult(false, $"File not found: {filePath}. Check the path and try again.", null, false, "file_not_found");

            var currentContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var preview = currentContent.Length > 800 ? currentContent[..800] + "\n...(truncated)" : currentContent;
            return new ToolExecutionResult(true,
                $"Current content of {filePath}:\n{preview}\n\nTo edit, tell me what change to make.",
                null, false, Payload: new { filePath, currentContent });
        }

        return await ProposeEditAsync(fullPath, filePath, editContent, context, cancellationToken);
    }

    private async Task<ToolExecutionResult> ReadFileAsync(string fullPath, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
            return new ToolExecutionResult(false, $"File not found: {filePath}.", null, false, "file_not_found");

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var preview = content.Length > 1000 ? content[..1000] + "\n...(truncated)" : content;
        return new ToolExecutionResult(true,
            $"{filePath} ({content.Length} chars):\n{preview}",
            null, true, Payload: new { filePath, content });
    }

    private async Task<ToolExecutionResult> ProposeEditAsync(
        string fullPath, string filePath, string newContent,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, newContent, cancellationToken);

            var branch = await _gitOps.EnsureAgentBranchAsync($"edit-{SanitizePath(filePath)}", cancellationToken);
            await _gitOps.CommitAsync($"agent: edit {filePath} requested by {context.AdminId}", cancellationToken);

            if (_gitOpsSettings.AllowPush)
            {
                await _gitOps.PushAsync(branch, cancellationToken);
                var prUrl = await _gitOps.CreatePrAsync(
                    branch,
                    $"[agent] Edit {filePath}",
                    $"Admin {context.AdminId} requested edit of {filePath}.\n\nChanges staged by agent for review.",
                    cancellationToken);
                return new ToolExecutionResult(true,
                    $"Edit staged for {filePath} and PR created: {prUrl}",
                    null, false);
            }

            return new ToolExecutionResult(true,
                $"Edit written to {filePath} and committed on branch {branch}. Push is disabled — merge manually when ready.",
                null, false);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false,
                $"Could not stage edit for {filePath}: {ex.Message}",
                null, false, "edit_failed");
        }
    }

    private static string? ResolveSafePath(string repoRoot, string relativePath)
    {
        var normalRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(repoRoot, relativePath.TrimStart('/', '\\')));
        return candidate.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static bool IsSafeExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return AllowedExtensions.Contains(ext);
    }

    private static bool IsReadRequest(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("show") || lower.Contains("read") || lower.Contains("view")
            || lower.Contains("display") || lower.Contains("open") || lower.Contains("print")
            || lower.Contains("cat ") || lower.Contains("what's in") || lower.Contains("contents of");
    }

    private static string? ExtractFilePath(string message)
    {
        var match = Regex.Match(message,
            @"(?:show|read|view|open|edit|modify|update|change|set)\s+(?:the\s+)?(?:file\s+)?(?<path>[a-zA-Z0-9_./-]+\.(?:cfg|json|txt|ini|env))",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups["path"].Value.Trim();

        var quoted = Regex.Match(message, "\"(?<path>[^\"]+\\.(?:cfg|json|txt|ini|env))\"", RegexOptions.IgnoreCase);
        if (quoted.Success)
            return quoted.Groups["path"].Value.Trim();

        return null;
    }

    private static string? ExtractEditContent(string message)
    {
        var block = Regex.Match(message, @"```[a-z]*\n(?<content>[\s\S]+?)```", RegexOptions.IgnoreCase);
        if (block.Success)
            return block.Groups["content"].Value.Trim();

        var setTo = Regex.Match(message, @"(?:set to|replace with|content):?\s*(?<content>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (setTo.Success)
            return setTo.Groups["content"].Value.Trim().Trim('"', '\'');

        return null;
    }

    private static string SanitizePath(string path) =>
        new string(path.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    // ── Server config handler ──────────────────────────────────────────────────

    private async Task<ToolExecutionResult> HandleServerConfigAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = context.Route.Slots.ServerName;
        if (string.IsNullOrWhiteSpace(server))
            server = ExtractServerNameFromMessage(context.Message);

        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.FileEdit, knownServers, allowAllServers: false),
                null, false, "clarification_required");
        }

        var configPath = ResolveServerConfigPath(server);
        if (configPath is null || !File.Exists(configPath))
        {
            return new ToolExecutionResult(
                false,
                $"Server config not found for '{server}'. Expected path: {BuildExpectedConfigPath(server)}",
                server, false, "file_not_found");
        }

        var canonicalServer = Path.GetFileNameWithoutExtension(configPath);
        var currentRaw = await File.ReadAllTextAsync(configPath, cancellationToken);
        var configNode = JsonNode.Parse(currentRaw) as JsonObject;
        if (configNode is null)
            return new ToolExecutionResult(false, $"Could not parse config for '{canonicalServer}'.", canonicalServer, false, "parse_error");

        // Prefer LLM-extracted slots when available (avoids re-parsing the raw message).
        var slotKey = context.Route.Slots.ConfigKey;
        var slotValue = context.Route.Slots.ConfigValue;

        ConfigMutationResult? mutation = null;
        if (!string.IsNullOrWhiteSpace(slotKey) && !string.IsNullOrWhiteSpace(slotValue) && !IsReadRequest(context.Message))
        {
            // Bug #1 fix: resolve alias on the key before writing
            var resolvedKey = ResolveConfigKeyAlias(slotKey);
            mutation = new ConfigMutationResult(resolvedKey, ParseJsonValue(slotValue), slotValue);
        }
        else
        {
            mutation = TryExtractConfigMutation(context.Message, canonicalServer);
        }

        if (mutation is not null && !IsReadRequest(context.Message))
        {
            ApplyConfigMutation(configNode, mutation.Key, mutation.ValueNode);
            var prettyUpdated = configNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, prettyUpdated, cancellationToken);

            return new ToolExecutionResult(
                true,
                $"Updated {canonicalServer} config at {configPath}: set `{mutation.Key}` to `{mutation.DisplayValue}`. Server runtime root: {BuildExpectedServerRootPath(canonicalServer)}\n```json\n{prettyUpdated}\n```",
                canonicalServer, true,
                Payload: prettyUpdated);
        }

        // Key lookup — prefer LLM slot, fall back to regex
        var lookupKey = !string.IsNullOrWhiteSpace(slotKey) ? slotKey : TryExtractConfigLookupKey(context.Message, includeAliases: true);
        if (!string.IsNullOrWhiteSpace(lookupKey))
        {
            if (TryReadConfigValue(configNode, lookupKey!, out var resolvedKey, out var valueNode))
            {
                var renderedValue = RenderJsonValue(valueNode);
                return new ToolExecutionResult(
                    true,
                    $"{canonicalServer} config `{resolvedKey}` = `{renderedValue}` (from {configPath}).",
                    canonicalServer, false,
                    Payload: new { key = resolvedKey, value = renderedValue, configPath });
            }

            return new ToolExecutionResult(
                false,
                $"Key `{lookupKey}` was not found in {canonicalServer} config ({configPath}).",
                canonicalServer, false, "key_not_found");
        }

        var pretty = configNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var serverRoot = BuildExpectedServerRootPath(canonicalServer);
        return new ToolExecutionResult(
            true,
            $"{canonicalServer} server config ({configPath}). Server runtime root: {serverRoot}\n\n```json\n{pretty}\n```",
            canonicalServer, true,
            Payload: pretty);
    }

    private static bool LooksLikeServerConfigRequest(string lowered)
    {
        if (!(lowered.Contains("config", StringComparison.Ordinal) || lowered.Contains("serverconfig", StringComparison.Ordinal)))
            return false;

        return lowered.Contains("server", StringComparison.Ordinal) ||
               lowered.Contains("json", StringComparison.Ordinal) ||
               lowered.Contains("set ", StringComparison.Ordinal) ||
               lowered.Contains("change ", StringComparison.Ordinal) ||
               lowered.Contains("update ", StringComparison.Ordinal) ||
               lowered.Contains("show", StringComparison.Ordinal) ||
               lowered.Contains("read", StringComparison.Ordinal) ||
               lowered.Contains("view", StringComparison.Ordinal) ||
               lowered.Contains("open", StringComparison.Ordinal);
    }

    // Bug #2 fix: guard with isPluginRequest so plugin messages don't match on shared alias keywords.
    private static bool LooksLikeServerConfigValueQuery(string lowered, bool isPluginRequest)
    {
        if (isPluginRequest)
            return false;

        var asksValue =
            lowered.Contains("what is", StringComparison.Ordinal) ||
            lowered.Contains("what's", StringComparison.Ordinal) ||
            lowered.Contains("value", StringComparison.Ordinal) ||
            lowered.Contains("get ", StringComparison.Ordinal) ||
            lowered.Contains("show ", StringComparison.Ordinal) ||
            lowered.Contains("read ", StringComparison.Ordinal);
        if (!asksValue)
            return false;

        return ServerConfigKeyAliases.Keys.Any(alias => lowered.Contains(alias, StringComparison.Ordinal));
    }

    private static bool LooksLikePluginConfigRequest(string lowered)
    {
        if (lowered.Contains("oxide/config", StringComparison.Ordinal) || lowered.Contains(@"oxide\config", StringComparison.Ordinal))
            return true;

        return lowered.Contains("plugin", StringComparison.Ordinal) && lowered.Contains("config", StringComparison.Ordinal);
    }

    private static string? ExtractServerNameFromMessage(string message)
    {
        var match = Regex.Match(
            message,
            @"\b(?:for|on|from|of|in)\s+(?<server>[A-Za-z0-9][A-Za-z0-9._-]{1,})\b(?:\s+server)?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var server = match.Groups["server"].Value.Trim();
        return ServerNameStopWords.Contains(server) ? null : server;
    }

    // ── Plugin config handler ──────────────────────────────────────────────────

    private async Task<ToolExecutionResult> HandlePluginConfigAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = context.Route.Slots.ServerName;
        if (string.IsNullOrWhiteSpace(server))
            server = ExtractServerNameFromMessage(context.Message);

        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.FileEdit, knownServers, allowAllServers: false),
                null, false, "clarification_required");
        }

        var configPath = ResolveServerConfigPath(server);
        if (configPath is null || !File.Exists(configPath))
        {
            return new ToolExecutionResult(
                false,
                $"Server config not found for '{server}' — cannot locate the oxide config directory. Expected path: {BuildExpectedConfigPath(server)}",
                server, false, "file_not_found");
        }

        var serverRaw = await File.ReadAllTextAsync(configPath, cancellationToken);
        var serverConfig = JsonNode.Parse(serverRaw) as JsonObject;
        if (serverConfig is null)
            return new ToolExecutionResult(false, $"Could not parse config for '{server}'.", server, false, "parse_error");

        var configDirs = ResolveOxideConfigDirectories(serverConfig);
        Console.WriteLine($"[plugin-config] Resolved {configDirs.Count} oxide config directories for '{server}': {string.Join(", ", configDirs)}");
        if (configDirs.Count == 0)
        {
            return new ToolExecutionResult(
                false,
                $"No oxide config directory found for '{server}'. Checked serverDir/logFile-derived oxide paths.",
                server, false, "file_not_found");
        }

        var pluginName = ExtractPluginNameFromMessage(context.Message);
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            var available = ListPluginConfigNames(configDirs);
            var suffix = available.Count == 0 ? "none found" : string.Join(", ", available.Take(20));
            return new ToolExecutionResult(
                false,
                $"Which plugin config should I use on {server}? Available: {suffix}.",
                server, false, "clarification_required");
        }

        var pluginPath = ResolvePluginConfigPath(configDirs, pluginName);
        if (string.IsNullOrWhiteSpace(pluginPath) || !File.Exists(pluginPath))
        {
            return new ToolExecutionResult(
                false,
                $"Plugin config '{pluginName}' not found for {server}.",
                server, false, "file_not_found");
        }

        var pluginRaw = await File.ReadAllTextAsync(pluginPath, cancellationToken);
        var pluginConfig = JsonNode.Parse(pluginRaw) as JsonObject;
        if (pluginConfig is null)
            return new ToolExecutionResult(false, $"Could not parse plugin config '{pluginName}' at {pluginPath}.", server, false, "parse_error");

        var mutation = TryExtractConfigMutation(context.Message, server);
        if (mutation is not null && !IsReadRequest(context.Message))
        {
            ApplyConfigMutation(pluginConfig, mutation.Key, mutation.ValueNode);
            var prettyUpdated = pluginConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(pluginPath, prettyUpdated, cancellationToken);
            return new ToolExecutionResult(
                true,
                $"Updated plugin config `{Path.GetFileNameWithoutExtension(pluginPath)}` on {server}: set `{mutation.Key}` to `{mutation.DisplayValue}`.\n```json\n{prettyUpdated}\n```",
                server, true,
                Payload: prettyUpdated);
        }

        var key = TryExtractConfigLookupKey(context.Message, includeAliases: false);
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (TryReadConfigValue(pluginConfig, key!, out var resolvedKey, out var valueNode))
            {
                var renderedValue = RenderJsonValue(valueNode);
                return new ToolExecutionResult(
                    true,
                    $"Plugin config `{Path.GetFileNameWithoutExtension(pluginPath)}` on {server}: `{resolvedKey}` = `{renderedValue}`.",
                    server, false,
                    Payload: new { key = resolvedKey, value = renderedValue, pluginPath });
            }

            return new ToolExecutionResult(
                false,
                $"Key `{key}` was not found in plugin config `{Path.GetFileNameWithoutExtension(pluginPath)}` on {server}.",
                server, false, "key_not_found");
        }

        var pretty = pluginConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new ToolExecutionResult(
            true,
            $"{Path.GetFileNameWithoutExtension(pluginPath)} plugin config ({pluginPath}).\n```json\n{pretty}\n```",
            server, true,
            Payload: pretty);
    }

    // ── Config key/value helpers ───────────────────────────────────────────────

    // Bug #1 fix: aliases are now resolved here so both the read and write paths go through the same resolver.
    private static string ResolveConfigKeyAlias(string key) =>
        ServerConfigKeyAliases.TryGetValue(key, out var canonical) ? canonical : key;

    internal static string? TryExtractConfigLookupKey(string message, bool includeAliases)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var explicitKey = Regex.Match(message, @"\b(?<key>(?:server|rcon|app)\.[A-Za-z0-9_.-]+)\b", RegexOptions.IgnoreCase);
        if (explicitKey.Success)
            return explicitKey.Groups["key"].Value.Trim();

        if (includeAliases)
        {
            var lowered = message.ToLowerInvariant();
            foreach (var alias in ServerConfigKeyAliases.OrderByDescending(item => item.Key.Length))
            {
                if (lowered.Contains(alias.Key, StringComparison.Ordinal))
                    return alias.Value;
            }
        }

        var genericKey = Regex.Match(
            message,
            @"\b(?:value of|what(?:'s| is)|show|get|read)\s+(?:the\s+)?(?<key>[A-Za-z0-9_.-]+)",
            RegexOptions.IgnoreCase);
        if (genericKey.Success)
        {
            var candidate = genericKey.Groups["key"].Value.Trim();
            if (!string.Equals(candidate, "server", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, "config", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool TryReadConfigValue(JsonObject config, string key, out string resolvedKey, out JsonNode? valueNode)
    {
        resolvedKey = key;
        valueNode = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (TryGetObjectValue(config, key, out resolvedKey, out valueNode))
            return true;

        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return false;

        JsonNode? current = config;
        var resolvedParts = new List<string>();
        foreach (var segment in segments)
        {
            if (current is not JsonObject currentObj)
                return false;

            if (!TryGetObjectValue(currentObj, segment, out var matchedKey, out var nextNode))
                return false;

            resolvedParts.Add(matchedKey);
            current = nextNode;
        }

        resolvedKey = string.Join('.', resolvedParts);
        valueNode = current;
        return true;
    }

    private static bool TryGetObjectValue(JsonObject obj, string key, out string matchedKey, out JsonNode? value)
    {
        foreach (var item in obj)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = item.Key;
                value = item.Value;
                return true;
            }
        }

        matchedKey = key;
        value = null;
        return false;
    }

    private static string RenderJsonValue(JsonNode? value)
    {
        if (value is null)
            return "null";

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str))
            return str;

        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // Bug #1 fix: TryExtractConfigMutation now resolves aliases before returning the key.
    private static ConfigMutationResult? TryExtractConfigMutation(string message, string? serverName)
    {
        var setMatch = Regex.Match(
            message,
            @"\b(?:set|change|update)\s+(?<key>[A-Za-z0-9._-]+)\s*(?:to|=)\s*(?<value>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!setMatch.Success)
            return null;

        var rawKey = setMatch.Groups["key"].Value.Trim();
        var valueText = setMatch.Groups["value"].Value.Trim().TrimEnd('.', ';');
        valueText = RustToolHelper.StripTrailingServerQualifier(valueText, serverName);
        if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(valueText))
            return null;

        // Resolve alias so "set worldsize to 3500" writes to "server.worldsize", not a new "worldsize" key.
        var resolvedKey = ResolveConfigKeyAlias(rawKey);
        var valueNode = ParseJsonValue(valueText);
        return new ConfigMutationResult(resolvedKey, valueNode, valueNode?.ToJsonString() ?? "null");
    }

    private static JsonNode? ParseJsonValue(string raw)
    {
        var text = raw.Trim();
        if ((text.StartsWith('{') && text.EndsWith('}')) || (text.StartsWith('[') && text.EndsWith(']')))
        {
            try { return JsonNode.Parse(text); } catch { }
        }

        if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length >= 2)
            return JsonValue.Create(text[1..^1]);
        if (text.StartsWith("'") && text.EndsWith("'") && text.Length >= 2)
            return JsonValue.Create(text[1..^1]);

        if (bool.TryParse(text, out var boolValue))
            return JsonValue.Create(boolValue);
        if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        if (long.TryParse(text, out var longValue))
            return JsonValue.Create(longValue);
        if (double.TryParse(text, out var doubleValue))
            return JsonValue.Create(doubleValue);

        return JsonValue.Create(text);
    }

    private static void ApplyConfigMutation(JsonObject config, string key, JsonNode? value)
    {
        // rustmgr config commonly uses dotted keys as literal properties (e.g. "server.maxplayers"),
        // so prefer direct assignment. If that key does not exist, try nested path.
        if (config.ContainsKey(key))
        {
            config[key] = value;
            return;
        }

        if (key.Contains('.') && TryAssignNested(config, key, value))
            return;

        config[key] = value;
    }

    private static bool TryAssignNested(JsonObject root, string dottedKey, JsonNode? value)
    {
        var segments = dottedKey.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return false;

        JsonObject current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
                return false;
            current = next;
        }

        current[segments[^1]] = value;
        return true;
    }

    // ── Plugin config helpers ──────────────────────────────────────────────────

    private static string? ExtractPluginNameFromMessage(string message)
    {
        var pathMatch = Regex.Match(
            message,
            @"oxide[\\/]+config[\\/]+(?<plugin>[A-Za-z0-9._-]+)\.json",
            RegexOptions.IgnoreCase);
        if (pathMatch.Success)
            return pathMatch.Groups["plugin"].Value.Trim();

        foreach (var pattern in new[]
        {
            @"\bplugin\s+(?<plugin>[A-Za-z0-9._-]+)\s+config\b",
            @"\b(?<plugin>[A-Za-z0-9._-]+)\s+plugin\s+config\b",
            @"\bconfig\s+for\s+(?<plugin>[A-Za-z0-9._-]+)\b"
        })
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["plugin"].Value.Trim();
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveOxideConfigDirectories(JsonObject serverConfig)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var serverDir = ReadJsonString(serverConfig, "serverDir");
        if (!string.IsNullOrWhiteSpace(serverDir))
        {
            var oxideDir = Path.Combine(serverDir, "oxide", "config");
            dirs.Add(oxideDir);
            Console.WriteLine($"[oxide-dirs] Added from serverDir: {oxideDir}");
        }

        var logFile = ReadJsonString(serverConfig, "logFile");
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            var logPath = Path.IsPathRooted(logFile)
                ? logFile
                : !string.IsNullOrWhiteSpace(serverDir)
                    ? Path.Combine(serverDir, logFile)
                    : logFile;

            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                var oxideDir = Path.Combine(logDir, "oxide", "config");
                dirs.Add(oxideDir);
                Console.WriteLine($"[oxide-dirs] Added from logFile: {oxideDir}");
            }
        }

        var filtered = dirs.Where(Directory.Exists).ToList();
        Console.WriteLine($"[oxide-dirs] After filtering: {(filtered.Count == 0 ? "NONE" : string.Join(", ", filtered))}");
        return filtered;
    }

    private static string? ReadJsonString(JsonObject obj, string key)
    {
        if (!TryGetObjectValue(obj, key, out _, out var node) || node is null)
            return null;

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var str) => str,
            _ => node.ToString()
        };
    }

    private static IReadOnlyList<string> ListPluginConfigNames(IReadOnlyList<string> configDirs) =>
        configDirs
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? ResolvePluginConfigPath(IReadOnlyList<string> configDirs, string pluginName)
    {
        Console.WriteLine($"[plugin-config] Looking for plugin '{pluginName}' in {configDirs.Count} directories");
        foreach (var dir in configDirs.Where(Directory.Exists))
        {
            Console.WriteLine($"[plugin-config]   Checking directory: {dir}");
            var direct = Path.Combine(dir, $"{pluginName}.json");
            Console.WriteLine($"[plugin-config]     Direct path: {direct} - {(File.Exists(direct) ? "EXISTS" : "not found")}");
            if (File.Exists(direct))
                return direct;

            var jsonFiles = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).ToList();
            Console.WriteLine($"[plugin-config]     Found {jsonFiles.Count} .json files in directory");
            var match = jsonFiles
                .FirstOrDefault(path =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        pluginName,
                        StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                Console.WriteLine($"[plugin-config]     Found match: {match}");
                return match;
            }
        }

        Console.WriteLine($"[plugin-config] Plugin '{pluginName}' not found in any config directory");
        return null;
    }

    // ── Path resolution ────────────────────────────────────────────────────────

    private string BuildExpectedConfigPath(string server)
    {
        var configRoot = ResolveConfigRootPath();
        return Path.Combine(configRoot, $"{server}.json");
    }

    // Bug #5 fix: returns null when no file is found rather than a non-null phantom path.
    private string? ResolveServerConfigPath(string server)
    {
        foreach (var configRoot in EnumerateConfigRoots())
        {
            var directPath = Path.Combine(configRoot, $"{server}.json");
            if (File.Exists(directPath))
                return directPath;

            if (!Directory.Exists(configRoot))
                continue;

            var match = Directory
                .EnumerateFiles(configRoot, "*.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        server,
                        StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
                return match;
        }

        return null;
    }

    private string ResolveConfigRootPath()
    {
        var envRoot = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return envRoot.Trim();

        var repoRoot = _gitOpsSettings.RepoPath;
        if (!string.IsNullOrWhiteSpace(repoRoot) && Directory.Exists(repoRoot))
            return repoRoot;

        return "/opt/rust-manager/config";
    }

    private IReadOnlyList<string> EnumerateConfigRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CatalogPathHelper.AddWithParents(roots, Environment.GetEnvironmentVariable("RUSTMGR_CONFIG"));
        CatalogPathHelper.AddWithParents(roots, _gitOpsSettings.RepoPath);
        CatalogPathHelper.AddWithParents(roots, Directory.GetCurrentDirectory());
        CatalogPathHelper.AddWithParents(roots, AppContext.BaseDirectory);
        CatalogPathHelper.AddWithParents(roots, "/opt/rust-manager/config", maxDepth: 1);
        return roots.ToList();
    }

    private static string BuildExpectedServerRootPath(string server)
    {
        var envRoot = Environment.GetEnvironmentVariable("RUST_SERVER_ROOT");
        var root = string.IsNullOrWhiteSpace(envRoot) ? "/srv/rust" : envRoot.Trim();
        return Path.Combine(root, server);
    }

    // ── Internal result type ───────────────────────────────────────────────────

    private sealed record ConfigMutationResult(string Key, JsonNode? ValueNode, string DisplayValue);
}
