using System.Diagnostics;
using System.Text;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure;

internal sealed class AutoPullService
{
    private readonly AutoPullSettings _settings;
    private DateTime _lastRunAtUtc = DateTime.MinValue;
    private AutoPullStatus _lastStatus = new("idle", null, null, DateTime.MinValue);

    public AutoPullService(AutoPullSettings settings)
    {
        _settings = settings;
    }

    public AutoPullStatus LastStatus => _lastStatus;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(5, _settings.IntervalMinutes));
        if (DateTime.UtcNow - _lastRunAtUtc < interval)
            return;

        await TriggerAsync(cancellationToken);
    }

    public async Task<AutoPullStatus> TriggerAsync(CancellationToken cancellationToken)
    {
        _lastRunAtUtc = DateTime.UtcNow;
        Console.WriteLine("[autopull] Starting pull cycle.");

        var output = new StringBuilder();
        try
        {
            var gitEnv = BuildGitEnv();
            var pullOut = await PullWithFallbackAsync(gitEnv, cancellationToken);
            output.AppendLine(pullOut);
            Console.WriteLine($"[autopull] git pull: {pullOut.Trim()}");

            if (pullOut.Contains("Already up to date", StringComparison.OrdinalIgnoreCase))
            {
                _lastStatus = new AutoPullStatus("up-to-date", pullOut.Trim(), null, DateTime.UtcNow);
                return _lastStatus;
            }

            if (_settings.BuildEnabled && !string.IsNullOrWhiteSpace(_settings.BuildScript))
            {
                Console.WriteLine($"[autopull] Running build script: {_settings.BuildScript}");
                var buildScript = Path.IsPathRooted(_settings.BuildScript)
                    ? _settings.BuildScript
                    : Path.Combine(_settings.RepoPath, _settings.BuildScript);

                var buildOut = await RunAsync("bash", $"\"{buildScript}\"", _settings.RepoPath, null, cancellationToken);
                output.AppendLine(buildOut);
                Console.WriteLine($"[autopull] build: {buildOut.Trim()}");
            }

            if (_settings.RestartEnabled && !string.IsNullOrWhiteSpace(_settings.ServiceName))
            {
                Console.WriteLine($"[autopull] Restarting service: {_settings.ServiceName}");
                var restartOut = await RunAsync("systemctl", $"restart {_settings.ServiceName}", "/", null, cancellationToken);
                output.AppendLine(restartOut);
            }

            _lastStatus = new AutoPullStatus("updated", output.ToString().Trim(), null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[autopull] Failed: {ex.Message}");
            RustOpsSentry.CaptureException(ex, "AutoPull cycle failed.", "agent.autopull");
            _lastStatus = new AutoPullStatus("error", output.ToString().Trim(), ex.Message, DateTime.UtcNow);
        }

        return _lastStatus;
    }

    private async Task<string> PullWithFallbackAsync(Dictionary<string, string>? gitEnv, CancellationToken cancellationToken)
    {
        // Prefer upstream tracking config first so autopull works regardless of branch naming.
        // This avoids hard dependency on autoPull.branchName and respects the checked-out branch.
        try
        {
            return await RunAsync("git", "pull --ff-only", _settings.RepoPath, gitEnv, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsNoTrackingInfoError(ex.Message) || IsMissingRemoteRefError(ex.Message))
        {
            var fallbackLines = new List<string>
            {
                $"[autopull] Upstream pull unavailable ({TrimMessage(ex.Message)}). Trying explicit remote branch fallback."
            };
            Console.WriteLine(fallbackLines[0]);

            var remoteHeadBranch = await TryResolveRemoteHeadBranchAsync(gitEnv, cancellationToken);
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(remoteHeadBranch))
            {
                candidates.Add(remoteHeadBranch!);
            }

            if (!string.IsNullOrWhiteSpace(_settings.BranchName) &&
                !candidates.Contains(_settings.BranchName, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(_settings.BranchName);
            }

            foreach (var branch in candidates)
            {
                try
                {
                    var fallbackArgs = $"pull --ff-only {_settings.RemoteName} {branch}";
                    var fallbackOut = await RunAsync("git", fallbackArgs, _settings.RepoPath, gitEnv, cancellationToken);
                    fallbackLines.Add($"[autopull] Fell back to remote '{_settings.RemoteName}/{branch}'.");
                    fallbackLines.Add(fallbackOut);
                    return string.Join(Environment.NewLine, fallbackLines);
                }
                catch (Exception fallbackEx)
                {
                    fallbackLines.Add($"[autopull] Fallback to '{_settings.RemoteName}/{branch}' failed: {TrimMessage(fallbackEx.Message)}");
                }
            }

            throw new InvalidOperationException(string.Join(Environment.NewLine, fallbackLines));
        }
    }

    private Dictionary<string, string>? BuildGitEnv()
    {
        if (string.IsNullOrWhiteSpace(_settings.GithubToken))
            return null;

        var token = _settings.GithubToken!;
        // Use GIT_CONFIG_* env vars (git 2.31+) to inject the PAT via URL rewrite,
        // avoiding the need for a ~/.git-credentials file under the service user account.
        return new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = $"url.https://oauth2:{token}@github.com/.insteadOf",
            ["GIT_CONFIG_VALUE_0"] = "https://github.com/",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = ""
        };
    }

    private async Task<string?> TryResolveRemoteHeadBranchAsync(Dictionary<string, string>? gitEnv, CancellationToken cancellationToken)
    {
        var symbolic = await RunAllowFailureAsync(
            "git",
            $"symbolic-ref --quiet --short refs/remotes/{_settings.RemoteName}/HEAD",
            _settings.RepoPath,
            gitEnv,
            cancellationToken);
        if (symbolic.ExitCode == 0 && !string.IsNullOrWhiteSpace(symbolic.StdOut))
        {
            var trimmed = symbolic.StdOut.Trim();
            var prefix = _settings.RemoteName + "/";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..];
            }
        }

        var remoteShow = await RunAllowFailureAsync(
            "git",
            $"remote show {_settings.RemoteName}",
            _settings.RepoPath,
            gitEnv,
            cancellationToken);
        if (remoteShow.ExitCode == 0)
        {
            var line = remoteShow.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(item => item.StartsWith("HEAD branch:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(line))
            {
                var branch = line.Split(':', 2)[1].Trim();
                return string.IsNullOrWhiteSpace(branch) ? null : branch;
            }
        }

        return null;
    }

    private static bool IsMissingRemoteRefError(string message) =>
        message.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("fatal: couldn't find remote ref", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoTrackingInfoError(string message) =>
        message.Contains("no tracking information", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("has no tracking information", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("There is no tracking information for the current branch", StringComparison.OrdinalIgnoreCase);

    private static string TrimMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "unknown error";

        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "...";
    }

    private static async Task<string> RunAsync(string fileName, string arguments, string workingDirectory, Dictionary<string, string>? env, CancellationToken cancellationToken)
    {
        var result = await RunAllowFailureAsync(fileName, arguments, workingDirectory, env, cancellationToken);
        if (result.ExitCode != 0)
        {
            var stderr = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new InvalidOperationException($"{fileName} {arguments} exited {result.ExitCode}: {stderr.Trim()}");
        }

        return string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr.Trim() : result.StdOut.Trim();
    }

    private static async Task<ProcessRunResult> RunAllowFailureAsync(string fileName, string arguments, string workingDirectory, Dictionary<string, string>? env, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env != null)
        {
            foreach (var kvp in env)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return new ProcessRunResult(process.ExitCode, stdOut.Trim(), stdErr.Trim());
    }

    private sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);
}

internal sealed record AutoPullStatus(
    string Phase,
    string? Output,
    string? Error,
    DateTime LastRunAtUtc);
