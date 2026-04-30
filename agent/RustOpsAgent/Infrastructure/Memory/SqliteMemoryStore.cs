using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;

namespace RustOpsAgent.Infrastructure.Memory;

internal sealed class SqliteMemoryStore : IInspectableMemoryStore
{
    private const int SchemaVersion = 1;
    private const int MaintenanceRecordLimit = 5000;

    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly MemorySettings _settings;
    private readonly Action<string>? _log;

    public SqliteMemoryStore(string dbPath, MemorySettings? settings = null, Action<string>? log = null)
    {
        _dbPath = dbPath;
        _settings = settings ?? new MemorySettings();
        _log = log;

        var directory = Path.GetDirectoryName(_dbPath);
        Directory.CreateDirectory(string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        EnsureSchema();
    }

    public async Task UpsertAsync(MemoryRecord record, CancellationToken cancellationToken)
    {
        MemoryRecord.Validate(record);
        if (record.HasEmbedding && string.IsNullOrWhiteSpace(record.EmbeddingModel))
        {
            throw new InvalidOperationException("Embedding model is required when an embedding vector is present.");
        }

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO memory_records (
                id, type, scope, source, text, summary, tags_json, related_entity_ids_json,
                created_at_utc, updated_at_utc, last_accessed_at_utc, access_count, importance,
                confidence, expiry_utc, approval_state, title, source_path, source_hash, chunk_index,
                category, last_verified_utc, metadata_json, embedding_json, embedding_model, embedding_dimensions,
                content_hash
            )
            VALUES (
                $id, $type, $scope, $source, $text, $summary, $tags_json, $related_entity_ids_json,
                $created_at_utc, $updated_at_utc, $last_accessed_at_utc, $access_count, $importance,
                $confidence, $expiry_utc, $approval_state, $title, $source_path, $source_hash, $chunk_index,
                $category, $last_verified_utc, $metadata_json, $embedding_json, $embedding_model, $embedding_dimensions,
                $content_hash
            )
            ON CONFLICT(content_hash) DO UPDATE SET
                type = excluded.type,
                scope = excluded.scope,
                source = excluded.source,
                text = excluded.text,
                summary = excluded.summary,
                tags_json = excluded.tags_json,
                related_entity_ids_json = excluded.related_entity_ids_json,
                updated_at_utc = excluded.updated_at_utc,
                last_accessed_at_utc = COALESCE(excluded.last_accessed_at_utc, memory_records.last_accessed_at_utc),
                access_count = MAX(memory_records.access_count, excluded.access_count),
                importance = MAX(memory_records.importance, excluded.importance),
                confidence = MAX(memory_records.confidence, excluded.confidence),
                expiry_utc = excluded.expiry_utc,
                approval_state = excluded.approval_state,
                title = excluded.title,
                source_path = excluded.source_path,
                source_hash = excluded.source_hash,
                chunk_index = excluded.chunk_index,
                category = excluded.category,
                last_verified_utc = COALESCE(excluded.last_verified_utc, memory_records.last_verified_utc),
                metadata_json = excluded.metadata_json,
                embedding_json = excluded.embedding_json,
                embedding_model = excluded.embedding_model,
                embedding_dimensions = excluded.embedding_dimensions
            ON CONFLICT(id) DO UPDATE SET
                type = excluded.type,
                scope = excluded.scope,
                source = excluded.source,
                text = excluded.text,
                summary = excluded.summary,
                tags_json = excluded.tags_json,
                related_entity_ids_json = excluded.related_entity_ids_json,
                updated_at_utc = excluded.updated_at_utc,
                last_accessed_at_utc = excluded.last_accessed_at_utc,
                access_count = excluded.access_count,
                importance = excluded.importance,
                confidence = excluded.confidence,
                expiry_utc = excluded.expiry_utc,
                approval_state = excluded.approval_state,
                title = excluded.title,
                source_path = excluded.source_path,
                source_hash = excluded.source_hash,
                chunk_index = excluded.chunk_index,
                category = excluded.category,
                last_verified_utc = excluded.last_verified_utc,
                metadata_json = excluded.metadata_json,
                embedding_json = excluded.embedding_json,
                embedding_model = excluded.embedding_model,
                embedding_dimensions = excluded.embedding_dimensions,
                content_hash = excluded.content_hash;
            """;

        BindRecord(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(request, cancellationToken);
        var results = new List<MemorySearchResult>();
        var now = DateTime.UtcNow;
        var queryEmbedding = request.QueryEmbedding;

        foreach (var candidate in candidates.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var similarity = queryEmbedding is { Length: > 0 }
                ? Similarity(queryEmbedding, candidate.Embedding)
                : KeywordSimilarity(request.Query, candidate);

            if (similarity < request.MinSimilarity)
            {
                continue;
            }

            var recency = ComputeRecencyScore(candidate.UpdatedAtUtc, now);
            var importance = Math.Clamp(candidate.Importance, 0.0, 1.0);
            var scopeBoost = request.Scope is not null && request.Scope == candidate.Scope ? 0.1 : 0.0;
            var typeBoost = candidate.Type is MemoryRecordType.Fix or MemoryRecordType.Procedure ? 0.08 :
                candidate.Type is MemoryRecordType.ServerConvar or MemoryRecordType.ServerCommand ? 0.06 :
                candidate.Type == MemoryRecordType.Exception ? 0.04 :
                candidate.Type == MemoryRecordType.Failure ? -0.04 : 0.0;
            var sourceBoost = candidate.Source is MemorySource.VerifiedFact ? 0.12 :
                candidate.Source is MemorySource.ServerCatalog or MemorySource.PluginSummary ? 0.1 :
                candidate.Source is MemorySource.ManualImport or MemorySource.SeededImport ? 0.08 : 0.0;
            var verifiedBoost = candidate.LastVerifiedUtc.HasValue
                ? Math.Clamp(Math.Exp(Math.Max(0, (now - candidate.LastVerifiedUtc.Value).TotalDays) / -90.0), 0.0, 1.0) * 0.08
                : 0.0;
            var confidencePenalty = candidate.Confidence < 0.6 ? 0.08 : 0.0;
            var final = (similarity * 0.58) + (importance * 0.16) + (recency * 0.08) + (candidate.Confidence * 0.08) +
                        scopeBoost + typeBoost + sourceBoost + verifiedBoost - confidencePenalty;

            results.Add(new MemorySearchResult
            {
                MemoryRecord = candidate,
                SimilarityScore = similarity,
                RecencyScore = recency,
                ImportanceScore = importance,
                FinalScore = Math.Round(final, 4),
                MatchReason = BuildMatchReason(request, candidate, similarity, scopeBoost, typeBoost + sourceBoost)
            });
        }

        if (candidates.Truncated)
        {
            Log($"search candidate set truncated at {_settings.MaxSearchCandidates} rows");
        }

        return results
            .OrderByDescending(result => result.FinalScore)
            .ThenByDescending(result => result.MemoryRecord.UpdatedAtUtc)
            .Take(Math.Max(1, request.MaxResults))
            .ToList();
    }

    public async Task<MemoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM memory_records WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (TryReadRecord(reader, out var record, out var error))
        {
            return record;
        }

        Log($"corrupted record skipped while loading id={id}: {error}");
        return null;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_records WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAccessedAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE memory_records
            SET access_count = access_count + 1,
                last_accessed_at_utc = $last_accessed_at_utc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$last_accessed_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CompactOrPruneAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM memory_records
            WHERE (expiry_utc IS NOT NULL AND expiry_utc <= $now)
               OR (
                    importance < $importance_threshold
                AND confidence < $confidence_threshold
                AND access_count = 0
                AND updated_at_utc < $old_cutoff
               );
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$importance_threshold", _settings.PruneLowImportanceThreshold);
        command.Parameters.AddWithValue("$confidence_threshold", _settings.PruneLowConfidenceThreshold);
        command.Parameters.AddWithValue("$old_cutoff", DateTime.UtcNow.AddDays(-Math.Max(0, _settings.PruneOlderThanDays)).ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MemoryDebugStats> GetDebugStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        var now = DateTime.UtcNow.ToString("O");
        var stats = new MemoryDebugStats
        {
            TotalRecords = await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_records;", cancellationToken),
            ActiveRecords = await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_records WHERE approval_state = 'Active' AND (expiry_utc IS NULL OR expiry_utc > $now);", cancellationToken, ("$now", now)),
            ExpiredRecords = await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_records WHERE expiry_utc IS NOT NULL AND expiry_utc <= $now;", cancellationToken, ("$now", now)),
            ByType = await GroupCountsAsync(connection, "SELECT type, COUNT(*) FROM memory_records GROUP BY type;", cancellationToken),
            ByScope = await GroupCountsAsync(connection, "SELECT scope, COUNT(*) FROM memory_records GROUP BY scope;", cancellationToken),
            EmbeddingModels = await GroupCountsAsync(connection, "SELECT COALESCE(NULLIF(embedding_model, ''), 'missing') AS model, COUNT(*) FROM memory_records GROUP BY model;", cancellationToken)
        };

        return stats;
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListRecentAsync(int maxResults, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM memory_records ORDER BY updated_at_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$max", Math.Max(1, maxResults));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListByApprovalStateAsync(MemoryApprovalState approvalState, int maxResults, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM memory_records WHERE approval_state = $approval_state ORDER BY updated_at_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$approval_state", approvalState.ToString());
        command.Parameters.AddWithValue("$max", Math.Max(1, maxResults));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM memory_records ORDER BY updated_at_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$max", MaintenanceRecordLimit);
        var records = await ReadRecordsAsync(command, cancellationToken);
        if (records.Count >= MaintenanceRecordLimit)
        {
            Log($"maintenance record scan truncated at {MaintenanceRecordLimit} rows");
        }

        return records;
    }

    public async Task<bool> ExistsByContentHashAsync(string contentHash, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM memory_records WHERE content_hash = $content_hash LIMIT 1;";
        command.Parameters.AddWithValue("$content_hash", contentHash);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && value != DBNull.Value;
    }

    private async Task<CandidateLoadResult> LoadCandidatesAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var clauses = new List<string> { "1 = 1" };
        if (!request.IncludeExpired)
        {
            clauses.Add("(expiry_utc IS NULL OR expiry_utc > $now)");
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        }

        if (!request.IncludeNonActive)
        {
            clauses.Add("approval_state = $approval_state");
            command.Parameters.AddWithValue("$approval_state", MemoryApprovalState.Active.ToString());
        }

        if (request.MinConfidence > 0)
        {
            clauses.Add("confidence >= $min_confidence");
            command.Parameters.AddWithValue("$min_confidence", request.MinConfidence);
        }

        if (request.Scope is not null)
        {
            clauses.Add("scope = $scope");
            command.Parameters.AddWithValue("$scope", request.Scope.ToString());
        }

        if (request.Types is { Count: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < request.Types.Count; i++)
            {
                var parameterName = $"$type_{i}";
                placeholders.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, request.Types[i].ToString());
            }

            clauses.Add($"type IN ({string.Join(", ", placeholders)})");
        }

        var queryLimit = Math.Max(1, _settings.MaxSearchCandidates) + 1;
        command.CommandText = $"SELECT * FROM memory_records WHERE {string.Join(" AND ", clauses)} ORDER BY updated_at_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", queryLimit);

        var results = await ReadRecordsAsync(command, cancellationToken);
        var filtered = new List<MemoryRecord>(Math.Min(results.Count, _settings.MaxSearchCandidates));
        foreach (var record in results)
        {
            if (request.Tags is { Count: > 0 } &&
                !request.Tags.All(tag => record.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (request.RelatedEntityIds is { Count: > 0 } &&
                !request.RelatedEntityIds.Any(id => record.RelatedEntityIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (request.QueryEmbedding is { Length: > 0 })
            {
                if (!record.HasEmbedding)
                {
                    continue;
                }

                if (record.Embedding.Length != request.QueryEmbedding.Length)
                {
                    Log($"embedding dimension mismatch for record {record.Id}: stored={record.Embedding.Length} query={request.QueryEmbedding.Length}");
                    continue;
                }
            }

            filtered.Add(record);
            if (filtered.Count >= _settings.MaxSearchCandidates)
            {
                break;
            }
        }

        return new CandidateLoadResult(filtered, results.Count >= queryLimit || filtered.Count >= _settings.MaxSearchCandidates);
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS memory_schema (
                version INTEGER NOT NULL
            );
            INSERT INTO memory_schema(version)
            SELECT $schema_version
            WHERE NOT EXISTS (SELECT 1 FROM memory_schema);

            CREATE TABLE IF NOT EXISTS memory_records (
                id TEXT NOT NULL PRIMARY KEY,
                type TEXT NOT NULL,
                scope TEXT NOT NULL,
                source TEXT NOT NULL,
                text TEXT NOT NULL,
                summary TEXT NOT NULL,
                tags_json TEXT NOT NULL,
                related_entity_ids_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                last_accessed_at_utc TEXT NULL,
                access_count INTEGER NOT NULL DEFAULT 0,
                importance REAL NOT NULL DEFAULT 0.5,
                confidence REAL NOT NULL DEFAULT 0.5,
                expiry_utc TEXT NULL,
                approval_state TEXT NOT NULL DEFAULT 'Active',
                title TEXT NOT NULL DEFAULT '',
                source_path TEXT NOT NULL DEFAULT '',
                source_hash TEXT NOT NULL DEFAULT '',
                chunk_index INTEGER NOT NULL DEFAULT 0,
                category TEXT NOT NULL DEFAULT '',
                last_verified_utc TEXT NULL,
                metadata_json TEXT NOT NULL,
                embedding_json TEXT NOT NULL DEFAULT '[]',
                embedding_model TEXT NOT NULL DEFAULT '',
                embedding_dimensions INTEGER NOT NULL DEFAULT 0,
                content_hash TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS idx_memory_type_scope ON memory_records(type, scope);
            CREATE INDEX IF NOT EXISTS idx_memory_updated_at ON memory_records(updated_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_memory_content_hash ON memory_records(content_hash);
            """;
        command.Parameters.AddWithValue("$schema_version", SchemaVersion);
        command.ExecuteNonQuery();

        AddColumnIfMissing(connection, "memory_records", "approval_state", "TEXT NOT NULL DEFAULT 'Active'");
        AddColumnIfMissing(connection, "memory_records", "title", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "memory_records", "source_path", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "memory_records", "source_hash", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "memory_records", "chunk_index", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "memory_records", "category", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "memory_records", "last_verified_utc", "TEXT NULL");

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_memory_approval_confidence ON memory_records(approval_state, confidence);
            CREATE INDEX IF NOT EXISTS idx_memory_source_path_hash ON memory_records(source_path, source_hash);
            """;
        indexCommand.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => new(_connectionString);

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table});";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void BindRecord(SqliteCommand command, MemoryRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$type", record.Type.ToString());
        command.Parameters.AddWithValue("$scope", record.Scope.ToString());
        command.Parameters.AddWithValue("$source", record.Source.ToString());
        command.Parameters.AddWithValue("$text", record.Text);
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue("$tags_json", JsonSerializer.Serialize(record.Tags, JsonDefaults.Default));
        command.Parameters.AddWithValue("$related_entity_ids_json", JsonSerializer.Serialize(record.RelatedEntityIds, JsonDefaults.Default));
        command.Parameters.AddWithValue("$created_at_utc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", record.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$last_accessed_at_utc", (object?)record.LastAccessedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$access_count", record.AccessCount);
        command.Parameters.AddWithValue("$importance", record.Importance);
        command.Parameters.AddWithValue("$confidence", record.Confidence);
        command.Parameters.AddWithValue("$expiry_utc", (object?)record.ExpiryUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$approval_state", record.ApprovalState.ToString());
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$source_path", record.SourcePath);
        command.Parameters.AddWithValue("$source_hash", record.SourceHash);
        command.Parameters.AddWithValue("$chunk_index", record.ChunkIndex);
        command.Parameters.AddWithValue("$category", record.Category);
        command.Parameters.AddWithValue("$last_verified_utc", (object?)record.LastVerifiedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(record.Metadata, JsonDefaults.Default));
        command.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(record.Embedding));
        command.Parameters.AddWithValue("$embedding_model", record.EmbeddingModel ?? string.Empty);
        command.Parameters.AddWithValue("$embedding_dimensions", record.Embedding.Length);
        command.Parameters.AddWithValue("$content_hash", record.ContentHash);
    }

    private async Task<List<MemoryRecord>> ReadRecordsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryReadRecord(reader, out var record, out var error))
            {
                results.Add(record);
            }
            else
            {
                Log($"corrupted record skipped: {error}");
            }
        }

        return results;
    }

    private static bool TryReadRecord(SqliteDataReader reader, out MemoryRecord record, out string error)
    {
        try
        {
            var tags = Deserialize<List<string>>(reader["tags_json"]?.ToString()) ?? new List<string>();
            var relatedIds = Deserialize<List<string>>(reader["related_entity_ids_json"]?.ToString()) ?? new List<string>();
            var metadata = Deserialize<Dictionary<string, string>>(reader["metadata_json"]?.ToString())
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var embedding = Deserialize<float[]>(reader["embedding_json"]?.ToString()) ?? Array.Empty<float>();
            if (embedding.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw new InvalidOperationException("Embedding vector contains invalid numeric values.");
            }

            record = new MemoryRecord
            {
                Id = reader["id"].ToString() ?? Guid.NewGuid().ToString("N"),
                Type = Enum.Parse<MemoryRecordType>(reader["type"].ToString() ?? nameof(MemoryRecordType.Fact), true),
                Scope = Enum.Parse<MemoryScope>(reader["scope"].ToString() ?? nameof(MemoryScope.Global), true),
                Source = Enum.Parse<MemorySource>(reader["source"].ToString() ?? nameof(MemorySource.AgentAction), true),
                Text = reader["text"].ToString() ?? string.Empty,
                Summary = reader["summary"].ToString() ?? string.Empty,
                Tags = tags,
                RelatedEntityIds = relatedIds,
                CreatedAtUtc = ParseDateTime(reader["created_at_utc"]?.ToString()),
                UpdatedAtUtc = ParseDateTime(reader["updated_at_utc"]?.ToString()),
                LastAccessedAtUtc = ParseNullableDateTime(reader["last_accessed_at_utc"]?.ToString()),
                AccessCount = Convert.ToInt32(reader["access_count"]),
                Importance = Convert.ToDouble(reader["importance"]),
                Confidence = Convert.ToDouble(reader["confidence"]),
                ExpiryUtc = ParseNullableDateTime(reader["expiry_utc"]?.ToString()),
                ApprovalState = Enum.TryParse<MemoryApprovalState>(reader["approval_state"]?.ToString(), true, out var approvalState)
                    ? approvalState
                    : MemoryApprovalState.Active,
                Title = reader["title"]?.ToString() ?? string.Empty,
                SourcePath = reader["source_path"]?.ToString() ?? string.Empty,
                SourceHash = reader["source_hash"]?.ToString() ?? string.Empty,
                ChunkIndex = Convert.ToInt32(reader["chunk_index"]),
                Category = reader["category"]?.ToString() ?? string.Empty,
                LastVerifiedUtc = ParseNullableDateTime(reader["last_verified_utc"]?.ToString()),
                Metadata = metadata,
                Embedding = embedding,
                EmbeddingModel = reader["embedding_model"].ToString(),
                ContentHash = reader["content_hash"].ToString() ?? string.Empty
            };
            record.Normalize();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            record = null!;
            error = ex.Message;
            return false;
        }
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, JsonDefaults.Default);
    }

    private static DateTime ParseDateTime(string? value) =>
        DateTime.TryParse(value, out var parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : DateTime.UtcNow;

    private static DateTime? ParseNullableDateTime(string? value) =>
        DateTime.TryParse(value, out var parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : null;

    private static double Similarity(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0.0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0.0;
        }

        var cosine = dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        return Math.Clamp((cosine + 1.0) / 2.0, 0.0, 1.0);
    }

    private static double ComputeRecencyScore(DateTime updatedAtUtc, DateTime nowUtc)
    {
        var ageDays = Math.Max(0, (nowUtc - updatedAtUtc).TotalDays);
        return Math.Clamp(Math.Exp(-ageDays / 14.0), 0.0, 1.0);
    }

    private static string BuildMatchReason(MemorySearchRequest request, MemoryRecord record, double similarity, double scopeBoost, double typeBoost)
    {
        if (request.QueryEmbedding is not { Length: > 0 })
        {
            if (scopeBoost > 0 && request.Scope is not null)
            {
                return $"keyword+scope:{request.Scope}";
            }

            if (typeBoost > 0)
            {
                return $"keyword+type:{record.Type}";
            }

            return similarity >= 0.85 ? "strong_keyword_match" : "keyword_match";
        }

        if (scopeBoost > 0 && request.Scope is not null)
        {
            return $"semantic+scope:{request.Scope}";
        }

        if (typeBoost > 0)
        {
            return $"semantic+type:{record.Type}";
        }

        return similarity >= 0.85 ? "strong_semantic_match" : "semantic_match";
    }

    private static double KeywordSimilarity(string query, MemoryRecord record)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return 0.0;
        }

        var haystack = string.Join(' ', new[]
        {
            record.Summary,
            record.Text,
            record.Title,
            record.Category,
            string.Join(' ', record.Tags),
            string.Join(' ', record.RelatedEntityIds)
        }).ToLowerInvariant();

        var matched = tokens.Count(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
        if (matched == 0)
        {
            return 0.0;
        }

        var coverage = matched / (double)tokens.Count;
        var phraseBoost = haystack.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0.2 : 0.0;
        return Math.Clamp(coverage + phraseBoost, 0.0, 1.0);
    }

    private static List<string> Tokenize(string query) =>
        Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9_.-]{3,}")
            .Select(match => match.Value)
            .Where(token => token is not "the" and not "and" and not "for" and not "with" and not "from" and not "what" and not "how")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<int> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    private async Task<Dictionary<string, int>> GroupCountsAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results[reader.GetString(0)] = reader.GetInt32(1);
        }

        return results;
    }

    private void Log(string message) => _log?.Invoke(message);

    private sealed record CandidateLoadResult(IReadOnlyList<MemoryRecord> Records, bool Truncated);
}
