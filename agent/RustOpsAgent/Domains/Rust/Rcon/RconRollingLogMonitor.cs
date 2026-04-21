using System.Collections.Concurrent;

namespace RustOpsAgent.Domains.Rust.Rcon;

internal sealed class RconRollingLogMonitor
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly int _maxLines;

    public RconRollingLogMonitor(int maxLines = 500)
    {
        _maxLines = Math.Max(50, maxLines);
    }

    public void Attach(IRconClient client)
    {
        client.UnsolicitedMessage += OnMessage;
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _lines.ToArray();
    }

    private void OnMessage(string line)
    {
        _lines.Enqueue($"{DateTime.UtcNow:O} {line}");
        while (_lines.Count > _maxLines && _lines.TryDequeue(out _))
        {
        }
    }
}