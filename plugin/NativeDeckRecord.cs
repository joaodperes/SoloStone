using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Deck record on the deck picker (owner 2026-11-05): while a deck is selected, a line "12 W - 7 L" (LocalDeckRecord: lifetime wins and
// losses of that deck across Ranked and Casual, folded from the settled ledger) sits under the picker's hero name. The line is a clone of
// the hero name's own UberText (same font, colour and layer as the retail label), shrunk, and placed from live rects (DeckRecordLayout):
// centred under the name, else above it, else nowhere. It is a label, not a control: no collider, no input, and it never touches the
// selection, Play, the profile or the save; the record is only read. The text updates as the selection changes and after each settle
// (the cache key holds the profile's match count). Every path fails closed and nothing throws into the probe's Update: a failure retires
// the line until the picker is left, the third for the session. Not shown over a dialog, the matchmaking screen or the format popup.
internal static class NativeDeckRecord
{
    internal const string Id = "picker.record", CloneName = "SoloStoneDeckRecord";
    // A little smaller than the hero name: a caption, not a title (live 2026-11-05: .55 and .7 were too small at 1280x800).
    internal const float TextScale = .9f;
    private static readonly FieldInfo NameField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_heroName");
    private static readonly FieldInfo PowerField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_heroPowerContainer");
    private static readonly FieldInfo PlayField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_playButton");
    private static readonly FieldInfo PrevField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_leftArrow");
    private static readonly FieldInfo NextField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_rightArrow");
    private static readonly FieldInfo FormatField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_switchFormatButton");
#if !SOLOSTONE_RELEASE
    private static readonly PropertyInfo TextValue = ProfileFormKit.TextType == null ? null : AccessTools.Property(ProfileFormKit.TextType, "Text");
#endif

    private static readonly FieldInfo XpBarField = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_xpBar");
    private static readonly FieldInfo HeroActorField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_heroActor");
    private static readonly FieldInfo PowerActorField = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_heroPowerActor");
    private static readonly FieldInfo PageField = AccessTools.Field(typeof(DeckPickerTrayDisplay), "m_currentPageIndex");
    // While no spot is free or bounds are unknown, look again this often (2 Hz); nothing runs per frame once a decision is made.
    internal const float RetrySeconds = .5f;

    private static ManualLogSource log;
    private static GameObject holder;
    private static GameObject clone;
    private static Component cloneText, sourceText;
    private static DeckPickerTrayDisplay lastTray;
    private static bool disabled, blocked, relabel, placed;
    private static int failures, slot;
    private static string logged = "", reason = "", shown = "", deckKey = "";
    private static long deckId;
    private static LocalDeckRecord record = LocalDeckRecord.None;
    // Cache: the fold runs again only when the profile, its match count or the deck changes.
    private static LocalProfile cachedProfile;
    private static int cachedMatches = -1;
    private static string cachedKey;
    private static DeckRect? lastAnchor, lastRect;
    private static string warned = "";
    // Work done, for state.deckRecord: a live test samples these twice a second apart on a still picker; they must not move per frame.
    // frames = Updates with a picker; passes = full recomputes (record, text, bounds, placement); placements = successful placements;
    // activations = clone shown; relabels = text rebuilt.
    private static int frames, passes, placements, placeFailures, activations, relabels;
    // The inputs of the last decision. While they and the outcome are unchanged nothing is recomputed (an unplaced line retries at 2 Hz).
    private static float[] signature = new float[0], lastSignature = new float[0];
    private static bool decided, decidedPlaced;
    private static float retryAt;

    internal static void Initialize(ManualLogSource logger) { log = logger; }
    internal static bool Visible => clone != null && clone.activeInHierarchy && placed;

    // tray: the picker NativeUIProbe.BindPicker found this frame (null outside TOURNAMENT).
    internal static void Update(DeckPickerTrayDisplay tray)
    {
        lastTray = tray;
        if (disabled) return;
        if (tray == null && blocked) blocked = false;
        if (tray == null) { if (clone != null || decided) Retire(); reason = "no picker"; return; }
        if (blocked) return;
        frames++;
        try { Tick(tray); }
        catch (Exception error)
        {
            blocked = true; disabled = ++failures >= 3;
            Retire();
            log?.LogError("NATIVE_DECK_RECORD line " + (disabled ? "disabled for the session" : "retired until the picker is left") + " after failure: " + error);
        }
    }

    private static void Tick(DeckPickerTrayDisplay tray)
    {
        if (SceneMgr.Get().IsTransitioning()) { Hide("transition"); return; }
        UIBButton back = tray.m_backButton;
        if (back == null || !back.gameObject.activeInHierarchy) { Hide("no picker"); return; }
        if (NativeMatchmaking.Visible || tray.IsModeSwitchShowing || UniversalInputManager.Get()?.IsDialogActive() == true) { Hide("busy"); return; }
        CollectionDeck selected = tray.GetSelectedCollectionDeck();
        if (selected == null) { deckId = 0; deckKey = ""; Hide("no selection"); return; }
        deckId = selected.ID;
        string key = NativeUIProbe.DeckKeyOf(selected);
        LocalProfile profile = LocalLadder.Profile;
        if (key == null || profile == null) { deckKey = ""; Hide("no deck"); return; }
        deckKey = key;
        var name = NameField?.GetValue(tray) as Component;
        if (name == null || !name.gameObject.activeInHierarchy) { Hide("no anchor"); return; }

        // Cheap per-frame part: the record (re-folded only when the profile, its match count or the deck changes) and the inputs signature.
        bool recordChanged = false;
        if (!ReferenceEquals(profile, cachedProfile) || profile.MatchCount != cachedMatches || key != cachedKey)
        {
            record = profile.DeckRecord(key);
            cachedProfile = profile; cachedMatches = profile.MatchCount; cachedKey = key; recordChanged = true;
        }
        string text = DeckRecordText.Format(record);
        Sign(tray, name, text);
        bool same = decided && !recordChanged && SameSignature() && ReferenceEquals(sourceText, name) && (clone != null);
        if (same && (decidedPlaced || Time.unscaledTime < retryAt)) return;

        passes++;
        System.Array.Copy(signature, lastSignature = EnsureSize(lastSignature, signature.Length), signature.Length);
        decided = true;
        retryAt = Time.unscaledTime + RetrySeconds;
        if (clone == null || !ReferenceEquals(sourceText, name)) { Retire(); Create(name); decided = true; }
        // Text is set while the clone is active, then the mesh rebuilt at once (an inactive text never draws). Only on a real change.
        if (!clone.activeSelf) { clone.SetActive(true); relabel = true; activations++; }
        if (relabel || shown != text)
        {
            relabel = false; shown = text; relabels++;
            ProfileFormKit.SetText(cloneText, text);
            ProfileFormKit.UpdateNow(cloneText);
        }
        decidedPlaced = Place(tray, name);
        if (!decidedPlaced) { placeFailures++; HideClone(); return; }
        placements++;
        reason = "";
        Log("shown deck=" + deckId + ";text=" + shown + ";slot=" + slot);
    }

    private static float[] EnsureSize(float[] array, int length) => array.Length == length ? array : new float[length];

    // Everything that can change the decision, as plain numbers (no allocation after the first frame): deck, text, screen, the hero name's
    // pose, the page, and each picker control's active state and pose.
    private static void Sign(DeckPickerTrayDisplay tray, Component name, string text)
    {
        var controls = ControlList(tray);
        int need = 12 + controls.Length * 5;
        if (signature.Length != need) signature = new float[need];
        int at = 0;
        signature[at++] = deckId; signature[at++] = text.GetHashCode(); signature[at++] = Screen.width; signature[at++] = Screen.height;
        Vector3 p = name.transform.position, sc = name.transform.lossyScale;
        signature[at++] = p.x; signature[at++] = p.y; signature[at++] = p.z; signature[at++] = sc.x; signature[at++] = sc.y; signature[at++] = sc.z;
        signature[at++] = PageField != null ? Convert.ToSingle(PageField.GetValue(tray)) : 0;
        signature[at++] = NativeUIProbe.DeckCount;
        foreach (var control in controls)
        {
            GameObject go = GoOf(control.Target);
            bool on = go != null && go.activeInHierarchy;
            signature[at++] = on ? 1 : 0;
            Vector3 q = on ? go.transform.position : Vector3.zero, r = on ? go.transform.lossyScale : Vector3.zero;
            signature[at++] = q.x; signature[at++] = q.y; signature[at++] = q.z; signature[at++] = r.x;
        }
    }
    private static bool SameSignature()
    {
        if (signature.Length != lastSignature.Length) return false;
        for (int i = 0; i < signature.Length; i++) if (signature[i] != lastSignature[i]) return false;
        return true;
    }

    private static GameObject GoOf(object control) => control as GameObject ?? (control as Component)?.gameObject;

    // The picker controls and art the line must clear, by the name state.deckRecord.overlaps reports. Controls use their colliders (what a click
    // hits); art has no collider, so it uses the union of its visible renderers' bounds ("art:" names).
    private static (string Name, object Target, bool Art)[] ControlList(DeckPickerTrayDisplay tray) => new (string, object, bool)[]
    {
        ("play", PlayField?.GetValue(tray), false), ("back", tray.m_backButton, false), ("collection", tray.m_collectionButton, false),
        ("rematch", NativeRematchUI.Button, false), ("guide", NativeDeckPickerGuide.Button, false),
        ("prev", PrevField?.GetValue(tray), false), ("next", NextField?.GetValue(tray), false), ("format", FormatField?.GetValue(tray), false),
        ("art:xpBar", XpBarField?.GetValue(tray), true), ("art:heroActor", HeroActorField?.GetValue(tray), true),
        ("art:heroPowerActor", PowerActorField?.GetValue(tray), true), ("art:heroPower", PowerField?.GetValue(tray), true),
    };

    private static void Hide(string why)
    {
        if (reason != why) { reason = why; Log(why == "no picker" || why == "busy" || why == "transition" ? "" : "none reason=" + why); }
        slot = 0; placed = false; decided = false;
        HideClone();
    }
    private static void HideClone() { placed = false; if (clone != null && clone.activeSelf) clone.SetActive(false); }

    private static void Create(Component name)
    {
        GameObject copy = null;
        try
        {
            // Made under an inactive holder, like the shop's price copies: no Awake runs, so the generated render object can be dropped
            // before the clone joins the hero name's parent and wakes up.
            if (holder == null) { holder = new GameObject("SoloStoneDeckRecordHolder"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder); }
            copy = UnityEngine.Object.Instantiate(name.gameObject, holder.transform);
            copy.name = CloneName;
            var text = copy.GetComponent(ProfileFormKit.TextType);
            if (text == null) throw new InvalidOperationException("hero name clone has no text");
            // A caption: one line, no wrapping or shrinking beyond its scale, no input.
            ProfileFormKit.SingleLine(text);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(collider);
            // The copy carries the source's generated render object, which keeps drawing the hero name (live 2026-11-05: both texts drawn on
            // top of each other). Drop it; the clone builds its own when the text is set.
            foreach (var node in copy.GetComponentsInChildren<Transform>(true))
                if (node != null && node.gameObject != copy && node.name.StartsWith("UberText_RenderObject", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(node.gameObject);
            copy.transform.SetParent(name.transform.parent, false);
            copy.transform.localPosition = name.transform.localPosition; copy.transform.localRotation = name.transform.localRotation;
            copy.transform.localScale = name.transform.localScale;
            copy.SetActive(true);
            clone = copy; cloneText = text; sourceText = name; relabel = true; copy = null;
            log?.LogInfo("NATIVE_DECK_RECORD line created");
        }
        finally { if (copy != null) UnityEngine.Object.Destroy(copy); }
    }

    private static void Retire()
    {
        try { if (clone != null) UnityEngine.Object.Destroy(clone); } catch { }
        clone = null; cloneText = null; sourceText = null; placed = false; slot = 0; shown = ""; relabel = false;
        lastAnchor = lastRect = null; decided = false; decidedPlaced = false;
    }

    private static Camera CameraOf(GameObject go) => go == null ? null : CameraUtils.FindFirstByLayerMask(1 << go.layer);

    // Screen rect of a control (colliders), bottom-left origin; null when missing, inactive or without bounds.
    private static DeckRect? RectOf(object control)
    {
        GameObject go = GoOf(control);
        if (go == null || !go.activeInHierarchy) return null;
        Camera camera = CameraOf(go);
        if (camera == null) return null;
        Rect rect = NativeRematchUI.ScreenRect(camera, go);
        return rect.height > 0 && rect.width > 0 ? new DeckRect(rect.x, rect.y, rect.width, rect.height) : (DeckRect?)null;
    }

    // Particles, trails and lines are effects, not art: their bounds are huge and mostly empty. (The particle module is not referenced.)
    private static bool IsEffect(Renderer renderer)
    {
        string type = renderer.GetType().Name;
        return type.StartsWith("ParticleSystem", StringComparison.Ordinal) || type == "TrailRenderer" || type == "LineRenderer";
    }

    // Screen rect of visible art: the union of its enabled mesh/sprite renderers' bounds (no particles, nothing under the record line).
    private static DeckRect? ArtRectOf(object art)
    {
        GameObject go = GoOf(art);
        if (go == null || !go.activeInHierarchy) return null;
        Camera camera = CameraOf(go);
        if (camera == null) return null;
        Bounds? union = null;
        foreach (var renderer in go.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer == null || !renderer.enabled || IsEffect(renderer)) continue;
            if (clone != null && renderer.transform.IsChildOf(clone.transform)) continue;
            if (renderer.bounds.size.sqrMagnitude <= 0) continue;
            if (union.HasValue) { var b = union.Value; b.Encapsulate(renderer.bounds); union = b; } else union = renderer.bounds;
        }
        Rect? rect = union.HasValue ? ProfileFormKit.ScreenRect(union.Value, camera) : null;
        return rect.HasValue && rect.Value.width > 0 && rect.Value.height > 0 ? new DeckRect(rect.Value.x, rect.Value.y, rect.Value.width, rect.Value.height) : (DeckRect?)null;
    }

    private static DeckRect? TextRect(Component text)
    {
        Bounds? bounds = ProfileFormKit.RenderedBounds(text, true);
        Camera camera = CameraOf(text.gameObject);
        Rect? rect = bounds.HasValue && camera != null ? ProfileFormKit.ScreenRect(bounds.Value, camera) : null;
        return rect.HasValue && rect.Value.width > 0 && rect.Value.height > 0 ? new DeckRect(rect.Value.x, rect.Value.y, rect.Value.width, rect.Value.height) : (DeckRect?)null;
    }

    // Everything on the picker the line must clear, with the names state.deckRecord.overlaps reports. Only called when a decision is made
    // (and by State on request), never per frame.
    private static List<KeyValuePair<string, DeckRect>> Obstacles(DeckPickerTrayDisplay tray)
    {
        var all = new List<KeyValuePair<string, DeckRect>>();
        foreach (var control in ControlList(tray))
        {
            DeckRect? rect = control.Art ? ArtRectOf(control.Target) : RectOf(control.Target);
            if (rect.HasValue) all.Add(new KeyValuePair<string, DeckRect>(control.Name, rect.Value));
        }
        foreach (var deckBox in UnityEngine.Object.FindObjectsByType<CollectionDeckBoxVisual>(FindObjectsSortMode.None))
        {
            DeckRect? rect = RectOf(deckBox);
            if (rect.HasValue) all.Add(new KeyValuePair<string, DeckRect>("deck:" + deckBox.GetDeckID(), rect.Value));
        }
        return all;
    }

    // Puts the clone under (or above) the hero name. False, with the reason, while the name or the clone has no bounds yet or no spot is free.
    private static bool Place(DeckPickerTrayDisplay tray, Component name)
    {
        Transform at = clone.transform, from = name.transform;
        Vector3 scale = from.localScale * TextScale;
        if (at.localScale != scale) at.localScale = scale;
        at.position = from.position; at.rotation = from.rotation;
        Camera camera = CameraOf(name.gameObject);
        DeckRect? anchor = TextRect(name), mine = TextRect(cloneText);
        if (camera == null || anchor == null || mine == null) { slot = 0; reason = "bounds unknown"; return false; }
        lastAnchor = anchor;
        var obstacles = Obstacles(tray);
        DeckRect? target = DeckRecordLayout.Place(anchor.Value, mine.Value.Width, mine.Value.Height, Screen.width, Screen.height,
            obstacles.Select(pair => pair.Value), out int step);
        if (target == null)
        {
            slot = 0; reason = "no free slot";
            string key = deckId + "|" + Screen.width + "x" + Screen.height;
            if (warned != key)
            {
                warned = key;
                log?.LogWarning("NATIVE_DECK_RECORD no free slot for deck=" + deckId + " screen=" + Screen.width + "x" + Screen.height + " anchor=" + Fmt(anchor.Value)
                    + " text=" + Fmt(mine.Value) + " obstacles=" + string.Join(",", obstacles.Select(pair => pair.Key + Fmt(pair.Value))) + ": no record line");
            }
            return false;
        }
        Vector3 start = camera.WorldToScreenPoint(at.position);
        var move = new Vector3(target.Value.X + target.Value.Width / 2 - (mine.Value.X + mine.Value.Width / 2),
            target.Value.Y + target.Value.Height / 2 - (mine.Value.Y + mine.Value.Height / 2), 0);
        at.position = camera.ScreenToWorldPoint(start + move);
        // The plan must hold on screen: fail closed, not on a control, if the measured text moved differently than planned.
        DeckRect? now = TextRect(cloneText);
        if (now.HasValue && obstacles.Any(pair => OptionsPaceLayout.Overlap(now.Value, pair.Value)))
            throw new InvalidOperationException("deck record overlaps " + string.Join("/", obstacles.Where(pair => OptionsPaceLayout.Overlap(now.Value, pair.Value)).Select(pair => pair.Key)));
        slot = step; placed = true; lastRect = now;
        return true;
    }
    private static string Fmt(DeckRect r) => "[" + (int)r.X + "," + (int)r.Y + "," + (int)r.Width + "," + (int)r.Height + "]";

    private static void Log(string state)
    {
        if (state == logged) return;
        logged = state;
        if (state.Length != 0) log?.LogInfo("NATIVE_DECK_RECORD " + state);
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.deckRecord): what the picker shows now. visible = the line is on screen; text is the label as drawn (the clone's
    // own Text); expected is what the save says for deckId; rect, anchor and overlaps are screen pixels, bottom-left origin; a visible line
    // with overlaps, or one outside screen, is a defect.
    internal static JsonNode State()
    {
        var node = new JsonNode { { "id", Id }, { "disabled", disabled }, { "blocked", blocked }, { "reason", reason }, { "visible", Visible },
            { "slot", slot }, { "deckId", deckId }, { "deckKey", deckKey },
            { "frames", frames }, { "passes", passes }, { "placements", placements }, { "placeFailures", placeFailures },
            { "activations", activations }, { "relabels", relabels } };
        try
        {
            node["screen"] = TestControlProtocol.List(new object[] { Screen.width, Screen.height });
            node["text"] = cloneText == null || TextValue == null ? null : TextValue.GetValue(cloneText, null) as string;
            node["expected"] = deckKey.Length == 0 ? null : DeckRecordText.Format(record);
            node["wins"] = record.Wins; node["losses"] = record.Losses; node["ties"] = record.Ties;
            node["rankedWins"] = record.RankedWins; node["rankedLosses"] = record.RankedLosses;
            node["casualWins"] = record.CasualWins; node["casualLosses"] = record.CasualLosses;
            if (lastAnchor.HasValue) node["anchor"] = Rect(lastAnchor.Value);
            if (!Visible || lastTray == null) return node;
            DeckRect? mine = TextRect(cloneText);
            if (!mine.HasValue) return node;
            node["rect"] = Rect(mine.Value);
            var overlaps = new JsonList();
            foreach (var pair in Obstacles(lastTray)) if (OptionsPaceLayout.Overlap(mine.Value, pair.Value)) overlaps.Add(pair.Key);
            node["overlaps"] = overlaps;
            node["inScreen"] = mine.Value.X >= 0 && mine.Value.Y >= 0 && mine.Value.X + mine.Value.Width <= Screen.width && mine.Value.Y + mine.Value.Height <= Screen.height;
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }
    private static object Rect(DeckRect r) => TestControlProtocol.List(new object[] { r.X, r.Y, r.Width, r.Height });
#endif
}
