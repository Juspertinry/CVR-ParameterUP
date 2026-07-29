using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ABI_RC.Core;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using ABI_RC.Core.Util.AnimatorManager;
using ABI_RC.Systems.ModNetwork;
using HarmonyLib;
using UnityEngine;

namespace ParameterUP;

/// <summary>
/// The high-rate AAS parameter stream for modded peers, over ModNetwork. Runs alongside the
/// stock tag-10100 output rather than replacing it, so unmodded players are unaffected.
///
/// One broadcast serves everyone while all peers are in range and healthy. As soon as one
/// differs, sending switches to addressed so each peer gets its own rate and delta baseline.
/// Nobody is slowed to match somebody else.
/// </summary>
internal static class FastPath
{
    // ModNetwork writes this id into every message, so its length is per-packet overhead. Kept
    // short but still distinctive enough not to collide with another mod's channel.
    internal const string MessageId = "PUPaas";

    private const byte PacketFull = 1;
    private const byte PacketReport = 2;
    private const byte PacketPresence = 3;
    private const byte PacketDelta = 4;
    private const byte PacketFragment = 5;

    private const float MinRateHz = 10f;

    // Measured ceiling: ModNetwork is reliable TCP through CVR's server and stops keeping up.
    internal const float MaxRateHz = 30f;

    private const float RateStepHz = 5f;
    private const float LossThreshold = 0.9f;      // below this fraction delivered = unhealthy
    private const int UnhealthyReportsToStep = 2;  // consecutive, from the same peer
    private const float RecoveryDelay = 10f;       // healthy for this long before stepping back up
    private const float StreamTimeout = 0.5f;      // fast stream considered dead after this
    private const float PruneAfter = 5f;

    private const float PresenceInterval = 2f;
    private const float PresenceTimeout = 8f;
    private const float KeyframeInterval = 2f;

    // Past this nobody can tell 30 Hz from 10, so distant players go back on the stock channel.
    private const float MaxDistanceMeters = 15f;
    private const float MaxDistanceSqr = MaxDistanceMeters * MaxDistanceMeters;

    // Held-back changes do not advance the baseline, so jitter costs nothing but slow drift
    // still arrives once it adds up to this much.
    private const float FloatDeadband = 0.002f;

    // Floats pick the smallest encoding that holds them: snorm8 when it round-trips inside the
    // deadband, snorm16 otherwise, raw float32 for anything outside -1..1. The tier travels in
    // the top two bits of the index, so no parameter is ever clamped.
    private const float Quant16 = 32767f;
    private const float Quant8 = 127f;
    private const int TierSnorm16 = 0;
    private const int TierSnorm8 = 1;
    private const int TierRaw = 2;
    private const int TierShift = 14;
    private const int MaxIndex = 0x3FFF;

    // A ModNetwork message caps out around 1100 bytes; longer payloads are fragmented.
    private const int MaxPayloadBytes = 1024;
    private const int ChunkBytes = 900;
    private const int MaxFragments = 32;

    private static bool _subscribed;
    private static int _fragMsgId;
    private static float _nextPresence;

    // Reflection into the animator's outbound caches. Deliberately not GetAASParameterSyncData():
    // that clears AASParameterChangedSinceLastSync, which would starve the native 10 Hz send.
    private static readonly FieldInfo CacheFloat = AccessTools.Field(typeof(AvatarAnimatorManager), "_aasOutboundCacheFloat");
    private static readonly FieldInfo CacheInt = AccessTools.Field(typeof(AvatarAnimatorManager), "_aasOutboundCacheInt");
    private static readonly FieldInfo CacheBool = AccessTools.Field(typeof(AvatarAnimatorManager), "_aasOutboundCacheBool");

    /// <summary>Per-peer outbound state. Only used while sending addressed.</summary>
    private sealed class Outbound
    {
        internal float LastSeen;
        internal float NextSend;
        internal float AdaptedHz = MaxRateHz;
        internal float LastUnhealthy;
        internal int Strikes;

        internal int Seq;
        internal float NextKeyframe;
        internal bool ForceKeyframe = true;

