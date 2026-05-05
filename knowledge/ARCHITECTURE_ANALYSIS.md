# RusticalandOPS API Architecture Analysis & Refactoring Plan

## Executive Summary

The current `api/Program.cs` is a **6,863-line monolithic application** that acts as:
- API Gateway (69 endpoints)
- Orchestration Layer
- Monitoring Service  
- Remote Management System
- RCON Broker
- Configuration Manager
- Dashboard Backend
- Agent Coordination System

This document provides a **complete architectural decomposition** with refactoring strategy.

---

## Current State Analysis

### File Statistics
- **Total Lines**: 6,863
- **Endpoints**: 69 (GET/POST/PUT/DELETE)
- **Helper Functions**: 100+ static methods
- **Inline Types**: 50+ classes/records
- **Dashboard HTML**: Embedded 2,500+ line string

### Responsibilities Breakdown

#### 1. **Request Pipeline & Middleware** (~150 LOC)
- Global error handling
- Auth middleware
- Request/response logging via Sentry
- CORS setup
- Static file serving

#### 2. **Server Management** (~1,200 LOC)
- RCON connection management
- Server lifecycle (start/stop/restart/kill/update)
- Server configuration (read/write/validate)
- Plugin installation & validation
- Server status queries (players, bans, commands)
- Moderation endpoints (kick/ban/unban)

#### 3. **Remote Server Management** (~600 LOC)
- Remote server registration/deletion
- Remote agent health tracking
- Remote agent proxying
- Remote RCON configuration
- Persistent RCON sessions across network

#### 4. **Dashboard & Monitoring** (~1,000 LOC)
- Agent memory snapshot aggregation
- NeoCortex evolution tracking
- LLM interaction history
- Self-repair tracking
- Incident management
- Host service status (systemctl queries)
- Network interface monitoring
- Process snapshot reading
- Embedded HTML UI generation

#### 5. **Configuration Management** (~800 LOC)
- Server config loading/saving/validation
- LLM config management (env file + JSON)
- Command allowlist config
- Log rules config
- Agent settings JSON parsing
- Bot settings JSON parsing
- Environment file parsing/updating

#### 6. **Logging & Persistence** (~600 LOC)
- Log tailing (file-based)
- Log slicing (offset-based reading)
- Command output capture
- Event trace parsing
- Log rotation detection

#### 7. **Process Execution** (~400 LOC)
- RustMgr.sh invocation
- External process execution (systemctl, ps, etc.)
- Output capture
- Error handling
- Cancellation token support (partial)

#### 8. **Agent Integration** (~500 LOC)
- Agent state file loading
- NeoCortex store reading
- LLM interaction tracking
- Incident/action/feedback reading
- Capability gap tracking

#### 9. **LLM Management** (~400 LOC)
- LMStudio integration
- Ollama config
- Multi-provider support
- HTTP summary fetching

#### 10. **Utilities & Helpers** (~800 LOC)
- JSON parsing helpers
- String parsing helpers  
- DateTime parsing
- Network calculations
- Path normalization
- Oxide plugin validation
- Regex-based metadata extraction

#### 11. **Data Types** (~600 LOC)
- Request DTOs
- Response models
- Dashboard snapshot models
- Configuration models

#### 12. **Background State Management** (~200 LOC)
- Network summary cache
- Persistent RCON connections
- Transaction/request state

---

## Identified Technical Debt & Risks

### High-Priority Debt

1. **Monolithic Organization**
   - All code in single file makes testing/reuse impossible
   - No clear boundaries between concerns
   - Risk: Hard to add new features without regression

2. **Static Helper Proliferation**
   - 100+ static methods with no discoverability
   - No composition, only utility functions
   - Risk: Code duplication, hard to mock

3. **File System Coupling**
   - Direct `File.ReadAllText()` scattered throughout
   - `Directory.CreateDirectory()` in handlers
   - Log tailing uses naive line-by-line reads
   - Risk: Hard to test, no abstraction for storage

4. **RCON Connection Leaks**
   - `PersistentRconConnections` is a singleton with internal state
   - Manual `DisposeAsync()` registration
   - Risk: Improper disposal, resource exhaustion

5. **HTTP Client Management**
   - Multiple `HttpClient` instances likely created
   - No `IHttpClientFactory` usage
   - Risk: Socket exhaustion, performance degradation

6. **Cancellation Token Misuse**
   - Only partial support in async endpoints
   - Some `async` methods don't respect cancellation
   - Risk: Hanging requests, server DOS

7. **Error Handling Inconsistency**
   - Mixed try-catch/Sentry capture patterns
   - No consistent error response shape
   - Risk: Client confusion, incomplete logging

