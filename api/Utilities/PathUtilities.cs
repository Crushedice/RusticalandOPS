using RusticalandOPS.Api.Models.Shared;

namespace RusticalandOPS.Api.Utilities;

public static class PathUtilities
{
    public static string GetServerLogPath(ServerConfig cfg) =>
        Path.Combine(cfg.ServerDir, cfg.LogFile);

    public static string GetConfigPath(string server) =>
        Path.Combine(
            Environment.GetEnvironmentVariable("RUSTMGR_CONFIG") ?? "/opt/rust-manager/config",
            $"{server}.json");

    public static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public static int? SafeReadInt(string path)
    {
        var text = SafeReadText(path);
        return int.TryParse(text, out var value) ? value : null;
    }

    public static long SafeReadLong(string path)
    {
        var text = SafeReadText(path);
        return long.TryParse(text, out var value) ? value : 0L;
    }
}
