using System;
using BepInEx.Logging;
using UnityEngine;

// #28 Rematch (owner 2026-10-28): post-match lands on the deck picker with the played deck selected; a native REMATCH button
// (a clone of the picker's own Back button, a UIBButton) sits just above Back while that deck is selected and
// LocalLadder.RematchOffer accepts it. A click is the picker's Play start with the last NPC and deck (NativeUIProbe.StartPicked);
// Play keeps normal matchmaking. Selecting another deck hides it (no counter-picking a deck already seen). Every path fails
// closed and nothing throws into the probe's Update: a failure retires the button until the picker is left, the third for
// the session. Per frame it only reads the tray BindPicker found and the cached offer; placement happens once per button.
// REMATCH on the results screen (NativeRewardUI) queues a Request: the normal results route lands here and the same start
// runs once the picker is ready, so both entry points share one validated path.
internal static class NativeRematchUI
{
    internal const string Label = "Rematch";   // title case like the native "Back" it sits on; caps shrink the text
    internal const string Id = "picker.rematch";
    // Gap between Back and REMATCH as a share of Back's on-screen height.
    internal const float GapShare = 0.25f;
    private static readonly LocalRematchRequest queued = new LocalRematchRequest();
    private static ManualLogSource log;
    private static UIBButton button;
    private static UIBButton source;
    private static bool disabled, blocked, placed;
    private static int failures;
    private static string logged = "";

    internal static UIBButton Button => button != null && button.gameObject.activeInHierarchy ? button : null;
    // The results screen offers REMATCH only while this side can still run the queued start.
    internal static bool Available => !disabled;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    // From the results screen: start a rematch of this settled match once its route reaches the picker.
    internal static void Request(string match)
    {
        queued.Request(match, Time.realtimeSinceStartup);
        log?.LogInfo("NATIVE_REMATCH requested match=" + match);
    }

    internal static void Cancel(string why)
    {
        if (queued.Match == null) return;
        queued.Clear();
        log?.LogInfo("NATIVE_REMATCH request dropped reason=" + why);
    }

    // tray: the picker NativeUIProbe.BindPicker found this frame (null outside TOURNAMENT).
    internal static void Update(DeckPickerTrayDisplay tray)
    {
        if (disabled) return;
        if (tray == null && blocked) blocked = false;
        if (blocked) return;
        try { Tick(tray); }
        catch (Exception error)
        {
            blocked = true; disabled = ++failures >= 3;
            Retire();
            log?.LogError("NATIVE_REMATCH button " + (disabled ? "disabled for the session" : "retired until the picker is left")
                + " after failure: " + error);
        }
    }