8. **Configuration Loading Fragmentation**
   - Env vars read at startup + during handler execution
   - Multiple config file formats (JSON, env, agent settings)
   - No configuration service abstraction
   - Risk: Race conditions on config changes, hard to reload

9. **Dashboard HTML Embedding**
   - 2,500+ line string literal in code
   - Makes versioning/updates hard
   - Risk: Maintainability nightmare

10. **Network Monitoring State**
    - `NetworkSummaryCacheState.Previous` is mutable singleton
    - No thread safety measures
    - Risk: Race conditions, data corruption

11. **Oxide Plugin Validation**
    - Heavy regex-based parsing
    - No AST understanding
    - Risk: False positives/negatives

12. **Log Parsing Heuristics**
    - Regex patterns scattered throughout
    - Timestamp parsing assumptions
    - Risk: Log rotation breaks offset tracking

### Medium-Priority Debt

1. **Tight Coupling to RustMgr**
   - Executor directly calls `rustmgr.sh`
   - No interface abstraction
   - Risk: Can't mock, can't swap implementations

2. **Remote Agent Proxying**
   - No clear request/response transformation
   - Credentials passed around loosely
   - Risk: Security issues, hard to audit

3. **Validation Logic Scattered**
   - Server config validation in multiple places
   - Port conflict detection duplicated
   - Risk: Validation inconsistency

4. **Response Building Complexity**
   - Anonymous objects returned from handlers
   - No typed responses
   - Risk: Breaking changes possible

5. **Dependency on Shared Types**
   - Uses types from shared assembly
   - Tight coupling to external code
   - Risk: Hard to version independently

### Low-Priority Debt

1. **Magic Numbers**
   - Default line counts (120, 100, etc.)
   - Hardcoded timeout values
   - Risk: Requires code changes to tune

2. **String Concatenation**
   - Path building without `Path.Combine()`
   - Risk: Cross-platform issues (partially mitigated by `NormalizePath`)

3. **Performance Optimizations Possible**
   - Dashboard snapshot generation is blocking
   - Could use caching for expensive reads
   - Risk: Dashboard latency under load

---

## Proposed Architecture

### Folder Structure

