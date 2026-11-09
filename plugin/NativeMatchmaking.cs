using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

internal static partial class NativeMatchmaking
{
    private const string Prefab = "MatchingPopup3D.prefab:4f4a40d14d907e94da1b81d97c18a44f";
    private static ManualLogSource log;
    private static MethodInfo showPopup, loadPopup;
    private static MatchingPopupDisplay popup;
    private static bool available, active, preloadHooked;
    private static float shownAt, readyAt;
    public static bool Visible => active;

    public static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        available = preloadHooked = false;
        try
        {
            showPopup = Required(typeof(GameMgr), "ShowTransitionPopup", new[] { typeof(string), typeof(int) });
            loadPopup = Required(typeof(GameMgr), "LoadTransitionPopup", new[] { typeof(string) });
            harmony.Patch(Required(typeof(TransitionPopup), "EnableCancelButtonIfPossible"),
                prefix: new HarmonyMethod(typeof(NativeMatchmaking), nameof(AllowCancel)));
            harmony.Patch(Required(typeof(MatchingPopupDisplay), "OnNavigateBack"),
                prefix: new HarmonyMethod(typeof(NativeMatchmaking), nameof(BlockBack)));
            harmony.Patch(Required(typeof(MatchingPopupDisplay), "OnCancelButtonReleased"),
                prefix: new HarmonyMethod(typeof(NativeMatchmaking), nameof(AllowCancelClick)));
            harmony.Patch(Required(typeof(MatchingPopupDisplay), "Hide"),
                prefix: new HarmonyMethod(typeof(NativeMatchmaking), nameof(AllowHide)));
            harmony.Patch(Required(typeof(MatchingPopupDisplay), "OnGameplaySceneLoaded"),
                postfix: new HarmonyMethod(typeof(NativeMatchmaking), nameof(GameplayLoaded)));
            available = true;
        }
        catch (Exception error) { log.LogWarning("LOCAL_MATCHMAKING fallback: native hooks unavailable " + error); }
    }

    private static MethodInfo Required(Type type, string name, Type[] args = null) =>
        AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

    // Retail preloads the popup in GameMgr.OnScenePreLoad when TOURNAMENT loads, but GameMgr registers that listener in
    // OnLoggedIn, which the offline boot never runs: GetTransitionPopup() stayed null. Mirror that preload (same
    // GameMgr.LoadTransitionPopup, same timing) from our own listener once SceneMgr exists.
    private static void HookPreload()
    {
        if (preloadHooked || !available || !SceneMgr.IsInitialized()) return;
        preloadHooked = true;
        SceneMgr.Get().RegisterScenePreLoadEvent(OnScenePreLoad);
        // Hooked while the picker is already loaded: preload now.
        if (SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT) Preload("picker already loaded");
    }

    private static void OnScenePreLoad(SceneMgr.Mode prevMode, SceneMgr.Mode mode, object userData)
    {
        if (mode == SceneMgr.Mode.TOURNAMENT) Preload("picker load");
    }

    private static MatchingPopupDisplay Preload(string reason)
    {
        try
        {
            var manager = GameMgr.Get();
            if (manager == null) return null;
            var loaded = manager.GetTransitionPopup() as MatchingPopupDisplay;
            if (loaded != null) return loaded;
            if (active) return null; // never replace a popup this controller still owns
            loadPopup.Invoke(manager, new object[] { Prefab });
            loaded = manager.GetTransitionPopup() as MatchingPopupDisplay;
            if (loaded != null) log.LogInfo("LOCAL_MATCHMAKING preloaded (" + reason + ")");
            else log.LogWarning("LOCAL_MATCHMAKING preload failed (" + reason + ")");
            return loaded;
        }
        catch (Exception error)
        {
            log.LogWarning("LOCAL_MATCHMAKING preload failed (" + reason + ") " + error);
            return null;
        }
    }

    public static void Begin()
    {
        if (!available || active) return;
        HookPreload();
        var manager = GameMgr.Get();
        // Normally loaded at picker load; a missed scene event loads it here, before Show (retail Show would too).
        var preloaded = Preload("queue");
        shownAt = Time.realtimeSinceStartup;
        readyAt = -1;
        active = true;
        try
        {
            popup = preloaded;
            if (popup == null) throw new InvalidOperationException("native matchmaking prefab could not be loaded");
            // Only show the retail presentation; FindGame would enter Battle.net matchmaking.
            showPopup.Invoke(manager, new object[] { Prefab, 0 });
            popup = manager.GetTransitionPopup() as MatchingPopupDisplay;
            if (popup == null || !popup.IsShown()) throw new InvalidOperationException("native prefab did not show");
            ShowDisabledCancel(popup);
            log.LogInfo("LOCAL_MATCHMAKING shown; Cancel disabled");
        }
        catch (Exception error)
        {
            popup = manager?.GetTransitionPopup() as MatchingPopupDisplay;
            Fallback("native popup failed to load " + error);
        }
    }

    public static void Update()
    {
        HookPreload();
        if (!active) return;
        if (popup == null) { Fallback("native popup disappeared"); return; }
        if (!popup.IsShown() && !popup.gameObject.activeSelf)
        {
            popup = null;
            active = false;
            return;
        }
        if (Time.realtimeSinceStartup - shownAt >= 45)
        {
            Fallback("native popup timed out; local match remains pending");
            return;
        }
        // Native Death/reveal runs for 3.5 seconds after scene readiness. Preserve it even on slow loads.
        if (popup.IsShown() && CanHide()) popup.Hide();
    }

    // Retail's own disabled look: TransitionPopup.DisableCancelButton flips the button face down (UIBButton.Flip) so the
    // bottom tab shows the charcoal back plate, and disables input. Never hide the button object: that left a black hole.
    // AnimateShow already did this; repeat it so a stale or hidden state from an earlier use can never persist.
    private static void ShowDisabledCancel(MatchingPopupDisplay shown)
    {
        var button = shown.m_cancelButton;
        button.gameObject.SetActive(true);
        button.SetEnabled(false);
        button.Flip(false, true);
    }

    private static bool Owns(TransitionPopup instance) => active && popup != null && instance == popup;
    private static bool CanHide() => readyAt >= 0
        && Time.realtimeSinceStartup >= Math.Max(shownAt + 4, readyAt + 3.5f);

    private static void GameplayLoaded(MatchingPopupDisplay __instance)
    {
        if (!Owns(__instance) || readyAt >= 0) return;
        readyAt = Time.realtimeSinceStartup;
        log.LogInfo("LOCAL_MATCHMAKING gameplay ready after " + (readyAt - shownAt) + " seconds");
    }

    private static bool AllowHide(MatchingPopupDisplay __instance) => !Owns(__instance) || CanHide();
    private static bool AllowCancelClick(MatchingPopupDisplay __instance) => !Owns(__instance);

    private static bool AllowCancel(TransitionPopup __instance, ref bool __result)
    {
        if (!Owns(__instance)) return true;
        __result = false;
        return false;
    }

    private static bool BlockBack(MatchingPopupDisplay __instance, ref bool __result)
    {
        if (!Owns(__instance)) return true;
        __result = true;
        return false;
    }

    private static void Fallback(string reason)
    {
        log.LogWarning("LOCAL_MATCHMAKING fallback: " + reason);
        var failed = popup;
        popup = null;
        active = false;
        if (failed == null) return;
        try
        {
            failed.Hide();
        }
        catch (Exception error) { log.LogWarning("LOCAL_MATCHMAKING popup cleanup " + error); }
        // OnDestroy releases the native LoadingScreen blocker even when loading never completes.
        UnityEngine.Object.Destroy(failed.gameObject);
    }
}
