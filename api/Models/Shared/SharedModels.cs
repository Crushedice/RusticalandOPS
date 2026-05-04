namespace RusticalandOPS.Api.Models.Shared;

public sealed class CommandExecutionResult
{
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public IEnumerable<string> Arguments { get; set; } = Array.Empty<string>();
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
}

public sealed class LogEntry
{
    public DateTime? Timestamp { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}

public sealed class TraceEvent
{
    public DateTime? Timestamp { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class ApiError
{
    public ApiError(string code, string message) { Code = code; Message = message; }
    public string Code { get; }
    public string Message { get; }
}

public sealed record PluginCommandReferenceView(string Command, string Type, string HandlerMethod);
public sealed record PluginMetadata(string? Name, string? Version);

internal sealed record IncidentFeedbackEntry(string? Verdict, string? Note, DateTime? AnsweredAtUtc);
