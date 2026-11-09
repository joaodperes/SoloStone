using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using MiniJSON;
using UnityEngine;

// Deck Guide on the deck picker (owner 2026-11-04): while the selected deck came from the shop (a ready-made deck purchase, or a deck built
// from an owned recipe, kept through renames and edits: LocalCustomDeck.SourceRecipeId), a native "Guide" button (label NativeDeckGuideText.PickerLabel; Back's own plate size) opens the same two-page
// guide dialog the deck builder's Recipe guide shows (NativeRecipeGuide.ShowPages). Starter, loaner and hand-built decks have no guide and no
// button. The button is a clone of the picker's own Back button (a UIBButton, like NativeRematchUI): right-aligned with Back and stacked above it
// at the first step that touches none of Play, My Collection or REMATCH (DeckGuideLayout). Every path fails closed and nothing throws into the
// probe's Update: a failure retires the button until the picker is left, the third for the session. Per frame it reads the tray BindPicker found
// and a cached guide lookup (re-resolved only when the tray or the selected deck changes); placement happens once per slot change.
// It never changes the selection, never starts a match and never touches Play.
internal static class NativeDeckPickerGuide
{
    internal const string Id = NativeDeckGuideText.PickerId, Label = NativeDeckGuideText.PickerLabel;
    private static readonly FieldInfo PlayField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_playButton");
#if !SOLOSTONE_RELEASE
    private static readonly FieldInfo PrevField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_leftArrow");
    private static readonly FieldInfo NextField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_rightArrow");
    private static readonly FieldInfo FormatField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_switchFormatButton");
#endif
    private static ManualLogSource log;
    private static UIBButton button, source;
    private static DeckPickerTrayDisplay lastTray, cachedTray;
    private static long cachedDeck;
    private static DeckRecipe cachedGuide;
    private static bool disabled, blocked, placed, open;
    private static float openAt;
    private static int failures, slot;
    private static string logged = "", reason = "";

    internal static UIBButton Button => button != null && button.gameObject.activeInHierarchy ? button : null;
    // True from the press until the last page is closed, including the short gap between pages when no dialog is up: Play does not start
    // and Escape does not open the Game Menu in it (NativeUIProbe.PickerCanStart, NativeRewardUI.BeforeRewardEscape).
    internal static bool Open => open;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    // tray: the picker NativeUIProbe.BindPicker found this frame (null outside TOURNAMENT).
    internal static void Update(DeckPickerTrayDisplay tray)
    {
        lastTray = tray;
        // The guide chain leaves the screen for 50 ms between pages (Open holds Play and Escape through it); never stay open if no dialog
        // follows, also once the button has been retired or disabled.
        if (open && (tray == null || Time.realtimeSinceStartup - openAt > 1f) && UniversalInputManager.Get()?.IsDialogActive() != true
            && NativeGuideCards.Current == null) open = false;   // a card page still loading (or waiting its turn) keeps Play and Escape held
        if (disabled) return;
        if (tray == null && blocked) blocked = false;
        if (tray == null) { open = false; cachedTray = null; cachedGuide = null; }
        if (blocked) return;
        try { Tick(tray); }
        catch (Exception error)
        {
            blocked = true; disabled = ++failures >= 3;
            Retire();
            log?.LogError("NATIVE_DECK_GUIDE button " + (disabled ? "disabled for the session" : "retired until the picker is left")
                + " after failure: " + error);
        }
    }

    private static void Tick(DeckPickerTrayDisplay tray)
    {
        if (tray != null && SceneMgr.Get().IsTransitioning()) tray = null;
        UIBButton back = tray != null ? tray.m_backButton : null;
        // Over a match, a dialog, the format popup or the matchmaking screen, or while Play is not startable, there is no guide button.
        reason = back == null || !back.gameObject.activeInHierarchy ? "no picker"
            : !NativeUIProbe.PickerCanStart || NativeMatchmaking.Visible || tray.IsModeSwitchShowing || open
                || UniversalInputManager.Get()?.IsDialogActive() == true ? "busy" : "";
        DeckRecipe guide = reason.Length == 0 ? Resolve(tray, out reason) : null;
        if (guide == null)
        {
            Hide();
            Log(reason == "no picker" || reason == "busy" ? "" : "none reason=" + reason);
            return;
        }
        if (button == null || source != back) { Retire(); button = Create(back); source = back; }
        // Decided from the live rects every frame, before the button may show: it is never active at a position Play's rect has not cleared.
        int step = Place(tray, back, guide);
        if (step < 0) { Hide(); return; }
        if (!button.gameObject.activeSelf)
        {
            button.gameObject.SetActive(true);
            button.SetText(Label);   // in case the cloned label re-localized while hidden
        }
        else if (Touches(tray))
        {
            // Pivot or scale off from the plan: fail closed rather than sit on a control.
            throw new InvalidOperationException("Deck Guide button overlaps a picker control at slot " + step);
        }
        Log("shown recipe=" + guide.Id + ";deck=" + cachedDeck + ";slot=" + slot);
    }

