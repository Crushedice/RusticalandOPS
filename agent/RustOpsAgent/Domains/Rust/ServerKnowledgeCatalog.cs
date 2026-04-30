using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RustOpsAgent.Domains.Rust;

internal sealed class ServerKnowledgeCatalog
{
    private static readonly Regex DottedIdentifierRegex = new(@"\b[A-Za-z][A-Za-z0-9_-]*\.[A-Za-z0-9_.-]+\b", RegexOptions.Compiled);
    // Kept for TXT-format fallback parsing only
    private static readonly Regex ParenValueRegex = new(@"\((?<value>[^()]*)\)", RegexOptions.Compiled);
    private static readonly Regex GeneratedMarkerRegex = new(@"\(\s*Generated\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly object _gate = new();
    private readonly string? _variablesPathOverride;
    private readonly string? _commandsPathOverride;

    private string? _loadedVariablesPath;
    private string? _loadedCommandsPath;
    private DateTime _loadedVariablesLastWriteUtc = DateTime.MinValue;
    private DateTime _loadedCommandsLastWriteUtc = DateTime.MinValue;
    private ServerKnowledgeSnapshot _snapshot = ServerKnowledgeSnapshot.Empty;

    public ServerKnowledgeCatalog(string? variablesPath = null, string? commandsPath = null)
    {
        _variablesPathOverride = variablesPath;
        _commandsPathOverride = commandsPath;
    }

    public ServerKnowledgeSnapshot GetSnapshot()
    {
        var variablesPath = ResolveVariablesPath();
        var commandsPath = ResolveCommandsPath();
        var variablesStamp = GetLastWriteUtc(variablesPath);
        var commandsStamp = GetLastWriteUtc(commandsPath);

        lock (_gate)
        {
            var unchanged =
                string.Equals(_loadedVariablesPath, variablesPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_loadedCommandsPath, commandsPath, StringComparison.OrdinalIgnoreCase) &&
                _loadedVariablesLastWriteUtc == variablesStamp &&
                _loadedCommandsLastWriteUtc == commandsStamp;

            if (unchanged)
                return _snapshot;

            _snapshot = LoadSnapshot(variablesPath, commandsPath);
            _loadedVariablesPath = variablesPath;
            _loadedCommandsPath = commandsPath;
            _loadedVariablesLastWriteUtc = variablesStamp;
            _loadedCommandsLastWriteUtc = commandsStamp;
            return _snapshot;
        }
    }

    public bool TryGetVariable(string? name, out ServerVariableDefinition? variable)
    {
        variable = null;
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var snapshot = GetSnapshot();
        if (!snapshot.Variables.TryGetValue(normalized, out var match))
            return false;

        variable = match;
        return true;
    }

    public bool TryGetCommand(string? name, out ServerCommandDefinition? command)
    {
        command = null;
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var snapshot = GetSnapshot();
        if (!snapshot.Commands.TryGetValue(normalized, out var match))
            return false;

        command = match;
        return true;
    }

    public CatalogLookupMatch? FindMentionedEntry(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var snapshot = GetSnapshot();
        var candidates = DottedIdentifierRegex.Matches(message)
            .Select(m => NormalizeName(m.Value))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (snapshot.Variables.TryGetValue(candidate!, out var variable))
                return new CatalogLookupMatch(CatalogEntryType.Variable, variable.Name, variable.Description);

            if (snapshot.Commands.TryGetValue(candidate!, out var command))
                return new CatalogLookupMatch(CatalogEntryType.Command, command.Name, command.Description);
        }

        return null;
    }

