using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class SqlitePluginReferenceIndexStore : IPluginReferenceIndexStore
{
    private readonly string _connectionString;

    public SqlitePluginReferenceIndexStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        EnsureSchema();
    }

    public async Task<PluginReferenceRecord?> GetBySourcePathAsync(string sourcePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM plugin_reference_records WHERE source_path = $source_path LIMIT 1;";
        command.Parameters.AddWithValue("$source_path", sourcePath);
        var records = await ReadRecordsAsync(command, cancellationToken);
        return records.FirstOrDefault();
    }

    public async Task UpsertAsync(PluginReferenceRecord record, string rawSource, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var raw = connection.CreateCommand())
        {
            raw.Transaction = (SqliteTransaction)tx;
            raw.CommandText =
                """
                INSERT INTO plugin_raw_sources (id, source_path, source_hash, raw_source, updated_at_utc)
                VALUES ($id, $source_path, $source_hash, $raw_source, $updated_at_utc)
                ON CONFLICT(id) DO UPDATE SET
                    source_path = excluded.source_path,
                    source_hash = excluded.source_hash,
                    raw_source = excluded.raw_source,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            raw.Parameters.AddWithValue("$id", record.RawSourceReferenceId);
            raw.Parameters.AddWithValue("$source_path", record.SourcePath);
            raw.Parameters.AddWithValue("$source_hash", record.SourceHash);
            raw.Parameters.AddWithValue("$raw_source", rawSource);
            raw.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O"));
            await raw.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)tx;
            command.CommandText =
                """
                INSERT INTO plugin_reference_records (
                    id, server_name, plugin_name, source_path, source_hash, version, author, description,
                    commands_json, permissions_json, hooks_json, config_keys_json, raw_source_reference_id, last_indexed_utc
                )
                VALUES (
                    $id, $server_name, $plugin_name, $source_path, $source_hash, $version, $author, $description,
                    $commands_json, $permissions_json, $hooks_json, $config_keys_json, $raw_source_reference_id, $last_indexed_utc
                )
                ON CONFLICT(id) DO UPDATE SET
                    server_name = excluded.server_name,
                    plugin_name = excluded.plugin_name,
                    source_path = excluded.source_path,
                    source_hash = excluded.source_hash,
                    version = excluded.version,
                    author = excluded.author,
                    description = excluded.description,
                    commands_json = excluded.commands_json,
                    permissions_json = excluded.permissions_json,
                    hooks_json = excluded.hooks_json,
                    config_keys_json = excluded.config_keys_json,
                    raw_source_reference_id = excluded.raw_source_reference_id,
                    last_indexed_utc = excluded.last_indexed_utc;
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$server_name", record.ServerName);
            command.Parameters.AddWithValue("$plugin_name", record.PluginName);
            command.Parameters.AddWithValue("$source_path", record.SourcePath);
            command.Parameters.AddWithValue("$source_hash", record.SourceHash);
            command.Parameters.AddWithValue("$version", record.Version);
            command.Parameters.AddWithValue("$author", record.Author);
            command.Parameters.AddWithValue("$description", record.Description);
            command.Parameters.AddWithValue("$commands_json", JsonSerializer.Serialize(record.Commands, JsonDefaults.Default));
            command.Parameters.AddWithValue("$permissions_json", JsonSerializer.Serialize(record.Permissions, JsonDefaults.Default));
            command.Parameters.AddWithValue("$hooks_json", JsonSerializer.Serialize(record.Hooks, JsonDefaults.Default));
            command.Parameters.AddWithValue("$config_keys_json", JsonSerializer.Serialize(record.ConfigKeys, JsonDefaults.Default));
            command.Parameters.AddWithValue("$raw_source_reference_id", record.RawSourceReferenceId);
            command.Parameters.AddWithValue("$last_indexed_utc", record.LastIndexedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteNormalizedRowsAsync(connection, (SqliteTransaction)tx, record.Id, cancellationToken);
        await InsertNormalizedRowsAsync(connection, (SqliteTransaction)tx, record, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task DeleteNormalizedRowsAsync(SqliteConnection connection, SqliteTransaction tx, string pluginId, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "plugin_commands", "plugin_hooks", "plugin_permissions", "plugin_config_keys" })
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE plugin_id = $plugin_id;";
            cmd.Parameters.AddWithValue("$plugin_id", pluginId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertNormalizedRowsAsync(SqliteConnection connection, SqliteTransaction tx, PluginReferenceRecord record, CancellationToken cancellationToken)
    {
        foreach (var c in record.Commands)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO plugin_commands (id, plugin_id, plugin_name, server_name, command, type, handler_method, required_permission)
                VALUES ($id, $plugin_id, $plugin_name, $server_name, $command, $type, $handler_method, $required_permission);
                """;
            cmd.Parameters.AddWithValue("$id", Hash($"{record.Id}|cmd|{c.Type}|{c.Command}"));
            cmd.Parameters.AddWithValue("$plugin_id", record.Id);
            cmd.Parameters.AddWithValue("$plugin_name", record.PluginName);
            cmd.Parameters.AddWithValue("$server_name", record.ServerName);
            cmd.Parameters.AddWithValue("$command", c.Command);
            cmd.Parameters.AddWithValue("$type", c.Type);
            cmd.Parameters.AddWithValue("$handler_method", c.HandlerMethod);
            cmd.Parameters.AddWithValue("$required_permission", c.RequiredPermission);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var hook in record.Hooks)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO plugin_hooks (id, plugin_id, plugin_name, server_name, hook_name)
                VALUES ($id, $plugin_id, $plugin_name, $server_name, $hook_name);
                """;
            cmd.Parameters.AddWithValue("$id", Hash($"{record.Id}|hook|{hook}"));
            cmd.Parameters.AddWithValue("$plugin_id", record.Id);
            cmd.Parameters.AddWithValue("$plugin_name", record.PluginName);
            cmd.Parameters.AddWithValue("$server_name", record.ServerName);
            cmd.Parameters.AddWithValue("$hook_name", hook);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var perm in record.Permissions)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO plugin_permissions (id, plugin_id, plugin_name, server_name, permission)
                VALUES ($id, $plugin_id, $plugin_name, $server_name, $permission);
                """;
            cmd.Parameters.AddWithValue("$id", Hash($"{record.Id}|perm|{perm}"));
            cmd.Parameters.AddWithValue("$plugin_id", record.Id);
            cmd.Parameters.AddWithValue("$plugin_name", record.PluginName);
            cmd.Parameters.AddWithValue("$server_name", record.ServerName);
            cmd.Parameters.AddWithValue("$permission", perm);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var key in record.ConfigKeys)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO plugin_config_keys (id, plugin_id, plugin_name, server_name, config_key)
                VALUES ($id, $plugin_id, $plugin_name, $server_name, $config_key);
                """;
            cmd.Parameters.AddWithValue("$id", Hash($"{record.Id}|key|{key}"));
            cmd.Parameters.AddWithValue("$plugin_id", record.Id);
            cmd.Parameters.AddWithValue("$plugin_name", record.PluginName);
            cmd.Parameters.AddWithValue("$server_name", record.ServerName);
            cmd.Parameters.AddWithValue("$config_key", key);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    public async Task<IReadOnlyList<PluginReferenceRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM plugin_reference_records ORDER BY plugin_name, server_name;";
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginReferenceRecord>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return await ListAsync(cancellationToken);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM plugin_reference_records
            WHERE plugin_name LIKE $q
               OR commands_json LIKE $q
               OR permissions_json LIKE $q
               OR hooks_json LIKE $q
               OR config_keys_json LIKE $q
               OR description LIKE $q
            ORDER BY last_indexed_utc DESC
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("$q", $"%{normalized}%");
        var direct = await ReadRecordsAsync(command, cancellationToken);
        if (direct.Count > 0)
        {
            return direct;
        }

        var tokens = Regex.Split(normalized, @"[^A-Za-z0-9_.-]+")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0)
        {
            return Array.Empty<PluginReferenceRecord>();
        }

        var all = await ListAsync(cancellationToken);
        return all
            .Where(record => tokens.Any(token =>
                record.PluginName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                record.Commands.Any(command => command.Command.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                record.Permissions.Any(permission => permission.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                record.Hooks.Any(hook => hook.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                record.ConfigKeys.Any(key => key.Contains(token, StringComparison.OrdinalIgnoreCase))))
            .Take(50)
            .ToList();
    }

    public async Task<IReadOnlyList<PluginReferenceRecord>> SearchByCommandAsync(string command, CancellationToken cancellationToken)
    {
        var normalized = command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<PluginReferenceRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT r.* FROM plugin_reference_records r
            WHERE r.id IN (
                SELECT DISTINCT plugin_id FROM plugin_commands
                WHERE command = $exact OR command LIKE $like
            )
            ORDER BY r.plugin_name, r.server_name;
            """;
        cmd.Parameters.AddWithValue("$exact", normalized);
        cmd.Parameters.AddWithValue("$like", $"%{normalized}%");
        return await ReadRecordsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginReferenceRecord>> SearchByHookAsync(string hook, CancellationToken cancellationToken)
    {
        var normalized = hook.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<PluginReferenceRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT r.* FROM plugin_reference_records r
            WHERE r.id IN (
                SELECT DISTINCT plugin_id FROM plugin_hooks
                WHERE hook_name = $exact OR hook_name LIKE $like
            )
            ORDER BY r.plugin_name, r.server_name;
            """;
        cmd.Parameters.AddWithValue("$exact", normalized);
        cmd.Parameters.AddWithValue("$like", $"%{normalized}%");
        return await ReadRecordsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginReferenceRecord>> SearchByPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        var normalized = permission.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<PluginReferenceRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT r.* FROM plugin_reference_records r
            WHERE r.id IN (
                SELECT DISTINCT plugin_id FROM plugin_permissions
                WHERE permission = $exact OR permission LIKE $like
            )
            ORDER BY r.plugin_name, r.server_name;
            """;
        cmd.Parameters.AddWithValue("$exact", normalized);
        cmd.Parameters.AddWithValue("$like", $"%{normalized}%");
        return await ReadRecordsAsync(cmd, cancellationToken);
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS plugin_reference_records (
                id TEXT NOT NULL PRIMARY KEY,
                server_name TEXT NOT NULL DEFAULT '',
                plugin_name TEXT NOT NULL,
                source_path TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                version TEXT NOT NULL DEFAULT '',
                author TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                commands_json TEXT NOT NULL DEFAULT '[]',
                permissions_json TEXT NOT NULL DEFAULT '[]',
                hooks_json TEXT NOT NULL DEFAULT '[]',
                config_keys_json TEXT NOT NULL DEFAULT '[]',
                raw_source_reference_id TEXT NOT NULL DEFAULT '',
                last_indexed_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS plugin_raw_sources (
                id TEXT NOT NULL PRIMARY KEY,
                source_path TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                raw_source TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_reference_source_path ON plugin_reference_records(source_path);
            CREATE INDEX IF NOT EXISTS idx_plugin_reference_plugin_name ON plugin_reference_records(plugin_name);
            CREATE TABLE IF NOT EXISTS plugin_commands (
                id TEXT NOT NULL PRIMARY KEY,
                plugin_id TEXT NOT NULL,
                plugin_name TEXT NOT NULL,
                server_name TEXT NOT NULL,
                command TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT '',
                handler_method TEXT NOT NULL DEFAULT '',
                required_permission TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS plugin_hooks (
                id TEXT NOT NULL PRIMARY KEY,
                plugin_id TEXT NOT NULL,
                plugin_name TEXT NOT NULL,
                server_name TEXT NOT NULL,
                hook_name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS plugin_permissions (
                id TEXT NOT NULL PRIMARY KEY,
                plugin_id TEXT NOT NULL,
                plugin_name TEXT NOT NULL,
                server_name TEXT NOT NULL,
                permission TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS plugin_config_keys (
                id TEXT NOT NULL PRIMARY KEY,
                plugin_id TEXT NOT NULL,
                plugin_name TEXT NOT NULL,
                server_name TEXT NOT NULL,
                config_key TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS server_convars (
                name TEXT NOT NULL PRIMARY KEY,
                default_value TEXT NOT NULL DEFAULT '',
                default_type TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                generated INTEGER NOT NULL DEFAULT 0,
                last_synced_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS server_commands_catalog (
                name TEXT NOT NULL PRIMARY KEY,
                description TEXT NOT NULL DEFAULT '',
                risk_level TEXT NOT NULL DEFAULT '',
                tags_json TEXT NOT NULL DEFAULT '[]',
                generated INTEGER NOT NULL DEFAULT 0,
                last_synced_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_commands_command ON plugin_commands(command);
            CREATE INDEX IF NOT EXISTS idx_plugin_commands_plugin_id ON plugin_commands(plugin_id);
            CREATE INDEX IF NOT EXISTS idx_plugin_hooks_hook_name ON plugin_hooks(hook_name);
            CREATE INDEX IF NOT EXISTS idx_plugin_hooks_plugin_id ON plugin_hooks(plugin_id);
            CREATE INDEX IF NOT EXISTS idx_plugin_permissions_permission ON plugin_permissions(permission);
            CREATE INDEX IF NOT EXISTS idx_plugin_permissions_plugin_id ON plugin_permissions(plugin_id);
            CREATE INDEX IF NOT EXISTS idx_plugin_config_keys_config_key ON plugin_config_keys(config_key);
            CREATE INDEX IF NOT EXISTS idx_plugin_config_keys_plugin_id ON plugin_config_keys(plugin_id);
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<IReadOnlyList<PluginReferenceRecord>> ReadRecordsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var records = new List<PluginReferenceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PluginReferenceRecord
            {
                Id = reader["id"].ToString() ?? Guid.NewGuid().ToString("N"),
                ServerName = reader["server_name"].ToString() ?? string.Empty,
                PluginName = reader["plugin_name"].ToString() ?? string.Empty,
                SourcePath = reader["source_path"].ToString() ?? string.Empty,
                SourceHash = reader["source_hash"].ToString() ?? string.Empty,
                Version = reader["version"].ToString() ?? string.Empty,
                Author = reader["author"].ToString() ?? string.Empty,
                Description = reader["description"].ToString() ?? string.Empty,
                Commands = Deserialize<List<PluginCommandReference>>(reader["commands_json"].ToString()) ?? new(),
                Permissions = Deserialize<List<string>>(reader["permissions_json"].ToString()) ?? new(),
                Hooks = Deserialize<List<string>>(reader["hooks_json"].ToString()) ?? new(),
                ConfigKeys = Deserialize<List<string>>(reader["config_keys_json"].ToString()) ?? new(),
                RawSourceReferenceId = reader["raw_source_reference_id"].ToString() ?? string.Empty,
                LastIndexedUtc = DateTime.TryParse(reader["last_indexed_utc"].ToString(), out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow
            });
        }

        return records;
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonDefaults.Default);
}

internal sealed class PluginReferenceIndexer
{
    private readonly RustOpsApiClient _api;
    private readonly IPluginReferenceIndexStore _store;
    private readonly ISemanticMemoryService? _semanticMemory;

    public PluginReferenceIndexer(RustOpsApiClient api, IPluginReferenceIndexStore store, ISemanticMemoryService? semanticMemory)
    {
        _api = api;
        _store = store;
        _semanticMemory = semanticMemory;
    }

    public async Task<PluginIndexRefreshReport> RefreshAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        var report = new PluginIndexRefreshReport();
        foreach (var server in servers.Where(server => !string.IsNullOrWhiteSpace(server)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var doc = await _api.GetAsync($"/servers/{Uri.EscapeDataString(server)}/oxide/validate", cancellationToken);
                if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var plugin in plugins.EnumerateArray())
                {
                    var path = plugin.TryGetProperty("path", out var pathNode) ? pathNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        report.Skipped++;
                        continue;
                    }

                    var raw = await File.ReadAllTextAsync(path, cancellationToken);
                    var extracted = PluginReferenceExtractor.Extract(server, path, raw);
                    var existing = await _store.GetBySourcePathAsync(path, cancellationToken);
                    if (existing is not null && string.Equals(existing.SourceHash, extracted.SourceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Unchanged++;
                        continue;
                    }

                    await _store.UpsertAsync(extracted, raw, cancellationToken);
                    report.Indexed++;
                    await StoreSemanticSummaryAsync(extracted, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                report.Errors++;
                report.Messages.Add($"{server}: {ex.Message}");
            }
        }

        return report;
    }

    public async Task<PluginIndexRefreshReport> RefreshAllAsync(CancellationToken cancellationToken)
    {
        using var doc = await _api.GetAsync("/servers", cancellationToken);
        var servers = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    servers.Add(item.GetString()!.Trim());
                }
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("name", out var nameNode) &&
                         nameNode.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(nameNode.GetString()))
                {
                    servers.Add(nameNode.GetString()!.Trim());
                }
            }
        }

        return await RefreshAsync(servers, cancellationToken);
    }

    public Task<IReadOnlyList<PluginReferenceRecord>> SearchAsync(string query, CancellationToken cancellationToken) =>
        _store.SearchAsync(query, cancellationToken);

    public Task<IReadOnlyList<PluginReferenceRecord>> SearchByCommandAsync(string command, CancellationToken cancellationToken) =>
        _store.SearchByCommandAsync(command, cancellationToken);

    public Task<IReadOnlyList<PluginReferenceRecord>> SearchByHookAsync(string hook, CancellationToken cancellationToken) =>
        _store.SearchByHookAsync(hook, cancellationToken);

    public Task<IReadOnlyList<PluginReferenceRecord>> SearchByPermissionAsync(string permission, CancellationToken cancellationToken) =>
        _store.SearchByPermissionAsync(permission, cancellationToken);

    public Task<IReadOnlyList<PluginReferenceRecord>> ListAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    private async Task StoreSemanticSummaryAsync(PluginReferenceRecord record, CancellationToken cancellationToken)
    {
        if (_semanticMemory is null || string.IsNullOrWhiteSpace(record.PluginName))
        {
            return;
        }

        var playerCommands = record.Commands
            .Where(command => !LooksAdminOnly(command))
            .Select(command => "/" + command.Command.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var adminCommands = record.Commands
            .Where(LooksAdminOnly)
            .Select(command => "/" + command.Command.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = $"Plugin {record.PluginName} reference summary";
        var text = $"Plugin {record.PluginName} provides Oxide/uMod behavior. " +
                   $"Player commands: {FormatList(playerCommands)}. " +
                   $"Admin commands: {FormatList(adminCommands)}. " +
                   $"Permissions include {FormatList(record.Permissions)}. " +
                   $"Hooks include {FormatList(record.Hooks.Take(8))}. " +
                   $"Source: {record.SourcePath}. Last indexed: {record.LastIndexedUtc:yyyy-MM-dd}.";

        await _semanticMemory.AddManualMemoryAsync(new ManualMemoryInput
        {
            Type = MemoryRecordType.PluginSummary,
            Scope = MemoryScope.Project,
            Source = MemorySource.PluginSummary,
            ApprovalState = MemoryApprovalState.Active,
            Summary = summary,
            Text = text,
            SourcePath = record.SourcePath,
            SourceHash = record.SourceHash,
            Tags = new List<string> { "plugin-summary", record.PluginName.ToLowerInvariant() },
            RelatedEntityIds = new List<string> { record.PluginName, record.ServerName },
            Importance = 0.72,
            Confidence = 0.9
        }, cancellationToken);
    }

    internal static bool LooksAdminOnly(PluginCommandReference command)
    {
        var combined = $"{command.Command} {command.RequiredPermission} {command.HandlerMethod}".ToLowerInvariant();
        return combined.Contains("admin", StringComparison.Ordinal) ||
               combined.Contains("owner", StringComparison.Ordinal) ||
               combined.Contains("moderator", StringComparison.Ordinal) ||
               combined.Contains("mod.", StringComparison.Ordinal) ||
               combined.Contains(".admin", StringComparison.Ordinal);
    }

    private static string FormatList(IEnumerable<string> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        return list.Count == 0 ? "none detected" : string.Join(", ", list);
    }
}

internal sealed class PluginIndexRefreshReport
{
    public int Indexed { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> Messages { get; set; } = new();

    public string ToSummary() => $"indexed={Indexed} unchanged={Unchanged} skipped={Skipped} errors={Errors}";
}

internal static class PluginReferenceExtractor
{
    private static readonly string[] KnownHooks =
    {
        "OnServerInitialized", "Init", "Loaded", "Unload", "OnPlayerConnected", "OnPlayerDisconnected",
        "OnEntityDeath", "OnPlayerDeath", "OnUserChat", "CanBuild", "CanLootEntity"
    };

    public static PluginReferenceRecord Extract(string serverName, string path, string source)
    {
        var info = Regex.Match(source, @"\[\s*Info\s*\(\s*""(?<name>[^""]+)""\s*,\s*""(?<author>[^""]*)""\s*,\s*""(?<version>[^""]+)""\s*\)\s*\]", RegexOptions.IgnoreCase);
        var description = Regex.Match(source, @"\[\s*Description\s*\(\s*""(?<description>[^""]+)""\s*\)\s*\]", RegexOptions.IgnoreCase);
        var hash = Hash(source);
        var pluginName = info.Success ? info.Groups["name"].Value.Trim() : Path.GetFileNameWithoutExtension(path);
        var record = new PluginReferenceRecord
        {
            Id = Hash($"{serverName}|{path}"),
            ServerName = serverName,
            PluginName = pluginName,
            SourcePath = path,
            SourceHash = hash,
            Author = info.Success ? info.Groups["author"].Value.Trim() : string.Empty,
            Version = info.Success ? info.Groups["version"].Value.Trim() : string.Empty,
            Description = description.Success ? description.Groups["description"].Value.Trim() : string.Empty,
            RawSourceReferenceId = Hash($"raw|{path}")
        };

        record.Commands = ExtractCommands(source);
        record.Permissions = ExtractPermissions(source);
        record.Hooks = ExtractHooks(source);
        record.ConfigKeys = ExtractConfigKeys(source);
        record.LastIndexedUtc = DateTime.UtcNow;
        return record;
    }

    private static List<PluginCommandReference> ExtractCommands(string source)
    {
        var commands = new List<PluginCommandReference>();
        AddAttributeCommands(commands, source, @"\[\s*ChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "ChatCommand");
        AddAttributeCommands(commands, source, @"\[\s*ConsoleCommand\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "ConsoleCommand");
        AddAttributeCommands(commands, source, @"\[\s*Command\s*\(\s*""(?<cmd>[^""]+)""\s*\)\s*\]", "CovalenceCommand");

        foreach (Match match in Regex.Matches(source, @"cmd\.AddChatCommand\s*\(\s*""(?<cmd>[^""]+)""\s*,\s*this\s*,\s*(?:nameof\s*\(\s*)?""?(?<handler>[A-Za-z_][A-Za-z0-9_]*)""?", RegexOptions.IgnoreCase))
        {
            commands.Add(NewCommand(match, "ChatCommand", source));
        }

        foreach (Match match in Regex.Matches(source, @"AddCovalenceCommand\s*\(\s*""(?<cmd>[^""]+)""\s*,\s*(?:nameof\s*\(\s*)?""?(?<handler>[A-Za-z_][A-Za-z0-9_]*)""?", RegexOptions.IgnoreCase))
        {
            commands.Add(NewCommand(match, "CovalenceCommand", source));
        }

        var permissions = ExtractPermissions(source);
        foreach (var command in commands)
        {
            var nearbyPermission = permissions.FirstOrDefault(permission =>
                command.HandlerMethod.Contains(permission.Split('.').Last(), StringComparison.OrdinalIgnoreCase));
            command.RequiredPermission = nearbyPermission ?? string.Empty;
        }

        return commands
            .GroupBy(command => $"{command.Type}:{command.Command}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(command => command.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddAttributeCommands(List<PluginCommandReference> commands, string source, string pattern, string type)
    {
        foreach (Match match in Regex.Matches(source, pattern, RegexOptions.IgnoreCase))
        {
            commands.Add(new PluginCommandReference
            {
                Command = match.Groups["cmd"].Value.Trim(),
                Type = type,
                HandlerMethod = FindHandlerAfter(source, match.Index + match.Length),
                LineNumber = LineNumber(source, match.Index)
            });
        }
    }

    private static string FindHandlerAfter(string source, int index)
    {
        var tail = source[Math.Min(index, source.Length)..];
        var match = Regex.Match(
            tail,
            @"\b(?:private|public|protected|internal)?\s*(?:void|bool|object|string|IEnumerable<[^>]+>)\s+(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["handler"].Value.Trim() : string.Empty;
    }

    private static PluginCommandReference NewCommand(Match match, string type, string source) => new()
    {
        Command = match.Groups["cmd"].Value.Trim(),
        Type = type,
        HandlerMethod = match.Groups["handler"].Value.Trim(),
        LineNumber = LineNumber(source, match.Index)
    };

    private static List<string> ExtractPermissions(string source)
    {
        var patterns = new[]
        {
            @"permission\.RegisterPermission\s*\(\s*""(?<perm>[^""]+)""",
            @"permission\.UserHasPermission\s*\([^,]+,\s*""(?<perm>[^""]+)""",
            @"\.HasPermission\s*\(\s*""(?<perm>[^""]+)"""
        };
        return patterns
            .SelectMany(pattern => Regex.Matches(source, pattern, RegexOptions.IgnoreCase).Select(match => match.Groups["perm"].Value.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractHooks(string source)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hook in KnownHooks)
        {
            if (Regex.IsMatch(source, $@"\b(?:void|object|bool|string|IEnumerable<[^>]+>)\s+{Regex.Escape(hook)}\s*\(", RegexOptions.IgnoreCase))
            {
                found.Add(hook);
            }
        }

        foreach (Match match in Regex.Matches(source, @"\b(?<hook>On[A-Z][A-Za-z0-9_]+|Can[A-Z][A-Za-z0-9_]+)\s*\(", RegexOptions.IgnoreCase))
        {
            found.Add(match.Groups["hook"].Value);
        }

        return found.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractConfigKeys(string source)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(source, @"Config\s*\[\s*""(?<key>[^""]+)""\s*\]", RegexOptions.IgnoreCase))
        {
            keys.Add(match.Groups["key"].Value.Trim());
        }

        foreach (Match match in Regex.Matches(source, @"GetConfig\s*\(\s*""(?<key>[^""]+)""", RegexOptions.IgnoreCase))
        {
            keys.Add(match.Groups["key"].Value.Trim());
        }

        foreach (Match match in Regex.Matches(source, @"JsonProperty\s*\(\s*""(?<key>[^""]+)""\s*\)", RegexOptions.IgnoreCase))
        {
            keys.Add(match.Groups["key"].Value.Trim());
        }

        foreach (Match match in Regex.Matches(source, @"configData\.(?<key>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase))
        {
            keys.Add(match.Groups["key"].Value.Trim());
        }

        return keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int LineNumber(string source, int index) => source[..Math.Min(index, source.Length)].Count(ch => ch == '\n') + 1;

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}

internal sealed class SqliteServerCatalogIndexStore : IServerCatalogIndexStore
{
    private readonly string _connectionString;

    public SqliteServerCatalogIndexStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task SyncAsync(IReadOnlyList<ServerVariableDefinition> variables, IReadOnlyList<ServerCommandDefinition> commands, CancellationToken cancellationToken)
    {
        var syncedAt = DateTime.UtcNow.ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var v in variables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText =
                """
                INSERT INTO server_convars (name, default_value, default_type, description, generated, last_synced_utc)
                VALUES ($name, $default_value, $default_type, $description, $generated, $last_synced_utc)
                ON CONFLICT(name) DO UPDATE SET
                    default_value = excluded.default_value,
                    default_type = excluded.default_type,
                    description = excluded.description,
                    generated = excluded.generated,
                    last_synced_utc = excluded.last_synced_utc;
                """;
            cmd.Parameters.AddWithValue("$name", v.Name);
            cmd.Parameters.AddWithValue("$default_value", v.DefaultValue ?? string.Empty);
            cmd.Parameters.AddWithValue("$default_type", v.DefaultType ?? string.Empty);
            cmd.Parameters.AddWithValue("$description", v.Description ?? string.Empty);
            cmd.Parameters.AddWithValue("$generated", v.Generated ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_synced_utc", syncedAt);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var c in commands)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText =
                """
                INSERT INTO server_commands_catalog (name, description, risk_level, tags_json, generated, last_synced_utc)
                VALUES ($name, $description, $risk_level, $tags_json, $generated, $last_synced_utc)
                ON CONFLICT(name) DO UPDATE SET
                    description = excluded.description,
                    risk_level = excluded.risk_level,
                    tags_json = excluded.tags_json,
                    generated = excluded.generated,
                    last_synced_utc = excluded.last_synced_utc;
                """;
            cmd.Parameters.AddWithValue("$name", c.Name);
            cmd.Parameters.AddWithValue("$description", c.Description ?? string.Empty);
            cmd.Parameters.AddWithValue("$risk_level", c.RiskLevel ?? string.Empty);
            cmd.Parameters.AddWithValue("$tags_json", JsonSerializer.Serialize(c.Tags ?? Array.Empty<string>(), JsonDefaults.Default));
            cmd.Parameters.AddWithValue("$generated", c.Generated ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_synced_utc", syncedAt);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<ServerVariableDefinition?> GetConvarAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM server_convars WHERE LOWER(name) = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", normalized);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConvar(reader) : null;
    }

    public async Task<IReadOnlyList<ServerVariableDefinition>> SearchConvarsAsync(string query, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        if (string.IsNullOrWhiteSpace(q))
            return Array.Empty<ServerVariableDefinition>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT * FROM server_convars
            WHERE name LIKE $q OR description LIKE $q
            ORDER BY
                CASE WHEN LOWER(name) = LOWER($exact) THEN 0
                     WHEN name LIKE $prefix THEN 1
                     ELSE 2 END,
                name
            LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$q", $"%{q}%");
        cmd.Parameters.AddWithValue("$exact", q);
        cmd.Parameters.AddWithValue("$prefix", $"{q}%");
        var results = new List<ServerVariableDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadConvar(reader));
        return results;
    }

    public async Task<IReadOnlyList<ServerCommandDefinition>> SearchServerCommandsAsync(string query, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        if (string.IsNullOrWhiteSpace(q))
            return Array.Empty<ServerCommandDefinition>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT * FROM server_commands_catalog
            WHERE name LIKE $q OR description LIKE $q OR tags_json LIKE $q
            ORDER BY
                CASE WHEN LOWER(name) = LOWER($exact) THEN 0
                     WHEN name LIKE $prefix THEN 1
                     ELSE 2 END,
                name
            LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$q", $"%{q}%");
        cmd.Parameters.AddWithValue("$exact", q);
        cmd.Parameters.AddWithValue("$prefix", $"{q}%");
        var results = new List<ServerCommandDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadServerCommand(reader));
        return results;
    }

    private static ServerVariableDefinition ReadConvar(SqliteDataReader reader) =>
        new(
            reader["name"].ToString()!,
            reader["generated"].ToString() == "1",
            NullIfEmpty(reader["default_value"].ToString()),
            NullIfEmpty(reader["description"].ToString()),
            NullIfEmpty(reader["default_type"].ToString())
        );

    private static ServerCommandDefinition ReadServerCommand(SqliteDataReader reader) =>
        new(
            reader["name"].ToString()!,
            reader["generated"].ToString() == "1",
            NullIfEmpty(reader["description"].ToString()),
            NullIfEmpty(reader["risk_level"].ToString()),
            Deserialize<List<string>>(reader["tags_json"].ToString())
        );

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonDefaults.Default);
}
