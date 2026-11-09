using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Single-pack Flip All clones the native Back plank onto the same UI layer as native Done, below the card fan.
// Each flip uses PackOpeningCard.ForceReveal; mass openings keep their native Reveal All and Continue controls.
// NativeUIProbe supplies only an unacknowledged single-pack director. Failures hide this optional button.
internal static class NativePackFlip
{
    private const float HandheldScale = 1.3f;
    private static readonly FieldInfo HiddenCards = AccessTools.Field(typeof(PackOpeningDirector), "m_hiddenCards");
    private static readonly FieldInfo HiddenCardList = HiddenCards == null ? null : AccessTools.Field(HiddenCards.FieldType, "m_cards");
    private static readonly PackFlipSequence sequence = new PackFlipSequence();
    private static readonly List<PackFlipCard> states = new List<PackFlipCard>();
    private static ManualLogSource log;
    private static UIBButton button;
    private static PackOpening host;
    private static PackOpeningDirector director;
    private static PackOpeningCard[] cards = Array.Empty<PackOpeningCard>();
    private static bool disabled, placed;
    private static int placedWidth, placedHeight;
    private static string logged = "", clearanceWarning = "";

    internal static UIBButton Button => button != null && button.gameObject.activeInHierarchy ? button : null;

    internal static void Initialize(ManualLogSource logger) { log = logger; }

    // reveal: the director of the committed, not yet acknowledged reveal (null otherwise). Main thread, every frame.
    internal static void Update(PackOpeningDirector reveal)
    {
        if (disabled) return;
        try { Tick(reveal); }
        catch (Exception error)
        {
            disabled = true;
            Retire();
            log?.LogError("PACK_FLIP disabled for the session after failure: " + error);
        }
    }

    private static void Tick(PackOpeningDirector reveal)
    {
        PackOpening pack = reveal != null && !reveal.IsMassPackOpening() ? PackOpening.Get() : null;
        director = pack != null ? reveal : null;
        Read(director);
        bool paused = Paused();
        float now = Time.realtimeSinceStartup;
        PackFlipStep step = sequence.Step(director, states, now, paused, out int index);
        if (step == PackFlipStep.Reveal)
        {
            Log("run");
            cards[index].ForceReveal();
            log?.LogInfo("PACK_FLIP reveal card=" + (index + 1) + ";revealed=" + cards[index].IsRevealed());
        }
        else if (sequence.Stalled) Log("stalled");
        else if (step == PackFlipStep.Idle) Log(director == null ? "" : PackFlipSequence.Landed(states) ? "ready" : "waiting");
        bool show = sequence.ButtonVisible(director, states, paused);
        if (!show) { if (button != null && button.gameObject.activeSelf) button.gameObject.SetActive(false); return; }
        if (button == null || host != pack) { Retire(); button = Create(pack.m_BackButton); host = pack; placed = false; }
        // Collider and camera bounds can lag a frame behind a fresh clone: stay hidden until the spot is on screen.
        if (placedWidth != Screen.width || placedHeight != Screen.height) placed = false;
        if (!placed && !(placed = Place(pack))) { button.gameObject.SetActive(false); return; }
        if (!button.gameObject.activeSelf) { button.gameObject.SetActive(true); button.SetText(PackFlipText.ButtonLabel); }
        CheckClearance(pack);
    }