    private static void Hide()
    {
        if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false);
    }

    // The selected native deck's guide, or null with the reason. Cached per tray and selected deck.
    private static DeckRecipe Resolve(DeckPickerTrayDisplay tray, out string why)
    {
        CollectionDeck selected = tray.GetSelectedCollectionDeck();
        if (selected == null) { why = "no selection"; return null; }
        if (!ReferenceEquals(tray, cachedTray) || selected.ID != cachedDeck)
        {
            cachedTray = tray; cachedDeck = selected.ID;
            cachedGuide = GuideFor(selected.ID);
            open = false;
        }
        why = cachedGuide == null ? "no guide" : "";
        return cachedGuide;
    }

    // Starters, loaners (no saved draft), hand-built decks and unoffered recipes resolve to null.
    private static DeckRecipe GuideFor(long nativeDeckId) =>
        NativeRecipeGuideText.ForSavedDeck(NativeRecipeLibrary.Current.Recipes, LocalLadder.Profile?.GetDraft(nativeDeckId));

    private static void Log(string state)
    {
        if (state == logged) return;
        logged = state;
        if (state.Length != 0) log?.LogInfo("NATIVE_DECK_GUIDE " + state);
    }

    private static UIBButton Create(UIBButton back)
    {
        UIBButton clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(back, back.transform.parent);
            clone.name = "SoloStoneDeckGuideButton";
            clone.ClearEventListeners();
            clone.BubbleUpEvent = "";
            clone.SetText(Label);
            // Back's own plate (never scaled), with a label box sized to its cream window; "Guide" is short enough to keep Back's letter size.
            // Not a 3-slice plate, or a refused fit: throw, so no button is shown.
            NativeButtonKit.FitOrThrow(clone);
            clone.gameObject.SetActive(true);
            clone.SetEnabled(true);
            clone.AddEventListener(UIEventType.RELEASE, _ => Click());
            log?.LogInfo("NATIVE_DECK_GUIDE button created");
            return clone;
        }
        catch
        {
            if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
            throw;
        }
    }

    // Screen rect of a live control, bottom-left origin; null when it is missing, inactive or has no bounds yet.
    private static DeckRect? RectOf(Camera camera, Component control)
    {
        if (control == null || !control.gameObject.activeInHierarchy) return null;
        Rect rect = NativeRematchUI.ScreenRect(camera, control.gameObject);
        return rect.height > 0 && rect.width > 0 ? new DeckRect(rect.x, rect.y, rect.width, rect.height) : (DeckRect?)null;
    }

    // The first free step above Back, right-aligned with it (DeckGuideLayout), recomputed each frame and moved as a sibling of Back by the
    // plate centre's offset from Back's centre. -1, with the reason in `reason`, until Back and Play have on-screen rects, or when no step is
    // free (a warning with the rects is logged once per deck, and there is no button).
    private static int Place(DeckPickerTrayDisplay tray, UIBButton back, DeckRecipe guide)
    {
        Camera camera = CameraUtils.FindFirstByLayerMask(1 << back.gameObject.layer);
        DeckRect? backRect = camera == null ? null : RectOf(camera, back);
        if (backRect == null) { slot = 0; reason = "back unknown"; return -1; }
        DeckRect? play = RectOf(camera, PlayField?.GetValue(tray) as Component);
        if (play == null) { slot = 0; reason = "play unknown"; return -1; }
        var others = new List<DeckRect>();
        foreach (var other in new Component[] { tray.m_collectionButton, NativeRematchUI.Button })
        {
            if (other == null || !other.gameObject.activeInHierarchy) continue;
            DeckRect? rect = RectOf(camera, other);
            if (rect == null) { slot = 0; reason = "controls unknown"; return -1; }   // active but no bounds yet: wait, do not guess
            others.Add(rect.Value);
        }
        FrameMargin? frame = NativeButtonKit.FrameMarginOf(back);
        if (frame == null) { slot = 0; reason = "back art unknown"; return -1; }
        DeckRect? plate = DeckGuideLayout.Place(backRect.Value, frame.Value, play, others, out int step);
        if (plate == null)
        {
            slot = 0; reason = "no free slot";
            string key = cachedDeck + "|" + Screen.width + "x" + Screen.height;
            if (warned != key)
            {
                warned = key;
                log?.LogWarning("NATIVE_DECK_GUIDE no free slot for recipe=" + guide.Id + ";deck=" + cachedDeck + " screen=" + Screen.width + "x" + Screen.height
                    + " back=" + Fmt(backRect.Value) + " play=" + Fmt(play.Value) + " others=" + string.Join(",", others.Select(Fmt)) + ": no Deck Guide button");
            }
            return -1;
        }
        Vector3 from = camera.WorldToScreenPoint(back.transform.position);
        // The clone's pivot sits where Back's does, so the move is the plate centre's offset from Back's centre.
        var move = new Vector3(plate.Value.X + plate.Value.Width / 2 - (backRect.Value.X + backRect.Value.Width / 2),
            plate.Value.Y + plate.Value.Height / 2 - (backRect.Value.Y + backRect.Value.Height / 2), 0);
        button.transform.localRotation = back.transform.localRotation;
        button.transform.position = camera.ScreenToWorldPoint(from + move);
        slot = step; placed = true;
        return step;
    }
    private static string warned = "";
    private static string Fmt(DeckRect r) => "[" + (int)r.X + "," + (int)r.Y + "," + (int)r.Width + "," + (int)r.Height + "]";

    // True when the shown clone's own bounds touch Play, My Collection or REMATCH (checked once it has been active a frame).
    private static bool Touches(DeckPickerTrayDisplay tray)
    {
        Camera camera = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
        DeckRect? mine = camera == null ? null : RectOf(camera, button);
        if (mine == null) return false;
        foreach (var other in new Component[] { PlayField?.GetValue(tray) as Component, tray.m_collectionButton, NativeRematchUI.Button })
        {
            DeckRect? rect = RectOf(camera, other);
            if (rect != null && OptionsPaceLayout.Overlap(mine.Value, rect.Value)) return true;
        }
        return false;
    }

    private static void Click()
    {
        try
        {
            if (button == null || !button.gameObject.activeInHierarchy || open || lastTray == null) return;
            DeckRecipe guide = Resolve(lastTray, out _);
            if (guide == null) return;
            DeckPickerTrayDisplay tray = lastTray;
            long deck = cachedDeck;
            log?.LogInfo("NATIVE_DECK_GUIDE opened recipe=" + guide.Id + ";deck=" + deck);
            // Valid while the same tray still has the same deck selected; the dialog is modal, so the selection cannot move under it.
            ShowGuide(guide, () => tray != null && ReferenceEquals(tray, lastTray) && tray.GetSelectedCollectionDeck()?.ID == deck);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_DECK_GUIDE click failed: " + error.Message); }
    }

    private static void ShowGuide(DeckRecipe guide, Func<bool> stillValid)
    {
        open = true;
        NativeRecipeGuide.ShowPages(guide, 0, stillValid, () => true, shown => { open = shown; openAt = Time.realtimeSinceStartup; });
    }

    private static void Retire()
    {
        try { if (button != null) UnityEngine.Object.Destroy(button.gameObject); } catch { }
        button = null; source = null; placed = false; slot = 0;
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.deckGuide): what the picker shows now. visible = the button is on screen and hit-testable by the tree; hasGuide = the
    // selected deck resolves to a guide (so a hidden button with hasGuide true is a defect); rect and overlaps against the picker's own controls.
    internal static JsonNode State()
    {
        var node = new JsonNode { { "id", Id }, { "disabled", disabled }, { "blocked", blocked }, { "reason", reason },
            { "visible", Button != null }, { "open", open }, { "slot", slot }, { "placed", placed } };
        try
        {
            node["screen"] = TestControlProtocol.List(new object[] { Screen.width, Screen.height });
            var tray = lastTray;
            CollectionDeck selected = tray == null ? null : tray.GetSelectedCollectionDeck();
            node["deckId"] = selected == null ? 0 : selected.ID;
            DeckRecipe guide = selected == null ? null : GuideFor(selected.ID);
            node["hasGuide"] = guide != null; node["guideId"] = guide?.Id; node["guideName"] = guide?.Name;
            if (Button == null || tray == null) return node;
            Camera camera = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
            if (camera == null) return node;
            Rect mine = NativeRematchUI.ScreenRect(camera, button.gameObject);
            node["rect"] = TestControlProtocol.List(new object[] { mine.x, mine.y, mine.width, mine.height });
            var overlaps = new JsonList();
            void Against(string name, Component other)
            {
                if (other == null || !other.gameObject.activeInHierarchy) return;
                Rect rect = NativeRematchUI.ScreenRect(camera, other.gameObject);
                if (rect.height > 0 && mine.Overlaps(rect)) overlaps.Add(name);
            }
            Against("play", PlayField?.GetValue(tray) as Component); Against("back", tray.m_backButton);
            Against("collection", tray.m_collectionButton); Against("rematch", NativeRematchUI.Button);
            Against("prev", PrevField?.GetValue(tray) as Component); Against("next", NextField?.GetValue(tray) as Component);
            Against("format", FormatField?.GetValue(tray) as Component);
            foreach (var deckBox in UnityEngine.Object.FindObjectsByType<CollectionDeckBoxVisual>(FindObjectsSortMode.None)) Against("deck:" + deckBox.GetDeckID(), deckBox);
            node["overlaps"] = overlaps;
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }
#endif
}
