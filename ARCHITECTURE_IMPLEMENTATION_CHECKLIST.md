# RusticalandOPS Architecture Refactoring - Implementation Checklist

## Phase 1: Infrastructure Setup (Week 1)

### Folder Structure Creation
- [ ] Create `/api/Middleware` directory
- [ ] Create `/api/Endpoints` directory with subdirectories
- [ ] Create `/api/Services` directory with subdirectories
- [ ] Create `/api/Infrastructure` directory with subdirectories
- [ ] Create `/api/Models` directory with subdirectories
- [ ] Create `/api/Utilities` directory
- [ ] Create `/api/Extensions` directory
- [ ] Create `/api/BackgroundServices` directory
- [ ] Create `/api/UI` directory
- [ ] Create `/api/Exceptions` directory

### Models & DTOs Extraction
- [ ] Create `/api/Models/Requests/` - Move all request DTOs
  - [ ] ServerCommandRequest.cs
  - [ ] ServerCommandExecRequest.cs
  - [ ] ModerationRequest.cs
  - [ ] ProvisionServerRequest.cs
  - [ ] ManagedTaskRequest.cs
  - [ ] PluginInstallRequest.cs
  - [ ] RemoteServerEntry.cs
  - [ ] ServerConfig.cs
  - [ ] TruncationRequest.cs
  - [ ] AgentLlmConfigUpdate.cs
  - [ ] AgentCommandConfigUpdate.cs

- [ ] Create `/api/Models/Responses/` - Move all response models
  - [ ] ServerStatusResponse.cs
  - [ ] LogSliceResult.cs
  - [ ] CommandOutputCapture.cs
  - [ ] ValidationResult.cs
  - [ ] TraceEvent.cs
  - [ ] LogEntry.cs
  - [ ] ManagedTaskInfo.cs

- [ ] Create `/api/Models/Dashboard/` - Move dashboard models
  - [ ] AgentDashboardSnapshot.cs
  - [ ] DashboardIncident.cs
  - [ ] DashboardAction.cs
  - [ ] DashboardPendingAction.cs
  - [ ] DashboardFeedback.cs
  - [ ] DashboardRuntimeStatus.cs
  - [ ] DashboardStateFileStatus.cs
  - [ ] DashboardLlmInteraction.cs
  - [ ] DashboardCapabilityGap.cs
  - [ ] DashboardSelfRepairRun.cs
  - [ ] DashboardServiceStatus.cs
  - [ ] AgentRuntimePaths.cs
  - [ ] AgentSettingsFileView.cs
  - [ ] BotSettingsFileView.cs
  - [ ] AgentLlmConfigView.cs
  - [ ] AgentCommandConfigView.cs
  - [ ] MailboxFileSummary.cs
  - [ ] HostInterfaceCounter.cs
  - [ ] ProcessSnapshot.cs
  - [ ] PlayerSnapshot.cs
  - [ ] ServerInfoSnapshot.cs
  - [ ] LlmSummaryView.cs
  - [ ] LlmModelView.cs
  - [ ] LlmLoadedModelView.cs

- [ ] Create `/api/Models/Shared/`
  - [ ] CommandExecutionResult.cs
  - [ ] RemoteServerEntry.cs
  - [ ] RconConnectionInfo.cs
  - [ ] PluginCommandReferenceView.cs
  - [ ] PluginMetadata.cs

### Utilities Extraction
- [ ] Create `/api/Utilities/JsonUtilities.cs`
  - [ ] Extract all JSON parsing helpers
  - [ ] Extract JSON reading methods (ReadString, ReadInt, etc.)
  - [ ] Extract TryExtractJson()
  - [ ] Extract ParseStatus()
  - [ ] Extract PrettyJson()

- [ ] Create `/api/Utilities/StringUtilities.cs`
  - [ ] Extract Escape()
  - [ ] Extract TailLines()
  - [ ] Extract string parsing helpers

- [ ] Create `/api/Utilities/PathUtilities.cs`
  - [ ] Extract GetServerLogPath()
  - [ ] Extract GetConfigPath()
  - [ ] Extract Path normalization

