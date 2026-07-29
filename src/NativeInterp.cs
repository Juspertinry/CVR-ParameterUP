using System.Collections.Generic;
using ABI_RC.Core.Player;
using UnityEngine;

namespace ParameterUP;

/// <summary>
/// Smooths the stock tag-10100 stream from players not running this mod, whose 10 Hz snapshots
/// are otherwise applied on arrival and step visibly.
/// </summary>
internal static class NativeInterp
{
    private const float StockInterval = 0.1f;   // stock send rate; refined from real arrivals
    private const float PruneAfter = 15f;

    private sealed class Entry
    {
        internal InterpStream Stream = new(StockInterval);
        internal float NextApply;
    }

    private static readonly Dictionary<string, Entry> Streams = new();

    internal static void OnNativePacket(string senderId, float[] floats, int[] ints, byte[] bytes, float now)
    {
        if (string.IsNullOrEmpty(senderId)) return;

        if (!Streams.TryGetValue(senderId, out var entry))
            entry = Streams[senderId] = new Entry { Stream = new InterpStream(StockInterval) };

        entry.Stream.Push(floats, ints, bytes, now);
    }

    internal static void Tick(float now)
    {
        // Smoothing off: the receive patch lets the stock apply run, so anything buffered
        // here is stale.
        if (!ParameterUPMod.SmoothingEnabled)
        {
            if (Streams.Count > 0) Streams.Clear();
            return;
        }

        if (Streams.Count == 0 || CVRPlayerManager.Instance == null) return;

        var rate = ParameterUPMod.UnmoddedInterpRateHz;

        List<string>? dead = null;
        foreach (var kvp in Streams)
        {
            var entry = kvp.Value;

            if (now - entry.Stream.LastArrival > PruneAfter)
            {
                (dead ??= new List<string>()).Add(kvp.Key);
                continue;
            }

            // On the fast path now. Stand down rather than drop the entry, which would lose
            // their interpolation state if that stream lapses.
            if (FastPath.IsDrivenByFastPath(kvp.Key)) continue;

            // 0 means every frame; anything else resamples on its own clock.
            if (rate > 0f)
            {
                if (now < entry.NextApply) continue;
                entry.NextApply = now + 1f / rate;
            }

            if (!CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(kvp.Key, out var player)) continue;
            entry.Stream.ApplyTo(player?.PuppetMaster, now);
        }

        if (dead == null) return;
        foreach (var id in dead) Streams.Remove(id);
    }

    internal static void Clear() => Streams.Clear();
}
