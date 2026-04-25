using rustmgrapi.Api.Models;

namespace rustmgrapi.Api.Services;

internal sealed class NetworkInspectionService
{
    private static readonly HashSet<string> IncludedInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "eth0", "wt1", "wg1"
    };

    private static Dictionary<string, (long Rx, long Tx, DateTime AtUtc)> _last = new(StringComparer.OrdinalIgnoreCase);

    public object CaptureSummary()
    {
        const string basePath = "/sys/class/net";
        var now = DateTime.UtcNow;
        var rows = new List<HostInterfaceCounter>();

        if (!Directory.Exists(basePath))
        {
            return new { capturedAtUtc = now, interfaces = rows };
        }

        foreach (var path in Directory.GetDirectories(basePath))
        {
            var name = Path.GetFileName(path);
            if (!IncludedInterfaces.Contains(name))
            {
                continue;
            }

            var statsDir = Path.Combine(path, "statistics");
            var rx = ReadLong(Path.Combine(statsDir, "rx_bytes"));
            var tx = ReadLong(Path.Combine(statsDir, "tx_bytes"));

            var row = new HostInterfaceCounter
            {
                Name = name,
                OperState = ReadText(Path.Combine(path, "operstate")),
                SpeedMbps = ReadInt(Path.Combine(path, "speed")),
                RxBytes = rx,
                TxBytes = tx
            };

            if (_last.TryGetValue(name, out var previous))
            {
                var elapsed = Math.Max(0.001, (now - previous.AtUtc).TotalSeconds);
                var rxRate = (rx - previous.Rx) / elapsed / 1024d / 1024d;
                var txRate = (tx - previous.Tx) / elapsed / 1024d / 1024d;
                row.RxRateMiBps = Math.Round(Math.Max(0, rxRate), 3);
                row.TxRateMiBps = Math.Round(Math.Max(0, txRate), 3);
                row.CombinedRateMbps = Math.Round((Math.Max(0, rxRate) + Math.Max(0, txRate)) * 8.388608d, 2);
            }

            rows.Add(row);
            _last[name] = (rx, tx, now);
        }

        return new
        {
            capturedAtUtc = now,
            interfaces = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static int? ReadInt(string path)
    {
        var text = ReadText(path);
        return int.TryParse(text, out var value) ? value : null;
    }

    private static long ReadLong(string path)
    {
        var text = ReadText(path);
        return long.TryParse(text, out var value) ? value : 0L;
    }
}
