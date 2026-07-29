using ABI_RC.Core.Networking.Jobs;
using ABI_RC.Core.Savior;
using ABI_RC.Systems.GameEventSystem;
using DarkRift;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ParameterUP.ParameterUPMod), "ParameterUP", "1.0.0", "Juspertinry")]
[assembly: MelonGame(null, "ChilloutVR")]

namespace ParameterUP;

/// <summary>
/// Raises the AAS parameter sync rate between players running this mod, changing nothing for
/// players who don't.
///
/// The stock 10 Hz cap is a hardcoded client interval, but the official server only forwards
/// ~15 msg/s on tag 10100 and drops the excess in bursts that look worse than stock. So that
/// channel is left alone and the high-rate stream rides ModNetwork instead (see FastPath).
/// </summary>
public sealed class ParameterUPMod : MelonMod
{
    private static MelonPreferences_Entry<bool>? _fastEnabled;
    private static MelonPreferences_Entry<float>? _fastHz;
    private static MelonPreferences_Entry<float>? _moddedInterpHz;
    private static MelonPreferences_Entry<float>? _unmoddedInterpHz;
    private static MelonPreferences_Entry<bool>? _smoothing;
    private static MelonPreferences_Entry<bool>? _debugPage;
    private static MelonLogger.Instance? _log;

    private static ParameterUPDebugUI? _debugUi;
    private static bool _debugUiFailed;
    private static bool _debugVisible;
    private static float _windowStart;

    internal static float FastRateHz => Mathf.Clamp(_fastHz?.Value ?? FastPath.MaxRateHz, 0f, FastPath.MaxRateHz);
    internal static bool FastPathEnabled => _fastEnabled?.Value ?? true;
    internal static bool SmoothingEnabled => _smoothing?.Value ?? true;

    /// <summary>Interpolation clock for modded senders. 0 means every frame.</summary>
    internal static float ModdedInterpRateHz => _moddedInterpHz?.Value ?? 0f;

    /// <summary>Interpolation clock for unmodded senders. 0 means every frame.</summary>
    internal static float UnmoddedInterpRateHz => _unmoddedInterpHz?.Value ?? 45f;

    internal static bool DebugPageEnabled => _debugPage?.Value ?? false;

    internal static void Log(string message) => _log?.Msg(message);

    public override void OnInitializeMelon()
    {
        _log = LoggerInstance;

        // Registration order is display order, so the master switch is created first.
        var category = MelonPreferences.CreateCategory("ParameterUP", "ParameterUP");

        _fastEnabled = category.CreateEntry("FastPathEnabled", true, "ParameterUP Enabled",
            "Off: neither send nor apply the high-rate stream, in either direction.");

        _fastHz = category.CreateEntry("FastRateHz", 30f, "Parameter send rate (Hz)",
            "Rate of the stream to other modded players. Capped at 30 because ModNetwork cannot " +
            "carry more. 0 disables sending.");

        _moddedInterpHz = category.CreateEntry("ModdedInterpolationRateHz", 0f,
            "Interpolate rate for modded senders",
            "How often smoothed values are written for players running this mod. 0 applies every " +
            "frame, which is smoothest and costs the most CPU.");

        _unmoddedInterpHz = category.CreateEntry("UnmoddedInterpolationRateHz", 45f,
            "Interpolate rate for unmodded senders",
            "Same, for players on the stock 10 Hz channel. 0 applies every frame.");

        _smoothing = category.CreateEntry("Smoothing", true, "Smooth parameter changes",
            "Off: apply each snapshot as it arrives, stepped, exactly like stock.");

        _debugPage = category.CreateEntry("DebugPage", false, "Show debug page",
            "Adds a BTKUILib page with live network stats: who is streaming to us, at what rate, " +
            "how much bandwidth, and what we are reporting back. Turning it off removes the page " +
            "and its menu button again.");

        // Corrected in place rather than silently ignored, so the config never claims a rate
        // that cannot happen. The write re-enters here and passes the guard.
        _fastHz.OnEntryValueChanged.Subscribe((_, value) =>
        {
            var clamped = Mathf.Clamp(value, 0f, FastPath.MaxRateHz);
            if (!Mathf.Approximately(clamped, value)) _fastHz.Value = clamped;
        });

        // Preferences are edited outside the mod, so side effects hang off the change event
        // rather than a setter.
        _fastEnabled.OnEntryValueChanged.Subscribe((_, enabled) =>
        {
            // Drop driven streams so senders revert to native rather than freezing on the
            // last snapshot.
            if (!enabled) FastPath.DropAllStreams();
        });

        _smoothing.OnEntryValueChanged.Subscribe((_, enabled) =>
        {
            if (!enabled) NativeInterp.Clear();
        });

        CVRGameEventSystem.Instance.OnDisconnected.AddListener(_ => FastPath.OnLeftInstance());
    }

