using System.Collections.Generic;

namespace ParameterUP;

/// <summary>
/// Rolling one-second counters, fed by FastPath and read by the debug page. Live counters are
/// kept separate from published ones so a mid-second read never shows a partial figure.
/// </summary>
internal static class NetStats
{
    // 40 (IPv4+TCP) + 22 (ModNetwork id string, toAll flag, payload length) + 6 (DarkRift
    // tag/length). An estimate: if the socket coalesces, the real cost is lower.
    private const int WireOverheadPerPacket = 68;

    internal sealed class PeerStats
    {
        // Accumulating.
        internal int RxPackets;
        internal long RxBytes;

        // Published once a second.
        internal int RxRate;
        internal float RxKbps;

        // Set directly by FastPath as it makes decisions.
        internal bool InRange = true;
        internal bool Recipient;
        internal float SendHz;
        internal int DeliveredPercent = 100;
        internal string Name = "";
    }

    private static readonly Dictionary<string, PeerStats> PeerMap = new();

    internal static IReadOnlyDictionary<string, PeerStats> Peers => PeerMap;

    private static int _txPackets;
    private static long _txBytes;

    internal static int TxRate;
    internal static float TxKbps;
    internal static int RxRate;
    internal static float RxKbps;
    internal static bool Broadcasting;

    internal static PeerStats For(string userId)
    {
        if (!PeerMap.TryGetValue(userId, out var stats))
            stats = PeerMap[userId] = new PeerStats();
        return stats;
    }

    internal static void CountTx(int payloadBytes)
    {
        _txPackets++;
        _txBytes += payloadBytes;
    }

    internal static void CountRx(string userId, int payloadBytes)
    {
        var stats = For(userId);
        stats.RxPackets++;
        stats.RxBytes += payloadBytes;
    }

    internal static void Forget(string userId) => PeerMap.Remove(userId);

    internal static void Clear()
    {
        PeerMap.Clear();
        _txPackets = 0;
        _txBytes = 0;
        TxRate = 0;
        TxKbps = 0f;
        RxRate = 0;
        RxKbps = 0f;
    }

    private static float Kbps(int packets, long payloadBytes)
        => (payloadBytes + (long)packets * WireOverheadPerPacket) / 1024f;

    internal static void Publish()
    {
        TxRate = _txPackets;
        TxKbps = Kbps(_txPackets, _txBytes);
        _txPackets = 0;
        _txBytes = 0;

        var rxPackets = 0;
        long rxBytes = 0;

        foreach (var stats in PeerMap.Values)
        {
            stats.RxRate = stats.RxPackets;
            stats.RxKbps = Kbps(stats.RxPackets, stats.RxBytes);

            rxPackets += stats.RxPackets;
            rxBytes += stats.RxBytes;

            stats.RxPackets = 0;
            stats.RxBytes = 0;
        }

        RxRate = rxPackets;
        RxKbps = Kbps(rxPackets, rxBytes);
    }
}