```
/api
├── Program.cs                          # Minimal bootstrap (100 LOC)
├── Middleware/
│   ├── IAuthenticationMiddleware.cs
│   ├── AuthenticationMiddleware.cs
│   ├── ErrorHandlingMiddleware.cs
│   └── RequestLoggingMiddleware.cs
├── Endpoints/
│   ├── MapEndpoints.cs                 # Extension method for registration
│   ├── Health/
│   │   ├── HealthEndpoints.cs
│   │   └── HealthCheckService.cs
│   ├── Servers/
│   │   ├── ServerEndpoints.cs
│   │   ├── ServerStatusEndpoints.cs
│   │   ├── ServerConfigEndpoints.cs
│   │   ├── ServerCommandEndpoints.cs
│   │   └── ServerPluginEndpoints.cs
│   ├── RemoteServers/
│   │   ├── RemoteServerEndpoints.cs
│   │   ├── RemoteAgentEndpoints.cs
│   │   └── RemoteRconEndpoints.cs
│   ├── Dashboard/
│   │   ├── DashboardEndpoints.cs
│   │   └── HostEndpoints.cs
│   ├── Agent/
│   │   ├── AgentEndpoints.cs
│   │   ├── AgentConfigEndpoints.cs
│   │   └── AgentIncidentEndpoints.cs
│   └── Tasks/
│       └── TaskEndpoints.cs
├── Services/
│   ├── Servers/
│   │   ├── IServerService.cs
│   │   ├── ServerService.cs
│   │   ├── IServerConfigService.cs
│   │   ├── ServerConfigService.cs
│   │   ├── IServerCommandService.cs
│   │   └── ServerCommandService.cs
│   ├── RemoteServers/
│   │   ├── IRemoteServerService.cs
│   │   ├── RemoteServerService.cs
│   │   ├── IRemoteAgentService.cs
│   │   └── RemoteAgentService.cs
│   ├── Dashboard/
│   │   ├── IDashboardAggregationService.cs
│   │   ├── DashboardAggregationService.cs
│   │   ├── IAgentSnapshotService.cs
│   │   └── AgentSnapshotService.cs
│   ├── Host/
│   │   ├── IHostMonitoringService.cs
│   │   ├── HostMonitoringService.cs
│   │   ├── INetworkMonitoringService.cs
│   │   └── NetworkMonitoringService.cs
│   ├── Configuration/
│   │   ├── IConfigurationProvider.cs
│   │   ├── ConfigurationProvider.cs
│   │   ├── ILlmConfigurationService.cs
│   │   └── LlmConfigurationService.cs
│   ├── Rcon/
│   │   ├── IRconConnectionManager.cs
│   │   ├── RconConnectionManager.cs
│   │   └── IPersistentRconSession.cs
│   ├── Process/
│   │   ├── IProcessExecutor.cs
│   │   ├── ProcessExecutor.cs
│   │   ├── IRustMgrExecutor.cs
│   │   └── RustMgrExecutor.cs
│   ├── Logging/
│   │   ├── ILogReadingService.cs
│   │   ├── LogReadingService.cs
│   │   ├── ILogParsingService.cs
│   │   └── LogParsingService.cs
│   ├── LLM/
│   │   ├── ILlmService.cs
│   │   └── LlmService.cs
│   └── Plugins/
│       ├── IPluginValidationService.cs
│       ├── PluginValidationService.cs
│       ├── IPluginInstallService.cs
│       └── PluginInstallService.cs
├── Infrastructure/
│   ├── Configuration/
│   │   ├── ApiConfiguration.cs
│   │   ├── ServerConfiguration.cs
│   │   └── EnvironmentVariables.cs
│   ├── Persistence/
│   │   ├── IFileStorageService.cs
│   │   ├── FileStorageService.cs
│   │   ├── IJsonPersistenceService.cs
│   │   └── JsonPersistenceService.cs
│   ├── ProcessExecution/
│   │   ├── ProcessStartInfoFactory.cs
│   │   └── ProcessExecutionOptions.cs
│   ├── Rcon/
│   │   ├── RconConnectionFactory.cs
│   │   ├── RconCredentialResolver.cs
│   │   └── RconConnectionInfo.cs
│   ├── Networking/
│   │   ├── IHttpClientProvider.cs
│   │   └── HttpClientProvider.cs
│   └── Logging/
│       ├── SentryIntegration.cs
│       └── RequestTracking.cs
├── Models/
│   ├── Requests/
│   │   ├── ServerCommandRequest.cs
│   │   ├── ServerCommandExecRequest.cs
│   │   ├── ModerationRequest.cs
│   │   ├── ProvisionServerRequest.cs
│   │   ├── ManagedTaskRequest.cs
│   │   ├── RemoteServerEntry.cs
│   │   ├── ServerConfig.cs
│   │   ├── PluginInstallRequest.cs
│   │   └── ...
│   ├── Responses/
│   │   ├── ServerStatusResponse.cs
│   │   ├── LogSliceResult.cs
│   │   ├── CommandOutputCapture.cs
│   │   ├── ValidationResult.cs
│   │   └── ...
│   ├── Dashboard/
│   │   ├── AgentDashboardSnapshot.cs
│   │   ├── DashboardIncident.cs
│   │   ├── DashboardAction.cs
│   │   └── ...
│   └── Shared/
│       ├── CommandExecutionResult.cs
│       ├── LogEntry.cs
│       ├── TraceEvent.cs
│       └── ...
├── Utilities/
│   ├── JsonUtilities.cs
│   ├── StringUtilities.cs
│   ├── PathUtilities.cs
│   ├── DateTimeUtilities.cs
│   ├── ValidationUtilities.cs
│   ├── NetworkUtilities.cs
│   └── PluginMetadataParser.cs
├── Extensions/
│   ├── ServiceCollectionExtensions.cs
│   ├── ApplicationBuilderExtensions.cs
│   ├── StringExtensions.cs
│   └── JsonExtensions.cs
├── BackgroundServices/
│   ├── RconConnectionMaintenanceService.cs
│   └── NetworkMonitoringBackgroundService.cs
└── UI/
    ├── DashboardUiGenerator.cs
    └── dashboard.html                 # Extracted to separate file
```

---

## Service Layer Breakdown

### 1. Server Management Services

**IServerService**
```csharp
public interface IServerService
{
    Task<ServerStatusResponse> GetStatusAsync(string server, CancellationToken ct);
    Task<List<ServerStatusResponse>> GetAllStatusesAsync(CancellationToken ct);
    Task<object> GetSummaryAsync(CancellationToken ct);
    Task StartServerAsync(string server, CancellationToken ct);
    Task StopServerAsync(string server, CancellationToken ct);
    Task RestartServerAsync(string server, CancellationToken ct);
    Task KillServerAsync(string server, CancellationToken ct);
    Task UpdateServerAsync(string server, CancellationToken ct);
    Task ApplyUModAsync(string server, CancellationToken ct);
    Task SyncConfigAsync(string server, CancellationToken ct);
    Task WipeServerAsync(string server, CancellationToken ct);
}
```

