# Phase 1: Infrastructure Setup - Complete Example

This document shows exactly what Phase 1 should produce using real examples from the current codebase.

## 1. Utilities Extraction Examples

### Before (scattered in Program.cs)
```csharp
// Lines 3033-3043 in Program.cs
static string BuildQueryString(IReadOnlyDictionary<string, string?> values)
{
    var parts = new List<string>();
    foreach (var kvp in values)
    {
        if (string.IsNullOrWhiteSpace(kvp.Value))
            continue;
        parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
    }
    return string.Join("&", parts);
}

static string? TryExtractJson(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return null;
    // ... implementation ...
}

static string Escape(string value) => value.Replace("\"", "\\\"");
```

### After (Utilities/StringUtilities.cs)
```csharp
namespace RusticalandOPS.Api.Utilities;

/// <summary>String manipulation helpers for API responses and logging.</summary>
public static class StringUtilities
{
    /// <summary>
    /// Builds a URL query string from key-value pairs.
    /// </summary>
    public static string BuildQueryString(IReadOnlyDictionary<string, string?> values)
    {
        var parts = new List<string>();
        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;
            parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        }
        return string.Join("&", parts);
    }

    /// <summary>
    /// Escapes double quotes in a string for JSON serialization.
    /// </summary>
    public static string EscapeQuotes(string value) => value.Replace("\"", "\\\"");

    /// <summary>
    /// Returns the last N lines from multi-line text.
    /// </summary>
    public static string TailLines(string text, int lines)
    {
        if (lines <= 0) return text;
        var textLines = text.Split('\n');
        return string.Join('\n', textLines.TakeLast(lines));
    }
}
```

### JsonUtilities.cs Example

```csharp
namespace RusticalandOPS.Api.Utilities;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>JSON parsing and extraction helpers.</summary>
public static class JsonUtilities
{
    /// <summary>
    /// Attempts to extract valid JSON from mixed output (e.g., logs with JSON at the end).
    /// Returns the raw JSON string if found, null otherwise.
    /// </summary>
    public static string? TryExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        
        // If starts with { or [, assume it's JSON
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.GetRawText();
            }
            catch
            {
                // Fall through to regex search
            }
        }

        // Search for JSON object in the text
        var match = Regex.Match(trimmed, @"\{.*\}", RegexOptions.Singleline);
        if (match.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Value);
                return doc.RootElement.GetRawText();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Pretty-prints JSON string with indentation.</summary>
    public static string? PrettyJson(string? jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            return JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Safely reads a string property from JsonElement, returning null if not found.</summary>
    public static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();

        return null;
    }

    /// <summary>Safely reads an integer property, returning null if not found.</summary>
    public static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value))
                return value;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>Safely reads a long property, returning null if not found.</summary>
    public static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value))
                return value;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>Safely reads a boolean property, returning null if not found.</summary>
    public static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True)
            return true;
        if (element.TryGetProperty(propertyName, out prop) && prop.ValueKind == JsonValueKind.False)
            return false;

        return null;
    }

    /// <summary>Safely reads a DateTime property, returning null if not found.</summary>
    public static DateTime? TryGetDateTime(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var str = prop.GetString();
            if (DateTime.TryParse(str, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }

        return null;
    }

    /// <summary>
    /// Tries to read a value from any of the specified property names (case-insensitive).
    /// Useful for APIs with inconsistent naming.
    /// </summary>
    public static string? TryGetStringAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to read an integer from any of the specified property names (case-insensitive).
    /// </summary>
    public static int? TryGetIntAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
                    return value;
                if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out var parsed))
                    return parsed;
            }
        }

        return null;
    }
}
```

## 2. Configuration Provider Example

### IConfigurationProvider.cs
```csharp
namespace RusticalandOPS.Api.Infrastructure.Configuration;

public interface IConfigurationProvider
{
    /// <summary>Gets an environment variable or returns null if not set.</summary>
    string? GetEnvironmentVariable(string key);

    /// <summary>Gets an environment variable or returns the default value if not set.</summary>
    string GetEnvironmentVariable(string key, string defaultValue);

    /// <summary>Gets the first non-empty value from multiple environment variables.</summary>
    string? GetFirstNonEmptyEnvironmentVariable(params string[] keys);

    /// <summary>Reads key-value pairs from an environment file (KEY=VALUE format).</summary>
    Dictionary<string, string> ReadEnvFile(string path);

    /// <summary>Writes key-value pairs to an environment file, merging with existing values.</summary>
    void WriteEnvFile(string path, Dictionary<string, string> values);

    /// <summary>Deserializes a JSON file to the specified type.</summary>
    T? DeserializeJsonFile<T>(string path) where T : class;

    /// <summary>Serializes an object to a JSON file.</summary>
    void SerializeJsonFile<T>(string path, T value);
}
```

