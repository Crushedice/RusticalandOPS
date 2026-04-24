using System.Text.RegularExpressions;
using RustOpsAgent.Core.Contracts;
using RustOpsAgent.Infrastructure;
using RustOpsAgent.Infrastructure.GitOps;

namespace RustOpsAgent.Domains.Rust;

internal sealed class RustFileEditToolHandler : IToolHandler
{
    private readonly RustOpsApiClient _api;
    private readonly IGitOpsService _gitOps;
    private readonly GitOpsSettings _gitOpsSettings;

    private static readonly string[] AllowedExtensions = { ".cfg", ".json", ".txt", ".ini", ".env" };

    public RustFileEditToolHandler(RustOpsApiClient api, IGitOpsService gitOps, GitOpsSettings gitOpsSettings)
    {
        _api = api;
        _gitOps = gitOps;
        _gitOpsSettings = gitOpsSettings;
    }

    public string Name => "rust.file.edit";
    public IReadOnlyCollection<AdminIntentType> EligibleIntents => new[] { AdminIntentType.FileEdit };

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!_gitOpsSettings.Enabled)
        {
            return new ToolExecutionResult(false, "File editing requires GitOps to be enabled (gitOps.enabled=true in config).", null, false, "not_configured");
        }

        var filePath = ExtractFilePath(context.Message);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ToolExecutionResult(false,
                "Which file should I open? Mention the config file name, for example: show server.cfg, or edit oxide/config/MyPlugin.json.",
                null, false, "clarification_required");
        }

        if (!IsSafeExtension(filePath))
        {
            return new ToolExecutionResult(false,
                $"Only {string.Join(", ", AllowedExtensions)} files can be read or edited for safety.",
                null, false, "not_allowed");
        }

        var fullPath = ResolveSafePath(_gitOpsSettings.RepoPath, filePath);
        if (fullPath is null)
        {
            return new ToolExecutionResult(false,
                "That path resolves outside the repository root. Only files inside the repo can be accessed.",
                null, false, "path_traversal");
        }

        var isReadRequest = IsReadRequest(context.Message);
        if (isReadRequest)
        {
            return await ReadFileAsync(fullPath, filePath, cancellationToken);
        }

        var editContent = ExtractEditContent(context.Message);
        if (string.IsNullOrWhiteSpace(editContent))
        {
            if (!File.Exists(fullPath))
            {
                return new ToolExecutionResult(false, $"File not found: {filePath}. Check the path and try again.", null, false, "file_not_found");
            }

            var currentContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var preview = currentContent.Length > 800 ? currentContent[..800] + "\n...(truncated)" : currentContent;
            return new ToolExecutionResult(true,
                $"Current content of {filePath}:\n{preview}\n\nTo edit, tell me what change to make.",
                null, false, Payload: new { filePath, currentContent });
        }

        return await ProposeEditAsync(fullPath, filePath, editContent, context, cancellationToken);
    }

    private async Task<ToolExecutionResult> ReadFileAsync(string fullPath, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return new ToolExecutionResult(false, $"File not found: {filePath}.", null, false, "file_not_found");
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var preview = content.Length > 1000 ? content[..1000] + "\n...(truncated)" : content;
        return new ToolExecutionResult(true,
            $"{filePath} ({content.Length} chars):\n{preview}",
            null, false, Payload: new { filePath, content });
    }

    private async Task<ToolExecutionResult> ProposeEditAsync(
        string fullPath, string filePath, string newContent,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, newContent, cancellationToken);

            var branch = await _gitOps.EnsureAgentBranchAsync($"edit-{SanitizePath(filePath)}", cancellationToken);
            await _gitOps.CommitAsync($"agent: edit {filePath} requested by {context.AdminId}", cancellationToken);

            if (_gitOpsSettings.AllowPush)
            {
                await _gitOps.PushAsync(branch, cancellationToken);
                var prUrl = await _gitOps.CreatePrAsync(
                    branch,
                    $"[agent] Edit {filePath}",
                    $"Admin {context.AdminId} requested edit of {filePath}.\n\nChanges staged by agent for review.",
                    cancellationToken);
                return new ToolExecutionResult(true,
                    $"Edit staged for {filePath} and PR created: {prUrl}",
                    null, false);
            }

            return new ToolExecutionResult(true,
                $"Edit written to {filePath} and committed on branch {branch}. Push is disabled — merge manually when ready.",
                null, false);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false,
                $"Could not stage edit for {filePath}: {ex.Message}",
                null, false, "edit_failed");
        }
    }

    private static string? ResolveSafePath(string repoRoot, string relativePath)
    {
        var normalRoot = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(repoRoot, relativePath.TrimStart('/', '\\')));
        return candidate.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static bool IsSafeExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return AllowedExtensions.Contains(ext);
    }

    private static bool IsReadRequest(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("show") || lower.Contains("read") || lower.Contains("view")
            || lower.Contains("display") || lower.Contains("open") || lower.Contains("print")
            || lower.Contains("cat ") || lower.Contains("what's in") || lower.Contains("contents of");
    }

    private static string? ExtractFilePath(string message)
    {
        // Match common config file patterns
        var match = Regex.Match(message,
            @"(?:show|read|view|open|edit|modify|update|change|set)\s+(?:the\s+)?(?:file\s+)?(?<path>[a-zA-Z0-9_./-]+\.(?:cfg|json|txt|ini|env))",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups["path"].Value.Trim();

        // Quoted path
        var quoted = Regex.Match(message, "\"(?<path>[^\"]+\\.(?:cfg|json|txt|ini|env))\"", RegexOptions.IgnoreCase);
        if (quoted.Success)
            return quoted.Groups["path"].Value.Trim();

        return null;
    }

    private static string? ExtractEditContent(string message)
    {
        // Look for content in a code block
        var block = Regex.Match(message, @"```[a-z]*\n(?<content>[\s\S]+?)```", RegexOptions.IgnoreCase);
        if (block.Success)
            return block.Groups["content"].Value.Trim();

        // Look for "set to: ..." or "content: ..."
        var setTo = Regex.Match(message, @"(?:set to|replace with|content):?\s*(?<content>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (setTo.Success)
            return setTo.Groups["content"].Value.Trim().Trim('"', '\'');

        return null;
    }

    private static string SanitizePath(string path)
    {
        return new string(path.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    }
}