        internal float[]? Floats;
        internal int[]? Ints;
        internal byte[]? Bools;
    }

    private static readonly Dictionary<string, Outbound> Peers = new();

    // One baseline and sequence space for broadcast mode, since the packet has to mean the
    // same thing to everyone receiving it.
    private static bool _broadcasting;
    private static float[]? _sharedFloats;
    private static int[]? _sharedInts;
    private static byte[]? _sharedBools;
    private static int _sharedSeq;
    private static float _nextSharedSend;
    private static float _nextSharedKeyframe;
    private static bool _forceSharedKeyframe = true;

    private static readonly List<string> Recipients = new();

    private sealed class Incoming
    {
        internal readonly InterpStream Stream = new(1f / MaxRateHz);
        internal int FirstSeq = -1;
        internal int LastSeq;
        internal int Received;
        internal float NextApply;
        internal float NextReport;
        internal bool InRange = true;

        // Last full state, so a delta has something to apply against.
        internal float[]? Floats;
        internal int[]? Ints;
        internal byte[]? Bools;

        // The channel is ordered, so only ever one message is mid-reassembly.
        internal int FragId = -1;
        internal byte[]?[] FragChunks = Array.Empty<byte[]?>();
        internal int FragHave;
    }

    private static readonly Dictionary<string, Incoming> Streams = new();

    /// <summary>
    /// True while a sender's stream is live and near enough to be worth it, which is what
    /// suppresses their native applies. Going false hands them back to the stock channel.
    /// </summary>
    internal static bool IsDrivenByFastPath(string senderId)
    {
        if (!ParameterUPMod.FastPathEnabled) return false;
        if (string.IsNullOrEmpty(senderId)) return false;
        return Streams.TryGetValue(senderId, out var s)
               && s.InRange
               && Time.realtimeSinceStartup - s.Stream.LastArrival < StreamTimeout;
    }

    internal static void Tick(float now)
    {
        // Disabled: send nothing, drive nothing. Driven senders fall back to native once their
        // entry ages out of Streams.
        if (!ParameterUPMod.FastPathEnabled) return;

        if (!EnsureSubscribed()) return;

        AnnouncePresence(now);
        PrunePeers(now);
        UpdateRanges();

        SendData(now);
        SendReports(now);
        ApplyInterpolated(now);
        PruneDeadStreams(now);
    }

    private static bool EnsureSubscribed()
    {
        if (_subscribed) return true;
        try
        {
            if (!ModNetworkManager.IsSubscribed(MessageId))
                ModNetworkManager.Subscribe(MessageId, OnMessage);
            _subscribed = true;
            return true;
        }
        catch
        {
            return false; // not in an online instance yet
        }
    }

    /// <summary>Forgets every inbound stream, so native applies resume on the next packet.</summary>
    internal static void DropAllStreams() => Streams.Clear();

    internal static void OnLeftInstance()
    {
        Peers.Clear();
        Streams.Clear();
        NativeInterp.Clear();
        NetStats.Clear();
        _subscribed = false;
        InvalidateShared();
    }

    private static void InvalidateShared()
    {
        _sharedFloats = null;
        _sharedInts = null;
        _sharedBools = null;
        _forceSharedKeyframe = true;
    }

    // Announced rather than inferred from traffic, since an idle avatar sends nothing at all.
    private static void AnnouncePresence(float now)
    {
        if (now < _nextPresence) return;
        _nextPresence = now + PresenceInterval;

        Broadcast(new[] { PacketPresence });
    }

    private static void PrunePeers(float now)
    {
        if (Peers.Count == 0) return;

        List<string>? dead = null;
        foreach (var kvp in Peers)
            if (now - kvp.Value.LastSeen > PresenceTimeout)
                (dead ??= new List<string>()).Add(kvp.Key);

        if (dead == null) return;
        foreach (var id in dead)
        {
            Peers.Remove(id);
            NetStats.Forget(id);
        }
    }

