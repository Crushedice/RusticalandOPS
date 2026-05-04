namespace RusticalandOPS.Api.Models.Requests;

public class AgentLlmConfigUpdate
{
    public string Provider { get; set; } = "lmstudio";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
    public bool UseForRecommendations { get; set; } = true;
    public string RequestStrategy { get; set; } = "fallback";
    public AgentLlmEndpointConfigView? Secondary { get; set; }
    public bool UseChatSystemPrompt { get; set; }
    public string? ChatSystemPrompt { get; set; }
}

public sealed class AgentLlmEndpointConfigView
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
}

public class AgentCommandConfigUpdate
{
    public bool Enabled { get; set; } = true;
    public bool FreeMode { get; set; }
    public int DefaultWaitMs { get; set; } = 2500;
    public int MaxWaitMs { get; set; } = 12_000;
    public int MaxOutputChars { get; set; } = 8000;
    public List<string>? AllowList { get; set; }
}