    public IReadOnlyList<ServerVariableDefinition> SearchVariables(string query, int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<ServerVariableDefinition>();

        var terms = ExtractSearchTerms(query);
        if (terms.Count == 0)
            return Array.Empty<ServerVariableDefinition>();

        var snapshot = GetSnapshot();
        return snapshot.Variables.Values
            .Select(variable => new
            {
                Variable = variable,
                Score = ScoreVariable(variable, terms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Variable.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => item.Variable)
            .ToList();
    }

    public IReadOnlyList<ServerCommandDefinition> SearchCommands(string query, int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<ServerCommandDefinition>();

        var terms = ExtractSearchTerms(query);
        if (terms.Count == 0)
            return Array.Empty<ServerCommandDefinition>();

        var snapshot = GetSnapshot();
        return snapshot.Commands.Values
            .Select(command => new
            {
                Command = command,
                Score = ScoreCommand(command, terms)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => item.Command)
            .ToList();
    }

    private static ServerKnowledgeSnapshot LoadSnapshot(string? variablesPath, string? commandsPath)
    {
        var variables = new Dictionary<string, ServerVariableDefinition>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(variablesPath) && File.Exists(variablesPath))
        {
            var useJson = IsJsonlPath(variablesPath);
            foreach (var line in File.ReadLines(variablesPath))
            {
                var parsed = useJson ? ParseVariableEntry(line) : ParseVariableLine(line);
                if (parsed is null)
                    continue;
                variables[NormalizeName(parsed.Name)!] = parsed;
            }
        }

        var commands = new Dictionary<string, ServerCommandDefinition>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(commandsPath) && File.Exists(commandsPath))
        {
            var useJson = IsJsonlPath(commandsPath);
            foreach (var line in File.ReadLines(commandsPath))
            {
                var parsed = useJson ? ParseCommandEntry(line) : ParseCommandLine(line);
                if (parsed is null)
                    continue;
                commands[NormalizeName(parsed.Name)!] = parsed;
            }
        }

        return new ServerKnowledgeSnapshot(variables, commands, variablesPath, commandsPath, DateTime.UtcNow);
    }

    private static bool IsJsonlPath(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    // ── JSONL parsers (primary format) ────────────────────────────────────────

    internal static ServerVariableDefinition? ParseVariableEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            return null;
        try
        {
            var obj = JsonNode.Parse(line) as JsonObject;
            if (obj is null)
                return null;

            var convar = obj["convar"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(convar))
                return null;

            var generated = obj["generated_on_start"]?.GetValue<bool>() ?? false;
            var defaultRaw = obj["default_raw"]?.GetValue<string>();
            var defaultType = obj["default_type"]?.GetValue<string>();
            var description = obj["description"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(description))
                description = null;

            return new ServerVariableDefinition(convar.Trim(), generated, defaultRaw, description, defaultType);
        }
        catch
        {
            return null;
        }
    }

    internal static ServerCommandDefinition? ParseCommandEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            return null;
        try
        {
            var obj = JsonNode.Parse(line) as JsonObject;
            if (obj is null)
                return null;

            var command = obj["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command))
                return null;

            var generated = obj["generated_command_metadata"]?.GetValue<bool>() ?? false;
            var description = obj["description"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(description))
                description = null;
            var riskLevel = obj["risk_level_inferred"]?.GetValue<string>();
            var tagsNode = obj["tags"] as JsonArray;
            IReadOnlyList<string>? tags = tagsNode?
                .Select(t => t?.GetValue<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList();

            return new ServerCommandDefinition(command.Trim(), generated, description, riskLevel, tags);
        }
        catch
        {
            return null;
        }
    }

    // ── TXT parsers (fallback for legacy files) ────────────────────────────────

    internal static ServerVariableDefinition? ParseVariableLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var nameMatch = Regex.Match(trimmed, @"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)");
        if (!nameMatch.Success)
            return null;

        var name = nameMatch.Groups["name"].Value.Trim();
        var remainder = trimmed[nameMatch.Length..].Trim();
        var generated = GeneratedMarkerRegex.IsMatch(remainder);
        var defaultValue = ExtractTxtDefaultValue(remainder);

        var description = GeneratedMarkerRegex.Replace(remainder, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            // Only strip the last paren group if it equals the extracted default value
            var escaped = Regex.Escape(defaultValue);
            description = Regex.Replace(description, $@"\(\s*{escaped}\s*\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        description = Regex.Replace(description, @"\s{2,}", " ").Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        return new ServerVariableDefinition(name, generated, defaultValue, description);
    }

    internal static ServerCommandDefinition? ParseCommandLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var nameMatch = Regex.Match(trimmed, @"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)");
        if (!nameMatch.Success)
            return null;

        var name = nameMatch.Groups["name"].Value.Trim();
        var remainder = trimmed[nameMatch.Length..].Trim();
        var generated = GeneratedMarkerRegex.IsMatch(remainder);

        var description = GeneratedMarkerRegex.Replace(remainder, string.Empty);
        description = Regex.Replace(description, @"^\(\s*\)\s*", string.Empty);
        description = Regex.Replace(description, @"\s{2,}", " ").Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        return new ServerCommandDefinition(name, generated, description);
    }

    private static string? ExtractTxtDefaultValue(string remainder)
    {
        var matches = ParenValueRegex.Matches(remainder);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var candidate = matches[i].Groups["value"].Value.Trim();
            if (candidate.Length == 0)
                continue;
            if (candidate.Equals("Generated", StringComparison.OrdinalIgnoreCase))
                continue;
            // Reject anything that reads like a description phrase (contains spaces and no digit/bool)
            if (candidate.Contains(' ') &&
                !bool.TryParse(candidate, out _) &&
                !double.TryParse(candidate, out _))
                continue;
            return candidate;
        }
        return null;
    }

    // ── Path resolution ────────────────────────────────────────────────────────

    private string? ResolveVariablesPath() =>
        ResolveCatalogPath(
            _variablesPathOverride,
            "RUSTOPS_SERVER_VARIABLES_PATH",
            "ServerVariables.agent-readable.jsonl",
            "ServerVariables.txt");

    private string? ResolveCommandsPath() =>
        ResolveCatalogPath(
            _commandsPathOverride,
            "RUSTOPS_SERVER_COMMANDS_PATH",
            "ServerCommands.agent-readable.jsonl",
            "ServerCommands.txt");

    private static string? ResolveCatalogPath(string? overridePath, string envName, params string[] defaultFileNames)
    {
        var direct = NormalizePathIfExists(overridePath);
        if (direct is not null)
            return direct;

        direct = NormalizePathIfExists(Environment.GetEnvironmentVariable(envName));
        if (direct is not null)
            return direct;

        foreach (var root in CatalogPathHelper.EnumerateSearchRoots())
        {
            foreach (var fileName in defaultFileNames)
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? NormalizePathIfExists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (Directory.Exists(trimmed))
            return null;
        return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
    }

    private static DateTime GetLastWriteUtc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DateTime.MinValue;
        return File.GetLastWriteTimeUtc(path);
    }

    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = name.Trim().Trim('"', '\'', '`');
        trimmed = Regex.Replace(trimmed, @"\(\s*\)$", string.Empty);
        return trimmed.Trim().ToLowerInvariant();
    }