- [ ] Create `/api/Utilities/DateTimeUtilities.cs`
  - [ ] Extract TryParseLogTimestamp()
  - [ ] Extract DateTime parsing methods

- [ ] Create `/api/Utilities/ValidationUtilities.cs`
  - [ ] Extract ValidateConfig()
  - [ ] Extract FindConfigConflicts()
  - [ ] Extract port conflict detection
  - [ ] Extract ValidateJsonFile()

- [ ] Create `/api/Utilities/NetworkUtilities.cs`
  - [ ] Extract BuildQueryString()
  - [ ] Extract network calculation helpers
  - [ ] Extract SafeReadText(), SafeReadInt(), SafeReadLong()

- [ ] Create `/api/Utilities/PluginMetadataParser.cs`
  - [ ] Extract ValidateOxidePluginFile()
  - [ ] Extract ExtractPluginCommands()
  - [ ] Extract ExtractPluginPermissions()
  - [ ] Extract ExtractPluginHooks()
  - [ ] Extract ExtractPluginConfigKeys()
  - [ ] Extract FindPluginHandlerAfter()
  - [ ] Extract ToPluginSlug()
  - [ ] Extract ParsePluginMetadata()

### Exceptions
- [ ] Create `/api/Exceptions/ApiException.cs` - Base exception
- [ ] Create `/api/Exceptions/ServerNotFoundException.cs`
- [ ] Create `/api/Exceptions/ServerConfigMissingException.cs`
- [ ] Create `/api/Exceptions/CommandExecutionException.cs`
- [ ] Create `/api/Exceptions/ValidationException.cs`
- [ ] Create `/api/Exceptions/RconConnectionException.cs`
- [ ] Create `/api/Exceptions/ConfigurationException.cs`
- [ ] Create `/api/Exceptions/RemoteAgentException.cs`

### Configuration
- [ ] Create `/api/Infrastructure/Configuration/EnvironmentVariables.cs`
  - [ ] Define all environment variable names as constants
  - [ ] Create ApiConfiguration class
  - [ ] Create validation for required vars

### DI Setup
- [ ] Create `/api/Extensions/ServiceCollectionExtensions.cs`
  - [ ] AddRustOpsServices() method
  - [ ] Register all services
  - [ ] Register HTTP clients
  - [ ] Configure JSON options

- [ ] Update `Program.cs` to call service registration
  - [ ] Remove inline configuration
  - [ ] Call `builder.Services.AddRustOpsServices()`
  - [ ] Call `app.UseRustOpsMiddleware()`

### Verification
- [ ] Code compiles without errors
- [ ] All tests pass with old implementation
- [ ] No functional changes visible to API consumers
- [ ] Dependency injection works correctly

---

## Phase 2: Infrastructure Services (Week 2)

### Configuration Provider
- [ ] Create `IConfigurationProvider.cs` interface
  - [ ] GetEnvironmentVariable(key)
  - [ ] GetEnvironmentVariable(key, default)
  - [ ] ReadEnvFile(path)
  - [ ] WriteEnvFile(path, values)
  - [ ] DeserializeJsonFile<T>(path)
  - [ ] SerializeJsonFile<T>(path, value)

- [ ] Create `ConfigurationProvider.cs` implementation
  - [ ] Use IConfiguration from DI
  - [ ] Implement file reading
  - [ ] Implement JSON serialization
  - [ ] Implement env file parsing
  - [ ] Add error handling

### File Storage Service
- [ ] Create `IFileStorageService.cs` interface
  - [ ] GetFileExistsAsync(path)
  - [ ] ReadFileAsync(path)
  - [ ] WriteFileAsync(path, content)
  - [ ] DeleteFileAsync(path)
  - [ ] GetDirectoryFilesAsync(directory, pattern)
  - [ ] CreateDirectoryAsync(directory)
  - [ ] GetFileSizeAsync(path)
  - [ ] GetLastWriteTimeAsync(path)

