using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class PlayerStore
{
    private readonly string _connectionString;
    private readonly Action<string>? _log;

    public PlayerStore(string dbPath, Action<string>? log = null)
    {
        _log = log;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

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

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS players (
                steam_id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL DEFAULT '',
                alias_history_json TEXT NOT NULL DEFAULT '[]',
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_server TEXT NOT NULL DEFAULT '',
                total_sessions INTEGER NOT NULL DEFAULT 0,
                ip_history_json TEXT NOT NULL DEFAULT '[]',
                last_ip TEXT NOT NULL DEFAULT '',
                last_chat_message TEXT NOT NULL DEFAULT '',
                last_chat_at_utc TEXT NULL,
                ban_state TEXT NOT NULL DEFAULT '',
                forced INTEGER NOT NULL DEFAULT 0,
                forced_checked_utc TEXT NULL,
                notes TEXT NOT NULL DEFAULT '',
                metadata_json TEXT NOT NULL DEFAULT '{}',
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_players_last_seen ON players(last_seen_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_players_display_name ON players(display_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_players_forced ON players(forced);
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordSighting(string steamId, string displayName, string? server, string? ip, bool startsSession)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return;
        steamId = steamId.Trim();
        displayName = (displayName ?? string.Empty).Trim();
        server ??= string.Empty;
        ip ??= string.Empty;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var existing = LoadInternal(connection, steamId);
            var now = DateTime.UtcNow;
            var aliases = existing?.Aliases ?? new List<string>();
            if (!string.IsNullOrEmpty(displayName) &&
                !aliases.Contains(displayName, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(displayName);
                if (aliases.Count > 25) aliases.RemoveAt(0);
            }
            var ips = existing?.IpHistory ?? new List<string>();
            if (!string.IsNullOrEmpty(ip) && !ips.Contains(ip, StringComparer.Ordinal))
            {
                ips.Add(ip);
                if (ips.Count > 25) ips.RemoveAt(0);
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO players (
                    steam_id, display_name, alias_history_json, first_seen_utc, last_seen_utc,
                    last_server, total_sessions, ip_history_json, last_ip,
                    last_chat_message, last_chat_at_utc, ban_state, forced, forced_checked_utc,
                    notes, metadata_json, updated_at_utc)
                VALUES (
                    $sid, $name, $aliases, $first, $last,
                    $server, $sessions, $ips, $lastip,
                    '', NULL, '', 0, NULL,
                    '', '{}', $updated)
                ON CONFLICT(steam_id) DO UPDATE SET
                    display_name = CASE WHEN length(excluded.display_name) > 0 THEN excluded.display_name ELSE players.display_name END,
                    alias_history_json = excluded.alias_history_json,
                    last_seen_utc = excluded.last_seen_utc,
                    last_server = CASE WHEN length(excluded.last_server) > 0 THEN excluded.last_server ELSE players.last_server END,
                    total_sessions = players.total_sessions + $session_inc,
                    ip_history_json = excluded.ip_history_json,
                    last_ip = CASE WHEN length(excluded.last_ip) > 0 THEN excluded.last_ip ELSE players.last_ip END,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$sid", steamId);
            cmd.Parameters.AddWithValue("$name", displayName);
            cmd.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
            cmd.Parameters.AddWithValue("$first", (existing?.FirstSeenUtc ?? now).ToString("O"));
            cmd.Parameters.AddWithValue("$last", now.ToString("O"));
            cmd.Parameters.AddWithValue("$server", server);
            cmd.Parameters.AddWithValue("$sessions", startsSession ? 1 : 0);
            cmd.Parameters.AddWithValue("$ips", JsonSerializer.Serialize(ips));
            cmd.Parameters.AddWithValue("$lastip", ip);
            cmd.Parameters.AddWithValue("$updated", now.ToString("O"));
            cmd.Parameters.AddWithValue("$session_inc", startsSession ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"player upsert failed for {steamId}: {ex.Message}");
        }
    }

    public void RecordChat(string steamId, string displayName, string server, string message)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return;
        RecordSighting(steamId, displayName, server, ip: null, startsSession: false);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE players
                SET last_chat_message = $msg, last_chat_at_utc = $at, updated_at_utc = $at
                WHERE steam_id = $sid;
                """;
            cmd.Parameters.AddWithValue("$sid", steamId.Trim());
            cmd.Parameters.AddWithValue("$msg", (message ?? string.Empty).Length > 500
                ? message![..500] : (message ?? string.Empty));
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"player chat update failed for {steamId}: {ex.Message}");
        }
    }

    public void ApplyForcedList(IReadOnlyList<ForcedListEntry> entries)
    {
        if (entries.Count == 0) return;
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();
            var now = DateTime.UtcNow.ToString("O");

            // Clear all forced flags first so removed entries get unset.
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "UPDATE players SET forced = 0, forced_checked_utc = $now WHERE forced = 1;";
                clear.Parameters.AddWithValue("$now", now);
                clear.ExecuteNonQuery();
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.SteamId)) continue;
                var sid = entry.SteamId.Trim();
                var name = (entry.DisplayName ?? string.Empty).Trim();
                var ip = (entry.LastIp ?? string.Empty).Trim();

                // Read existing aliases / IP history so we can append rather than replace.
                var existing = LoadInternal(connection, sid);
                var aliases = existing?.Aliases ?? new List<string>();
                if (!string.IsNullOrEmpty(name) &&
                    !aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    aliases.Add(name);
                    if (aliases.Count > 25) aliases.RemoveAt(0);
                }
                var ips = existing?.IpHistory ?? new List<string>();
                if (!string.IsNullOrEmpty(ip) && !ips.Contains(ip, StringComparer.Ordinal))
                {
                    ips.Add(ip);
                    if (ips.Count > 25) ips.RemoveAt(0);
                }

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO players (
                        steam_id, display_name, alias_history_json, first_seen_utc, last_seen_utc,
                        last_server, total_sessions, ip_history_json, last_ip,
                        last_chat_message, last_chat_at_utc, ban_state, forced, forced_checked_utc,
                        notes, metadata_json, updated_at_utc)
                    VALUES (
                        $sid, $name, $aliases, $now, $now,
                        '', 0, $ips, $lastip,
                        '', NULL, '', 1, $now,
                        '', '{}', $now)
                    ON CONFLICT(steam_id) DO UPDATE SET
                        display_name = CASE WHEN length(excluded.display_name) > 0 THEN excluded.display_name ELSE players.display_name END,
                        alias_history_json = excluded.alias_history_json,
                        ip_history_json = excluded.ip_history_json,
                        last_ip = CASE WHEN length(excluded.last_ip) > 0 THEN excluded.last_ip ELSE players.last_ip END,
                        forced = 1,
                        forced_checked_utc = excluded.forced_checked_utc,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                cmd.Parameters.AddWithValue("$sid", sid);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(aliases));
                cmd.Parameters.AddWithValue("$ips", JsonSerializer.Serialize(ips));
                cmd.Parameters.AddWithValue("$lastip", ip);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"forced-list apply failed: {ex.Message}");
        }
    }

    public PlayerRecord? Get(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return null;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return LoadInternal(connection, steamId.Trim());
    }

    public IReadOnlyDictionary<string, string> GetDisplayNamesBySteamId(int max = 5000)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT steam_id, display_name FROM players ORDER BY last_seen_utc DESC LIMIT $max;";
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sid = reader.GetString(0);
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                result[sid] = name;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"player list failed: {ex.Message}");
        }
        return result;
    }

    public HashSet<string> GetAllKnownNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT display_name, alias_history_json FROM players;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                var aliasesJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                try
                {
                    var aliases = JsonSerializer.Deserialize<List<string>>(aliasesJson);
                    if (aliases is not null)
                        foreach (var a in aliases)
                            if (!string.IsNullOrWhiteSpace(a)) names.Add(a);
                }
                catch { /* ignore corrupt rows */ }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"player names load failed: {ex.Message}");
        }
        return names;
    }

    private static PlayerRecord? LoadInternal(SqliteConnection connection, string steamId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM players WHERE steam_id = $sid LIMIT 1;";
        cmd.Parameters.AddWithValue("$sid", steamId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var aliases = TryDeserializeList(reader["alias_history_json"]?.ToString());
        var ips = TryDeserializeList(reader["ip_history_json"]?.ToString());
        return new PlayerRecord
        {
            SteamId = reader["steam_id"].ToString() ?? steamId,
            DisplayName = reader["display_name"]?.ToString() ?? string.Empty,
            Aliases = aliases,
            FirstSeenUtc = ParseDt(reader["first_seen_utc"]?.ToString()) ?? DateTime.UtcNow,
            LastSeenUtc = ParseDt(reader["last_seen_utc"]?.ToString()) ?? DateTime.UtcNow,
            LastServer = reader["last_server"]?.ToString() ?? string.Empty,
            TotalSessions = SafeInt(reader["total_sessions"]),
            IpHistory = ips,
            LastIp = reader["last_ip"]?.ToString() ?? string.Empty,
            LastChatMessage = reader["last_chat_message"]?.ToString() ?? string.Empty,
            LastChatAtUtc = ParseDt(reader["last_chat_at_utc"]?.ToString()),
            BanState = reader["ban_state"]?.ToString() ?? string.Empty,
            Forced = SafeInt(reader["forced"]) == 1,
            ForcedCheckedAtUtc = ParseDt(reader["forced_checked_utc"]?.ToString()),
            Notes = reader["notes"]?.ToString() ?? string.Empty,
            UpdatedAtUtc = ParseDt(reader["updated_at_utc"]?.ToString()) ?? DateTime.UtcNow
        };
    }

    private static List<string> TryDeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static DateTime? ParseDt(string? value) =>
        DateTime.TryParse(value, out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;

    private static int SafeInt(object? value)
    {
        if (value is null || value is DBNull) return 0;
        return Convert.ToInt32(value);
    }
}

internal sealed record ForcedListEntry(string SteamId, string? DisplayName, string? LastIp);

internal sealed class PlayerRecord
{
    public string SteamId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string LastServer { get; set; } = string.Empty;
    public int TotalSessions { get; set; }
    public List<string> IpHistory { get; set; } = new();
    public string LastIp { get; set; } = string.Empty;
    public string LastChatMessage { get; set; } = string.Empty;
    public DateTime? LastChatAtUtc { get; set; }
    public string BanState { get; set; } = string.Empty;
    public bool Forced { get; set; }
    public DateTime? ForcedCheckedAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