    /// <summary>
    /// Cached once a frame rather than computed per packet, since the receive patch consults
    /// it on every arrival.
    /// </summary>
    private static void UpdateRanges()
    {
        if (Streams.Count == 0) return;

        foreach (var kvp in Streams)
        {
            kvp.Value.InRange = !TryGetDistanceSqr(kvp.Key, out var d) || d <= MaxDistanceSqr;
            NetStats.For(kvp.Key).InRange = kvp.Value.InRange;
        }
    }

    /// <summary>Display name for the debug page, falling back to the raw id.</summary>
    private static string NameOf(string userId)
    {
        try
        {
            if (CVRPlayerManager.Instance != null
                && CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(userId, out var player)
                && !string.IsNullOrEmpty(player?.Username))
                return player!.Username;
        }
        catch
        {
            // Player table churning mid-join; the id is a fine fallback.
        }

        return userId;
    }

    /// <summary>
    /// False when either end has no transform yet. Callers treat that as in range, since going
    /// dark on someone is worse than a little wasted traffic.
    /// </summary>
    private static bool TryGetDistanceSqr(string userId, out float distanceSqr)
    {
        distanceSqr = 0f;

        var local = PlayerSetup.Instance;
        if (local == null) return false;
        if (CVRPlayerManager.Instance == null) return false;
        if (!CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(userId, out var player)) return false;

        var puppetMaster = player?.PuppetMaster;
        if (puppetMaster == null) return false;

        distanceSqr = (puppetMaster.transform.position - local.transform.position).sqrMagnitude;
        return true;
    }

    private static void SendData(float now)
    {
        if (Peers.Count == 0) return;

        var userRate = ParameterUPMod.FastRateHz;
        if (userRate <= 0f) return;

        var setup = PlayerSetup.Instance;
        var animatorManager = setup == null ? null : setup.AnimatorManager;
        if (animatorManager == null) return;

        if (CacheFloat?.GetValue(animatorManager) is not List<float> floatList) return;
        if (CacheInt?.GetValue(animatorManager) is not List<int> intList) return;
        if (CacheBool?.GetValue(animatorManager) is not List<bool> boolList) return;

        float[] floats;
        int[] ints;
        byte[] bools;
        try
        {
            floats = floatList.ToArray();
            ints = intList.ToArray();
            bools = CVRTools.ConvertBoolArrayToByteArray(boolList.ToArray());
        }
        catch
        {
            return; // animator swapping mid-read; the next tick tries again
        }

        // An index has to fit in 15 bits, with the escape flag taking the 16th.
        if (floats.Length > MaxIndex || ints.Length > MaxIndex) return;

        Recipients.Clear();
        var sharedRate = 0f;
        var uniform = true;
        var everyoneInRange = true;

        foreach (var kvp in Peers)
        {
            var peerStats = NetStats.For(kvp.Key);

            if (TryGetDistanceSqr(kvp.Key, out var d) && d > MaxDistanceSqr)
            {
                // Left on the stock channel. Their baseline goes stale meanwhile, so coming
                // back into range needs a keyframe.
                kvp.Value.ForceKeyframe = true;
                everyoneInRange = false;
                peerStats.Recipient = false;
                peerStats.SendHz = 0f;
                continue;
            }

            Recipients.Add(kvp.Key);

            var rate = Mathf.Min(userRate, kvp.Value.AdaptedHz);
            peerStats.Recipient = true;
            peerStats.SendHz = rate;
            peerStats.Name = NameOf(kvp.Key);
            if (sharedRate == 0f) sharedRate = rate;
            else if (!Mathf.Approximately(rate, sharedRate)) uniform = false;
        }

        if (Recipients.Count == 0) return;

        // A broadcast reaches every modded player whether or not they are a recipient, so it
        // only pays when everyone is one. Otherwise the excluded download and discard it.
        var broadcast = uniform && everyoneInRange && Recipients.Count > 1 && sharedRate > 0f;
        NetStats.Broadcasting = broadcast;

        if (broadcast != _broadcasting)
        {
            // The shared and per-peer baselines describe different histories, so neither
            // carries across a mode change. Both resync with a keyframe.
            _broadcasting = broadcast;
            InvalidateShared();
            foreach (var peer in Peers.Values) peer.ForceKeyframe = true;
        }

        if (broadcast) SendShared(now, sharedRate, floats, ints, bools);
        else SendAddressed(now, userRate, floats, ints, bools);
    }

