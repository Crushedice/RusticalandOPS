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
        if (LooksLikeServerConfigRequest(lowered))
        {
            return await HandleServerConfigAsync(context, cancellationToken);
        }

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
        {
            return await ReadFileAsync(fullPath, filePath, cancellationToken);
        }

        if (!_gitOpsSettings.Enabled)
        {
            return new ToolExecutionResult(false, "File editing requires GitOps to be enabled (gitOps.enabled=true in config).", null, false, "not_configured");
        }

        var editContent = ExtractEditContent(context.Message);
        if (string.IsNullOrWhiteSpace(editContent))
        {
            if (!File.Exists(fullPath))
            {
                return new ToolExecutionResult(false, $"File not found: {filePath}. Check the path and try again.", null, false, "file_not_found");
            }

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
        {
            return new ToolExecutionResult(false, $"File not found: {filePath}.", null, false, "file_not_found");
        }

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
        // Match common config file patterns
        var match = Regex.Match(message,
            @"(?:show|read|view|open|edit|modify|update|change|set)\s+(?:the\s+)?(?:file\s+)?(?<path>[a-zA-Z0-9_./-]+\.(?:cfg|json|txt|ini|env))",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups["path"].Value.Trim();

        // Quoted path
        var quoted = Regex.Match(message, "\"(?<path>[^\"]+\\.(?:cfg|json|txt|ini|env))\"", RegexOptions.IgnoreCase);
        if (quoted.Success)
            return quoted.Groups["path"].Value.Trim();

        return null;
    }

    private static string? ExtractEditContent(string message)
    {
        // Look for content in a code block
        var block = Regex.Match(message, @"```[a-z]*\n(?<content>[\s\S]+?)```", RegexOptions.IgnoreCase);
        if (block.Success)
            return block.Groups["content"].Value.Trim();

        // Look for "set to: ..." or "content: ..."
        var setTo = Regex.Match(message, @"(?:set to|replace with|content):?\s*(?<content>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (setTo.Success)
            return setTo.Groups["content"].Value.Trim().Trim('"', '\'');

        return null;
    }

    private static string SanitizePath(string path)
    {
        return new string(path.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    }

    private async Task<ToolExecutionResult> HandleServerConfigAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var server = context.Route.Slots.ServerName;
        if (string.IsNullOrWhiteSpace(server))
        {
            server = ExtractServerNameFromMessage(context.Message);
        }

        if (string.IsNullOrWhiteSpace(server))
        {
            var knownServers = await RustToolHelper.GetKnownServersAsync(_api, cancellationToken);
            return new ToolExecutionResult(
                false,
                RustToolHelper.BuildScopeClarificationQuestion(AdminIntentType.FileEdit, knownServers, allowAllServers: false),
                null,
                false,
                "clarification_required");
        }

        var configPath = ResolveServerConfigPath(server);
        if (configPath is null || !File.Exists(configPath))
        {
            return new ToolExecutionResult(
                false,
                $"Server config not found for '{server}'. Expected path: {BuildExpectedConfigPath(server)}",
                server,
                false,
                "file_not_found");
        }

        var canonicalServer = Path.GetFileNameWithoutExtension(configPath);
        var mutation = TryExtractConfigMutation(context.Message, canonicalServer);
        if (mutation is null || IsReadRequest(context.Message))
        {
            var raw = await File.ReadAllTextAsync(configPath, cancellationToken);
            using var doc = JsonDocument.Parse(raw);
            var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            var serverRoot = BuildExpectedServerRootPath(canonicalServer);
            var header = $"{canonicalServer} server config ({configPath}). Server runtime root: {serverRoot}";
            var fullMessage = $"{header}\n\n```json\n{pretty}\n```";

            return new ToolExecutionResult(
                true,
                fullMessage,
                canonicalServer,
                true,
                Payload: pretty);
        }

        var currentRaw = await File.ReadAllTextAsync(configPath, cancellationToken);
        var configNode = JsonNode.Parse(currentRaw) as JsonObject;
        if (configNode is null)
        {
            return new ToolExecutionResult(false, $"Could not parse config for '{canonicalServer}'.", canonicalServer, false, "parse_error");
        }

        ApplyConfigMutation(configNode, mutation.Value.Key, mutation.Value.ValueNode);
        var prettyUpdated = configNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, prettyUpdated, cancellationToken);

        return new ToolExecutionResult(
            true,
            $"Updated {canonicalServer} config at {configPath}: set `{mutation.Value.Key}` to `{mutation.Value.DisplayValue}`. Server runtime root: {BuildExpectedServerRootPath(canonicalServer)}\n```json\n{prettyUpdated}\n```",
            canonicalServer,
            true,
            Payload: prettyUpdated);
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

    private static string? ExtractServerNameFromMessage(string message)
    {
        var match = Regex.Match(
            message,
            @"\b(?:for|on|from)\s+(?<server>[A-Za-z0-9][A-Za-z0-9._-]{1,})\b(?:\s+server)?",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["server"].Value.Trim() : null;
    }

    private static (string Key, JsonNode? ValueNode, string DisplayValue)? TryExtractConfigMutation(string message, string? serverName)
    {
        var setMatch = Regex.Match(
            message,
            @"\b(?:set|change|update)\s+(?<key>[A-Za-z0-9._-]+)\s*(?:to|=)\s*(?<value>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!setMatch.Success)
            return null;

        var key = setMatch.Groups["key"].Value.Trim();
        var valueText = setMatch.Groups["value"].Value.Trim().TrimEnd('.', ';');
        valueText = StripTrailingServerQualifier(valueText, serverName);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(valueText))
            return null;

        var valueNode = ParseJsonValue(valueText);
        var display = valueNode?.ToJsonString() ?? "null";
        return (key, valueNode, display);
    }

    private static string StripTrailingServerQualifier(string valueText, string? serverName)
    {
        if (string.IsNullOrWhiteSpace(valueText))
            return valueText;

        var result = valueText;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var escapedServer = Regex.Escape(serverName);
            result = Regex.Replace(
                result,
                $@"\s+(?:on|for)\s+{escapedServer}(?:\s+server)?\s*$",
                string.Empty,
                RegexOptions.IgnoreCase);
        }

        result = Regex.Replace(
            result,
            @"\s+(?:on|for)\s+[A-Za-z0-9._-]+(?:\s+server)?\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        return result.Trim();
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
        // rustmgr config commonly uses dotted keys as literal properties (e.g., "server.maxplayers"),
        // so prefer direct assignment. If that key does not already exist and nested object path exists,
        // update nested path instead.
        if (config.ContainsKey(key))
        {
            config[key] = value;
            return;
        }

        if (key.Contains('.') && TryAssignNested(config, key, value))
        {
            return;
        }

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
            {
                return false;
            }

            current = next;
        }

        current[segments[^1]] = value;
        return true;
    }

    private static string BuildExpectedConfigPath(string server)
    {
        var configRoot = ResolveConfigRootPath();
        return Path.Combine(configRoot, $"{server}.json");
    }

    private static string? ResolveServerConfigPath(string server)
    {
        var configRoot = ResolveConfigRootPath();
        var directPath = Path.Combine(configRoot, $"{server}.json");
        if (File.Exists(directPath))
            return directPath;

        if (!Directory.Exists(configRoot))
            return directPath;

        var match = Directory
            .EnumerateFiles(configRoot, "*.json", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    server,
                    StringComparison.OrdinalIgnoreCase));

        return match ?? directPath;
    }

    private static string ResolveConfigRootPath()
    {
        var envRoot = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG");
        return string.IsNullOrWhiteSpace(envRoot)
            ? "/opt/rust-manager/config"
            : envRoot.Trim();
    }

    private static string BuildExpectedServerRootPath(string server)
    {
        var envRoot = Environment.GetEnvironmentVariable("RUST_SERVER_ROOT");
        var root = string.IsNullOrWhiteSpace(envRoot) ? "/srv/rust" : envRoot.Trim();
        return Path.Combine(root, server);
    }
}