    public override void OnUpdate()
    {
        var now = Time.realtimeSinceStartup;
        FastPath.Tick(now);
        NativeInterp.Tick(now);

        if (now - _windowStart >= 1f)
        {
            _windowStart = now;
            NetStats.Publish();
        }

        TickDebugPage();
    }

    /// <summary>
    /// Built the first time the preference is switched on, so BTKUILib is only touched when
    /// asked for. A missing library costs the page, not the mod.
    /// </summary>
    private static void TickDebugPage()
    {
        if (!DebugPageEnabled)
        {
            if (_debugUi == null || !_debugVisible) return;
            _debugVisible = false;
            _debugUi.SetVisible(false);
            return;
        }

        if (_debugUi == null)
        {
            if (_debugUiFailed) return;
            try
            {
                _debugUi = new ParameterUPDebugUI();
                _debugUi.Build();
                Log("Debug page registered.");
            }
            catch (System.Exception e)
            {
                _debugUi = null;
                _debugUiFailed = true;
                Log($"BTKUILib not available ({e.GetType().Name}); debug page disabled.");
                return;
            }
        }

        if (!_debugVisible)
        {
            _debugVisible = true;
            _debugUi.SetVisible(true);
        }

        _debugUi.Tick();
    }

    /// <summary>
    /// Skips the native apply for any sender whose fast stream is live, since their stale 10 Hz
    /// snapshots would fight it. Per-sender and lapses on timeout, so unmodded players are
    /// untouched and a peer that stops mid-session falls back instead of freezing.
    /// </summary>
    [HarmonyPatch(typeof(AdvancedSettingsUpdate), nameof(AdvancedSettingsUpdate.Apply))]
    private static class NativeReceivePatch
    {
        private static bool Prefix(Message message)
        {
            string senderId;
            float[] floats;
            int[] ints;
            byte[] bytes;

            try
            {
                // GetReader clones the buffer, so the stock parse is undisturbed on fallthrough.
                using var reader = message.GetReader();
                senderId = reader.ReadString();

                floats = new float[reader.ReadInt32()];
                for (int i = 0; i < floats.Length; i++) floats[i] = reader.ReadSingle();
                ints = new int[reader.ReadInt32()];
                for (int i = 0; i < ints.Length; i++) ints[i] = reader.ReadInt32();
                bytes = new byte[reader.ReadInt32()];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = reader.ReadByte();
            }
            catch
            {
                return true; // anything unexpected on the wire: let the stock path deal with it
            }

            if (FastPath.IsDrivenByFastPath(senderId)) return false;

            // Our own id never reaches a PuppetMaster; the stock method discards it too.
            if (senderId == MetaPort.Instance?.ownerId) return true;

            // Fall through rather than apply it here, so stock behaviour stays genuinely stock.
            if (!SmoothingEnabled) return true;

            NativeInterp.OnNativePacket(senderId, floats, ints, bytes, Time.realtimeSinceStartup);
            return false;
        }
    }
}
