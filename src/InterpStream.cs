using System;
using ABI_RC.Core.Player;
using UnityEngine;

namespace ParameterUP;

/// <summary>
/// One remote player's AAS snapshots, eased between rather than snapped. Used by both receive
/// paths. Stepping is worst on unmodded senders at 10 Hz, so they gain the most, though their
/// added latency is also the highest (~100 ms against ~33 ms).
/// </summary>
internal sealed class InterpStream
{
    private float[] _prev = Array.Empty<float>();
    private float[] _target = Array.Empty<float>();
    private float[] _scratch = Array.Empty<float>();
    private int[] _ints = Array.Empty<int>();
    private byte[] _bytes = Array.Empty<byte>();

    private float _targetTime;
    private float _interval;
    private bool _hasData;

    internal float LastArrival { get; private set; }

    internal InterpStream(float defaultInterval) => _interval = defaultInterval;

    internal void Push(float[] floats, int[] ints, byte[] bytes, float now)
    {
        // Start the next segment from what is currently on screen, so a late or dropped packet
        // eases on rather than snapping backwards.
        _prev = _hasData ? (float[])Sample(now).Clone() : floats;
        _target = floats;
        _ints = ints;
        _bytes = bytes;

        if (_hasData)
        {
            // Measure real spacing rather than trusting a nominal rate, since senders may be
            // frame-limited, stepped down, or idle. The wrong interval stutters or lags.
            var measured = now - LastArrival;
            if (measured > 0.001f && measured < 1f)
                _interval = Mathf.Lerp(_interval, measured, 0.2f);
        }

        _targetTime = now;
        LastArrival = now;
        _hasData = true;
    }

    private float[] Sample(float now)
    {
        if (!_hasData || _target.Length == 0) return _target;
        if (_prev.Length != _target.Length) return _target; // avatar changed; no sane blend

        var alpha = _interval > 0f ? Mathf.Clamp01((now - _targetTime) / _interval) : 1f;
        if (alpha >= 1f) return _target;

        if (_scratch.Length != _target.Length) _scratch = new float[_target.Length];
        for (int i = 0; i < _target.Length; i++)
            _scratch[i] = _prev[i] + (_target[i] - _prev[i]) * alpha;
        return _scratch;
    }

    /// <summary>Pushes one interpolated frame at the caller's frame rate.</summary>
    internal void ApplyTo(PuppetMaster? puppetMaster, float now)
    {
        if (!_hasData || puppetMaster == null) return;

        // Route through PuppetMaster, not the animator: it keeps the hidden-avatar gate and the
        // payload validity checks the stock receive path relies on.
        try { puppetMaster.ApplyAdvancedAvatarSettings(Sample(now), _ints, _bytes); }
        catch { /* avatar swapping mid-stream; the next packet lands on the new one */ }
    }
}