### ConfigurationProvider.cs
```csharp
namespace RusticalandOPS.Api.Infrastructure.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;

public class ConfigurationProvider : IConfigurationProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationProvider> _logger;

    public ConfigurationProvider(IConfiguration configuration, ILogger<ConfigurationProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string? GetEnvironmentVariable(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return Environment.GetEnvironmentVariable(key);
    }

    public string GetEnvironmentVariable(string key, string defaultValue)
    {
        return GetEnvironmentVariable(key) ?? defaultValue;
    }

    public string? GetFirstNonEmptyEnvironmentVariable(params string[] keys)
    {
        if (keys == null || keys.Length == 0)
            return null;

        foreach (var key in keys)
        {
            var value = GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public Dictionary<string, string> ReadEnvFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            _logger.LogWarning("Environment file not found: {Path}", path);
            return values;
        }

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                
                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                values[key] = value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read environment file: {Path}", path);
        }

        return values;
    }

    public void WriteEnvFile(string path, Dictionary<string, string> updates)
    {
        try
        {
            var lines = File.Exists(path) 
                ? File.ReadAllLines(path).ToList() 
                : new List<string>();

            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Update existing lines
            for (var i = 0; i < lines.Count; i++)
            {
                var rawLine = lines[i];
                var trimmed = rawLine.Trim();
                
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var separator = rawLine.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = rawLine[..separator].Trim();
                if (!updates.TryGetValue(key, out var newValue))
                    continue;

                lines[i] = $"{key}={newValue}";
                touched.Add(key);
            }

            // Add new lines for keys not found
            foreach (var update in updates.Where(u => !touched.Contains(u.Key)))
                lines.Add($"{update.Key}={update.Value}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);

            _logger.LogInformation("Updated environment file: {Path} ({Count} values)", path, updates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write environment file: {Path}", path);
            throw;
        }
    }

    public T? DeserializeJsonFile<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("JSON file not found: {Path}", path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize JSON file: {Path}", path);
            return null;
        }
    }

    public void SerializeJsonFile<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(value, JsonSerializerOptions);
            File.WriteAllText(path, json);
            _logger.LogInformation("Serialized to JSON file: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize to JSON file: {Path}", path);
            throw;
        }
    }

    private static JsonSerializerOptions JsonSerializerOptions => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
```

## 3. DI Container Setup

### Extensions/ServiceCollectionExtensions.cs

```csharp
namespace RusticalandOPS.Api.Extensions;

using RusticalandOPS.Api.Infrastructure.Configuration;
using RusticalandOPS.Api.Utilities;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RustOps API services with the dependency injection container.
    /// </summary>
    public static IServiceCollection AddRustOpsServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // Infrastructure Services - Singletons (stateless)
        services.AddSingleton<IConfigurationProvider, ConfigurationProvider>();
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddSingleton<IProcessExecutor, ProcessExecutor>();
        services.AddSingleton<IRustMgrExecutor, RustMgrExecutor>();

        // Utilities - Static helpers accessed via DI where needed
        services.AddSingleton<JsonUtilities>();
        services.AddSingleton<ValidationUtilities>();
        services.AddSingleton<PluginMetadataParser>();

        // HTTP Client Factory
        services.AddHttpClient("RemoteAgent")
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.Add("User-Agent", "RusticalandOPS/1.0");
            });

        return services;
    }

    /// <summary>
    /// Registers core services (Phase 2+).
    /// </summary>
    public static IServiceCollection AddCoreServices(
        this IServiceCollection services)
    {
        // These will be added in Phase 2
        // services.AddScoped<IServerConfigService, ServerConfigService>();
        // services.AddScoped<ILogReadingService, LogReadingService>();
        // etc.

        return services;
    }

    /// <summary>
    /// Registers business services (Phase 4+).
    /// </summary>
    public static IServiceCollection AddBusinessServices(
        this IServiceCollection services)
    {
        // These will be added in Phase 4
        // services.AddScoped<IServerService, ServerService>();
        // services.AddScoped<IDashboardAggregationService, DashboardAggregationService>();
        // etc.

        return services;
    }
}
```

## 4. Updated Program.cs (Phase 1)

```csharp
using RusticalandOPS.Api.Extensions;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;

RustOpsEnv.LoadFromDefaultLocations();
using var sentry = RustOpsSentry.Initialize("rustmgrapi");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure JSON serialization
    builder.Services.Configure<JsonOptions>(options =>
    {
        options.SerializerOptions.WriteIndented = true;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

    // Register all RustOps services
    builder.Services.AddRustOpsServices(builder.Configuration);

    var app = builder.Build();

    // Configure URL
    var bindUrl = builder.Configuration["RUSTMGR_BIND"] ?? "http://0.0.0.0:2077";
    app.Urls.Clear();
    app.Urls.Add(bindUrl);

    // Middleware
    app.UseRustOpsMiddleware();

    // Routes (to be refactored in Phase 5)
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapGet("/", () => Results.Redirect("/ui"));

    // ... all existing endpoints remain unchanged during Phase 1 ...

    await app.RunAsync();
}
catch (Exception ex)
{
    RustOpsSentry.CaptureException(ex, "Fatal API startup error", "api.startup");
    throw;
}
finally
{
    await RustOpsSentry.FlushAsync();
}
```

