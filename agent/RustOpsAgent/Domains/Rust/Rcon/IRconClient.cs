namespace RustOpsAgent.Domains.Rust.Rcon;

internal interface IRconClient : IAsyncDisposable
{
    event Action<string>? UnsolicitedMessage;
    Task ConnectAsync(Uri uri, string password, CancellationToken cancellationToken);
    Task<string> SendCommandAsync(string command, CancellationToken cancellationToken);
}