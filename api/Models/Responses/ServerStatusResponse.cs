using RusticalandOPS.Api.Models.Shared;

namespace RusticalandOPS.Api.Models.Responses;

public sealed class ServerStatusResponse
{
    public string Name { get; set; } = string.Empty;
    /// <summary>running | offline | starting | session-only | unknown</summary>
    public string State { get; set; } = "unknown";
    /// <summary>true only when State == "running"</summary>
    public bool Online { get; set; }
    public bool AutoRestart { get; set; }
    public int? Pid { get; set; }
    public string Raw { get; set; } = string.Empty;
}

public sealed class LogSliceResult
{
    public bool Exists { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public bool Truncated { get; set; }
    public bool Reset { get; set; }
    public List<LogEntry> Entries { get; set; } = new();
}

public sealed class CommandOutputCapture
{
    public bool Exists { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public bool Truncated { get; set; }
    public bool Reset { get; set; }
    public int Count { get; set; }
    public List<LogEntry> Entries { get; set; } = new();
    public List<string> Messages { get; set; } = new();
}

public sealed class ValidationResult
{
    public string Path { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? PluginName { get; set; }
    public string? PluginAuthor { get; set; }
    public string? PluginVersion { get; set; }
    public string? PluginSlug { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public List<RusticalandOPS.Api.Models.Shared.PluginCommandReferenceView> Commands { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
    public List<string> Hooks { get; set; } = new();
    public List<string> ConfigKeys { get; set; } = new();
}

public sealed class ManagedTaskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Schedule { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class MailboxFileSummary
{
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAtUtc { get; set; }
    public long SizeBytes { get; set; }
}
