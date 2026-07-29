using System;
using System.Collections.Generic;
using System.Reflection;
using BTKUILib;
using BTKUILib.UIObjects;
using BTKUILib.UIObjects.Components;
using UnityEngine;

namespace ParameterUP;

/// <summary>
/// Optional BTKUILib page of live network stats. Buttons act as read-only labels, refreshed
/// once a second, and the page is built on demand the first time it is switched on.
///
/// Hiding has to survive a menu regeneration: tabbing away and back rebuilds the menu from
/// BTKUILib's element tree and drops the flags, so visibility is reapplied on every regenerate.
/// </summary>
internal sealed class ParameterUPDebugUI
{
    private const string IconName = "ParameterUPMenu";
    private const int PeerRows = 8;

    private Page? _page;
    private bool _visible;
    private Button? _modeBtn;
    private Button? _outBtn;
    private Button? _inBtn;
    private Button? _peersBtn;
    private readonly List<Button> _rows = new();

    private float _lastRefresh;

    internal void Build()
    {
        RegisterIcon();

        var page = new Page("ParameterUP", "ParameterUPDebug", true, IconName)
        {
            MenuTitle = "ParameterUP Debug",
            MenuSubtitle = "Live fast path network stats"
        };
        _page = page;

        var summary = page.AddCategory("Summary");
        _modeBtn = summary.AddButton("Mode: n/a", "", "Broadcast when everyone matches, addressed otherwise");
        _outBtn = summary.AddButton("Out: n/a", "", "Packets/sec and bandwidth we are sending");
        _inBtn = summary.AddButton("In: n/a", "", "Packets/sec and bandwidth arriving from modded peers");
        _peersBtn = summary.AddButton("Peers: n/a", "", "Modded peers known, and how many we are streaming to");

        // A fixed pool, since rebuilding elements every second would flicker. Unused rows blank.
        var senders = page.AddCategory("Senders");
        for (int i = 0; i < PeerRows; i++)
            _rows.Add(senders.AddButton("n/a", "", "Per-peer rate, bandwidth and delivery"));

        QuickMenuAPI.OnMenuRegenerate += _ => Reapply();
        QuickMenuAPI.OnOpenedPage += OnOpenedPage;
    }

    /// <summary>Hides or restores the page, tab button included.</summary>
    internal void SetVisible(bool visible)
    {
        _visible = visible;

        // Stale numbers are worse than none if the page is ever forced back into view.
        if (!visible) Blank();

        Reapply();
    }

    /// <summary>
    /// Reasserts visibility after a menu rebuild. Hidden alone leaves the tab button behind on a
    /// root page, so HideTab goes with it.
    /// </summary>
    private void Reapply()
    {
        if (_page == null) return;

        try
        {
            _page.Hidden = !_visible;
            _page.HideTab = !_visible;
        }
        catch
        {
            // Menu mid-rebuild; the next regenerate calls us again.
        }

        // Force the next Tick to repaint rather than waiting out the refresh interval.
        _lastRefresh = 0f;
    }

    /// <summary>
    /// Say so outright when the page is reached while switched off, rather than presenting
    /// numbers that mean nothing.
    /// </summary>
    private void OnOpenedPage(string target, string previous)
    {
        if (_page == null || target != _page.ElementID) return;

        try
        {
            if (!ParameterUPMod.DebugPageEnabled)
                QuickMenuAPI.ShowNotice("ParameterUP",
                    "The debug page is switched off. Enable \"Show debug page\" in the ParameterUP " +
                    "section of MelonPreferences to use it.", null, "OK");
            else if (!ParameterUPMod.FastPathEnabled)
                QuickMenuAPI.ShowNotice("ParameterUP",
                    "ParameterUP is disabled, so these figures will stay at zero. Enable " +
                    "\"ParameterUP Enabled\" in MelonPreferences to start streaming.", null, "OK");
        }
        catch
        {
            // Notice system not ready; the page still works.
        }
    }

    private void Blank()
    {
        if (_modeBtn == null || _outBtn == null || _inBtn == null || _peersBtn == null) return;

        _modeBtn.ButtonText = "Mode: n/a";
        _outBtn.ButtonText = "Out: n/a";
        _inBtn.ButtonText = "In: n/a";
        _peersBtn.ButtonText = "Peers: n/a";
        foreach (var row in _rows) row.ButtonText = "n/a";
    }

    /// <summary>Removes the page outright. The toggle prefers Hidden, which is reversible.</summary>
    internal void Destroy()
    {
        try { _page?.Delete(); }
        catch { /* already gone */ }

        _page = null;
        _rows.Clear();
        _modeBtn = _outBtn = _inBtn = _peersBtn = null;
    }

    private static void RegisterIcon()
    {
        try
        {
            if (QuickMenuAPI.DoesIconExist("ParameterUP", IconName)) return;

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("ParameterUP.ParameterUPMenu.png");
            if (stream == null) return;

            QuickMenuAPI.PrepareIcon("ParameterUP", IconName, stream);
        }
        catch (Exception e)
        {
            // A missing icon is cosmetic; the page still works without it.
            ParameterUPMod.Log($"Could not register menu icon ({e.GetType().Name}).");
        }
    }

    internal void Tick()
    {
        if (Time.realtimeSinceStartup - _lastRefresh < 1f) return;
        _lastRefresh = Time.realtimeSinceStartup;

        if (_modeBtn == null || _outBtn == null || _inBtn == null || _peersBtn == null) return;

        _modeBtn.ButtonText = NetStats.Broadcasting
            ? "Mode: broadcast (1 copy)"
            : "Mode: addressed (1 copy per peer)";

        _outBtn.ButtonText = $"Out: {NetStats.TxRate}/s, {NetStats.TxKbps:0.0} KB/s";
        _inBtn.ButtonText = $"In: {NetStats.RxRate}/s, {NetStats.RxKbps:0.0} KB/s";

        var recipients = 0;
        foreach (var stats in NetStats.Peers.Values)
            if (stats.Recipient) recipients++;

        _peersBtn.ButtonText = $"Peers: {NetStats.Peers.Count} known, {recipients} receiving";

        var row = 0;
        foreach (var kvp in NetStats.Peers)
        {
            if (row >= _rows.Count) break;

            var stats = kvp.Value;
            var name = string.IsNullOrEmpty(stats.Name) ? kvp.Key : stats.Name;
            var range = stats.InRange ? "" : ", out of range";

            _rows[row].ButtonText =
                $"{name}: in {stats.RxRate}/s {stats.RxKbps:0.0}KB/s, " +
                $"out {stats.SendHz:0}Hz {stats.DeliveredPercent}%{range}";
            row++;
        }

        // Blank whatever the last refresh left behind.
        for (; row < _rows.Count; row++) _rows[row].ButtonText = "n/a";
    }
}