    private static void Read(PackOpeningDirector reveal)
    {
        states.Clear();
        cards = Array.Empty<PackOpeningCard>();
        if (reveal == null) return;
        var hidden = HiddenCards?.GetValue(reveal) ?? throw new InvalidOperationException("director hidden cards missing");
        var list = HiddenCardList?.GetValue(hidden) as IEnumerable
            ?? throw new InvalidOperationException("hidden card list missing");
        cards = list.Cast<PackOpeningCard>().Where(card => card != null && card.gameObject.activeInHierarchy).ToArray();
        foreach (var card in cards)
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << card.gameObject.layer);
            float x = view != null ? view.WorldToScreenPoint(card.transform.position).x : card.transform.position.x;
            states.Add(new PackFlipCard(card.IsReady() && card.IsRevealEnabled() && card.IsInputEnabled(), card.IsRevealed(), x));
        }
    }

    // A dialog, the Game Menu, Options or a scene change covers the cards: no flip and no button until it closes.
    private static bool Paused() =>
        SceneMgr.Get()?.IsTransitioning() != false
        || UniversalInputManager.Get()?.IsDialogActive() == true || DialogManager.Get()?.ShowingDialog() == true
        || GameMenu.Get()?.GameMenuIsShown() == true || OptionsMenu.Get()?.IsShown() == true;

    private static void Click()
    {
        try
        {
            if (button == null || !button.gameObject.activeInHierarchy) return;
            button.gameObject.SetActive(false);
            Read(director);
            bool started = sequence.Start(director, states, Time.realtimeSinceStartup);
            log?.LogInfo("PACK_FLIP button started=" + started);
        }
        catch (Exception error) { log?.LogWarning("PACK_FLIP click failed: " + error.Message); }
    }

    private static UIBButton Create(UIBButton back)
    {
        if (back == null) throw new InvalidOperationException("pack screen Back button missing");
        UIBButton clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(back, back.transform.parent);
            clone.name = PackFlipText.ButtonObject;
            // PackOpeningDirector.OnDoneButtonLoadAttempted uses this layer recursively for its native button.
            ProfileFormKit.SetLayer(clone.gameObject, 19);
            clone.ClearEventListeners();
            clone.SetText(PackFlipText.ButtonLabel);
            NativeButtonKit.FitOrThrow(clone);   // same plate as Back; the label box is sized to its cream window; not a 3-slice plate: no button
            clone.gameObject.SetActive(false);
            clone.SetEnabled(true);
            clone.AddEventListener(UIEventType.RELEASE, _ => Click());
            log?.LogInfo("PACK_FLIP button created");
            return clone;
        }
        catch
        {
            if (clone != null) UnityEngine.Object.Destroy(clone.gameObject);
            throw;
        }
    }

    // Keep the card fan and central Done clear. Use the native button camera, not the cards' scene camera.
    private static bool Place(PackOpening pack)
    {
        UIBButton back = pack.m_BackButton;
        if (back == null) throw new InvalidOperationException("Back button missing");
        Camera ui = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
        if (ui == null) return false;
        float depth = ui.WorldToScreenPoint(back.transform.position).z;
        if (depth <= 0) return false;
        Vector3 spot = new Vector3(Screen.width * 0.59f, Screen.height * 0.1f, depth);
        button.transform.localRotation = back.transform.localRotation;
        button.transform.localScale = back.transform.localScale * (HandheldUi.Enabled ? HandheldScale : 1f);
        button.transform.position = ui.ScreenToWorldPoint(spot);
        placedWidth = Screen.width; placedHeight = Screen.height;
        log?.LogInfo("PACK_FLIP button placed screen=" + Mathf.RoundToInt(spot.x) + "," + Mathf.RoundToInt(spot.y)
            + ";layer=" + button.gameObject.layer);
        return true;
    }

    // Recheck after activation and while visible: native card transforms and colliders can settle later than placement.
    private static void CheckClearance(PackOpening pack)
    {
        float[] own = ScreenBounds(button.gameObject);
        var obstacles = new List<float[]> { ScreenBounds(pack.m_BackButton.gameObject) };
        obstacles.AddRange(cards.Select(card => ScreenBounds(card.gameObject)));
        string warning = PackFlipBounds.Clear(own, obstacles, Screen.width, Screen.height) ? ""
            : "PACK_FLIP clearance warning: Flip All overlaps Back/cards, leaves screen, or bounds unavailable; screen="
                + Screen.width + "x" + Screen.height + ";flip=" + BoundsText(own)
                + ";back=" + BoundsText(obstacles[0]) + ";cards=" + string.Join("|", obstacles.Skip(1).Select(BoundsText));
        // One diagnostic per conflict, not one per animated frame.
        if (warning.Length != 0 && clearanceWarning.Length == 0) log?.LogWarning(warning);
        clearanceWarning = warning;
    }
    private static string BoundsText(float[] bounds) => bounds == null ? "unknown" : string.Join(",", bounds.Select(value => Mathf.RoundToInt(value).ToString()));
    private static float[] ScreenBounds(GameObject target)
    {
        var boxes = new List<KeyValuePair<GameObject, Bounds>>();
        foreach (var collider in target.GetComponentsInChildren<Collider>())
            if (collider.enabled && collider.bounds.size.sqrMagnitude > 0)
                boxes.Add(new KeyValuePair<GameObject, Bounds>(collider.gameObject, collider.bounds));
        foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            if (renderer.enabled && renderer.bounds.size.sqrMagnitude > 0)
                boxes.Add(new KeyValuePair<GameObject, Bounds>(renderer.gameObject, renderer.bounds));
        if (boxes.Count == 0) return null;
        var cameras = new Dictionary<int, Camera>();
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
        foreach (var box in boxes)
        {
            Camera camera = NativeUiTree.ProjectionCamera(box.Key, cameras);
            if (camera == null) return null;
            Bounds bounds = box.Value;
            for (int corner = 0; corner < 8; corner++)
            {
                var world = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 point = camera.WorldToScreenPoint(world);
                if (point.z <= 0) return null;
                left = Math.Min(left, point.x); bottom = Math.Min(bottom, point.y);
                right = Math.Max(right, point.x); top = Math.Max(top, point.y);
            }
        }
        return new[] { left, bottom, right, top };
    }

    private static void Log(string state)
    {
        if (state == logged) return;
        logged = state;
        if (state.Length != 0) log?.LogInfo("PACK_FLIP " + state);
    }

    private static void Retire()
    {
        try { if (button != null) UnityEngine.Object.Destroy(button.gameObject); } catch { }
        button = null; host = null; clearanceWarning = "";
    }
}
