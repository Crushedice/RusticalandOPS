namespace rustmgrapi.Api.Services;

internal sealed class RuntimePaths
{
    public string ConfigDir { get; init; } = "/opt/rust-manager/config";
    public string RuntimeDir { get; init; } = "/opt/rust-manager/runtime";
    public string TasksDir { get; init; } = "/opt/rust-manager/tasks";
    public string BindUrl { get; init; } = "http://0.0.0.0:2077";
    public string ApiKey { get; init; } = "changeme";

    public static RuntimePaths ReadFromEnv()
    {
        return new RuntimePaths
        {
            ConfigDir = Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config",
            RuntimeDir = Environment.GetEnvironmentVariable("RUSTMGR_RUNTIME") ?? "/opt/rust-manager/runtime",
            TasksDir = Environment.GetEnvironmentVariable("RUSTMGR_TASKS_DIR") ?? "/opt/rust-manager/tasks",
            BindUrl = Environment.GetEnvironmentVariable("RUSTMGR_BIND") ?? "http://0.0.0.0:2077",
            ApiKey = RustOpsEnv.FirstNonEmptyEnvironment("RUSTMGR_API_KEY", "RUSTOPS_API_KEY") ?? "changeme"
        };
    }
}
