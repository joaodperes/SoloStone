using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Native Collection status the offline client otherwise loses: the seen ("NEW") state of each card and the dust balance.
internal static class NativeCollectionStatus
{
    private static ManualLogSource log;
    private static FieldInfo loggedIn;
    // Currency the native bottom bar is up for: DUST in the Collection, GOLD on the HUB (and its Shop), NONE = hidden.
    private static CurrencyType barCurrency = CurrencyType.NONE;
    // The bar is also up in a match, with no currency frame, so retail's in-match gear / menu button exists (owner 2026-10-28).
    private static bool barShown;
    private static int bindAttempts;
    private static bool disabled;
    private static bool flushWarned;
    private static float lastSeenAt, nextFlushAt;
    // Viewed NEW cards are batched: one commit after this long without another one (or on leaving the Collection / quit).
    private const float SeenIdleSeconds = 2f;

    /// <summary>True while the native bar shows the Collection dust frame (set by Update).</summary>
    internal static bool DustShown => barCurrency == CurrencyType.DUST;
    /// <summary>True while the native bar shows the HUB / Shop gold frame (set by Update).</summary>
    internal static bool GoldShown => barCurrency == CurrencyType.GOLD;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        // CollectionManager.MarkAllInstancesAsSeen (card hover on a NEW card) acknowledges through this transport call.
        harmony.Patch(Required(typeof(Network), "AckCardSeenBefore"),
            prefix: new HarmonyMethod(typeof(NativeCollectionStatus), nameof(AckSeen)) { priority = Priority.First });
        // The bottom bar only activates on Battle.net login, so offline its Collection dust frame never shows. An inactive
        // bar never runs Start, so its own scene-loaded callback is not registered either: Update polls the scene instead.
        loggedIn = AccessTools.Field(typeof(BnetBar), "m_isLoggedIn") ?? throw new MissingFieldException("BnetBar", "m_isLoggedIn");
    }

    private static MethodInfo Required(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    // Persist instead of queueing a server packet: native already set SeenCount and refreshes the glow and tab counts.
    private static bool AckSeen(int __0, TAG_PREMIUM __1)
    {
        if (!NativeUIProbe.Active) return true;
        try
        {
            string id = GameUtils.TranslateDbIdToCardId(__0);
            // In memory now (UI and later boot projection read it); disk write is batched by FlushPendingSeen.
            if (!string.IsNullOrEmpty(id) && NativeUIProbe.RewardProfile != null && NativeUIProbe.RewardProfile.MarkSeen(id, (int)__1))
                lastSeenAt = Time.realtimeSinceStartup;
        }
        catch (Exception error) { log.LogError("COLLECTION_SEEN save failed (marker returns after restart): " + error); }
        return false;
    }

    // Writes the viewed-card queue in one commit. force: leaving the Collection / quit / pause; otherwise only after
    // the idle delay; final: quit/pause, ignores the retry backoff. Never throws: a failed write keeps the queue (any later commit folds it in) and retries slowly.
    internal static void FlushPendingSeen(bool force, bool final = false)
    {
        try
        {
            var profile = NativeUIProbe.RewardProfile;
            if (profile == null || profile.PendingSeenCount == 0) return;
            float now = Time.realtimeSinceStartup;
            if ((!force && now - lastSeenAt < SeenIdleSeconds) || (!final && now < nextFlushAt)) return;
            profile.FlushSeen();
            flushWarned = false;
        }
        catch (Exception error)
        {
            nextFlushAt = Time.realtimeSinceStartup + 5f;
            if (!flushWarned) log?.LogError("COLLECTION_SEEN save failed (markers return after restart): " + error);
            flushWarned = true;
        }
    }

    // Retail shows the bottom bar with a currency frame per scene (CurrencyFrame.GetVisibleCurrencies): GOLD on the HUB
    // (an open Shop asks its store, which offline is GOLD only) and DUST in the Collection. Offline the bar never logs
    // in, so show it in those two scenes once loaded; elsewhere it stays hidden exactly as before.
    // Runs every frame inside the probe's Update, so it must never throw (that would block the whole probe): any
    // failure logs once and disables this feature for the session.
    internal static void Update()
    {
        if (disabled) return;
        try { Tick(); }
        catch (Exception error)
        {
            disabled = true;
            if (barShown)
            {
                barCurrency = CurrencyType.NONE; barShown = false;
                try { BnetBar.Get()?.ToggleActive(false); } catch { }
            }
            log?.LogError("CURRENCY_BAR disabled after failure: " + error);
        }
    }

    internal static CurrencyType BarCurrency(SceneMgr.Mode mode, bool transitioning) =>
        transitioning ? CurrencyType.NONE
        : mode == SceneMgr.Mode.COLLECTIONMANAGER ? CurrencyType.DUST
        : mode == SceneMgr.Mode.HUB ? CurrencyType.GOLD
        : CurrencyType.NONE;

    // Retail keeps the bottom bar (its menu gear) up during a match; CurrencyFrame.GetVisibleCurrencies lists none there.
    internal static bool BarVisible(SceneMgr.Mode mode, bool transitioning) =>
        HandheldPolicy.BarVisible(BarCurrency(mode, false) != CurrencyType.NONE, mode == SceneMgr.Mode.GAMEPLAY, transitioning);

    private static void Tick()
    {
        if (!NativeUIProbe.IsolatedLaunch || !NativeUIProbe.Active || !SceneMgr.IsInitialized()) return;
        var scene = SceneMgr.Get();
        CurrencyType want = BarCurrency(scene.GetMode(), scene.IsTransitioning());
        FlushPendingSeen(want != CurrencyType.DUST);
        BnetBar bar = BnetBar.Get();
        if (bar == null) return;
        bool showBar = BarVisible(scene.GetMode(), scene.IsTransitioning());
        if (want == barCurrency && showBar == barShown)
        {
            // Currency frames register in their own Start, a frame after the bar first activates: bind once they exist.
            if (want != CurrencyType.NONE && bindAttempts > 0 && !bar.TryGetRelevantCurrencyFrame(want, out _))
            {
                bindAttempts--;
                try { bar.RefreshCurrency(); }
                catch (Exception error) { bindAttempts = 0; log.LogError("CURRENCY_BAR " + want + " frame bind failed: " + error); }
            }
            return;
        }
        bool show = showBar, wasShown = barShown;
        barCurrency = want; barShown = showBar;
        bindAttempts = want != CurrencyType.NONE ? 300 : 0;
        try
        {
            // UpdateLayout positions the menu button and currency frames only for a logged-in bar.
            if (show != wasShown) loggedIn.SetValue(bar, show);
            NativeShop.RefreshDust();
            NativeShop.RefreshGold(true);
            if (show != wasShown) bar.ToggleActive(show);
            if (show) bar.RefreshCurrency();
        }
        catch (Exception error) { log.LogError("CURRENCY_BAR " + (show ? "show " + want : "hide") + " failed: " + error); }
    }
}