    private static void SendShared(float now, float rate, float[] floats, int[] ints, byte[] bools)
    {
        if (now < _nextSharedSend) return;
        _nextSharedSend = now + 1f / rate;

        var keyframe = _forceSharedKeyframe
                       || now >= _nextSharedKeyframe
                       || _sharedFloats == null || _sharedInts == null || _sharedBools == null
                       || _sharedFloats.Length != floats.Length
                       || _sharedInts.Length != ints.Length
                       || _sharedBools.Length != bools.Length;

        byte[]? payload;
        float[] newFloats = floats;
        int[] newInts = ints;
        byte[] newBools = bools;

        try
        {
            if (keyframe)
            {
                payload = EncodeFull(_sharedSeq++, floats, ints, bools);
            }
            else
            {
                payload = EncodeDelta(_sharedSeq, _sharedFloats!, _sharedInts!, _sharedBools!,
                                      floats, ints, bools, out newFloats, out newInts, out newBools);
                if (payload != null) _sharedSeq++;
            }
        }
        catch
        {
            return;
        }

        if (payload == null) return; // nothing moved, so an idle avatar costs nothing

        if (keyframe)
        {
            _nextSharedKeyframe = now + KeyframeInterval;
            _forceSharedKeyframe = false;
        }

        _sharedFloats = newFloats;
        _sharedInts = newInts;
        _sharedBools = newBools;

        SendPayload(null, payload);
    }

    private static void SendAddressed(float now, float userRate, float[] floats, int[] ints, byte[] bools)
    {
        foreach (var userId in Recipients)
        {
            if (!Peers.TryGetValue(userId, out var peer)) continue;

            var rate = Mathf.Min(userRate, peer.AdaptedHz);
            if (rate <= 0f) continue;
            if (now < peer.NextSend) continue;
            peer.NextSend = now + 1f / rate;

            Recover(peer, now);

            var keyframe = peer.ForceKeyframe
                           || now >= peer.NextKeyframe
                           || peer.Floats == null || peer.Ints == null || peer.Bools == null
                           || peer.Floats.Length != floats.Length
                           || peer.Ints.Length != ints.Length
                           || peer.Bools.Length != bools.Length;

            byte[]? payload;
            float[] newFloats = floats;
            int[] newInts = ints;
            byte[] newBools = bools;

            try
            {
                if (keyframe)
                {
                    payload = EncodeFull(peer.Seq++, floats, ints, bools);
                }
                else
                {
                    payload = EncodeDelta(peer.Seq, peer.Floats!, peer.Ints!, peer.Bools!,
                                          floats, ints, bools, out newFloats, out newInts, out newBools);
                    if (payload != null) peer.Seq++;
                }
            }
            catch
            {
                continue;
            }

            if (payload == null) continue;

            if (keyframe)
            {
                peer.NextKeyframe = now + KeyframeInterval;
                peer.ForceKeyframe = false;
            }

            peer.Floats = newFloats;
            peer.Ints = newInts;
            peer.Bools = newBools;

            SendPayload(userId, payload);
        }
    }

    /// <summary>
    /// Cheapest encoding that reproduces this value. snorm8 is only chosen when its error is
    /// under the deadband, which is already the threshold below which a change is not sent.
    /// </summary>
    private static int TierOf(float value)
    {
        if (value < -1f || value > 1f || float.IsNaN(value)) return TierRaw;

        var quantised = Mathf.RoundToInt(value * Quant8) / Quant8;
        return Mathf.Abs(quantised - value) <= FloatDeadband ? TierSnorm8 : TierSnorm16;
    }

    private static void WriteFloat(BinaryWriter w, float value, int tier)
    {
        switch (tier)
        {
            case TierRaw: w.Write(value); break;
            case TierSnorm8: w.Write((sbyte)Mathf.RoundToInt(value * Quant8)); break;
            default: w.Write((short)Mathf.RoundToInt(value * Quant16)); break;
        }
    }