**IServerConfigService**
```csharp
public interface IServerConfigService
{
    Task<ServerConfig?> LoadConfigAsync(string server, CancellationToken ct);
    Task SaveConfigAsync(ServerConfig config, CancellationToken ct);
    ValidationResult ValidateConfig(ServerConfig config);
    List<string> FindConfigConflicts(ServerConfig config, string? ignoreServer = null);
    ServerConfig NormalizeConfig(string server, ServerConfig config);
}
```

**IServerCommandService**
```csharp
public interface IServerCommandService
{
    Task<object> ExecuteCommandAsync(string server, string command, CancellationToken ct);
    Task<CommandOutputCapture> ExecuteCommandWithCaptureAsync(
        string server, 
        ServerCommandExecRequest request, 
        CancellationToken ct);
}
```

### 2. Remote Server Services

**IRemoteServerService**
```csharp
public interface IRemoteServerService
{
    List<RemoteServerEntry> GetConfiguredServers();
    RemoteServerEntry? GetServerConfig(string name);
    void AddServer(RemoteServerEntry server);
    void UpdateServer(string name, RemoteServerEntry updated);
    void RemoveServer(string name);
    Task<bool> CheckHealthAsync(string name, CancellationToken ct);
    Task<object> GetAgentStatusAsync(string name, CancellationToken ct);
    Task<object> TestConnectionAsync(string name, CancellationToken ct);
}
```

**IRemoteAgentService**
```csharp
public interface IRemoteAgentService
{
    Task<IActionResult> RegisterAgentAsync(HttpContext ctx, CancellationToken ct);
    Task<AgentProxyResponse> ProxyAgentRequestAsync(
        string serverName,
        string path,
        HttpMethod method,
        HttpContent? body,
        CancellationToken ct);
}
```

### 3. RCON Management Services

**IRconConnectionManager**
```csharp
public interface IRconConnectionManager : IAsyncDisposable
{
    Task<IRconConnection> GetOrCreateConnectionAsync(
        string server,
        RconConnectionInfo info,
        CancellationToken ct);
    
    Task<string> ExecuteCommandAsync(
        string server,
        string command,
        CancellationToken ct);
    
    Task CloseConnectionAsync(string server, CancellationToken ct);
}
```

### 4. Configuration Services

**IConfigurationProvider**
```csharp
public interface IConfigurationProvider
{
    string? GetEnvironmentVariable(string key);
    string GetEnvironmentVariable(string key, string defaultValue);
    Dictionary<string, string> ReadEnvFile(string path);
    void WriteEnvFile(string path, Dictionary<string, string> values);
    T? DeserializeJsonFile<T>(string path);
    void SerializeJsonFile<T>(string path, T value);
}
```

**ILlmConfigurationService**
```csharp
public interface ILlmConfigurationService
{
    Task<AgentLlmConfigView> ReadConfigAsync(CancellationToken ct);
    Task WriteConfigAsync(AgentLlmConfigView config, CancellationToken ct);
    string? ValidateConfig(AgentLlmConfigView config);
}
```

### 5. Dashboard Services

**IDashboardAggregationService**
```csharp
public interface IDashboardAggregationService
{
    Task<AgentDashboardSnapshot> GetDashboardSummaryAsync(CancellationToken ct);
}
```

**IAgentSnapshotService**
```csharp
public interface IAgentSnapshotService
{
    Task<AgentDashboardSnapshot> LoadSnapshotAsync(CancellationToken ct);
    Task UpdateLlmInteractionsAsync(CancellationToken ct);
    Task UpdateIncidentsAsync(CancellationToken ct);
    Task UpdateCapabilityGapsAsync(CancellationToken ct);
}
```

### 6. Host Monitoring Services

**IHostMonitoringService**
```csharp
public interface IHostMonitoringService
{
    Task<List<DashboardServiceStatus>> GetManagedServicesAsync(CancellationToken ct);
    Task<DashboardServiceStatus> GetServiceStatusAsync(
        string unitName,
        string label,
        CancellationToken ct);
}
```

**INetworkMonitoringService**
```csharp
public interface INetworkMonitoringService
{
    object GetNetworkSummary();
    List<HostInterfaceCounter> GetInterfaceCounters();
}
```

### 7. Logging Services

