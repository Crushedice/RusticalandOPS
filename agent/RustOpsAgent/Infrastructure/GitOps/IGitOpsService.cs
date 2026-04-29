namespace RustOpsAgent.Infrastructure.GitOps;

internal interface IGitOpsService
{
    Task<string> EnsureAgentBranchAsync(string slug, CancellationToken cancellationToken);
    Task CommitAsync(string message, CancellationToken cancellationToken);
    Task PushAsync(string branchName, CancellationToken cancellationToken);
    Task<string> CreatePrAsync(string branchName, string title, string body, CancellationToken cancellationToken);
    Task CheckoutMainAsync(CancellationToken cancellationToken);
}