    private static int SizeOf(int tier) => tier == TierRaw ? 4 : tier == TierSnorm8 ? 1 : 2;

    private static float ReadFloat(BinaryReader r, int tier) => tier switch
    {
        TierRaw => r.ReadSingle(),
        TierSnorm8 => r.ReadSByte() / Quant8,
        _ => r.ReadInt16() / Quant16
    };

    /// <summary>
    /// Every parameter, preceded by two tier bits each. Cheaper than repeating an index per
    /// value, and most parameters land in one or two bytes.
    /// </summary>
    private static byte[] EncodeFull(int seq, float[] floats, int[] ints, byte[] bools)
    {
        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms);
        w.Write(PacketFull);
        w.Write(seq);

        w.Write((ushort)floats.Length);
        var tiers = new byte[(floats.Length * 2 + 7) / 8];
        for (int i = 0; i < floats.Length; i++)
            tiers[i >> 2] |= (byte)(TierOf(floats[i]) << ((i & 3) * 2));
        w.Write(tiers);

        for (int i = 0; i < floats.Length; i++)
            WriteFloat(w, floats[i], (tiers[i >> 2] >> ((i & 3) * 2)) & 3);

        w.Write((ushort)ints.Length);
        foreach (var i in ints) w.Write(i);

        w.Write((ushort)bools.Length);
        w.Write(bools);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Changed indices only, against what this recipient last received. Null when nothing
    /// moved. The new baseline reflects exactly what was sent, never what was held back.
    /// </summary>
    private static byte[]? EncodeDelta(int seq, float[] baseFloats, int[] baseInts, byte[] baseBools,
                                       float[] floats, int[] ints, byte[] bools,
                                       out float[] newFloats, out int[] newInts, out byte[] newBools)
    {
        newFloats = (float[])baseFloats.Clone();
        newInts = (int[])baseInts.Clone();
        newBools = baseBools;

        var changedFloats = new List<int>();
        for (int i = 0; i < floats.Length; i++)
        {
            var difference = floats[i] - baseFloats[i];
            if (difference < 0f) difference = -difference;
            if (difference <= FloatDeadband) continue;

            changedFloats.Add(i);
            newFloats[i] = floats[i];
        }

        var changedInts = new List<int>();
        for (int i = 0; i < ints.Length; i++)
        {
            if (ints[i] == baseInts[i]) continue;
            changedInts.Add(i);
            newInts[i] = ints[i];
        }

        var boolsChanged = false;
        for (int i = 0; i < bools.Length; i++)
            if (bools[i] != baseBools[i]) { boolsChanged = true; break; }
        if (boolsChanged) newBools = bools;

        if (changedFloats.Count == 0 && changedInts.Count == 0 && !boolsChanged) return null;

        using var ms = new MemoryStream(128);
        using var w = new BinaryWriter(ms);
        w.Write(PacketDelta);
        w.Write(seq);

        w.Write((ushort)changedFloats.Count);
        foreach (var i in changedFloats)
        {
            var tier = TierOf(floats[i]);
            w.Write((ushort)(i | (tier << TierShift)));
            WriteFloat(w, floats[i], tier);
        }

        w.Write((ushort)changedInts.Count);
        foreach (var i in changedInts) { w.Write((ushort)i); w.Write(ints[i]); }

        // The packed bool block is a few bytes, so it goes whole rather than paying two bytes
        // of index per changed bit.
        w.Write((byte)(boolsChanged ? 1 : 0));
        if (boolsChanged)
        {
            w.Write((ushort)bools.Length);
            w.Write(bools);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Fragments anything too large for one ModNetwork message; a null recipient broadcasts.
    /// The channel is ordered, so reassembly only has to count chunks.
    /// </summary>
    private static void SendPayload(string? userId, byte[] payload)
    {
        if (payload.Length <= MaxPayloadBytes)
        {
            Emit(userId, payload);
            return;
        }

        var count = (payload.Length + ChunkBytes - 1) / ChunkBytes;
        if (count > MaxFragments) return; // implausibly large; the native path still covers us

        var id = _fragMsgId++;
        for (int i = 0; i < count; i++)
        {
            var offset = i * ChunkBytes;
            var size = Mathf.Min(ChunkBytes, payload.Length - offset);

            var frame = new byte[7 + size];
            frame[0] = PacketFragment;
            frame[1] = (byte)id;
            frame[2] = (byte)(id >> 8);
            frame[3] = (byte)(id >> 16);
            frame[4] = (byte)(id >> 24);
            frame[5] = (byte)i;
            frame[6] = (byte)count;
            Buffer.BlockCopy(payload, offset, frame, 7, size);

            Emit(userId, frame);
        }
    }

    private static void Emit(string? userId, byte[] payload)
    {
        try
        {
            using var msg = userId == null
                ? new ModNetworkMessage(MessageId)
                : new ModNetworkMessage(MessageId, userId);
            msg.Write(payload);
            msg.Send();
            NetStats.CountTx(payload.Length);
        }
        catch
        {
            _subscribed = false;
        }
    }

    private static void Broadcast(byte[] payload) => Emit(null, payload);

    private static void OnMessage(ModNetworkMessage msg)
    {
        try
        {
            msg.Read(out byte[] payload);
            if (payload == null || payload.Length < 1) return;

            if (!ParameterUPMod.FastPathEnabled) return;

            var sender = msg.Sender;
            if (string.IsNullOrEmpty(sender)) return;
            if (sender == MetaPort.Instance?.ownerId) return; // our own broadcast, bounced back

            NetStats.CountRx(sender, payload.Length);

            switch (payload[0])
            {
                case PacketReport:
                    OnReport(sender, payload);
                    break;

                case PacketPresence:
                    OnPresence(sender);
                    break;

                case PacketFragment:
                    OnFragment(sender, payload);
                    break;

                default:
                    Dispatch(sender, payload);
                    break;
            }
        }
        catch
        {
            // A malformed packet from one peer must not take the channel down.
        }
    }

    private static void OnPresence(string sender)
    {
        if (!Peers.TryGetValue(sender, out var peer))
        {
            // A new peer has no baseline, so a broadcast shared baseline is stale for everyone.
            peer = Peers[sender] = new Outbound();
            if (_broadcasting) InvalidateShared();
        }

        peer.LastSeen = Time.realtimeSinceStartup;
    }

    private static void Dispatch(string sender, byte[] payload)
    {
        switch (payload[0])
        {
            case PacketFull:
                OnFull(sender, payload);
                break;
            case PacketDelta:
                OnDelta(sender, payload);
                break;
        }
    }

    private static void OnFragment(string sender, byte[] frame)
    {
        if (frame.Length < 8) return;

        var id = frame[1] | (frame[2] << 8) | (frame[3] << 16) | (frame[4] << 24);
        int index = frame[5];
        int count = frame[6];
        if (count == 0 || count > MaxFragments || index >= count) return;

        var stream = GetOrCreate(sender);

        if (stream.FragId != id)
        {
            stream.FragId = id;
            stream.FragChunks = new byte[]?[count];
            stream.FragHave = 0;
        }

        if (stream.FragChunks.Length != count) return;
        if (stream.FragChunks[index] != null) return;

        var chunk = new byte[frame.Length - 7];
        Buffer.BlockCopy(frame, 7, chunk, 0, chunk.Length);
        stream.FragChunks[index] = chunk;
        stream.FragHave++;

        if (stream.FragHave != count) return;

        var total = 0;
        foreach (var c in stream.FragChunks) total += c!.Length;

        var payload = new byte[total];
        var offset = 0;
        foreach (var c in stream.FragChunks)
        {
            Buffer.BlockCopy(c!, 0, payload, offset, c!.Length);
            offset += c.Length;
        }

        stream.FragId = -1;
        stream.FragChunks = Array.Empty<byte[]?>();
        stream.FragHave = 0;

        if (payload.Length > 0) Dispatch(sender, payload);
    }

    private static Incoming GetOrCreate(string sender)
    {
        if (!Streams.TryGetValue(sender, out var stream))
            stream = Streams[sender] = new Incoming();
        return stream;
    }

    private static void OnFull(string sender, byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms);
        r.ReadByte();
        var seq = r.ReadInt32();

        int lf = r.ReadUInt16();
        var tierBytes = (lf * 2 + 7) / 8;
        if (tierBytes > ms.Length - ms.Position) return;
        var tiers = r.ReadBytes(tierBytes);

        var floats = new float[lf];
        for (int i = 0; i < lf; i++)
        {
            var tier = (tiers[i >> 2] >> ((i & 3) * 2)) & 3;
            if (SizeOf(tier) > ms.Length - ms.Position) return;
            floats[i] = ReadFloat(r, tier);
        }

        int li = r.ReadUInt16();
        if ((long)li * 4 > ms.Length - ms.Position) return;
        var ints = new int[li];
        for (int i = 0; i < li; i++) ints[i] = r.ReadInt32();

        int lb = r.ReadUInt16();
        if (lb > ms.Length - ms.Position) return;
        var bools = r.ReadBytes(lb);

        Accept(sender, seq, floats, ints, bools);
    }

    private static void OnDelta(string sender, byte[] payload)
    {
        if (!Streams.TryGetValue(sender, out var stream)) return;
        if (stream.Floats == null || stream.Ints == null || stream.Bools == null) return; // no keyframe yet

        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms);
        r.ReadByte();
        var seq = r.ReadInt32();

        var floats = (float[])stream.Floats.Clone();
        var ints = (int[])stream.Ints.Clone();
        var bools = stream.Bools;

        int nf = r.ReadUInt16();
        for (int n = 0; n < nf; n++)
        {
            if (2 > ms.Length - ms.Position) return;
            int packed = r.ReadUInt16();
            var tier = packed >> TierShift;
            var index = packed & MaxIndex;

            if (SizeOf(tier) > ms.Length - ms.Position) return;
            var value = ReadFloat(r, tier);
            if (index < floats.Length) floats[index] = value;
        }

        int ni = r.ReadUInt16();
        if ((long)ni * 6 > ms.Length - ms.Position) return;
        for (int n = 0; n < ni; n++)
        {
            int index = r.ReadUInt16();
            var value = r.ReadInt32();
            if (index < ints.Length) ints[index] = value;
        }

        if (ms.Position >= ms.Length) return;
        if (r.ReadByte() != 0)
        {
            int lb = r.ReadUInt16();
            if (lb > ms.Length - ms.Position) return;
            bools = r.ReadBytes(lb);
        }

        Accept(sender, seq, floats, ints, bools);
    }