**ILogReadingService**
```csharp
public interface ILogReadingService
{
    Task<LogSliceResult> ReadLogSliceAsync(
        string logPath,
        long? offset,
        int maxBytes,
        CancellationToken ct);
    
    Task<List<LogEntry>> TailLogsAsync(
        string logPath,
        int lines,
        string? since,
        int? offset,
        CancellationToken ct);
}
```

**ILogParsingService**
```csharp
public interface ILogParsingService
{
    List<LogEntry> ParseLogLines(IEnumerable<string> lines);
    TraceEvent? ParseTraceEvent(string line);
    DateTime? TryParseLogTimestamp(string line);
}
```

### 8. Process Execution Services

**IProcessExecutor**
```csharp
public interface IProcessExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(
        string fileName,
        string[] args,
        CancellationToken ct);
}
```

**IRustMgrExecutor**
```csharp
public interface IRustMgrExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(
        string[] args,
        CancellationToken ct);
}
```

---

## Dependency Injection Graph

```
IServiceCollection
├─ IConfigurationProvider
├─ IFileStorageService
├─ IProcessExecutor
├─ IRustMgrExecutor
│  └─ depends on: IProcessExecutor
├─ IHttpClientFactory (built-in)
│
├─ Server Management
│  ├─ IServerService
│  │  ├─ depends on: IRustMgrExecutor, IServerConfigService, ILogReadingService
│  ├─ IServerConfigService
│  │  └─ depends on: IConfigurationProvider, IFileStorageService
│  └─ IServerCommandService
│     ├─ depends on: IRustMgrExecutor, IProcessExecutor
│
├─ Remote Servers
│  ├─ IRemoteServerService
│  │  └─ depends on: IConfigurationProvider, IFileStorageService, IProcessExecutor
│  └─ IRemoteAgentService
│     ├─ depends on: IRemoteServerService, IHttpClientFactory
│
├─ RCON Management
│  └─ IRconConnectionManager
│     └─ depends on: IRconCredentialResolver, IServerConfigService
│
├─ Dashboard
│  ├─ IDashboardAggregationService
│  │  └─ depends on: IAgentSnapshotService, IHostMonitoringService
│  └─ IAgentSnapshotService
│     ├─ depends on: IConfigurationProvider, IFileStorageService, IJsonPersistenceService
│
├─ Host Monitoring
│  ├─ IHostMonitoringService
│  │  └─ depends on: IProcessExecutor
│  └─ INetworkMonitoringService
│     └─ depends on: IProcessExecutor
│
├─ Logging
│  ├─ ILogReadingService
│  │  └─ depends on: IFileStorageService, ILogParsingService
│  └─ ILogParsingService
│
├─ LLM
│  └─ ILlmService
│     ├─ depends on: ILlmConfigurationService, IHttpClientFactory
│
└─ Plugins
   └─ IPluginValidationService
      └─ depends on: IFileStorageService
```

---

## Refactoring Strategy

### Phase 1: Prepare Infrastructure (Week 1)
1. Create folder structure
2. Extract all Models/DTOs to `/Models`
3. Create Utilities classes
4. Add `IConfigurationProvider` interface + implementation
5. Add `IFileStorageService` interface + implementation
6. Add `IProcessExecutor` interface + implementation
7. Register in DI container
8. Update Program.cs to use new services
9. **No endpoint changes yet** — keep everything working

### Phase 2: Core Services (Weeks 2-3)
1. Extract RCON management → `IRconConnectionManager`
2. Extract Server config → `IServerConfigService`
3. Extract Process execution → `IRustMgrExecutor`
4. Extract Log reading → `ILogReadingService`
5. Extract Configuration → `ILlmConfigurationService`
6. Update Program.cs endpoints to use services
7. Run tests after each service

### Phase 3: Business Services (Weeks 4-5)
1. Extract Server management → `IServerService`
2. Extract Remote servers → `IRemoteServerService`, `IRemoteAgentService`
3. Extract Dashboard → `IDashboardAggregationService`, `IAgentSnapshotService`
4. Extract Host monitoring → `IHostMonitoringService`, `INetworkMonitoringService`
5. Extract Plugins → `IPluginValidationService`
6. Update endpoints to use services

### Phase 4: Endpoint Refactoring (Weeks 6-7)
1. Create endpoint handler classes (e.g., `ServerEndpoints`)
2. Move route registrations to `MapEndpoints()` extension
3. Extract handlers to endpoint classes
4. Create `RequestLoggingMiddleware`
5. Create `ErrorHandlingMiddleware`
6. Extract `AuthenticationMiddleware`

### Phase 5: Cleanup & Optimization (Week 8)
1. Extract HTML to separate `dashboard.html` file
2. Remove unused code
3. Add XML doc comments
4. Performance profiling
5. Load testing
6. Security review

