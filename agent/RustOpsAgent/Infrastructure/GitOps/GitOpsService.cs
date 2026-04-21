using System.Diagnostics;
using RustOpsAgent.Core.Contracts;

namespace RustOpsAgent.Infrastructure.GitOps;

internal sealed class GitOpsService : IGitOpsService
{
    private readonly GitOpsSettings _settings;

    public GitOpsService(GitOpsSettings settings)
    {
        _settings = settings;
        _settings.PushBranchPrefix = "agent/";
    }

    public async Task<string> EnsureAgentBranchAsync(string slug, CancellationToken cancellationToken)
    {
        var branch = $"agent/{DateTime.UtcNow:yyyyMMdd}-{SanitizeSlug(slug)}";
        await RunGitAsync($"checkout -B {branch}", cancellationToken);
        return branch;
    }

    public async Task CommitAsync(string message, CancellationToken cancellationToken)
    {
        await RunGitAsync("add -A", cancellationToken);
        await RunGitAsync($"commit -m \"{Escape(message)}\"", cancellationToken);
    }

    public async Task PushAsync(string branchName, CancellationToken cancellationToken)
    {
        if (string.Equals(branchName, "main", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase) ||
            !branchName.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Direct push to non-agent branches is blocked. Use agent/* only.");
        }

        await RunGitAsync($"push -u {_settings.RemoteName} {branchName}", cancellationToken);
    }

    public async Task<string> CreatePrAsync(string branchName, string title, string body, CancellationToken cancellationToken)
    {
        if (!branchName.StartsWith("agent/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PR branch must be under agent/*.");
        }

        var cmd = $"pr create --base {_settings.BaseBranch} --head {branchName} --title \"{Escape(title)}\" --body \"{Escape(body)}\"";
        return await RunGitAsync(cmd, cancellationToken);
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

    private static string SanitizeSlug(string value)
    {
        var safe = new string(value.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "update" : safe;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}