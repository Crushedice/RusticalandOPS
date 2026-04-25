using System.Text.Json;
using System.Text.RegularExpressions;
using rustmgrapi.Api.Models;

namespace rustmgrapi.Api.Services;

internal sealed class PluginService
{
    private readonly RustManagerService _rust;
    private readonly HttpClient _http = new();

    public PluginService(RustManagerService rust)
    {
        _rust = rust;
    }

    public object ValidateOxide(string server)
    {
        var cfg = _rust.LoadConfig(server) ?? throw new InvalidOperationException($"No config for {server}");
        var roots = new[]
        {
            Path.Combine(cfg.ServerDir, "oxide"),
            Path.Combine(Path.GetDirectoryName(cfg.LogFile) ?? cfg.ServerDir, "oxide")
        };

        var configFiles = roots
            .Select(root => Path.Combine(root, "config"))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var pluginFiles = roots
            .Select(root => Path.Combine(root, "plugins"))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var invalidJson = new List<string>();
        foreach (var json in configFiles)
        {
            try { _ = JsonDocument.Parse(File.ReadAllText(json)); }
            catch { invalidJson.Add(json); }
        }

        var badPlugins = new List<string>();
        foreach (var plugin in pluginFiles)
        {
            var text = File.ReadAllText(plugin);
            var hasBase = text.Contains(": RustPlugin", StringComparison.Ordinal) || text.Contains(": CovalencePlugin", StringComparison.Ordinal) || text.Contains(": CSPlugin", StringComparison.Ordinal);
            if (!hasBase)
            {
                badPlugins.Add(plugin);
            }
        }

        return new
        {
            server,
            ok = invalidJson.Count == 0 && badPlugins.Count == 0,
            configFiles = configFiles.Length,
            pluginFiles = pluginFiles.Length,
            invalidJson,
            badPlugins
        };
    }

    public async Task<object> CheckUpdatesAsync(string server, CancellationToken cancellationToken)
    {
        var cfg = _rust.LoadConfig(server) ?? throw new InvalidOperationException($"No config for {server}");
        var pluginsDir = Path.Combine(cfg.ServerDir, "oxide", "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            return new { server, updates = Array.Empty<object>(), note = "plugins directory missing" };
        }

        var plugins = Directory.GetFiles(pluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(ParsePlugin)
            .Where(p => p.Name is not null)
            .ToList();

        var updates = new List<object>();
        foreach (var plugin in plugins)
        {
            var searchName = Uri.EscapeDataString(plugin.Name!);
            var url = $"https://umod.org/plugins/search.json?query={searchName}&page=1&sort=title&sortdir=asc&filter=rust";
            string body;
            try
            {
                body = await _http.GetStringAsync(url, cancellationToken);
            }
            catch
            {
                updates.Add(new { plugin = plugin.Name, state = "not_found", reason = "umod lookup failed" });
                continue;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                updates.Add(new { plugin = plugin.Name, state = "not_found" });
                continue;
            }

            var first = data[0];
            var latest = first.TryGetProperty("latest_release_version", out var latestNode) ? latestNode.GetString() : null;
            var current = plugin.Version;
            var needsUpdate = !string.IsNullOrWhiteSpace(latest) && !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);

            updates.Add(new
            {
                plugin = plugin.Name,
                current,
                latest,
                state = needsUpdate ? "update_available" : "current"
            });
        }

        return new { server, updates };
    }

    private static (string? Name, string? Version) ParsePlugin(string path)
    {
        var text = File.ReadAllText(path);
        var info = Regex.Match(text, "\\[\\s*Info\\s*\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<author>[^\"]*)\"\\s*,\\s*\"(?<version>[^\"]+)\"\\s*\\)\\s*\\]", RegexOptions.IgnoreCase);
        if (!info.Success)
        {
            return (Path.GetFileNameWithoutExtension(path), null);
        }

        return (info.Groups["name"].Value.Trim(), info.Groups["version"].Value.Trim());
    }
}
