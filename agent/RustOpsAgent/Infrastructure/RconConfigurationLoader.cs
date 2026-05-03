using System.Text.Json;
using Sentry;
using RustOpsAgent.Domains.Rust.Rcon;

namespace RustOpsAgent.Infrastructure;

/// <summary>
/// Loads RCON configuration for both local and remote Rust servers.
/// Integrates with the RustOpsApiClient to fetch remote server credentials.
/// </summary>
internal sealed class RconConfigurationLoader
{
    private readonly string _configDir;
    private readonly RustOpsApiClient? _apiClient;

    public RconConfigurationLoader(
        string configDir,
        RustOpsApiClient? apiClient)
    {
        _configDir = configDir;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Load all RCON server configurations (local + remote).
    /// </summary>
    public async Task<IReadOnlyList<(string Name, Uri RconUri, string Password)>> LoadAllConfigurationsAsync(
        CancellationToken cancellationToken = default)
    {
        var configs = new List<(string, Uri, string)>();

        // Load local servers
        var localConfigs = LoadLocalServerConfigurations();
        configs.AddRange(localConfigs);
        RustOpsSentry.AddBreadcrumb($"Loaded {localConfigs.Count} local RCON configurations", "rcon");

        // Load remote servers if API client is available
        if (_apiClient != null)
        {
            try
            {
                var remoteConfigs = await LoadRemoteServerConfigurationsAsync(cancellationToken);
                configs.AddRange(remoteConfigs);
                RustOpsSentry.AddBreadcrumb($"Loaded {remoteConfigs.Count} remote RCON configurations", "rcon");
            }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureException(ex, "Failed to load remote server configurations", "rcon");
            }
        }
        else
        {
            RustOpsSentry.AddBreadcrumb("API client not available, skipping remote server configurations", "rcon");
        }

        return configs;
    }

    /// <summary>
    /// Load RCON configurations for local servers.
    /// </summary>
    private List<(string Name, Uri RconUri, string Password)> LoadLocalServerConfigurations()
    {
        var configs = new List<(string, Uri, string)>();

        if (!Directory.Exists(_configDir))
        {
            RustOpsSentry.AddBreadcrumb($"Config directory does not exist: {_configDir}", "rcon");
            return configs;
        }

        var configFiles = Directory.GetFiles(_configDir, "*.json", SearchOption.TopDirectoryOnly);

        foreach (var filePath in configFiles)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Extract RCON configuration
                var rconIp = root.TryGetProperty("rconIp", out var ip) ? ip.GetString() : "127.0.0.1";
                var rconPort = root.TryGetProperty("rconPort", out var port) && port.ValueKind == JsonValueKind.Number
                    ? port.GetInt32()
                    : 28016;
                var rconPassword = root.TryGetProperty("rconPassword", out var pwd) ? pwd.GetString() : null;

                if (string.IsNullOrWhiteSpace(rconPassword))
                {
                    RustOpsSentry.AddBreadcrumb($"Skipping server '{fileName}': no RCON password configured", "rcon");
                    continue;
                }

                var rconUri = new Uri($"ws://{rconIp?.Trim().Trim('"')}/{rconPassword}");
                configs.Add((fileName, rconUri, rconPassword));
                RustOpsSentry.AddBreadcrumb($"Loaded local RCON config for '{fileName}'", "rcon");
            }
            catch (Exception ex)
            {
                RustOpsSentry.CaptureException(ex, $"Failed to load RCON config from {filePath}", "rcon");
            }
        }

        return configs;
    }

    /// <summary>
    /// Load RCON configurations for remote servers via API.
    /// </summary>
    private async Task<List<(string Name, Uri RconUri, string Password)>> LoadRemoteServerConfigurationsAsync(
        CancellationToken cancellationToken)
    {
        var configs = new List<(string, Uri, string)>();

        if (_apiClient == null)
            return configs;

        try
        {
            // Fetch RCON configurations for remote servers from the main API
            using var response = await _apiClient.GetAsync("/servers/remote/rcon-config", cancellationToken);
            var root = response.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var serverConfig in root.EnumerateArray())
                {
                    try
                    {
                        var name = serverConfig.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        var rconIp = serverConfig.TryGetProperty("rconIp", out var ipEl) ? ipEl.GetString() : null;
                        var rconPort = serverConfig.TryGetProperty("rconPort", out var portEl) && portEl.ValueKind == JsonValueKind.Number
                            ? portEl.GetInt32()
                            : 28016;
                        var rconPassword = serverConfig.TryGetProperty("rconPassword", out var pwdEl) ? pwdEl.GetString() : null;

                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rconPassword) || string.IsNullOrWhiteSpace(rconIp))
                        {
                            RustOpsSentry.AddBreadcrumb($"Skipping remote server '{name}': missing RCON configuration", "rcon");
                            continue;
                        }

                        var rconUri = new Uri($"ws://{rconIp.Trim()}:{rconPort}/{rconPassword}");
                        configs.Add((name, rconUri, rconPassword));
                        RustOpsSentry.AddBreadcrumb($"Loaded remote RCON config for '{name}' via API", "rcon");
                    }
                    catch (Exception ex)
                    {
                        RustOpsSentry.CaptureException(ex, "Failed to parse remote server RCON configuration from API response", "rcon");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(ex, "Error loading remote server configurations from API", "rcon");
        }

        return configs;
    }
}
