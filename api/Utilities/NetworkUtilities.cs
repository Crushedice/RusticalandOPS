namespace RusticalandOPS.Api.Utilities;

using RusticalandOPS.Api.Models.Dashboard;

public static class NetworkUtilities
{
    public static List<HostInterfaceCounter> ReadInterfaceCounters()
    {
        const string sysClassNet = "/sys/class/net";
        if (!Directory.Exists(sysClassNet))
            return new List<HostInterfaceCounter>();

        return Directory.GetDirectories(sysClassNet)
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var statsDir = Path.Combine(path, "statistics");

                return new HostInterfaceCounter
                {
                    Name = name,
                    OperState = PathUtilities.SafeReadText(Path.Combine(path, "operstate")),
                    Mtu = PathUtilities.SafeReadInt(Path.Combine(path, "mtu")),
                    SpeedMbps = PathUtilities.SafeReadInt(Path.Combine(path, "speed")),
                    RxBytes = PathUtilities.SafeReadLong(Path.Combine(statsDir, "rx_bytes")),
                    TxBytes = PathUtilities.SafeReadLong(Path.Combine(statsDir, "tx_bytes")),
                    RxPackets = PathUtilities.SafeReadLong(Path.Combine(statsDir, "rx_packets")),
                    TxPackets = PathUtilities.SafeReadLong(Path.Combine(statsDir, "tx_packets")),
                    RxErrors = PathUtilities.SafeReadLong(Path.Combine(statsDir, "rx_errors")),
                    TxErrors = PathUtilities.SafeReadLong(Path.Combine(statsDir, "tx_errors")),
                    RxDropped = PathUtilities.SafeReadLong(Path.Combine(statsDir, "rx_dropped")),
                    TxDropped = PathUtilities.SafeReadLong(Path.Combine(statsDir, "tx_dropped"))
                };
            })
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static long GetDirSize(string path)
    {
        if (!Directory.Exists(path))
            return 0L;

        return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
            .AsParallel()
            .Sum(file =>
            {
                try
                {
                    return new FileInfo(file).Length;
                }
                catch
                {
                    return 0L;
                }
            });
    }

    public static int CountJsonFiles(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly).Length;
    }
}
