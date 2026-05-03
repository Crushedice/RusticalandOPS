using System.Net;
using CoreRCON;
using Sentry;

namespace RustOpsAgent.Domains.Rust.Rcon;

internal sealed class RustRconClient : IRconClient
{
    private RCON? _rcon;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<string>? UnsolicitedMessage;

    public async Task ConnectAsync(Uri uri, string password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            var address = addresses.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            if (address is null)
            {
                throw new InvalidOperationException($"Could not resolve hostname '{uri.Host}' to an IPv4 address");
            }

            var endpoint = new IPEndPoint(address, uri.Port > 0 ? uri.Port : 28016);
            _rcon = new RCON(endpoint, password, (uint)endpoint.Port);

            await _rcon.ConnectAsync();
            RustOpsSentry.AddBreadcrumb($"RCON connected to {uri.Host}:{endpoint.Port}", "agent.rcon");
        }
        catch (Exception ex)
        {
            RustOpsSentry.CaptureException(
                ex,
                "Failed to connect to RCON server",
                "agent.rcon",
                extras: new Dictionary<string, object?>
                {
                    ["host"] = uri.Host,
                    ["port"] = uri.Port
                });
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_rcon is null)
        {
            throw new InvalidOperationException("RCON is not connected. Call ConnectAsync first.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _rcon.SendCommandAsync(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_rcon is not null)
            {
                _rcon.Dispose();
                _rcon = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