    private static void Accept(string sender, int seq, float[] floats, int[] ints, byte[] bools)
    {
        var stream = GetOrCreate(sender);
        if (stream.Received == 0 && stream.FirstSeq < 0) stream.FirstSeq = seq;

        // Out-of-order arrivals would drag the interpolation backwards; the newest snapshot is
        // the only one worth showing.
        if (stream.Received > 0 && seq <= stream.LastSeq) return;

        stream.Floats = floats;
        stream.Ints = ints;
        stream.Bools = bools;

        stream.Stream.Push(floats, ints, bools, Time.realtimeSinceStartup);
        stream.LastSeq = seq;
        stream.Received++;

        // Smoothing off: ApplyInterpolated stands down, so this is the only apply.
        if (!ParameterUPMod.SmoothingEnabled) ApplyNow(sender, floats, ints, bools);
    }

    private static void ApplyNow(string senderId, float[] floats, int[] ints, byte[] bytes)
    {
        if (CVRPlayerManager.Instance == null) return;
        if (!CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(senderId, out var player)) return;

        try { player?.PuppetMaster?.ApplyAdvancedAvatarSettings(floats, ints, bytes); }
        catch { /* avatar swapping mid-stream; the next packet lands on the new one */ }
    }

    /// <summary>
    /// Resampled independently of the wire rate, which is what turns 30 Hz of packets into
    /// continuous motion.
    /// </summary>
    private static void ApplyInterpolated(float now)
    {
        if (!ParameterUPMod.SmoothingEnabled) return;
        if (Streams.Count == 0 || CVRPlayerManager.Instance == null) return;

        var rate = ParameterUPMod.ModdedInterpRateHz;

        foreach (var kvp in Streams)
        {
            var stream = kvp.Value;
            if (stream.Received == 0) continue;
            if (!stream.InRange) continue;                                 // stock channel has them
            if (now - stream.Stream.LastArrival > StreamTimeout) continue; // native takes over

            // 0 means every frame; anything else resamples on its own clock.
            if (rate > 0f)
            {
                if (now < stream.NextApply) continue;
                stream.NextApply = now + 1f / rate;
            }

            if (!CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(kvp.Key, out var player)) continue;
            stream.Stream.ApplyTo(player?.PuppetMaster, now);
        }
    }

