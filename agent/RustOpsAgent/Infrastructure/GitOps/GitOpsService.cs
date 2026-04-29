using System.Diagnostics;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.GitOps;

internal sealed class GitOpsService : IGitOpsService
{
    private readonly GitOpsSettings _settings;

    public GitOpsService(GitOpsSettings settings)
    {
        _settings = settings;
        // PushBranchPrefix is validated upstream in ConfigLoader and Program.cs — do not mutate here.
    }

    public async Task<string> EnsureAgentBranchAsync(string slug, CancellationToken cancellationToken)
    {
        const string branch = "agent-updates";
        await RunGitAsync("fetch --all", cancellationToken);
        // Create/reset agent-updates from the upstream base so the working tree is at origin/main.
        // This ensures AutoPullService can fast-forward main cleanly after we push and return to it.
        await RunGitAsync($"checkout -B {branch} {_settings.RemoteName}/{_settings.BaseBranch}", cancellationToken);
        return branch;
    }

    public async Task CheckoutMainAsync(CancellationToken cancellationToken)
    {
        await RunGitAsync($"checkout {_settings.BaseBranch}", cancellationToken);
    }

    public async Task CommitAsync(string message, CancellationToken cancellationToken)
    {
        await RunGitAsync("add -A", cancellationToken);
        await RunGitAsync($"commit -m \"{Escape(message)}\"", cancellationToken);
    }

    public async Task PushAsync(string branchName, CancellationToken cancellationToken)
    {
        var isAgentBranch = string.Equals(branchName, "agent-updates", StringComparison.OrdinalIgnoreCase) ||
                            branchName.StartsWith("agent/", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(branchName, "main", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase) ||
            !isAgentBranch)
        {
            throw new InvalidOperationException("Direct push to non-agent branches is blocked. Use 'agent-updates' or 'agent/*' only.");
        }

        await RunGitAsync($"push -u {_settings.RemoteName} {branchName}", cancellationToken);
    }

    public async Task<string> CreatePrAsync(string branchName, string title, string body, CancellationToken cancellationToken)
    {
        var isAgentBranch = string.Equals(branchName, "agent-updates", StringComparison.OrdinalIgnoreCase) ||
                            branchName.StartsWith("agent/", StringComparison.OrdinalIgnoreCase);

        if (!isAgentBranch)
        {
            throw new InvalidOperationException("PR branch must be 'agent-updates' or under 'agent/*'.");
        }

        // Write body to a temp file so multi-line markdown isn't mangled by shell argument quoting.
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, body, cancellationToken);
            var cmd = $"pr create --base {_settings.BaseBranch} --head {branchName} --title \"{Escape(title)}\" --body-file \"{tempFile}\"";
            return await RunGhAsync(cmd, cancellationToken);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private async Task<string> RunGhAsync(string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = args,
            WorkingDirectory = _settings.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_settings.GithubToken))
            psi.Environment["GH_TOKEN"] = _settings.GithubToken;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch git process.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh {args} failed: {stdErr}");
        }

        return string.IsNullOrWhiteSpace(stdOut) ? "ok" : stdOut.Trim();
    }

    private async Task<string> RunGitAsync(string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _settings.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_settings.GithubToken))
        {
            var token = _settings.GithubToken!;
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_CONFIG_COUNT"] = "2";
            psi.Environment["GIT_CONFIG_KEY_0"] = $"url.https://oauth2:{token}@github.com/.insteadOf";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "https://github.com/";
            psi.Environment["GIT_CONFIG_KEY_1"] = "credential.helper";
            psi.Environment["GIT_CONFIG_VALUE_1"] = "";
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch git process.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args} failed: {stdErr}");
        }

        return string.IsNullOrWhiteSpace(stdOut) ? "ok" : stdOut.Trim();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}