- [ ] Create `FileStorageService.cs` implementation
  - [ ] Implement file operations
  - [ ] Add path validation (prevent traversal)
  - [ ] Add error handling
  - [ ] Add logging

### Process Executor
- [ ] Create `IProcessExecutor.cs` interface
  - [ ] ExecuteAsync(fileName, args, cancellationToken)
  - [ ] ExecuteAsync(fileName, args)

- [ ] Create `ProcessExecutor.cs` implementation
  - [ ] Use ProcessStartInfo
  - [ ] Capture stdout/stderr
  - [ ] Handle exit codes
  - [ ] Respect cancellation tokens
  - [ ] Add timeout handling

- [ ] Create `IRustMgrExecutor.cs` interface
  - [ ] ExecuteAsync(args, cancellationToken)

- [ ] Create `RustMgrExecutor.cs` implementation (wraps ProcessExecutor)
  - [ ] Resolve rustmgr.sh path
  - [ ] Build command arguments
  - [ ] Parse results

### JSON Persistence Service
- [ ] Create `IJsonPersistenceService.cs` interface
  - [ ] LoadAsync<T>(path)
  - [ ] SaveAsync<T>(path, data)

- [ ] Create `JsonPersistenceService.cs` implementation
  - [ ] Use JsonSerializer with standard options
  - [ ] Implement atomic writes (temp file pattern)
  - [ ] Add error handling

### Tests for Infrastructure Services
- [ ] Create `tests/Infrastructure/ConfigurationProviderTests.cs`
- [ ] Create `tests/Infrastructure/FileStorageServiceTests.cs`
- [ ] Create `tests/Infrastructure/ProcessExecutorTests.cs`

### Verification
- [ ] All infrastructure services compile
- [ ] Unit tests pass
- [ ] No behavior changes to existing endpoints
- [ ] Old code still works (dual implementations during transition)

---

## Phase 3: Core Services (Week 3)

### RCON Connection Manager
- [ ] Create `IRconConnectionManager.cs` interface
- [ ] Create `RconConnectionManager.cs` implementation
  - [ ] Extract PersistentRconConnections logic
  - [ ] Implement connection pooling
  - [ ] Implement proper disposal
  - [ ] Register as singleton with graceful shutdown

- [ ] Create `RconCredentialResolver.cs`
  - [ ] Extract credential resolution logic
  - [ ] Implement fallback chain

### Server Configuration Service
- [ ] Create `IServerConfigService.cs` interface
- [ ] Create `ServerConfigService.cs` implementation
  - [ ] LoadConfigAsync(server)
  - [ ] SaveConfigAsync(config)
  - [ ] ValidateConfig(config)
  - [ ] FindConfigConflicts(config)
  - [ ] NormalizeConfig(server, config)

### Log Reading Service
- [ ] Create `ILogReadingService.cs` interface
- [ ] Create `LogReadingService.cs` implementation
  - [ ] ReadLogSliceAsync(path, offset, maxBytes)
  - [ ] TailLogsAsync(path, lines, since, offset)
  - [ ] Extract LogSliceResult logic
  - [ ] Implement efficient log reading

### Log Parsing Service
- [ ] Create `ILogParsingService.cs` interface
- [ ] Create `LogParsingService.cs` implementation
  - [ ] ParseLogLines(lines)
  - [ ] ParseTraceEvent(line)
  - [ ] TryParseLogTimestamp(line)
  - [ ] DetectLogLevel(line)

### LLM Configuration Service
- [ ] Create `ILlmConfigurationService.cs` interface
- [ ] Create `LlmConfigurationService.cs` implementation
  - [ ] ReadConfigAsync()
  - [ ] WriteConfigAsync(config)
  - [ ] ValidateConfig(config)
  - [ ] Extract LlmConfigView logic

### Tests
- [ ] Create comprehensive tests for each service
- [ ] Mock dependencies properly
- [ ] Test error scenarios

### Verification
- [ ] All core services compile
- [ ] Existing endpoints still work
- [ ] Can inject services into handlers
- [ ] No memory leaks in connection management