    private static IReadOnlyList<string> ExtractSearchTerms(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "about", "all", "and", "are", "available", "can", "catalog", "convar",
            "convars", "control", "does", "for", "give", "have", "list", "main", "me",
            "of", "on", "server", "show", "switch", "switches", "that", "the", "these",
            "to", "variable", "variables", "what", "which", "with"
        };

        return Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9._-]{2,}")
            .Select(match => match.Value.Trim('.', '-', '_'))
            .Where(term => term.Length >= 2 && !stopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ScoreVariable(ServerVariableDefinition variable, IReadOnlyList<string> terms)
    {
        var name = variable.Name.ToLowerInvariant();
        var category = name.Split('.', 2)[0];
        var description = variable.Description?.ToLowerInvariant() ?? string.Empty;
        var score = 0;

        foreach (var term in terms)
        {
            if (name.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (name.EndsWith("." + term, StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
            else if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            if (category.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 12;

            if (description.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 8;
        }

        return score;
    }

    private static int ScoreCommand(ServerCommandDefinition command, IReadOnlyList<string> terms)
    {
        var name = command.Name.ToLowerInvariant();
        var category = name.Split('.', 2)[0];
        var description = command.Description?.ToLowerInvariant() ?? string.Empty;
        var tags = command.Tags is null ? string.Empty : string.Join(" ", command.Tags).ToLowerInvariant();
        var score = 0;

        foreach (var term in terms)
        {
            if (name.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (name.EndsWith("." + term, StringComparison.OrdinalIgnoreCase))
                score += 60;
            else if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 40;

            if (category.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 12;
            if (description.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 8;
            if (tags.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 6;
        }

        return score;
    }
}

// Shared path-search helper used by both ServerKnowledgeCatalog and RustFileEditToolHandler.
internal static class CatalogPathHelper
{
    public static IReadOnlyList<string> EnumerateSearchRoots(params string?[] extraRoots)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddWithParents(roots, Directory.GetCurrentDirectory());
        AddWithParents(roots, AppContext.BaseDirectory);

        foreach (var env in new[]
        {
            Environment.GetEnvironmentVariable("RUSTOPS_REPO_ROOT"),
            Environment.GetEnvironmentVariable("RUSTOPS_AGENT_ROOT"),
            Environment.GetEnvironmentVariable("RUSTMGR_ROOT"),
            Environment.GetEnvironmentVariable("RUSTMGR_CONFIG")
        })
        {
            AddWithParents(roots, env);
        }

        foreach (var extra in extraRoots)
            AddWithParents(roots, extra);

        return roots.ToList();
    }

    public static void AddWithParents(HashSet<string> roots, string? path, int maxDepth = 6)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            for (var depth = 0; depth < maxDepth && current is not null; depth++)
            {
                roots.Add(current.FullName);
                current = current.Parent;
            }
        }
        catch
        {
            // Ignore invalid path candidates.
        }
    }
}

internal sealed record ServerKnowledgeSnapshot(
    IReadOnlyDictionary<string, ServerVariableDefinition> Variables,
    IReadOnlyDictionary<string, ServerCommandDefinition> Commands,
    string? VariablesPath,
    string? CommandsPath,
    DateTime LoadedAtUtc)
{
    public static ServerKnowledgeSnapshot Empty { get; } =
        new(new Dictionary<string, ServerVariableDefinition>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ServerCommandDefinition>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            DateTime.MinValue);
}

internal sealed record ServerVariableDefinition(
    string Name,
    bool Generated,
    string? DefaultValue,
    string? Description,
    string? DefaultType = null);

internal sealed record ServerCommandDefinition(
    string Name,
    bool Generated,
    string? Description,
    string? RiskLevel = null,
    IReadOnlyList<string>? Tags = null);

internal enum CatalogEntryType
{
    Variable,
    Command
}

internal sealed record CatalogLookupMatch(
    CatalogEntryType EntryType,
    string Name,
    string? Description);