### Phase 6: Testing & Stabilization (Week 9)
1. Write integration tests for each service
2. Write unit tests for utilities
3. Test migration path
4. Rollout plan

---

## Example Refactored Endpoint Module

### Before (Current)
```csharp
app.MapGet("/servers/{server}/status", async (string server) =>
{
    if (!await IsValidServerAsync(server))
        return Results.BadRequest(new { error = "Invalid server." });
    
    var result = await ExecRustMgrAsync("query", server, "status");
    if (!result.Ok)
    {
        CaptureHandledApiException(result.StdErr, "Failed to query server status", server: server);
        return Results.Problem("Failed to query server status.");
    }
    
    var payload = TryExtractJson(result.StdOut);
    var response = ParseStatus(server, payload);
    return Results.Ok(response);
});
```

### After (Refactored)
```csharp
// ServerEndpoints.cs
public class ServerEndpoints
{
    private readonly IServerService _serverService;
    private readonly ILogger<ServerEndpoints> _logger;
    
    public ServerEndpoints(IServerService serverService, ILogger<ServerEndpoints> logger)
    {
        _serverService = serverService;
        _logger = logger;
    }
    
    public async Task<IResult> GetStatus(string server, CancellationToken ct)
    {
        try
        {
            var status = await _serverService.GetStatusAsync(server, ct);
            return Results.Ok(status);
        }
        catch (ServerNotFoundException)
        {
            return Results.BadRequest(new { error = "Invalid server." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get server status for {Server}", server);
            return Results.Problem("Failed to query server status.");
        }
    }
}

// Extension method
public static void MapServerEndpoints(this WebApplication app)
{
    var group = app.MapGroup("/servers/{server}")
        .WithName("Servers")
        .WithOpenApi();
    
    var endpoints = new ServerEndpoints(
        app.Services.GetRequiredService<IServerService>(),
        app.Services.GetRequiredService<ILogger<ServerEndpoints>>());
    
    group.MapGet("/status", endpoints.GetStatus)
        .WithName("GetServerStatus")
        .WithDescription("Get server status and player info");
}
```

---

## Example Extracted Service

### IServerService Implementation
```csharp
public class ServerService : IServerService
{
    private readonly IServerConfigService _configService;
    private readonly IRustMgrExecutor _executor;
    private readonly ILogReadingService _logService;
    private readonly IRconConnectionManager _rconManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerService> _logger;
    
    public async Task<ServerStatusResponse> GetStatusAsync(
        string server,
        CancellationToken ct = default)
    {
        if (!await ValidateServerExistsAsync(server, ct))
            throw new ServerNotFoundException(server);
        
        var result = await _executor.ExecuteAsync(
            new[] { "query", server, "status" },
            ct);
        
        if (!result.Ok)
        {
            _logger.LogError(
                "Failed to query status for {Server}: {Error}",
                server,
                result.StdErr);
            throw new CommandExecutionException("Failed to query server status", result);
        }
        
        var json = JsonUtilities.TryExtractJson(result.StdOut);
        return JsonUtilities.ParseStatus(server, json);
    }
    
    public async Task StartServerAsync(string server, CancellationToken ct)
    {
        var config = await _configService.LoadConfigAsync(server, ct)
            ?? throw new ServerConfigMissingException(server);
        
        var result = await _executor.ExecuteAsync(
            new[] { "start", server },
            ct);
        
        if (!result.Ok)
            throw new CommandExecutionException("Failed to start server", result);
    }
    
    // Other methods...
    
    private async Task<bool> ValidateServerExistsAsync(string server, CancellationToken ct)
    {
        try
        {
            var config = await _configService.LoadConfigAsync(server, ct);
            return config is not null;
        }
        catch
        {
            return false;
        }
    }
}
```

---

## Example Middleware

### ErrorHandlingMiddleware
```csharp
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    
    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }
    
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ServerNotFoundException ex)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new { error = ex.Message },
                cancellationToken: context.RequestAborted);
        }
        catch (ValidationException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = ex.Message, details = ex.Details },
                cancellationToken: context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in HTTP pipeline");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = "Internal server error" },
                cancellationToken: context.RequestAborted);
        }
    }
}
```

---

## Exception Hierarchy

