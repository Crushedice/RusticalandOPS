using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using RustOpsAgent.Core;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Core.Interaction;
using RustOpsAgent.Domains.Rust;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;
using RustOpsAgent.Infrastructure.Memory;
using AutoPullService = RustOpsAgent.Infrastructure.AutoPullService;

namespace RustOpsAgent.Tests;

public class SemanticMemoryTests
{
    [Fact]
    public void MemoryRecord_Validation_Allows_Missing_Embeddings_For_Migration()
    {
        var record = new MemoryRecord
        {
            Type = MemoryRecordType.Fact,
            Scope = MemoryScope.Project,
            Source = MemorySource.ManualImport,
            Summary = "Known config path",
            Text = "The config lives at /srv/rust/server.json"
        };

        MemoryRecord.Validate(record);
        Assert.False(record.HasEmbedding);
    }

    [Fact]
    public void MemoryRecord_Validation_Rejects_Invalid_Embedding_Numbers()
    {
        var record = NewRecord("Known config path", new[] { 1f, float.NaN, 0f, 0f });
        Assert.Throws<InvalidOperationException>(() => MemoryRecord.Validate(record));
    }

    [Fact]
    public async Task SqliteMemoryStore_Creates_Directory_And_Schema_Idempotently()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "nested", "semantic", "memory.db");
        var settings = TestMemorySettings(dbPath);

        var first = new SqliteMemoryStore(dbPath, settings);
        var second = new SqliteMemoryStore(dbPath, settings);

        await first.UpsertAsync(NewRecord("restart monthly with countdown", new[] { 1f, 0f, 0f, 0f }), CancellationToken.None);
        var stats = await second.GetDebugStatsAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.GetDirectoryName(dbPath)!));
        Assert.Equal(1, stats.TotalRecords);
    }

    [Fact]
    public async Task SqliteMemoryStore_Deduplicates_By_ContentHash()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath));
        var record = NewRecord("Restart monthly with 120s countdown", new[] { 1f, 0f, 0f, 0f });

        await store.UpsertAsync(record, CancellationToken.None);
        await store.UpsertAsync(record, CancellationToken.None);

        var stats = await store.GetDebugStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.TotalRecords);
    }

    [Fact]
    public async Task SqliteMemoryStore_Search_Respects_MinSimilarity_MaxResults_And_Filters()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath));
        await store.UpsertAsync(NewRecord("Restart monthly with 120s countdown", new[] { 1f, 0f, 0f, 0f }, MemoryRecordType.Procedure, MemoryScope.Server, tags: new[] { "restart" }, relatedIds: new[] { "monthly" }), CancellationToken.None);
        await store.UpsertAsync(NewRecord("Broken weekly restart attempt", new[] { 0f, 1f, 0f, 0f }, MemoryRecordType.Failure, MemoryScope.Server, tags: new[] { "restart" }, relatedIds: new[] { "weekly" }), CancellationToken.None);

        var results = await store.SearchAsync(new MemorySearchRequest
        {
            Query = "restart monthly",
            QueryEmbedding = new[] { 1f, 0f, 0f, 0f },
            Types = new List<MemoryRecordType> { MemoryRecordType.Procedure },
            Scope = MemoryScope.Server,
            Tags = new List<string> { "restart" },
            RelatedEntityIds = new List<string> { "monthly" },
            MaxResults = 1,
            MinSimilarity = 0.5
        }, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("monthly", results[0].MemoryRecord.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqliteMemoryStore_Search_Truncates_Candidates_And_Logs()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var logs = new List<string>();
        var settings = TestMemorySettings(dbPath);
        settings.MaxSearchCandidates = 2;
        var store = new SqliteMemoryStore(dbPath, settings, logs.Add);

        for (var i = 0; i < 5; i++)
        {
            await store.UpsertAsync(NewRecord($"restart monthly #{i}", new[] { 1f, 0f, 0f, 0f }, MemoryRecordType.Procedure, MemoryScope.Server), CancellationToken.None);
        }

        var results = await store.SearchAsync(new MemorySearchRequest
        {
            Query = "restart monthly",
            QueryEmbedding = new[] { 1f, 0f, 0f, 0f },
            MaxResults = 5,
            MinSimilarity = 0.1
        }, CancellationToken.None);

        Assert.True(results.Count <= 2);
        Assert.Contains(logs, message => message.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SqliteMemoryStore_Search_Skips_Corrupted_Records_And_Logs()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var logs = new List<string>();
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath), logs.Add);
        await store.UpsertAsync(NewRecord("good restart procedure", new[] { 1f, 0f, 0f, 0f }), CancellationToken.None);

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO memory_records (
                    id, type, scope, source, text, summary, tags_json, related_entity_ids_json,
                    created_at_utc, updated_at_utc, last_accessed_at_utc, access_count, importance,
                    confidence, expiry_utc, metadata_json, embedding_json, embedding_model, embedding_dimensions,
                    content_hash
                ) VALUES (
                    'broken', 'Fact', 'Project', 'ManualImport', 'bad', 'bad', '{oops}', '[]',
                    $created, $updated, NULL, 0, 0.2, 0.2, NULL, '[]', '[1,2,3]', 'fake', 3, 'broken-hash'
                );
                """;
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var results = await store.SearchAsync(new MemorySearchRequest
        {
            Query = "restart",
            QueryEmbedding = new[] { 1f, 0f, 0f, 0f },
            MaxResults = 5,
            MinSimilarity = 0.1
        }, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains(logs, message => message.Contains("corrupted record skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SqliteMemoryStore_Excludes_Expired_And_Deleted_By_Default()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath));

        var active = NewRecord("active memory", new[] { 1f, 0f, 0f, 0f });
        var expired = NewRecord("expired memory", new[] { 1f, 0f, 0f, 0f });
        expired.ExpiryUtc = DateTime.UtcNow.AddMinutes(-1);
        expired.Normalize();

        await store.UpsertAsync(active, CancellationToken.None);
        await store.UpsertAsync(expired, CancellationToken.None);
        await store.DeleteAsync(active.Id, CancellationToken.None);

        var results = await store.SearchAsync(new MemorySearchRequest
        {
            Query = "memory",
            QueryEmbedding = new[] { 1f, 0f, 0f, 0f },
            MaxResults = 5,
            MinSimilarity = 0.1
        }, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SqliteMemoryStore_Zero_Vector_Does_Not_Crash_Search()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath));
        await store.UpsertAsync(NewRecord("zero vector", new[] { 0f, 0f, 0f, 0f }), CancellationToken.None);

        var results = await store.SearchAsync(new MemorySearchRequest
        {
            Query = "zero",
            QueryEmbedding = new[] { 1f, 0f, 0f, 0f },
            MaxResults = 5,
            MinSimilarity = 0.1
        }, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SqliteMemoryStore_Concurrent_Reads_And_Writes_Do_Not_Corrupt_Database()
    {
        var root = TempRoot();
        var dbPath = Path.Combine(root, "memory.db");
        var store = new SqliteMemoryStore(dbPath, TestMemorySettings(dbPath));

        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            await store.UpsertAsync(NewRecord($"record-{i}", new[] { 1f, 0f, 0f, i % 2 }), CancellationToken.None);
            await store.SearchAsync(new MemorySearchRequest
            {
                Query = $"record-{i}",
                QueryEmbedding = new[] { 1f, 0f, 0f, i % 2 },
                MaxResults = 3,
                MinSimilarity = 0.1
            }, CancellationToken.None);
        });

        await Task.WhenAll(tasks);

        var stats = await store.GetDebugStatsAsync(CancellationToken.None);
        Assert.True(stats.TotalRecords >= 20);
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_Normalizes_BaseUrl_And_Parses_Response()
    {
        var observedUri = string.Empty;
        var handler = new StubHttpMessageHandler(request =>
        {
            observedUri = request.RequestUri!.ToString();
            return JsonResponse("""
            {
              "data": [
                { "index": 0, "embedding": [0.1, 0.2, 0.3] }
              ]
            }
            """);
        });

        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1/",
            "text-embedding-test",
            null,
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        var vector = await provider.GenerateEmbeddingAsync("hello", CancellationToken.None);

        Assert.Equal("http://127.0.0.1:1234/v1/embeddings", observedUri);
        Assert.Equal(3, vector.Length);
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_Allows_Missing_ApiKey_When_Not_Required()
    {
        AuthenticationHeaderValue? header = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            header = request.Headers.Authorization;
            return JsonResponse("""
            {
              "data": [
                { "index": 0, "embedding": [0.1, 0.2] }
              ]
            }
            """);
        });

        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "text-embedding-test",
            "",
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        await provider.GenerateEmbeddingAsync("hello", CancellationToken.None);
        Assert.Null(header);
    }

    [Fact]
    public void OpenAiCompatibleEmbeddingProvider_Requires_ApiKey_When_Configured()
    {
        Assert.Throws<InvalidOperationException>(() => new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "text-embedding-test",
            null,
            requireApiKey: true,
            timeoutSeconds: 30,
            batchSize: 8));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task OpenAiCompatibleEmbeddingProvider_Handles_Http_Failures(HttpStatusCode statusCode, string expectedCode)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"error":{"message":"model not found"}}""")
        });

        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "missing-model",
            "key",
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateEmbeddingAsync("hello", CancellationToken.None));
        Assert.Contains(expectedCode, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_Handles_Timeouts()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("timed out"));
        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "text-embedding-test",
            "key",
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateEmbeddingAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_Rejects_Empty_Vectors_And_Dimension_Mismatch()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "data": [
            { "index": 0, "embedding": [0.1, 0.2, 0.3] }
          ]
        }
        """));

        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "text-embedding-test",
            "key",
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        await provider.GenerateEmbeddingAsync("hello", CancellationToken.None);

        handler.ResponseFactory = _ => JsonResponse("""
        {
          "data": [
            { "index": 0, "embedding": [] }
          ]
        }
        """);
        var emptyVector = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateEmbeddingAsync("hello", CancellationToken.None));
        Assert.Contains("empty vector", emptyVector.Message, StringComparison.OrdinalIgnoreCase);

        handler.ResponseFactory = _ => JsonResponse("""
        {
          "data": [
            { "index": 0, "embedding": [0.1, 0.2] }
          ]
        }
        """);
        var dimensionMismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateEmbeddingAsync("hello", CancellationToken.None));
        Assert.Contains("dimension mismatch", dimensionMismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_Rejects_Batch_Count_Mismatch()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "data": [
            { "index": 0, "embedding": [0.1, 0.2, 0.3] }
          ]
        }
        """));

        using var provider = new OpenAiCompatibleEmbeddingProvider(
            "http://127.0.0.1:1234/v1",
            "text-embedding-test",
            "key",
            requireApiKey: false,
            timeoutSeconds: 30,
            batchSize: 8,
            httpClient: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateEmbeddingsAsync(new[] { "a", "b" }, CancellationToken.None));
        Assert.Contains("batch size mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migration_DryRun_Reports_Without_Importing()
    {
        var root = TempRoot();
        var legacyPath = CreateLegacyState(root);
        var service = MakeService(root, new FakeEmbeddingProvider(), legacyPath, Path.Combine(root, "NeoCortex"));

        var report = await service.MigrateLegacyMemoryAsync(true, CancellationToken.None);
        var recent = await service.ListRecentAsync(10, CancellationToken.None);

        Assert.True(report.DryRun);
        Assert.True(report.RecordsDiscovered >= 2);
        Assert.Empty(recent);
    }

    [Fact]
    public async Task Migration_Is_Idempotent_Across_Repeated_Runs()
    {
        var root = TempRoot();
        var legacyPath = CreateLegacyState(root);
        var service = MakeService(root, new FakeEmbeddingProvider(), legacyPath, Path.Combine(root, "NeoCortex"));

        var first = await service.MigrateLegacyMemoryAsync(false, CancellationToken.None);
        var second = await service.MigrateLegacyMemoryAsync(false, CancellationToken.None);
        var stats = await service.GetStatsAsync(CancellationToken.None);

        Assert.True(first.RecordsImported >= 2);
        Assert.True(second.DuplicatesRemoved >= 2);
        Assert.Equal(first.RecordsImported, stats.TotalRecords);
    }

    [Fact]
    public async Task Migration_Skips_Malformed_Records_With_Reason()
    {
        var root = TempRoot();
        Directory.CreateDirectory(Path.Combine(root, "NeoCortex", "evolution"));
        var legacyPath = CreateLegacyState(root);
        await File.WriteAllTextAsync(Path.Combine(root, "NeoCortex", "evolution", "incidents.jsonl"), """
        {"classification":"timeout","request":"restart monthly","failureReason":"timed out"}
        {not-json}
        """);

        var service = MakeService(root, new FakeEmbeddingProvider(), legacyPath, Path.Combine(root, "NeoCortex"));
        var report = await service.MigrateLegacyMemoryAsync(false, CancellationToken.None);

        Assert.True(report.MalformedEntries >= 1);
        Assert.Contains(report.SkipReasons, reason => reason.Contains("malformed_jsonl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Migration_Without_EmbeddingProvider_Imports_Metadata_And_Rebuild_Fills_Embeddings()
    {
        var root = TempRoot();
        var legacyPath = CreateLegacyState(root);
        var settings = TestMemorySettings(Path.Combine(root, "memory.db"));
        var serviceWithoutEmbeddings = new SemanticMemoryService(
            settings,
            new SqliteMemoryStore(settings.DatabasePath, settings),
            embeddingProvider: null,
            legacyPath,
            Path.Combine(root, "NeoCortex"));

        var migration = await serviceWithoutEmbeddings.MigrateLegacyMemoryAsync(false, CancellationToken.None);
        var imported = await serviceWithoutEmbeddings.ListRecentAsync(10, CancellationToken.None);
        Assert.True(migration.RecordsImported >= 2);
        Assert.Contains(imported, record => !record.HasEmbedding);

        var serviceWithEmbeddings = MakeService(root, new FakeEmbeddingProvider(), legacyPath, Path.Combine(root, "NeoCortex"));
        var rebuilt = await serviceWithEmbeddings.RebuildEmbeddingsAsync(CancellationToken.None);
        var rebuiltRecords = await serviceWithEmbeddings.ListRecentAsync(10, CancellationToken.None);

        Assert.True(rebuilt >= 1);
        Assert.All(rebuiltRecords, record => Assert.True(record.HasEmbedding));
    }

    [Fact]
    public async Task RustChatToolHandler_Routes_Memory_Commands()
    {
        var service = new CommandMemoryService();
        var handler = new RustChatToolHandler(MakeNeoCortex(TempRoot()), service);

        var stats = await ExecuteChatCommandAsync(handler, "memory stats");
        var search = await ExecuteChatCommandAsync(handler, "memory search restart failure");
        var show = await ExecuteChatCommandAsync(handler, "memory show abc");
        var delete = await ExecuteChatCommandAsync(handler, "memory delete abc");
        var recent = await ExecuteChatCommandAsync(handler, "memory recent");
        var migrate = await ExecuteChatCommandAsync(handler, "memory migrate dry-run");
        var rebuild = await ExecuteChatCommandAsync(handler, "memory rebuild");
        var prune = await ExecuteChatCommandAsync(handler, "memory prune");

        Assert.Contains("Memory stats", stats.Message);
        Assert.Contains("Known fix", search.Message);
        Assert.Contains("Id: abc", show.Message);
        Assert.True(delete.MutatedState);
        Assert.Contains("Known fix", recent.Message);
        Assert.Contains("dryRun=True", migrate.Message);
        Assert.Contains("Rebuilt embeddings", rebuild.Message);
        Assert.Contains("Pruned", prune.Message);
        Assert.Equal("restart failure", service.LastSearchQuery);
        Assert.True(service.LastMigrationDryRun);
        Assert.Equal("abc", service.LastDeletedId);
    }

    [Fact]
    public async Task AgentRuntime_Uses_Failure_Memory_To_Change_Action_Choice()
    {
        var root = TempRoot();
        var harness = CreateRuntimeHarness(
            root,
            new[] { new FailureAwareRconHandler() },
            semanticMemoryFactory: service => SeedFailureMemoryAsync(service, "monthly"));

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("rcon status on monthly"), CancellationToken.None);
        var reply = ReadOnlyOutboxMessage(harness.Config.Outbox.MessageOutboxPath);

        Assert.Contains("Used previous failure memory", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentRuntime_Uses_Fix_Memory_To_Influence_Composed_Response()
    {
        var root = TempRoot();
        var fakeKernel = BuildKernel(prompt => prompt.Contains("Graceful restart fix", StringComparison.OrdinalIgnoreCase) ? "MEMORY-SEEN" : "NO-MEMORY");
        var harness = CreateRuntimeHarness(
            root,
            new[] { new StaticStatusHandler("status collected") },
            composeKernel: fakeKernel,
            composeSettings: new LlmSettings { Enabled = true, UseChatSystemPrompt = false },
            semanticMemoryFactory: service => service.AddManualMemoryAsync(new ManualMemoryInput
            {
                Type = MemoryRecordType.Fix,
                Scope = MemoryScope.Server,
                Summary = "Graceful restart fix",
                Text = "Use restart 120 on monthly to avoid a dirty shutdown.",
                RelatedEntityIds = new List<string> { "monthly" },
                Tags = new List<string> { "restart", "monthly" }
            }, CancellationToken.None));

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("status on monthly"), CancellationToken.None);
        var reply = ReadOnlyOutboxMessage(harness.Config.Outbox.MessageOutboxPath);

        Assert.Equal("MEMORY-SEEN", reply);
    }

    [Fact]
    public async Task AgentRuntime_Writes_Failure_Memory_After_Failed_Action()
    {
        var root = TempRoot();
        var harness = CreateRuntimeHarness(
            root,
            new[] { new StaticServerControlHandler(false, "restart failed token=abc123 password=hunter2", "timeout", new FailurePayload("Use restart 120 first")) });

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("restart on monthly"), CancellationToken.None);
        var recent = await harness.SemanticMemory.ListRecentAsync(10, CancellationToken.None);
        var failure = Assert.Single(recent, record => record.Type == MemoryRecordType.Failure);

        Assert.Contains("monthly", failure.Metadata["selectedServer"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NextStepHint: Use restart 120 first", failure.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", failure.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", failure.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentRuntime_Writes_Success_Memory_Only_For_Durable_Results()
    {
        var root = TempRoot();
        var harness = CreateRuntimeHarness(
            root,
            new IToolHandler[]
            {
                new StaticRconHandler("Use status before restart on monthly."),
                new StaticStatusHandler("ok")
            });

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("rcon status on monthly"), CancellationToken.None);
        await harness.Runtime.ProcessSingleChatMessageAsync(Path.Combine(root, "second.json"), harness.Chat("status on monthly"), CancellationToken.None);

        var recent = await harness.SemanticMemory.ListRecentAsync(10, CancellationToken.None);

        Assert.Contains(recent, record => record.Type == MemoryRecordType.Procedure && record.Summary.Contains("Use status before restart", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recent, record => record.Summary.Contains(" - ok", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentRuntime_Skips_Search_And_Write_When_Memory_Disabled()
    {
        var root = TempRoot();
        var provider = new CountingEmbeddingProvider();
        var settings = TestMemorySettings(Path.Combine(root, "memory.db"));
        settings.SearchEnabled = false;
        settings.WriteEnabled = false;
        var semanticMemory = new SemanticMemoryService(settings, new SqliteMemoryStore(settings.DatabasePath, settings), provider, Path.Combine(root, "legacy-state.json"), Path.Combine(root, "NeoCortex"));
        var harness = CreateRuntimeHarness(root, new[] { new StaticStatusHandler("status collected") }, semanticMemory: semanticMemory);

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("status on monthly"), CancellationToken.None);
        var reply = ReadOnlyOutboxMessage(harness.Config.Outbox.MessageOutboxPath);

        Assert.Equal(0, provider.GenerateSingleCalls + provider.GenerateBatchCalls);
        Assert.Contains("status collected", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentRuntime_Continues_When_EmbeddingProvider_Is_Down()
    {
        var root = TempRoot();
        var semanticMemory = MakeService(root, new ThrowingEmbeddingProvider());
        var harness = CreateRuntimeHarness(root, new[] { new StaticStatusHandler("status collected") }, semanticMemory: semanticMemory);

        await harness.Runtime.ProcessSingleChatMessageAsync(harness.InboxFile, harness.Chat("status on monthly"), CancellationToken.None);
        var reply = ReadOnlyOutboxMessage(harness.Config.Outbox.MessageOutboxPath);

        Assert.Contains("status collected", reply, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedFailureMemoryAsync(SemanticMemoryService service, string server)
    {
        await service.AddManualMemoryAsync(new ManualMemoryInput
        {
            Type = MemoryRecordType.Failure,
            Scope = MemoryScope.Server,
            Summary = $"Status command previously failed on {server}",
            Text = $"Running status on {server} failed because the service was offline.",
            RelatedEntityIds = new List<string> { server, AdminIntentType.RconCommand.ToString() },
            Tags = new List<string> { "status", "failure", server },
            Importance = 1.0,
            Confidence = 0.95
        }, CancellationToken.None);
    }

    private static RuntimeHarness CreateRuntimeHarness(
        string root,
        IEnumerable<IToolHandler> handlers,
        Kernel? composeKernel = null,
        LlmSettings? composeSettings = null,
        SemanticMemoryService? semanticMemory = null,
        Func<SemanticMemoryService, Task>? semanticMemoryFactory = null)
    {
        Directory.CreateDirectory(root);
        var config = TestAgentConfig(root);
        var neoCortex = MakeNeoCortex(root);
        neoCortex.EnsureMigrated();
        var legacyState = new LegacyAgentStateStore(config.Memory.StatePath);
        var memory = semanticMemory ?? MakeService(root, new FakeEmbeddingProvider(), config.Memory.StatePath, config.Memory.NeoCortexRoot);
        semanticMemoryFactory?.Invoke(memory).GetAwaiter().GetResult();

        var classifier = new AdminIntentClassifier(kernel: null, settings: new LlmSettings { Enabled = false }, neoCortex: neoCortex, semanticMemory: memory);
        var registry = new ToolRegistry(handlers);
        var executor = new ActionExecutor(registry, memory);
        var composer = new ResponseComposer(composeKernel, composeSettings ?? new LlmSettings { Enabled = false });
        var gitOps = new GitOpsService(new GitOpsSettings { Enabled = false, RepoPath = root, PushBranchPrefix = "agent/" });
        var autoPull = new AutoPullService(new AutoPullSettings { Enabled = false });
        var api = new RustOpsApiClient(new ApiSettings { BaseUrl = "http://127.0.0.1:1", ApiKey = "test" });
        var runtime = new AgentRuntime(config, classifier, executor, composer, neoCortex, legacyState, memory, gitOps, autoPull, api, kernel: null);
        var inboxFile = Path.Combine(root, "chat-item.json");
        File.WriteAllText(inboxFile, "{}");

        return new RuntimeHarness(runtime, memory, config, inboxFile);
    }

    private static ChatInboxItem Chat(string message) => new()
    {
        AdminId = "admin",
        Id = Guid.NewGuid().ToString("N"),
        Message = message
    };

    private static string ReadOnlyOutboxMessage(string outboxPath)
    {
        var file = Directory.GetFiles(outboxPath, "*.json").Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
    }

    private static Task<ToolExecutionResult> ExecuteChatCommandAsync(RustChatToolHandler handler, string message)
    {
        var route = new AdminIntentRoute(AdminIntentType.Chat, new AdminIntentSlots(null, null, null, null, null), 0.9, false, null, "rust.chat.reply");
        return handler.ExecuteAsync(new ToolExecutionContext("admin", message, route, new ConversationSelectionState { AdminId = "admin" }, DateTime.UtcNow), CancellationToken.None);
    }

    private static SemanticMemoryService MakeService(string root, IEmbeddingProvider? provider, string? legacyPath = null, string? neoRoot = null)
    {
        Directory.CreateDirectory(root);
        var settings = TestMemorySettings(Path.Combine(root, "memory.db"));
        return new SemanticMemoryService(
            settings,
            new SqliteMemoryStore(settings.DatabasePath, settings),
            provider,
            legacyPath ?? Path.Combine(root, "legacy-state.json"),
            neoRoot ?? Path.Combine(root, "NeoCortex"));
    }

    private static MemorySettings TestMemorySettings(string dbPath)
    {
        return new MemorySettings
        {
            DatabasePath = dbPath,
            SearchEnabled = true,
            WriteEnabled = true,
            SimilarityThreshold = 0.1,
            MaxRetrievedMemoriesPerStep = 6,
            MaxSearchCandidates = 50,
            MaxInjectedMemoryCharacters = 4000,
            MaxWritesPerWorkflowStep = 1,
            DebugLoggingEnabled = true
        };
    }

    private static AgentConfig TestAgentConfig(string root)
    {
        var inbox = Path.Combine(root, "chat-inbox");
        var feedback = Path.Combine(root, "feedback-inbox");
        var decision = Path.Combine(root, "decision-inbox");
        var outbox = Path.Combine(root, "outbox");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(feedback);
        Directory.CreateDirectory(decision);
        Directory.CreateDirectory(outbox);

        return new AgentConfig
        {
            Api = new ApiSettings { BaseUrl = "http://127.0.0.1:1", ApiKey = "test" },
            Memory = new MemorySettings
            {
                StatePath = Path.Combine(root, "agent-state.json"),
                NeoCortexRoot = Path.Combine(root, "NeoCortex"),
                DatabasePath = Path.Combine(root, "memory.db"),
                SearchEnabled = true,
                WriteEnabled = true,
                SimilarityThreshold = 0.1,
                MaxRetrievedMemoriesPerStep = 6,
                MaxSearchCandidates = 50,
                MaxInjectedMemoryCharacters = 4000,
                MaxWritesPerWorkflowStep = 1,
                DebugLoggingEnabled = true
            },
            Inbox = new InboxSettings
            {
                ChatInboxPath = inbox,
                FeedbackInboxPath = feedback,
                DecisionInboxPath = decision
            },
            Outbox = new OutboxSettings { MessageOutboxPath = outbox },
            Monitor = new MonitorSettings { PollSeconds = 1 },
            GitOps = new GitOpsSettings { Enabled = false, RepoPath = root, PushBranchPrefix = "agent/" },
            Llm = new LlmSettings { Enabled = false }
        };
    }

    private static NeoCortexStore MakeNeoCortex(string root) =>
        new(Path.Combine(root, "NeoCortex"), Path.Combine(root, "legacy-state.json"));

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rustops-memory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateLegacyState(string root)
    {
        var legacyPath = Path.Combine(root, "agent-state.json");
        File.WriteAllText(legacyPath, """
        {
          "actionHistory": [
            {
              "serverName": "monthly",
              "actionType": "status_check",
              "success": true,
              "summary": "monthly is running"
            }
          ],
          "feedbackHistory": [
            {
              "adminId": "42",
              "serverName": "monthly",
              "note": "Prefer graceful restart countdowns"
            }
          ]
        }
        """);
        return legacyPath;
    }

    private static MemoryRecord NewRecord(
        string summary,
        float[] embedding,
        MemoryRecordType type = MemoryRecordType.Fact,
        MemoryScope scope = MemoryScope.Project,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? relatedIds = null)
    {
        var record = new MemoryRecord
        {
            Type = type,
            Scope = scope,
            Source = MemorySource.AgentAction,
            Summary = summary,
            Text = summary,
            Tags = tags?.ToList() ?? new List<string>(),
            RelatedEntityIds = relatedIds?.ToList() ?? new List<string>(),
            Embedding = embedding,
            EmbeddingModel = "test"
        };
        record.Normalize();
        return record;
    }

    private static Kernel BuildKernel(Func<string, string> responder)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new FakeChatCompletionService(responder));
        return builder.Build();
    }

    private sealed record RuntimeHarness(
        AgentRuntime Runtime,
        SemanticMemoryService SemanticMemory,
        AgentConfig Config,
        string InboxFile)
    {
        public ChatInboxItem Chat(string message) => SemanticMemoryTests.Chat(message);
    }

    private class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelName => "fake-embedding";
        public int? Dimensions => 4;

        public virtual Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Embed(text));

        public virtual Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

        private static float[] Embed(string text)
        {
            var vector = new float[4];
            foreach (var ch in (text ?? string.Empty).ToLowerInvariant())
            {
                vector[ch % 4] += ch;
            }

            return vector;
        }
    }

    private sealed class CountingEmbeddingProvider : FakeEmbeddingProvider
    {
        public int GenerateSingleCalls { get; private set; }
        public int GenerateBatchCalls { get; private set; }

        public override Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
        {
            GenerateSingleCalls++;
            return base.GenerateEmbeddingAsync(text, cancellationToken);
        }

        public override Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken)
        {
            GenerateBatchCalls++;
            return base.GenerateEmbeddingsAsync(texts, cancellationToken);
        }
    }

    private sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelName => "failing";
        public int? Dimensions => 4;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) => throw new HttpRequestException("embedding offline");
        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken) => throw new HttpRequestException("embedding offline");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            ResponseFactory = responseFactory;
        }

        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(ResponseFactory(request));
    }

    private sealed class FakeChatCompletionService : IChatCompletionService
    {
        private readonly Func<string, string> _responder;

        public FakeChatCompletionService(Func<string, string> responder)
        {
            _responder = responder;
        }

        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", chatHistory.Select(message => message.Content));
            IReadOnlyList<ChatMessageContent> result =
            [
                new ChatMessageContent(AuthorRole.Assistant, _responder(prompt), modelId: "fake-chat")
            ];
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, _responder(string.Join("\n", chatHistory.Select(message => message.Content))), modelId: "fake-chat");
            await Task.CompletedTask;
        }
    }

    private sealed class FailureAwareRconHandler : IToolHandler
    {
        public string Name => "rust.rcon.command";
        public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.RconCommand };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var usedRuntimeMemory = context.ExecutionMemoryContext?.RetrievalOrigin == "runtime" &&
                                    context.ExecutionMemoryContext.Results.Any(result => result.MemoryRecord.Type == MemoryRecordType.Failure) == true;
            var message = usedRuntimeMemory
                ? "Used previous failure memory and avoided the risky first step."
                : "Default risky step selected.";
            return Task.FromResult(new ToolExecutionResult(true, message, context.Route.Slots.ServerName));
        }
    }

    private sealed class StaticStatusHandler : IToolHandler
    {
        private readonly string _message;

        public StaticStatusHandler(string message)
        {
            _message = message;
        }

        public string Name => "rust.status.check";
        public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.StatusCheck };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, _message, context.Route.Slots.ServerName));
    }

    private sealed class StaticRconHandler : IToolHandler
    {
        private readonly string _message;

        public StaticRconHandler(string message)
        {
            _message = message;
        }

        public string Name => "rust.rcon.command";
        public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.RconCommand };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(true, _message, context.Route.Slots.ServerName));
    }

    private sealed class StaticServerControlHandler : IToolHandler
    {
        private readonly bool _success;
        private readonly string _message;
        private readonly string? _errorCode;
        private readonly object? _payload;

        public StaticServerControlHandler(bool success, string message, string? errorCode = null, object? payload = null)
        {
            _success = success;
            _message = message;
            _errorCode = errorCode;
            _payload = payload;
        }

        public string Name => "rust.server.control";
        public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.ServerControl };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(_success, _message, context.Route.Slots.ServerName, false, _errorCode, _payload));
    }

    private sealed record FailurePayload(string NextStepHint);

    private sealed class CommandMemoryService : ISemanticMemoryService
    {
        public string? LastSearchQuery { get; private set; }
        public string? LastDeletedId { get; private set; }
        public bool LastMigrationDryRun { get; private set; }

        public Task<WorkflowMemoryContext> RecallForPlanningAsync(string message, ConversationSelectionState state, IReadOnlyList<string> knownServers, CancellationToken cancellationToken) => Task.FromResult(WorkflowMemoryContext.Empty);
        public Task<WorkflowMemoryContext> RecallForExecutionAsync(ToolExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(WorkflowMemoryContext.Empty);
        public Task RecordActionOutcomeAsync(ToolExecutionContext context, ToolExecutionResult result, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordUserInstructionAsync(string? adminId, string? serverName, string instruction, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordReflectionAsync(string summary, string detail, IReadOnlyList<string> tags, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordServerFactAsync(string serverName, string summary, string detail, IReadOnlyList<string> tags, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<MemoryDebugStats> GetStatsAsync(CancellationToken cancellationToken) => Task.FromResult(new MemoryDebugStats { TotalRecords = 1, ActiveRecords = 1, ByType = new Dictionary<string, int> { ["Fix"] = 1 } });
        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
        {
            LastSearchQuery = query;
            IReadOnlyList<MemorySearchResult> results =
            [
                new MemorySearchResult
                {
                    MemoryRecord = NewRecord("Known fix", new[] { 1f, 0f, 0f, 0f }),
                    SimilarityScore = 0.9,
                    RecencyScore = 0.9,
                    ImportanceScore = 0.9,
                    FinalScore = 0.9,
                    MatchReason = "semantic_match"
                }
            ];
            return Task.FromResult(results);
        }

        public Task<MemoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken)
        {
            var record = NewRecord(id, new[] { 1f, 0f, 0f, 0f });
            record.Id = id;
            return Task.FromResult<MemoryRecord?>(record);
        }
        public Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            LastDeletedId = id;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryRecord>> ListRecentAsync(int maxResults, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MemoryRecord>>([NewRecord("Known fix", new[] { 1f, 0f, 0f, 0f })]);
        public Task<IReadOnlyList<IGrouping<string, MemoryRecord>>> ListRepeatedFailuresAsync(int minOccurrences, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IGrouping<string, MemoryRecord>>>(Array.Empty<IGrouping<string, MemoryRecord>>());
        public Task<MemoryRecord> AddManualMemoryAsync(ManualMemoryInput input, CancellationToken cancellationToken) => Task.FromResult(NewRecord(input.Summary, new[] { 1f, 0f, 0f, 0f }));
        public Task<int> RebuildEmbeddingsAsync(CancellationToken cancellationToken) => Task.FromResult(3);
        public Task<MemoryMigrationReport> MigrateLegacyMemoryAsync(bool dryRun, CancellationToken cancellationToken)
        {
            LastMigrationDryRun = dryRun;
            return Task.FromResult(new MemoryMigrationReport { DryRun = dryRun, RecordsImported = dryRun ? 0 : 2 });
        }

        public Task<int> PruneAsync(CancellationToken cancellationToken) => Task.FromResult(2);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };
}
