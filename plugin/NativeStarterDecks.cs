using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// #57 hidden starter decks (owner 2026-10-28). Deleting a Core starter loaner in the native tray hides it for this save
// (NativeDeckEditor). This adds the way back as a rare fallback inside the native Options panel: a clone of Options' own
// Miscellaneous plank (hidden by NativeHiddenEntries), labelled "Restore Starter Decks", at the bottom of the Other
// group: Miscellaneous's slot is taken by the Handheld UI checkbox, so the plank's visual centre sits a native row and a
// quarter below that checkbox's (lane 1 live: overlapped it otherwise); with the FPS Counter row under Handheld UI, a row and 5%
// below that one. Shown only while the save hides at least one starter (re-checked
// each time Options shows). It opens one native confirm;
// RESTORE clears the hidden list, and the starters return with the next native deck-list rebuild (the next start).
// Every path fails closed: nothing throws into Options.
internal static class NativeStarterDecks
{
    private static ManualLogSource log;
    private static UIBButton button;
    private static bool placementFailed;

    internal static UIBButton OptionsButton => button != null && button.gameObject.activeInHierarchy ? button : null;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeStarterDecks), nameof(AddButton)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show") ?? throw new MissingMethodException("OptionsMenu", "Show"),
            postfix: new HarmonyMethod(typeof(NativeStarterDecks), nameof(SyncButton)));
    }

    internal static bool ButtonWanted(LocalProfile profile, bool inMatch) => profile != null && !inMatch && profile.HiddenLoaners.Count != 0;

    private static bool Wanted() =>
        ButtonWanted(NativeUIProbe.RewardProfile, SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY);

    private static void AddButton(OptionsMenu __instance)
    {
        try
        {
            UIBButton source = __instance.m_miscellaneousButton;
            if (source == null) throw new InvalidOperationException("Options plank template missing");
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            clone.name = NativeStarterText.OptionsObject;
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;
            UIBButton plank = clone.GetComponent<UIBButton>();
            if (plank == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no plank"); }
            plank.ClearEventListeners();
            plank.SetText(NativeStarterText.OptionsLabel);
            plank.AddEventListener(UIEventType.RELEASE, _ => ConfirmRestore());
            clone.SetActive(Wanted());
            button = plank;
        }
        catch (Exception error) { button = null; log?.LogWarning("STARTER_DECKS Options button unavailable: " + error.Message); }
    }

    // Every frame while shown (cheap): after HandheldUi settles its checkbox, line the plank's visual centre up one row
    // and a quarter below the checkbox's along the native row axis (pivots differ between checkbox and plank prefabs, so
    // rendered bounds are compared), keeping Miscellaneous's position across the row.
    private const float FpsCounterGapRows = 1.05f;

    internal static void Tick()
    {
        try
        {
            if (placementFailed || button == null || !button.gameObject.activeInHierarchy) return;
            var options = OptionsMenu.Get();
            if (options == null || !options.IsShown()) return;
            CheckBox handheld = HandheldUi.Checkbox, shake = options.m_screenShakeCheckbox;
            if (handheld == null || shake == null || !handheld.gameObject.activeInHierarchy) return;
            // The FPS Counter row takes the slot under Handheld UI: the plank goes one row (and a little) under that instead.
            CheckBox anchor = NativeFpsCounter.Shown ?? handheld;
            float gap = anchor == handheld ? 1.25f : FpsCounterGapRows;
            Vector3 step = handheld.transform.position - shake.transform.position;
            if (step.sqrMagnitude < 1e-8f || !Visual(anchor.gameObject, out Bounds below) || !Visual(button.gameObject, out Bounds plank)) return;
            Vector3 row = step.normalized;
            float move = Vector3.Dot(below.center - plank.center, row) + step.magnitude * gap;
            if (Mathf.Abs(move) > step.magnitude * .01f) button.transform.position += row * move;
        }
        catch (Exception error) { placementFailed = true; log?.LogWarning("STARTER_DECKS Options button placement: " + error.Message); }
    }

    private static bool Visual(GameObject root, out Bounds bounds)
    {
        bounds = default; bool any = false;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled) continue;
            if (any) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; any = true; }
        }
        return any;
    }

    private static void SyncButton()
    {
        try { if (button != null) button.gameObject.SetActive(Wanted()); }
        catch (Exception error) { log?.LogWarning("STARTER_DECKS Options button sync: " + error.Message); }
    }

    private static void ConfirmRestore()
    {
        try
        {
            DialogManager.Get()?.ShowUniquePopup(new AlertPopup.PopupInfo {
                m_id = NativeStarterText.RestorePopupId, m_headerText = NativeStarterText.RestoreHeader, m_text = NativeStarterText.RestoreBody,
                m_showAlertIcon = false, m_responseDisplay = AlertPopup.ResponseDisplay.CONFIRM_CANCEL,
                m_confirmText = NativeStarterText.RestoreLabel, m_cancelText = NativeStarterText.CancelLabel, m_keyboardEscIsCancel = true,
                m_responseCallback = (response, data) => { if (response == AlertPopup.Response.CONFIRM) Restore(); }
            });
        }
        catch (Exception error) { log?.LogWarning("STARTER_DECKS restore confirm unavailable: " + error.Message); }
    }

    private static void Restore()
    {
        try
        {
            var profile = NativeUIProbe.RewardProfile;
            if (profile == null) return;
            bool changed = profile.RestoreLoaners();
            log?.LogInfo("STARTER_DECKS restored=" + changed);
            SyncButton(); // Nothing is hidden now: the fallback leaves Options.
        }
        catch (Exception error) { log?.LogWarning("STARTER_DECKS restore not saved: " + error.Message); }
    }
}
