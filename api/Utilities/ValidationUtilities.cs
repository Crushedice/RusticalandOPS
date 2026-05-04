namespace RusticalandOPS.Api.Utilities;

using System.Text.Json;
using RusticalandOPS.Api.Models.Shared;

public static class ValidationUtilities
{
    public static string? ValidateConfig(ServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Name))            return "Name is required.";
        if (string.IsNullOrWhiteSpace(cfg.ServerHostname))  return "server.hostname is required.";
        if (string.IsNullOrWhiteSpace(cfg.ServerIdentity))  return "server.identity is required.";
        if (string.IsNullOrWhiteSpace(cfg.ServerDir))       return "serverDir is required.";
        if (string.IsNullOrWhiteSpace(cfg.RconPassword))    return "rcon.password is required.";
        if (cfg.ServerPort     is <= 0 or > 65535)          return "server.port is invalid.";
        if (cfg.RconPort       is <= 0 or > 65535)          return "rcon.port is invalid.";
        if (cfg.AppPort        is <= 0 or > 65535)          return "app.port is invalid.";
        if (cfg.ServerWorldSize <= 0)                       return "server.worldsize must be > 0.";
        if (cfg.ServerMaxPlayers <= 0)                      return "server.maxplayers must be > 0.";
        return null;
    }

    public static List<string> FindConfigConflicts(ServerConfig cfg, string? ignoreServer = null)
    {
        var conflicts = new List<string>();
        var configDir = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config";

        foreach (var path in Directory.GetFiles(configDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var other = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (other is null) continue;
                if (!string.IsNullOrWhiteSpace(ignoreServer) && string.Equals(other.Name, ignoreServer, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (other.ServerPort == cfg.ServerPort) conflicts.Add($"server.port {cfg.ServerPort} already used by '{other.Name}'.");
                if (other.RconPort   == cfg.RconPort)   conflicts.Add($"rcon.port {cfg.RconPort} already used by '{other.Name}'.");
                if (other.AppPort    == cfg.AppPort)    conflicts.Add($"app.port {cfg.AppPort} already used by '{other.Name}'.");
                if (string.Equals(other.ServerIdentity, cfg.ServerIdentity, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"server.identity '{cfg.ServerIdentity}' already used by '{other.Name}'.");
                if (string.Equals(other.ServerDir, cfg.ServerDir, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"serverDir '{cfg.ServerDir}' already used by '{other.Name}'.");
            }
            catch
            {
                conflicts.Add($"Failed to inspect existing config '{Path.GetFileName(path)}'.");
            }
        }

        return conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ValidationResult ValidateJsonFile(string path)
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

    public static ValidationResult ValidateOxidePluginFile(string path)
    {
        var text = File.ReadAllText(path);
        var infoMatch = System.Text.RegularExpressions.Regex.Match(
            text,
            "\\[\\s*Info\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<author>[^\"]+)\"\\s*,\\s*\"(?<version>[^\"]+)\"\\s*\\)\\s*\\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var pluginName = infoMatch.Success ? infoMatch.Groups["name"].Value.Trim() : null;
        var pluginAuthor = infoMatch.Success ? infoMatch.Groups["author"].Value.Trim() : null;
        var pluginVersion = infoMatch.Success ? infoMatch.Groups["version"].Value.Trim() : null;
        var pluginSlug = !string.IsNullOrWhiteSpace(pluginName) ? ToPluginSlug(pluginName) : null;
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        var commands = ExtractPluginCommands(text);
        var permissions = ExtractPluginPermissions(text);
        var hooks = ExtractPluginHooks(text);
        var configKeys = ExtractPluginConfigKeys(text);

        var hasPluginBase =
            text.Contains(": RustPlugin", StringComparison.Ordinal) ||
            text.Contains(": CovalencePlugin", StringComparison.Ordinal) ||
            text.Contains(": CSPlugin", StringComparison.Ordinal);

        if (!hasPluginBase)
        {
            return new ValidationResult
            {
                Path = path,
                Ok = false,
                Message = "Missing expected Oxide plugin base class.",
                PluginName = pluginName,
                PluginAuthor = pluginAuthor,
                PluginVersion = pluginVersion,
                PluginSlug = pluginSlug,
                SourceHash = sourceHash,
                Commands = commands,
                Permissions = permissions,
                Hooks = hooks,
                ConfigKeys = configKeys
            };
        }

        var open = text.Count(c => c == '{');
        var close = text.Count(c => c == '}');
        if (open != close)
        {
            return new ValidationResult
            {
                Path = path,
                Ok = false,
                Message = $"Brace mismatch: {open} '{{' vs {close} '}}'.",
                PluginName = pluginName,
                PluginAuthor = pluginAuthor,
                PluginVersion = pluginVersion,
                PluginSlug = pluginSlug,
                SourceHash = sourceHash,
                Commands = commands,
                Permissions = permissions,
                Hooks = hooks,
                ConfigKeys = configKeys
            };
        }

        return new ValidationResult
        {
            Path = path,
            Ok = true,
            PluginName = pluginName,
            PluginAuthor = pluginAuthor,
            PluginVersion = pluginVersion,
            PluginSlug = pluginSlug,
            SourceHash = sourceHash,
            Commands = commands,
            Permissions = permissions,
            Hooks = hooks,
            ConfigKeys = configKeys
        };
    }

    public static string ToPluginSlug(string input)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(input.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-");
        return slug.Trim('-');
    }

    public static List<RusticalandOPS.Api.Models.Shared.PluginCommandReferenceView> ExtractPluginCommands(string source)
    {
        var commands = new List<RusticalandOPS.Api.Models.Shared.PluginCommandReferenceView>();
        AddPluginAttributeCommands(commands, source, @"\[\s*ChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "ChatCommand");
        AddPluginAttributeCommands(commands, source, @"\[\s*ConsoleCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "ConsoleCommand");
        AddPluginAttributeCommands(commands, source, @"\[\s*Command\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "CovalenceCommand");

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, @"cmd\.AddChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*,\s*this\s*,\s*(?:nameof\s*\(\s*)?""?(?<handler>[A-Za-z_][A-Za-z0-9_]*)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            commands.Add(new PluginCommandReferenceView(((System.Text.RegularExpressions.Match)match).Groups["cmd"].Value.Trim(), "ChatCommand", ((System.Text.RegularExpressions.Match)match).Groups["handler"].Value.Trim()));

        return commands
            .GroupBy(command => $"{command.Type}:{command.Command}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(command => command.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddPluginAttributeCommands(List<RusticalandOPS.Api.Models.Shared.PluginCommandReferenceView> commands, string source, string pattern, string type)
    {
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            commands.Add(new RusticalandOPS.Api.Models.Shared.PluginCommandReferenceView(((System.Text.RegularExpressions.Match)match).Groups["cmd"].Value.Trim(), type, FindPluginHandlerAfter(source, ((System.Text.RegularExpressions.Match)match).Index + ((System.Text.RegularExpressions.Match)match).Length)));
    }

    private static string FindPluginHandlerAfter(string source, int index)
    {
        var tail = source[Math.Min(index, source.Length)..];
        var match = System.Text.RegularExpressions.Regex.Match(
            tail,
            @"\b(?:private|public|protected|internal)?\s*(?:void|bool|object|string)\s+(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["handler"].Value.Trim() : string.Empty;
    }

    public static List<string> ExtractPluginPermissions(string source)
    {
        var patterns = new[]
        {
            @"permission\.RegisterPermission\s*\(\s*""(?<value>[^""]+)""",
            @"permission\.UserHasPermission\s*\([^,]+,\s*""(?<value>[^""]+)""",
            @"\.HasPermission\s*\(\s*""(?<value>[^""]+)"""
        };
        return patterns
            .SelectMany(pattern => System.Text.RegularExpressions.Regex.Matches(source, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Select(match => match.Groups["value"].Value.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> ExtractPluginHooks(string source)
    {
        var known = new[] { "OnServerInitialized", "Init", "Loaded", "Unload", "OnPlayerConnected", "OnPlayerDisconnected", "OnEntityDeath", "OnPlayerDeath", "OnUserChat", "CanBuild", "CanLootEntity" };
        return known
            .Where(hook => System.Text.RegularExpressions.Regex.IsMatch(source, $@"\b(?:void|object|bool|string)\s+{System.Text.RegularExpressions.Regex.Escape(hook)}\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> ExtractPluginConfigKeys(string source)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, @"Config\s*\[\s*""(?<key>[^""]+)""\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            keys.Add(((System.Text.RegularExpressions.Match)match).Groups["key"].Value.Trim());
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, @"GetConfig\s*\(\s*""(?<key>[^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            keys.Add(((System.Text.RegularExpressions.Match)match).Groups["key"].Value.Trim());
        return keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