### Extensions/ApplicationBuilderExtensions.cs

```csharp
namespace RusticalandOPS.Api.Extensions;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Configures RustOps middleware pipeline.
    /// </summary>
    public static void UseRustOpsMiddleware(this WebApplication app)
    {
        // Error handling should be first
        app.UseMiddleware<ErrorHandlingMiddleware>();

        // Request logging
        app.UseMiddleware<RequestLoggingMiddleware>();

        // Authentication
        app.UseMiddleware<AuthenticationMiddleware>();

        // CORS if needed
        app.UseCors(builder => builder
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
    }
}
```

## 5. Middleware Examples

### Middleware/AuthenticationMiddleware.cs

```csharp
namespace RusticalandOPS.Api.Middleware;

public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for health endpoint
        if (context.Request.Path == "/health")
        {
            await _next(context);
            return;
        }

        var apiKey = _configuration["RUSTMGR_API_KEY"] ?? 
                     _configuration["RUSTOPS_API_KEY"] ?? 
                     "changeme";

        var providedKey = context.Request.Headers["X-API-Key"].ToString();

        if (string.IsNullOrWhiteSpace(providedKey) || providedKey != apiKey)
        {
            _logger.LogWarning("Unauthorized request to {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new { error = "Unauthorized" },
                cancellationToken: context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
```

### Middleware/ErrorHandlingMiddleware.cs

```csharp
namespace RusticalandOPS.Api.Middleware;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in HTTP pipeline: {Path}", context.Request.Path);
            
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(
                new { error = "Internal server error", traceId = context.TraceIdentifier },
                cancellationToken: context.RequestAborted);
        }
    }
}
```

### Middleware/RequestLoggingMiddleware.cs

```csharp
namespace RusticalandOPS.Api.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _next(context);
            sw.Stop();

            _logger.LogInformation(
                "HTTP {Method} {Path} {StatusCode} ({ElapsedMs}ms)",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            throw;
        }
    }
}
```

## 6. Project File Configuration

Ensure the .csproj has these settings:

```xml
<PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <WarningLevel>4</WarningLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

## 7. Phase 1 Verification Checklist

Use this to verify Phase 1 is complete:

```csharp
// Program.cs - Should be able to run with this minimal code
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRustOpsServices(builder.Configuration);

var app = builder.Build();
app.UseRustOpsMiddleware();

// All old endpoints still work
app.MapGet("/health", async (IConfigurationProvider config) => 
{
    // Can now inject services!
    return Results.Ok(new { status = "healthy" });
});

await app.RunAsync();
```

## 8. Testing Phase 1

Create a simple integration test:

```csharp
// Tests/Phase1/InfrastructureIntegrationTests.cs

[TestClass]
public class InfrastructureIntegrationTests
{
    private WebApplication? _app;

    [TestInitialize]
    public void Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRustOpsServices(builder.Configuration);
        _app = builder.Build();
    }

    [TestMethod]
    public void ConfigurationProvider_IsRegistered()
    {
        var provider = _app!.Services.GetRequiredService<IConfigurationProvider>();
        Assert.IsNotNull(provider);
    }

    [TestMethod]
    public void FileStorageService_IsRegistered()
    {
        var service = _app!.Services.GetRequiredService<IFileStorageService>();
        Assert.IsNotNull(service);
    }

    [TestMethod]
    public void ProcessExecutor_IsRegistered()
    {
        var executor = _app!.Services.GetRequiredService<IProcessExecutor>();
        Assert.IsNotNull(executor);
    }

    [TestMethod]
    public void RustMgrExecutor_IsRegistered()
    {
        var executor = _app!.Services.GetRequiredService<IRustMgrExecutor>();
        Assert.IsNotNull(executor);
    }
}
```

---

## Phase 1 Completion Criteria

✓ All folders created  
✓ All utilities extracted to `/Utilities`  
✓ All models extracted to `/Models`  
✓ `IConfigurationProvider` implemented  
✓ `IFileStorageService` implemented  
✓ `IProcessExecutor` implemented  
✓ DI container configured  
✓ Middleware pipeline defined  
✓ Program.cs simplified  
✓ All endpoints still work  
✓ No functional changes visible to API  
✓ Tests pass  

**Estimated Time**: 3-5 days for experienced .NET engineer

---

**Document Version**: 1.0  
**Date**: 2026-05-04  
**Status**: Ready for Implementation