    // Report the delivered fraction back to each sender. The decision to slow down is theirs.
    private static void SendReports(float now)
    {
        foreach (var kvp in Streams)
        {
            var stream = kvp.Value;
            if (stream.Received <= 0) continue;
            if (now < stream.NextReport) continue;
            stream.NextReport = now + 1f;

            var expected = stream.LastSeq - stream.FirstSeq + 1;
            var delivered = expected > 0 ? Mathf.Clamp01((float)stream.Received / expected) : 1f;

            NetStats.For(kvp.Key).DeliveredPercent = Mathf.RoundToInt(delivered * 100f);

            try
            {
                using var msg = new ModNetworkMessage(MessageId, kvp.Key);
                using var ms = new MemoryStream(4);
                using var w = new BinaryWriter(ms);
                w.Write(PacketReport);
                w.Write((byte)Mathf.RoundToInt(delivered * 100f));
                w.Flush();
                msg.Write(ms.ToArray());
                msg.Send();
            }
            catch
            {
                // Peer left mid-window; their stream ages out on its own.
            }

            // Next window starts after this one's last packet. Starting *at* it counts the
            // boundary packet twice and reports a permanent ~2% phantom loss.
            stream.FirstSeq = stream.LastSeq + 1;
            stream.Received = 0;
        }
    }

