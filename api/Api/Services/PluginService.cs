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
        var roots = GetOxideRootCandidates(cfg);

        string[] configFiles;
        string[] pluginFiles;
        try
        {
            configFiles = roots
                .Select(root => Path.Combine(root, "config"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            pluginFiles = roots
                .Select(root => Path.Combine(root, "plugins"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            return new { server, ok = false, error = "access_denied", note = ex.Message };
        }

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
        var pluginsDir = GetOxideRootCandidates(cfg)
            .Select(root => Path.Combine(root, "plugins"))
            .FirstOrDefault(Directory.Exists);

        if (pluginsDir is null)
        {
            return new { server, updates = Array.Empty<object>(), note = "plugins directory not found in any expected location" };
        }

        List<(string? Name, string? Version)> plugins;
        try
        {
            plugins = Directory.GetFiles(pluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(ParsePlugin)
                .Where(p => p.Name is not null)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new { server, updates = Array.Empty<object>(), error = "access_denied", path = pluginsDir, note = $"Read permission denied for '{pluginsDir}'. Grant read access to the RustOps API service user for that directory." };
        }

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

            var downloadUrl = needsUpdate && first.TryGetProperty("download_url", out var dlNode)
                ? dlNode.GetString() : null;

            updates.Add(new
            {
                plugin = plugin.Name,
                current,
                latest,
                downloadUrl,
                state = needsUpdate ? "update_available" : "current"
            });
        }

        return new { server, updates };
    }

    public async Task<object> InstallPluginAsync(string server, string pluginName, string downloadUrl, CancellationToken cancellationToken)
    {
        var cfg = _rust.LoadConfig(server) ?? throw new InvalidOperationException($"No config for {server}");
        var pluginsDir = GetOxideRootCandidates(cfg)
                             .Select(root => Path.Combine(root, "plugins"))
                             .FirstOrDefault(Directory.Exists)
                         ?? Path.Combine(cfg.ServerDir, "oxide", "plugins");
        Directory.CreateDirectory(pluginsDir);

        // Sanitize the plugin name to a safe filename.
        var safeName = string.Concat(pluginName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        var destPath = Path.Combine(pluginsDir, $"{safeName}.cs");

        var bytes = await _http.GetByteArrayAsync(downloadUrl, cancellationToken);
        await File.WriteAllBytesAsync(destPath, bytes, cancellationToken);

        return new { server, plugin = pluginName, installed = true, bytes = bytes.Length };
    }

    private static List<string> GetOxideRootCandidates(ServerConfig cfg)
    {
        // Explicit override takes priority — useful when oxide lives outside serverDir.
        if (!string.IsNullOrWhiteSpace(cfg.OxideDir))
            return new List<string> { cfg.OxideDir.TrimEnd('/') };

        return new[]
        {
            Path.Combine(cfg.ServerDir, "oxide"),
            Path.Combine(cfg.ServerDir, cfg.ServerIdentity, "oxide"),
            Path.Combine(cfg.ServerDir, "server", cfg.ServerIdentity, "oxide"),
            Path.Combine("/srv/rust", cfg.Name, "oxide"),
            Path.Combine("/srv/rust", cfg.ServerIdentity, "oxide"),
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
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
