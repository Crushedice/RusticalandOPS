using System.Text.Json;
using System.Text.RegularExpressions;
using rustmgrapi.Api.Models;

namespace rustmgrapi.Api.Services;

internal sealed class PluginService
{
    private readonly RustManagerService _rust;
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "RustOpsAgent/1.0" } }
    };

    public PluginService(RustManagerService rust)
    {
        _rust = rust;
    }

    public object ValidateOxide(string server)
    {
        var cfg = _rust.LoadConfig(server) ?? throw new InvalidOperationException($"No config for {server}");
        var roots = GetOxideRootCandidates(cfg, server);

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
        var candidates = GetOxideRootCandidates(cfg, server)
            .Select(root => Path.Combine(root, "plugins"))
            .ToList();
        var pluginsDir = candidates.FirstOrDefault(Directory.Exists);

        if (pluginsDir is null)
        {
            return new { server, updates = Array.Empty<object>(), note = "plugins directory not found in any expected location", triedPaths = candidates };
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
            return new { server, updates = Array.Empty<object>(), error = "access_denied", path = pluginsDir, note = $"Read permission denied for '{pluginsDir}'. Grant read access to the RustOps API service user for that directory.", triedPaths = candidates };
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
            var resultName = first.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            var resultTitle = first.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
            var isMatch = string.Equals(resultName, plugin.Name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(resultTitle, plugin.Name, StringComparison.OrdinalIgnoreCase);
            if (!isMatch)
            {
                updates.Add(new { plugin = plugin.Name, state = "not_found", reason = "umod name mismatch" });
                continue;
            }

            var latest = first.TryGetProperty("latest_release_version", out var latestNode)
                ? latestNode.GetString()
                : first.TryGetProperty("latest_release_version_formatted", out var latestFNode)
                    ? latestFNode.GetString()
                    : null;
            var current = plugin.Version;
            var needsUpdate = !string.IsNullOrWhiteSpace(latest) && !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);

            // uMod search returns a page slug in `url`, not a direct download_url.
            var pluginSlug = first.TryGetProperty("url", out var urlNode) ? urlNode.GetString()?.TrimStart('/') : null;
            var fallbackSlug = plugin.Name is null
                ? null
                : $"plugins/{Uri.EscapeDataString(plugin.Name.ToLowerInvariant())}";
            var downloadUrl = needsUpdate
                ? $"https://umod.org/{(string.IsNullOrWhiteSpace(pluginSlug) ? fallbackSlug : pluginSlug)}/download?id={latest}"
                : null;

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
        var pluginsDir = GetOxideRootCandidates(cfg, server)
                             .Select(root => Path.Combine(root, "plugins"))
                             .FirstOrDefault(Directory.Exists)
                         ?? Path.Combine(string.IsNullOrWhiteSpace(cfg.ServerDir) ? $"/srv/rust/{server}" : cfg.ServerDir, "oxide", "plugins");
        Directory.CreateDirectory(pluginsDir);

        // Sanitize the plugin name to a safe filename.
        var safeName = string.Concat(pluginName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        var destPath = Path.Combine(pluginsDir, $"{safeName}.cs");

        var bytes = await _http.GetByteArrayAsync(downloadUrl, cancellationToken);
        await File.WriteAllBytesAsync(destPath, bytes, cancellationToken);

        return new { server, plugin = pluginName, installed = true, bytes = bytes.Length };
    }

    // serverName is the filename-derived name (always known), used when cfg.Name is empty
    // because LoadConfig returns raw JSON which may omit the "name" field.
    private static List<string> GetOxideRootCandidates(ServerConfig cfg, string serverName)
    {
        // Explicit override takes priority — useful when oxide lives outside serverDir.
        if (!string.IsNullOrWhiteSpace(cfg.OxideDir))
            return new List<string> { cfg.OxideDir.TrimEnd('/') };

        // Apply the same fallbacks the Program.cs normalizer applies so raw configs work.
        var name      = string.IsNullOrWhiteSpace(cfg.Name)         ? serverName    : cfg.Name;
        var identity  = string.IsNullOrWhiteSpace(cfg.ServerIdentity) ? name         : cfg.ServerIdentity;
        var root      = Environment.GetEnvironmentVariable("RUST_SERVER_ROOT") ?? "/srv/rust";
        var serverDir = string.IsNullOrWhiteSpace(cfg.ServerDir)    ? Path.Combine(root, name) : cfg.ServerDir;

        return new[]
        {
            Path.Combine(root, name, "oxide"),
            Path.Combine(serverDir, "oxide"),
            Path.Combine(serverDir, identity, "oxide"),
            Path.Combine(serverDir, "server", identity, "oxide"),
            Path.Combine(root, identity, "oxide"),
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