    /// <summary>
    /// Steps down the complaining peer alone, after two consecutive complaints so one unlucky
    /// window does not count. Their differing rate is what drops the room out of broadcast.
    /// </summary>
    private static void OnReport(string sender, byte[] payload)
    {
        if (payload.Length < 2) return;
        if (!Peers.TryGetValue(sender, out var peer)) return;

        var deliveredPercent = payload[1];

        if (deliveredPercent >= LossThreshold * 100f)
        {
            peer.Strikes = 0;
            return;
        }

        peer.LastUnhealthy = Time.realtimeSinceStartup;

        if (++peer.Strikes < UnhealthyReportsToStep) return;
        peer.Strikes = 0;

        if (peer.AdaptedHz <= MinRateHz) return; // already at the floor for them

        var previous = peer.AdaptedHz;
        peer.AdaptedHz = Mathf.Max(MinRateHz, peer.AdaptedHz - RateStepHz);
        ParameterUPMod.Log($"{sender} received only {deliveredPercent}% of their stream; " +
                           $"stepping them {previous:F0} -> {peer.AdaptedHz:F0} Hz.");
    }

    /// <summary>
    /// Climbs a peer back toward the ceiling once they stop complaining, so a brief bad patch
    /// does not pin them down for the session.
    /// </summary>
    private static void Recover(Outbound peer, float now)
    {
        if (peer.AdaptedHz >= MaxRateHz) return;
        if (now - peer.LastUnhealthy < RecoveryDelay) return;

        peer.LastUnhealthy = now;
        peer.AdaptedHz = Mathf.Min(MaxRateHz, peer.AdaptedHz + RateStepHz);
    }

    private static void PruneDeadStreams(float now)
    {
        if (Streams.Count == 0) return;

        List<string>? dead = null;
        foreach (var kvp in Streams)
            if (now - kvp.Value.Stream.LastArrival > PruneAfter)
                (dead ??= new List<string>()).Add(kvp.Key);

        if (dead == null) return;
        foreach (var id in dead) Streams.Remove(id);
    }
}