    private static void Tick(DeckPickerTrayDisplay tray)
    {
        if (tray != null && SceneMgr.Get().IsTransitioning()) tray = null;
        UIBButton back = tray != null ? tray.m_backButton : null;
        string reason = back == null || !back.gameObject.activeInHierarchy ? "no picker"
            : !NativeUIProbe.PickerCanStart || NativeMatchmaking.Visible || tray.IsModeSwitchShowing || NativeDeckPickerGuide.Open
                || UniversalInputManager.Get()?.IsDialogActive() == true ? "busy" : null;
        NpcEncounter offer = reason == null ? Offer(tray, out reason) : null;
        if (RunQueued(tray, offer, reason)) return;
        if (offer == null)
        {
            if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
            if (reason == "no picker") LocalLadder.ForgetRematchOffer();
            Log(reason == "no picker" ? "" : "none reason=" + reason);
            return;
        }
        if (button == null || source != back) { Retire(); button = Create(back); source = back; placed = false; }
        // Collider bounds can lag a frame behind a fresh clone or tray: stay hidden until Back has an on-screen size.
        if (!placed && !(placed = Place(back))) { button.gameObject.SetActive(false); return; }
        if (!button.gameObject.activeSelf)
        {
            button.gameObject.SetActive(true);
            button.SetText(Label);   // in case the cloned label re-localized while hidden
        }
        Log("shown npc=" + offer.Npc.Id + ";deck=" + offer.Deck.Id
            + ";rating=" + offer.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    }

    // True while a results-screen request owns the picker (the button stays hidden) or has just started the match.
    private static bool RunQueued(DeckPickerTrayDisplay tray, NpcEncounter offer, string reason)
    {
        if (queued.Match == null) return false;
        bool onPicker = reason != "no picker";
        if (!onPicker) LocalLadder.ForgetRematchOffer();
        // Ready once the tray has a selection and Back is on screen (the tray has slid in).
        bool ready = onPicker && reason != "busy" && tray.GetSelectedCollectionDeck() != null && OnScreen(tray.m_backButton);
        switch (queued.Next(Time.realtimeSinceStartup, LocalLadder.Profile?.LastMatchId, onPicker, ready, offer == null ? reason : null, out string why))
        {
            case LocalRematchStep.Wait:
                if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
                return true;
            case LocalRematchStep.Start:
                if (button != null) button.gameObject.SetActive(false);
                log?.LogInfo("NATIVE_REMATCH queued start npc=" + offer.Npc.Id + ";deck=" + offer.Deck.Id);
                NativeUIProbe.StartPicked(true);
                return true;
            case LocalRematchStep.Drop:
                log?.LogInfo("NATIVE_REMATCH request dropped reason=" + why);
                return false;
            default: return false;
        }
    }

    internal static bool OnScreen(UIBButton back)
    {
        Camera camera = CameraUtils.FindFirstByLayerMask(1 << back.gameObject.layer);
        return camera != null && ScreenRect(camera, back.gameObject).height > 0;
    }

    // Each state once, when it changes (the probe calls Update every frame).
    private static void Log(string state)
    {
        if (state == logged) return;
        logged = state;
        if (state.Length != 0) log?.LogInfo("NATIVE_REMATCH " + state);
    }

    // The selected native deck must be the one the last match used; LocalLadder then applies the owner's rematch rules.
    private static NpcEncounter Offer(DeckPickerTrayDisplay tray, out string reason)
    {
        string played = LocalLadder.Profile?.LastMatchDeck;
        long nativeId = NativeUIProjection.PlayedDeckId(played);
        CollectionDeck selected = nativeId == 0 ? null : tray.GetSelectedCollectionDeck();
        if (selected == null || selected.ID != nativeId) { reason = nativeId == 0 ? "no played deck" : "other deck selected"; return null; }
        NpcEncounter offer = LocalLadder.RematchOffer(played);
        reason = offer == null ? LocalLadder.RematchRefusal ?? "refused" : null;
        return offer;
    }

    private static UIBButton Create(UIBButton back)
    {
        UIBButton clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(back, back.transform.parent);
            clone.name = "SoloStoneRematchButton";
            clone.ClearEventListeners();
            clone.SetText(Label);
            NativeButtonKit.FitOrThrow(clone);   // same plate as Back; the label box is sized to its cream window; not a 3-slice plate: no button
            NativeButtonTint.Apply(clone, NativeButtonTint.Tint.Red);
            clone.gameObject.SetActive(true);
            clone.SetEnabled(true);
            clone.AddEventListener(UIEventType.RELEASE, _ => Click());
            log?.LogInfo("NATIVE_REMATCH button created");
            return clone;
        }
        catch
        {
            if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
            throw;
        }
    }

    // Directly above Back, one Back height plus a gap. A sibling of Back with a fixed local offset, so it follows the tray as it
    // slides without per-frame work.
    private static bool Place(UIBButton back)
    {
        Camera camera = CameraUtils.FindFirstByLayerMask(1 << back.gameObject.layer);
        Rect rect = camera == null ? Rect.zero : ScreenRect(camera, back.gameObject);
        if (rect.height <= 0) return false;
        Vector3 from = camera.WorldToScreenPoint(back.transform.position);
        Vector3 world = camera.ScreenToWorldPoint(from + new Vector3(0, rect.height * (1 + GapShare), 0));
        button.transform.localRotation = back.transform.localRotation;
        button.transform.localScale = back.transform.localScale;
        button.transform.position = world;
        return true;
    }

    internal static Rect ScreenRect(Camera camera, GameObject root)
    {
        bool any = false; float left = 0, right = 0, bottom = 0, top = 0;
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(false))
        {
            if (!collider.enabled) continue;
            Bounds b = collider.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = camera.WorldToScreenPoint(new Vector3((corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y, (corner & 4) == 0 ? b.min.z : b.max.z));
                if (!any) { left = right = p.x; bottom = top = p.y; any = true; }
                left = Mathf.Min(left, p.x); right = Mathf.Max(right, p.x); bottom = Mathf.Min(bottom, p.y); top = Mathf.Max(top, p.y);
            }
        }
        return any ? Rect.MinMaxRect(left, bottom, right, top) : Rect.zero;
    }

    private static void Click()
    {
        try
        {
            if (button == null || !button.gameObject.activeInHierarchy) return;
            if (LocalLadder.RematchOffer(LocalLadder.Profile?.LastMatchDeck) == null)
            {
                log?.LogWarning("NATIVE_REMATCH refused refusal=" + (LocalLadder.RematchRefusal ?? ""));
                button.gameObject.SetActive(false);
                return;
            }
            button.gameObject.SetActive(false);
            NativeUIProbe.StartPicked(true);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_REMATCH click failed: " + error.Message); }
    }

    private static void Retire()
    {
        try { if (button != null) UnityEngine.Object.Destroy(button.gameObject); } catch { }
        button = null; source = null;
    }
}