```csharp
public abstract class ApiException : Exception
{
    protected ApiException(string message) : base(message) { }
}

public class ServerNotFoundException : ApiException
{
    public string ServerName { get; }
    public ServerNotFoundException(string server)
        : base($"Server '{server}' not found.")
    {
        ServerName = server;
    }
}

public class ServerConfigMissingException : ApiException
{
    public ServerConfigMissingException(string server)
        : base($"Configuration for server '{server}' not found.")
    { }
}

public class CommandExecutionException : ApiException
{
    public CommandExecutionResult Result { get; }
    public CommandExecutionException(string message, CommandExecutionResult result)
        : base(message)
    {
        Result = result;
    }
}

public class ValidationException : ApiException
{
    public List<string> Details { get; }
    public ValidationException(string message, List<string> details)
        : base(message)
    {
        Details = details;
    }
}

public class RconConnectionException : ApiException
{
    public RconConnectionException(string message) : base(message) { }
}
```

---

## Technical Decisions & Rationale

### 1. Service-Based Architecture
- **Decision**: Use interfaces for all major services
- **Rationale**: Enables testing, composition, decoupling
- **Trade-off**: Slight overhead, but massive maintainability gain

### 2. Endpoint Grouping
- **Decision**: Group related endpoints into handler classes
- **Rationale**: Better discoverability, easier navigation
- **Trade-off**: More files, but clear structure

### 3. Configuration Provider Abstraction
- **Decision**: Single `IConfigurationProvider` for all config reads/writes
- **Rationale**: Testable, centralized, audit-friendly
- **Trade-off**: Tiny indirection overhead

### 4. File Storage Abstraction
- **Decision**: `IFileStorageService` for all filesystem access
- **Rationale**: Enables testing, future cloud storage, audit logging
- **Trade-off**: One more abstraction layer

### 5. Process Execution Abstraction
- **Decision**: `IProcessExecutor` + `IRustMgrExecutor` separation
- **Rationale**: Composability, flexibility, testability
- **Trade-off**: Two layers instead of one

### 6. Middleware Chain Over Global Handler
- **Decision**: Use middleware for cross-cutting concerns
- **Rationale**: Standard ASP.NET pattern, composable
- **Trade-off**: Slightly different structure than current inline handlers

### 7. Background Services
- **Decision**: Use `IHostedService` for long-running work
- **Rationale**: Proper lifecycle management, graceful shutdown
- **Trade-off**: More code, but more reliable

### 8. Typed Exceptions
- **Decision**: Create domain-specific exception hierarchy
- **Rationale**: Better error handling, clearer intent
- **Trade-off**: More types to define

---

## Migration Path

### Week 1-2: Silent Migration
1. Deploy new DI-based services alongside old code
2. New services run in parallel without changing endpoints
3. Log metrics for both old and new implementations
4. Validate behavior matches

### Week 3-4: Gradual Endpoint Migration
1. Migrate 25% of endpoints (least used first)
2. Monitor error rates carefully
3. A/B test if possible
4. Keep old handlers as fallback

### Week 5-6: Main Migration
1. Migrate remaining 75% of endpoints
2. Disable old code paths
3. Final monitoring and tuning

### Week 7: Cleanup
1. Remove old code
2. Remove temporary fallback logic
3. Run final performance tests

---

## Performance Considerations

### Current Bottlenecks
1. **Dashboard Summary**: Synchronous reads of multiple JSON files
   - **Solution**: Async reading + caching with TTL
2. **Log Tailing**: Reads entire file then counts lines
   - **Solution**: Seek to end, read backwards efficiently
3. **Network Monitoring**: Full scan of `/sys/class/net` each call
   - **Solution**: Cache with delta calculation
4. **HTTP Clients**: Multiple instances possible
   - **Solution**: `IHttpClientFactory` from DI

### Optimizations (Post-Refactor)
1. Add `IMemoryCache` for dashboard snapshots (5-minute TTL)
2. Implement lazy-load pattern for agent snapshots
3. Add request deduplication middleware
4. Profile hot paths and optimize

---

## Testing Strategy

### Unit Tests (Per Service)
```
/tests/
├── Services/
│   ├── ServerServiceTests.cs
│   ├── ServerConfigServiceTests.cs
│   ├── RconConnectionManagerTests.cs
│   └── ...
├── Infrastructure/
│   ├── ProcessExecutorTests.cs
│   ├── FileStorageServiceTests.cs
│   └── ...
└── Utilities/
    ├── JsonUtilitiesTests.cs
    ├── ValidationUtilitiesTests.cs
    └── ...
```

### Integration Tests
```
/tests/
├── Endpoints/
│   ├── ServerEndpointsTests.cs
│   ├── RemoteServerEndpointsTests.cs
│   └── ...
└── Fixtures/
    ├── TestServerFixture.cs
    ├── TestConfigFixture.cs
    └── ...
```

