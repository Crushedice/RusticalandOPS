using System.Net;
using CoreRCON;

namespace rustmgrapi.Api.Services;

internal sealed class RconService
{
    private readonly RustManagerService _rust;

    public RconService(RustManagerService rust)
    {
        _rust = rust;
    }

    public async Task<object> ExecuteAsync(string server, string command)
    {
        var cfg = _rust.LoadConfig(server) ?? throw new InvalidOperationException($"Missing config for {server}.");

        if (cfg.RconPort <= 0 || string.IsNullOrWhiteSpace(cfg.RconPassword))
        {
            throw new InvalidOperationException($"RCON credentials missing in /opt/rust-manager/config/{server}.json.");
        }

        var host = "127.0.0.1";
        await using var rcon = new RustRcon(host, (uint)cfg.RconPort, cfg.RconPassword);
        await rcon.ConnectAsync();
        var reply = await rcon.SendAndReceiveAsync(command);

        return new
        {
            server,
            command,
            directReply = reply,
            transport = "rcon-web"
        };
    }

    private sealed class RustRcon : IAsyncDisposable
    {
        private readonly string _host;
        private readonly uint _port;
        private readonly string _password;
        private RCON? _rcon;

        public RustRcon(string host, uint port, string password)
        {
            _host = host;
            _port = port;
            _password = password;
        }

        public async Task ConnectAsync()
        {
            var addresses = await Dns.GetHostAddressesAsync(_host);
            var address = addresses.First(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            var endpoint = new IPEndPoint(address, (int)_port);
            _rcon = new RCON(endpoint, _password, _port);
            await _rcon.ConnectAsync();
        }

        public async Task<string> SendAndReceiveAsync(string command)
        {
            if (_rcon is null)
            {
                throw new InvalidOperationException("RCON not connected.");
            }

            return await _rcon.SendCommandAsync(command);
        }

        public ValueTask DisposeAsync()
        {
            _rcon?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