---

## Phase 4: Business Services (Week 4-5)

### Server Management Service
- [ ] Create `IServerService.cs` interface
- [ ] Create `ServerService.cs` implementation
  - [ ] GetStatusAsync()
  - [ ] GetAllStatusesAsync()
  - [ ] StartServerAsync()
  - [ ] StopServerAsync()
  - [ ] RestartServerAsync()
  - [ ] KillServerAsync()
  - [ ] UpdateServerAsync()
  - [ ] ApplyUModAsync()
  - [ ] SyncConfigAsync()
  - [ ] WipeServerAsync()

### Server Command Service
- [ ] Create `IServerCommandService.cs` interface
- [ ] Create `ServerCommandService.cs` implementation
  - [ ] ExecuteCommandAsync(server, command)
  - [ ] ExecuteCommandWithCaptureAsync(server, request)

### Plugin Validation Service
- [ ] Create `IPluginValidationService.cs` interface
- [ ] Create `PluginValidationService.cs` implementation
  - [ ] ValidateOxidePluginAsync(path)
  - [ ] ExtractPluginMetadataAsync(path)
  - [ ] CheckPluginUpdatesAsync(server)
  - [ ] InstallPluginAsync(server, pluginName, uri)

### Remote Server Service
- [ ] Create `IRemoteServerService.cs` interface
- [ ] Create `RemoteServerService.cs` implementation
  - [ ] GetConfiguredServers()
  - [ ] GetServerConfig(name)
  - [ ] AddServer(entry)
  - [ ] UpdateServer(name, entry)
  - [ ] RemoveServer(name)
  - [ ] CheckHealthAsync(name)
  - [ ] GetAgentStatusAsync(name)
  - [ ] TestConnectionAsync(name)

### Remote Agent Service
- [ ] Create `IRemoteAgentService.cs` interface
- [ ] Create `RemoteAgentService.cs` implementation
  - [ ] RegisterAgentAsync(ctx)
  - [ ] ProxyAgentRequestAsync(serverName, path, method, body)

### Dashboard Services
- [ ] Create `IAgentSnapshotService.cs` interface
- [ ] Create `AgentSnapshotService.cs` implementation
  - [ ] LoadSnapshotAsync()
  - [ ] UpdateLlmInteractionsAsync()
  - [ ] UpdateIncidentsAsync()
  - [ ] UpdateCapabilityGapsAsync()
  - [ ] UpdateSelfRepairHistoryAsync()

- [ ] Create `IDashboardAggregationService.cs` interface
- [ ] Create `DashboardAggregationService.cs` implementation
  - [ ] GetDashboardSummaryAsync()
  - [ ] Aggregate all data sources

### Host Monitoring Services
- [ ] Create `IHostMonitoringService.cs` interface
- [ ] Create `HostMonitoringService.cs` implementation
  - [ ] GetManagedServicesAsync()
  - [ ] GetServiceStatusAsync(unitName, label)
  - [ ] GetProcessSnapshotAsync(pid)

- [ ] Create `INetworkMonitoringService.cs` interface
- [ ] Create `NetworkMonitoringService.cs` implementation
  - [ ] GetNetworkSummary()
  - [ ] GetInterfaceCounters()
  - [ ] Calculate network rates and statistics

### LLM Service
- [ ] Create `ILlmService.cs` interface
- [ ] Create `LlmService.cs` implementation
  - [ ] GetLmStudioSummaryAsync(config)
  - [ ] GetLoadedModelsAsync(config)
  - [ ] TryGetRemoteJsonAsync(http, path)

### Tests
- [ ] Create integration tests for business services
- [ ] Mock infrastructure services
- [ ] Test error scenarios
- [ ] Test data transformations

### Verification
- [ ] All business services compile
- [ ] Services integrate properly
- [ ] Dependency injection graph works
- [ ] Dashboard data still generates correctly

---

## Phase 5: Endpoint Refactoring (Week 6-7)