### Test Doubles
- `IFileStorageService`: Mock returning test data
- `IProcessExecutor`: Mock returning canned outputs
- `IRconConnectionManager`: Mock connection
- `IConfigurationProvider`: Test env config

---

## Security Review Points

1. **Auth Middleware**: Ensure API key validation is comprehensive
2. **RCON Credentials**: Never log passwords, use secure credential store
3. **Remote Agent Proxy**: Validate forwarded headers carefully
4. **File Access**: Ensure path traversal is prevented
5. **Process Execution**: Sanitize arguments to avoid injection
6. **Deserialization**: Use JSON options that reject unknown properties

---

## Rollback Plan

1. **Version Control**: Tag current version before refactor
2. **Feature Flags**: Wrap new implementations with feature flag
3. **Parallel Running**: Old + new code can run simultaneously
4. **Automated Rollback**: Script to revert to previous tag
5. **Monitoring**: Alert on error rate increase > 5%

---

## Success Metrics

### Code Quality
- ✓ Reduce Program.cs to < 150 lines
- ✓ 30+ interfaces for services
- ✓ Zero static helper accumulation
- ✓ 80%+ test coverage for services

### Performance
- ✓ Dashboard load time maintained or improved
- ✓ No memory leaks in RCON connections
- ✓ HTTP requests complete within same latency
- ✓ Startup time unchanged

### Maintainability
- ✓ New features can be added without modifying Program.cs
- ✓ Services are independently testable
- ✓ Clear dependency graph
- ✓ Documentation updated

---

## Estimated Effort

| Phase | Duration | Risk |
|-------|----------|------|
| Phase 1: Infrastructure | 5 days | Low |
| Phase 2: Core Services | 8 days | Low-Medium |
| Phase 3: Business Services | 8 days | Medium |
| Phase 4: Endpoints | 8 days | Medium-High |
| Phase 5: Cleanup | 5 days | Low |
| Phase 6: Testing | 5 days | Low |
| **Total** | **~6 weeks** | **Medium** |

---

## Next Steps

1. **Review this analysis** with team
2. **Prioritize phases** based on team bandwidth
3. **Create branch** for refactoring work
4. **Begin Phase 1** infrastructure setup
5. **Weekly sync** to review progress
6. **Merge incrementally** as phases complete

---

## Appendix: Code Examples

### Utilities Example
```csharp
// Utilities/JsonUtilities.cs
public static class JsonUtilities
{
    public static string? TryExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.GetRawText();
            }
            catch { }
        }
        
        var match = Regex.Match(trimmed, @"\{.*\}", RegexOptions.Singleline);
        return match.Success ? match.Value : null;
    }
    
    public static ServerStatusResponse ParseStatus(
        string server,
        string? payload)
    {
        if (payload is null)
            return new ServerStatusResponse { Name = server, State = "unknown" };
        
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            
            return new ServerStatusResponse
            {
                Name = server,
                State = ReadStringAny(root, "state", "status") ?? "unknown",
                Online = ReadBoolAny(root, "online") ?? false,
                Pid = ReadIntAny(root, "pid"),
                Raw = payload
            };
        }
        catch
        {
            return new ServerStatusResponse { Name = server, State = "unknown" };
        }
    }
}
```

### Extension Method Example
```csharp
// Extensions/ServiceCollectionExtensions.cs
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRustOpsServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        // Infrastructure
        services.AddSingleton<IConfigurationProvider, ConfigurationProvider>();
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddSingleton<IProcessExecutor, ProcessExecutor>();
        services.AddSingleton<IRustMgrExecutor, RustMgrExecutor>();
        
        // Core Services
        services.AddSingleton<IRconConnectionManager, RconConnectionManager>();
        services.AddScoped<IServerConfigService, ServerConfigService>();
        services.AddScoped<ILogReadingService, LogReadingService>();
        services.AddScoped<ILlmConfigurationService, LlmConfigurationService>();
        
        // Business Services
        services.AddScoped<IServerService, ServerService>();
        services.AddScoped<IRemoteServerService, RemoteServerService>();
        services.AddScoped<IRemoteAgentService, RemoteAgentService>();
        services.AddScoped<IDashboardAggregationService, DashboardAggregationService>();
        services.AddScoped<IHostMonitoringService, HostMonitoringService>();
        services.AddScoped<INetworkMonitoringService, NetworkMonitoringService>();
        services.AddScoped<IPluginValidationService, PluginValidationService>();
        
        // HTTP
        services.AddHttpClient("RemoteAgent")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        
        return services;
    }
}
```

---

**Document Version**: 1.0  
**Date**: 2026-05-04  
**Status**: Ready for Review
