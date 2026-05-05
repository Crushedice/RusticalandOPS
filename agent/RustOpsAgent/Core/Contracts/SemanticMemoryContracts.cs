using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RustOpsAgent.Core.Contracts;

internal enum MemoryRecordType
{
    Fact,
    Procedure,
    Failure,
    Fix,
    UserInstruction,
    ServerState,
    ToolObservation,
    Reflection,
    PluginSummary,
    Exception,
    ServerConvar,
    ServerCommand
}

internal enum MemoryScope
{
    Global,
    Server,
    Project,
    User,
    Tool
}

internal enum MemorySource
{
    SteamChat,
    AgentAction,
    LogMonitor,
    AdminCommand,
    ReflectionLoop,
    ConfigScan,
    ManualImport,
    SeededImport,
    AiGeneratedImport,
    FailedAttempt,
    VerifiedFact,
    PluginSummary,
    LogClassifier,
    ServerCatalog
}

internal enum MemoryApprovalState
{
    Active,
    Pending,
    Rejected
}

internal sealed class MemoryRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("type")] public MemoryRecordType Type { get; set; }
    [JsonPropertyName("scope")] public MemoryScope Scope { get; set; } = MemoryScope.Global;
    [JsonPropertyName("source")] public MemorySource Source { get; set; } = MemorySource.AgentAction;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("relatedEntityIds")] public List<string> RelatedEntityIds { get; set; } = new();
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastAccessedAtUtc")] public DateTime? LastAccessedAtUtc { get; set; }
    [JsonPropertyName("accessCount")] public int AccessCount { get; set; }
    [JsonPropertyName("importance")] public double Importance { get; set; } = 0.5;
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.5;
    [JsonPropertyName("expiryUtc")] public DateTime? ExpiryUtc { get; set; }
    [JsonPropertyName("approvalState")] public MemoryApprovalState ApprovalState { get; set; } = MemoryApprovalState.Active;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("sourcePath")] public string SourcePath { get; set; } = string.Empty;
    [JsonPropertyName("sourceHash")] public string SourceHash { get; set; } = string.Empty;
    [JsonPropertyName("chunkIndex")] public int ChunkIndex { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("lastVerifiedUtc")] public DateTime? LastVerifiedUtc { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = Array.Empty<float>();
    [JsonPropertyName("embeddingModel")] public string? EmbeddingModel { get; set; }
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = string.Empty;
    [JsonIgnore] public bool HasEmbedding => Embedding.Length > 0;

    public void Normalize()
    {
        Text = Text?.Trim() ?? string.Empty;
        Summary = Summary?.Trim() ?? string.Empty;
        Title = Title?.Trim() ?? string.Empty;
        SourcePath = SourcePath?.Trim() ?? string.Empty;
        SourceHash = SourceHash?.Trim() ?? string.Empty;
        Category = Category?.Trim() ?? string.Empty;
        Tags = Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RelatedEntityIds = RelatedEntityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        if (CreatedAtUtc == default)
        {
            CreatedAtUtc = DateTime.UtcNow;
        }

        UpdatedAtUtc = UpdatedAtUtc == default ? CreatedAtUtc : UpdatedAtUtc;
        Importance = Math.Clamp(Importance, 0.0, 1.0);
        Confidence = Math.Clamp(Confidence, 0.0, 1.0);
        ContentHash = ComputeContentHash(this);
    }

    public static string ComputeContentHash(MemoryRecord record)
    {
        var builder = new StringBuilder();
        builder.Append(record.Type).Append('|');
        builder.Append(record.Scope).Append('|');
        builder.Append(record.Source).Append('|');
        builder.Append(record.Summary.Trim()).Append('|');
        builder.Append(record.Text.Trim()).Append('|');
        builder.Append(string.Join(',', record.Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))).Append('|');
        builder.Append(string.Join(',', record.RelatedEntityIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    public static void Validate(MemoryRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        record.Normalize();

        if (string.IsNullOrWhiteSpace(record.Text))
        {
            throw new InvalidOperationException("Memory text is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Summary))
        {
            throw new InvalidOperationException("Memory summary is required.");
        }

        if (record.Embedding.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(record.EmbeddingModel))
        {
            throw new InvalidOperationException("Embedding model is required.");
        }

        if (record.Embedding.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
        {
            throw new InvalidOperationException("Memory embedding contains invalid numeric values.");
        }
    }
}

internal sealed class MemorySearchRequest
{
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;
    [JsonPropertyName("types")] public List<MemoryRecordType>? Types { get; set; }
    [JsonPropertyName("scope")] public MemoryScope? Scope { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("relatedEntityIds")] public List<string>? RelatedEntityIds { get; set; }
    [JsonPropertyName("maxResults")] public int MaxResults { get; set; } = 6;
    [JsonPropertyName("minSimilarity")] public double MinSimilarity { get; set; } = 0.55;
    [JsonPropertyName("minConfidence")] public double MinConfidence { get; set; }
    [JsonPropertyName("includeExpired")] public bool IncludeExpired { get; set; }
    [JsonPropertyName("includeNonActive")] public bool IncludeNonActive { get; set; }
    [JsonIgnore] public float[]? QueryEmbedding { get; set; }
    [JsonIgnore] public string? QueryEmbeddingModel { get; set; }
}

internal sealed class MemorySearchResult
{
    [JsonPropertyName("memoryRecord")] public required MemoryRecord MemoryRecord { get; init; }
    [JsonPropertyName("similarityScore")] public double SimilarityScore { get; init; }
    [JsonPropertyName("recencyScore")] public double RecencyScore { get; init; }
    [JsonPropertyName("importanceScore")] public double ImportanceScore { get; init; }
    [JsonPropertyName("finalScore")] public double FinalScore { get; init; }
    [JsonPropertyName("matchReason")] public string MatchReason { get; init; } = "semantic";
}

internal sealed class MemoryDebugStats
{
    [JsonPropertyName("totalRecords")] public int TotalRecords { get; set; }
    [JsonPropertyName("activeRecords")] public int ActiveRecords { get; set; }
    [JsonPropertyName("expiredRecords")] public int ExpiredRecords { get; set; }
    [JsonPropertyName("byType")] public Dictionary<string, int> ByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("byScope")] public Dictionary<string, int> ByScope { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("embeddingModels")] public Dictionary<string, int> EmbeddingModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record WorkflowMemoryContext
{
    public static readonly WorkflowMemoryContext Empty = new();

    public string Query { get; init; } = string.Empty;
    public List<MemorySearchResult> Results { get; init; } = new();
    public string CompactContext { get; init; } = string.Empty;
    public bool RetrievalSkipped { get; init; }
    public string? SkipReason { get; init; }
    public string? RetrievalOrigin { get; init; }

    public bool HasResults => Results.Count > 0;
}

internal sealed class MemoryMigrationReport
{
    public List<string> FilesScanned { get; set; } = new();
    public int RecordsDiscovered { get; set; }
    public int RecordsImported { get; set; }
    public int RecordsSkipped { get; set; }
    public int DuplicatesRemoved { get; set; }
    public int EmbeddingFailures { get; set; }
    public int MalformedEntries { get; set; }
    public int OtherErrors { get; set; }
    public bool DryRun { get; set; }
    public List<string> SkipReasons { get; set; } = new();

    public string ToSummary() =>
        $"files={FilesScanned.Count} discovered={RecordsDiscovered} imported={RecordsImported} skipped={RecordsSkipped} duplicates={DuplicatesRemoved} embeddingFailures={EmbeddingFailures} malformed={MalformedEntries} errors={OtherErrors} dryRun={DryRun}";
}

internal sealed class MemoryImportOptions
{
    public string FolderPath { get; set; } = string.Empty;
    public bool Trusted { get; set; }
    public bool DryRun { get; set; }
}

internal sealed class MemoryImportReport
{
    public int FilesScanned { get; set; }
    public int ChunksDiscovered { get; set; }
    public int Imported { get; set; }
    public int Pending { get; set; }
    public int Rejected { get; set; }
    public int Duplicates { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> Messages { get; set; } = new();

    public string ToSummary() =>
        $"files={FilesScanned} chunks={ChunksDiscovered} imported={Imported} pending={Pending} duplicates={Duplicates} skipped={Skipped} errors={Errors}";
}

internal enum MemoryImportDisposition
{
    Imported,
    Duplicate,
    Skipped,
    EmbeddingFailure,
    Invalid
}

internal sealed class ManualMemoryInput
{
    public MemoryRecordType Type { get; set; } = MemoryRecordType.Fact;
    public MemoryScope Scope { get; set; } = MemoryScope.Global;
    public MemorySource Source { get; set; } = MemorySource.AdminCommand;
    public string Text { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string> RelatedEntityIds { get; set; } = new();
    public double Importance { get; set; } = 0.6;
    public double Confidence { get; set; } = 0.8;
    public MemoryApprovalState ApprovalState { get; set; } = MemoryApprovalState.Active;
    public string Title { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string Category { get; set; } = string.Empty;
    public DateTime? LastVerifiedUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal interface IMemoryStore
{
    Task UpsertAsync(MemoryRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task MarkAccessedAsync(string id, CancellationToken cancellationToken);
    Task<int> CompactOrPruneAsync(CancellationToken cancellationToken);
    Task<MemoryDebugStats> GetDebugStatsAsync(CancellationToken cancellationToken);
}

internal interface IInspectableMemoryStore : IMemoryStore
{
    Task<IReadOnlyList<MemoryRecord>> ListRecentAsync(int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListByApprovalStateAsync(MemoryApprovalState approvalState, int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> GetAllAsync(CancellationToken cancellationToken);
    Task<bool> ExistsByContentHashAsync(string contentHash, CancellationToken cancellationToken);
}

internal interface IEmbeddingProvider
{
    string ModelName { get; }
    int? Dimensions { get; }
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken);
}

internal interface ISemanticMemoryService
{
    Task<WorkflowMemoryContext> RecallForPlanningAsync(
        string message,
        ConversationSelectionState state,
        IReadOnlyList<string> knownServers,
        CancellationToken cancellationToken);

    Task<WorkflowMemoryContext> RecallForExecutionAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken);

    Task RecordActionOutcomeAsync(
        ToolExecutionContext context,
        ToolExecutionResult result,
        CancellationToken cancellationToken);

    Task RecordUserInstructionAsync(
        string? adminId,
        string? serverName,
        string instruction,
        CancellationToken cancellationToken);

    Task RecordReflectionAsync(
        string summary,
        string detail,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken);

    Task RecordServerFactAsync(
        string serverName,
        string summary,
        string detail,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken);

    Task<MemoryDebugStats> GetStatsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListRecentAsync(int maxResults, CancellationToken cancellationToken);
    Task<IReadOnlyList<IGrouping<string, MemoryRecord>>> ListRepeatedFailuresAsync(int minOccurrences, CancellationToken cancellationToken);
    Task<MemoryRecord> AddManualMemoryAsync(ManualMemoryInput input, CancellationToken cancellationToken);
    Task<MemoryImportDisposition> ImportRecordAsync(MemoryRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListPendingAsync(int maxResults, CancellationToken cancellationToken);
    Task<bool> SetApprovalStateAsync(string id, MemoryApprovalState approvalState, CancellationToken cancellationToken);
    Task<int> RebuildEmbeddingsAsync(CancellationToken cancellationToken);
    Task<MemoryMigrationReport> MigrateLegacyMemoryAsync(bool dryRun, CancellationToken cancellationToken);
    Task<int> PruneAsync(CancellationToken cancellationToken);
}

internal interface IMemoryImportService
{
    Task<MemoryImportReport> ImportFolderAsync(MemoryImportOptions options, CancellationToken cancellationToken);
}

internal sealed class PluginReferenceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerName { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<PluginCommandReference> Commands { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public List<string> Hooks { get; set; } = new();
    public List<string> ConfigKeys { get; set; } = new();
    public string RawSourceReferenceId { get; set; } = string.Empty;
    public DateTime LastIndexedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class PluginCommandReference
{
    public string Command { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string HandlerMethod { get; set; } = string.Empty;
    public string RequiredPermission { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? LineNumber { get; set; }
}

internal interface IPluginReferenceIndexStore
{
    Task<PluginReferenceRecord?> GetBySourcePathAsync(string sourcePath, CancellationToken cancellationToken);
    Task UpsertAsync(PluginReferenceRecord record, string rawSource, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginReferenceRecord>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginReferenceRecord>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginReferenceRecord>> SearchByCommandAsync(string command, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginReferenceRecord>> SearchByHookAsync(string hook, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginReferenceRecord>> SearchByPermissionAsync(string permission, CancellationToken cancellationToken);
}