### Middleware
- [ ] Create `ErrorHandlingMiddleware.cs`
  - [ ] Catch ApiException types
  - [ ] Return appropriate status codes
  - [ ] Log exceptions

- [ ] Create `RequestLoggingMiddleware.cs`
  - [ ] Add breadcrumbs to Sentry
  - [ ] Log request details

- [ ] Create `AuthenticationMiddleware.cs`
  - [ ] Extract API key validation
  - [ ] Validate on each request

- [ ] Create `IAuthenticationMiddleware.cs` interface

- [ ] Create `ApplicationBuilderExtensions.cs`
  - [ ] UseRustOpsMiddleware() method
  - [ ] Register middleware in correct order

### Endpoint Classes

#### Health Endpoints
- [ ] Create `Endpoints/Health/HealthEndpoints.cs`
  - [ ] MapHealth()
  - [ ] GetHealth()

#### Server Endpoints
- [ ] Create `Endpoints/Servers/ServerEndpoints.cs`
  - [ ] MapServers()
  - [ ] GetAllServers()
  - [ ] GetServerStatus()
  - [ ] GetServerSummary()
  - [ ] StartServer()
  - [ ] StopServer()
  - [ ] RestartServer()
  - [ ] KillServer()
  - [ ] UpdateServer()
  - [ ] ApplyUMod()
  - [ ] SyncConfig()
  - [ ] WipeServer()

- [ ] Create `Endpoints/Servers/ServerConfigEndpoints.cs`
  - [ ] GetServerConfig()
  - [ ] SaveServerConfig()
  - [ ] ValidateServerConfig()
  - [ ] ProvisionServer()

- [ ] Create `Endpoints/Servers/ServerLogsEndpoints.cs`
  - [ ] GetServerConsole()
  - [ ] TailServerLogs()
  - [ ] ReadServerLogs()
  - [ ] GetServerCommands()
  - [ ] GetServerEvents()

- [ ] Create `Endpoints/Servers/ServerQueryEndpoints.cs`
  - [ ] GetServerInfo()
  - [ ] GetServerPlayers()
  - [ ] GetServerBans()

- [ ] Create `Endpoints/Servers/ServerModerationEndpoints.cs`
  - [ ] KickPlayer()
  - [ ] BanPlayer()
  - [ ] UnbanPlayer()

- [ ] Create `Endpoints/Servers/ServerCommandEndpoints.cs`
  - [ ] ExecuteServerCommand()
  - [ ] ExecuteServerCommandWithCapture()

- [ ] Create `Endpoints/Servers/ServerPluginEndpoints.cs`
  - [ ] ValidateOxidePlugin()
  - [ ] CheckPluginUpdates()
  - [ ] InstallPlugin()

#### Remote Server Endpoints
- [ ] Create `Endpoints/RemoteServers/RemoteServerEndpoints.cs`
  - [ ] GetRemoteServers()
  - [ ] GetRemoteServerConfig()
  - [ ] AddRemoteServer()
  - [ ] UpdateRemoteServer()
  - [ ] DeleteRemoteServer()
  - [ ] TestRemoteServer()

- [ ] Create `Endpoints/RemoteServers/RemoteAgentEndpoints.cs`
  - [ ] GetRemoteAgentStatus()
  - [ ] RegisterRemoteAgent()

#### Dashboard Endpoints
- [ ] Create `Endpoints/Dashboard/DashboardEndpoints.cs`
  - [ ] GetDashboardSummary()
  - [ ] GetDashboardUI()

#### Host Endpoints
- [ ] Create `Endpoints/Dashboard/HostEndpoints.cs`
  - [ ] GetManagedServices()
  - [ ] GetHostLlmSummary()
  - [ ] GetHostNetworkSummary()
  - [ ] GetNetworkInterfaces()

#### Agent Endpoints
- [ ] Create `Endpoints/Agent/AgentEndpoints.cs`
  - [ ] GetAgentLogRules()
  - [ ] SetAgentLogRules()
  - [ ] GetTruncationStatus()
  - [ ] TruncateErrors()
  - [ ] TruncateIncidents()

- [ ] Create `Endpoints/Agent/AgentConfigEndpoints.cs`
  - [ ] GetLlmConfig()
  - [ ] SaveLlmConfig()
  - [ ] GetCommandConfig()
  - [ ] SaveCommandConfig()

- [ ] Create `Endpoints/Agent/AgentIncidentEndpoints.cs`
  - [ ] GetIncidents()
  - [ ] SubmitIncidentFeedback()
  - [ ] GetConsoleLogs()
  - [ ] GetPlayerChat()
  - [ ] GetAdminCalls()

#### Tasks Endpoints
- [ ] Create `Endpoints/Tasks/TaskEndpoints.cs`
  - [ ] GetManagedTasks()
  - [ ] CreateManagedTask()

### Endpoint Registration
- [ ] Create `Endpoints/MapEndpoints.cs` extension method
  - [ ] Call all endpoint mapping functions
  - [ ] Use minimal APIs with grouping
  - [ ] Add tags and descriptions for OpenAPI

- [ ] Update `Program.cs` to call `app.MapEndpoints()`

### Dashboard UI
- [ ] Extract HTML from Program.cs to `/api/UI/dashboard.html`
- [ ] Create `DashboardUiGenerator.cs` to serve it
- [ ] Update endpoint to return `Results.Content()`

### Verification
- [ ] All endpoints compile
- [ ] All endpoints still work
- [ ] API contract unchanged
- [ ] Dependency injection in handlers works
- [ ] Load testing shows similar performance

---

## Phase 6: Cleanup & Polish (Week 7)

### Code Cleanup
- [ ] Remove old inline endpoint code from Program.cs
- [ ] Remove old inline helper functions
- [ ] Delete unused code
- [ ] Verify no dead code remains

### Documentation
- [ ] Add XML doc comments to all public types
- [ ] Add interface documentation
- [ ] Add service documentation
- [ ] Update API documentation/OpenAPI

### Background Services
- [ ] Create `BackgroundServices/RconConnectionMaintenanceService.cs`
  - [ ] IHostedService implementation
  - [ ] Periodic health checks
  - [ ] Connection cleanup

- [ ] Register background services in DI

### Tests
- [ ] Create comprehensive integration tests
- [ ] Test all endpoint groups
- [ ] Test error scenarios
- [ ] Test middleware chain

### Performance
- [ ] Profile hot paths
- [ ] Measure dashboard generation time
- [ ] Check memory usage
- [ ] Load test with concurrent requests

### Security
- [ ] Review auth middleware
- [ ] Check RCON credential handling
- [ ] Validate input sanitization
- [ ] Check file path traversal prevention

### Verification
- [ ] Program.cs < 200 lines (target: ~100)
- [ ] All functionality works
- [ ] No regressions observed
- [ ] Tests pass
- [ ] Performance acceptable

---

## Phase 7: Testing & Rollout (Week 8)

### Integration Testing
- [ ] Write comprehensive endpoint tests
- [ ] Test request/response contracts
- [ ] Test error handling
- [ ] Test cancellation tokens

### Load Testing
- [ ] Test concurrent endpoint access
- [ ] Measure response times
- [ ] Monitor memory usage
- [ ] Test RCON connection pooling

### Regression Testing
- [ ] Manual smoke tests all endpoints
- [ ] Test against production data patterns
- [ ] Verify backward compatibility
- [ ] Test with external agents

### Documentation
- [ ] Update architecture documentation
- [ ] Document service responsibilities
- [ ] Document configuration options
- [ ] Create maintenance guide

### Rollout Plan
- [ ] Create feature flag for new implementation
- [ ] Deploy to staging
- [ ] Run full test suite
- [ ] Monitor for 24 hours
- [ ] Deploy to production
- [ ] Monitor for 1 week
- [ ] Remove feature flag
- [ ] Remove old code paths

---

## Quality Gates

### Before Phase 2
- [ ] All project files compile without warnings
- [ ] Models and utilities extracted
- [ ] DI container bootstraps correctly

### Before Phase 3
- [ ] Infrastructure services 100% unit test coverage
- [ ] No breaking changes to existing endpoints
- [ ] Services are independently testable

### Before Phase 4
- [ ] All business services compile
- [ ] Services integrate properly
- [ ] Integration tests pass
- [ ] Dashboard still generates correctly

### Before Phase 5
- [ ] Endpoints compile
- [ ] All existing routes still work
- [ ] API contract identical
- [ ] No performance regression

### Before Phase 6
- [ ] All endpoints migrated
- [ ] Old code removed
- [ ] Program.cs minimal
- [ ] Full test coverage

### Before Phase 7
- [ ] All tests pass
- [ ] Load tests pass
- [ ] Regression tests pass
- [ ] Security review passed

---

## Risk Mitigation

### High Risk: Breaking API Contract
- **Mitigation**: Compare endpoint responses with original
- **Test**: Unit tests for response mapping
- **Rollback**: Keep old implementation as fallback

### High Risk: RCON Connection Leaks
- **Mitigation**: Add connection tracking
- **Test**: Memory profiling under load
- **Rollback**: Revert RconConnectionManager

### Medium Risk: Performance Regression
- **Mitigation**: Profile before and after each service
- **Test**: Load testing with 1000+ concurrent requests
- **Rollback**: Cache optimization strategies

### Medium Risk: Configuration Service Failures
- **Mitigation**: Validate all config paths at startup
- **Test**: Unit tests with missing files
- **Rollback**: Keep old env var reading as fallback

### Low Risk: Dependency Injection Misconfiguration
- **Mitigation**: Validate DI during startup
- **Test**: Unit tests for service registration
- **Rollback**: Revert DI configuration

---

## Success Criteria

### Code Metrics
- [ ] Program.cs reduced from 6,863 to < 200 lines
- [ ] 50+ interfaces created for dependency injection
- [ ] 30+ service classes created
- [ ] 20+ endpoint handler classes created
- [ ] 0 static helper method accumulation
- [ ] 80%+ unit test coverage for services

### Functional Metrics
- [ ] 100% API endpoint compatibility
- [ ] 0 breaking changes to request/response contracts
- [ ] 0 environment variable name changes
- [ ] All 69 endpoints functional
- [ ] All error codes maintained

### Performance Metrics
- [ ] Dashboard load time ≤ original + 5%
- [ ] Endpoint response time ≤ original + 5%
- [ ] Memory usage ≤ original + 5%
- [ ] RCON connection pooling reduces connection churn by 50%

### Maintainability Metrics
- [ ] New features can be added without modifying Program.cs
- [ ] Services are independently testable
- [ ] Clear responsibility separation
- [ ] No circular dependencies

---

## Timeline Summary

| Week | Phase | Deliverables | Risk |
|------|-------|--------------|------|
| 1 | Infrastructure | All folders, models, utilities extracted | Low |
| 2 | Core Services | Config, file, process, logging services | Low-Medium |
| 3 | Core Services | RCON, server config services | Low-Medium |
| 4 | Business Services | Server, plugin, remote services | Medium |
| 5 | Business Services | Dashboard, host, LLM services | Medium |
| 6 | Endpoints | Middleware, endpoint handlers, mapping | Medium-High |
| 7 | Cleanup | Dashboard HTML extraction, docs, tests | Low-Medium |
| 8 | Testing | Load tests, regression tests, rollout | Low |

**Total Duration**: 8 weeks  
**Total Estimated Hours**: ~320 (40 hrs/week)  
**Recommended Team Size**: 2-3 engineers

---

## Approval & Sign-Off

- [ ] Architecture approved by tech lead
- [ ] Timeline approved by project manager
- [ ] Risk assessment reviewed
- [ ] Resource allocation confirmed
- [ ] Ready to begin Phase 1

---

**Document Version**: 1.0  
**Date**: 2026-05-04  
**Status**: Ready for Implementation
