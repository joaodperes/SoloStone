using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.UI;
using PegasusShared;
using UnityEngine;

// SoloStone plays Standard ranked and Standard Casual (#37; NativeUIProbe.LocalDeckFormat). The deck picker's format toggle
// opens the native picker, where Standard and Casual are selectable: Wild (and Classic/Twist if a layout shows them)
// stay visible, greyed and click-dead, with a "Coming later" note on hover. Greyed the native way: Clickable.Active = false
// plays the button's own authored "inactive" state and stops its click states (as the client disables a widget button);
// the hover listener stays live, and the art is desaturated and dimmed through property blocks that survive state swaps.
// Every other way into another mode is coerced:
// Options.SetFormatType (hero picker, set filter, tutorials) and DeckPickerTrayDisplay.SwitchFormatTypeAndRankedPlayMode
// (picker events, adventure/set-rotation flows) land on Standard or Casual; the choice is kept in
// NativeUIProjection.CasualPlayMode (IN_RANKED_PLAY_MODE reads its inverse) and remembered per save.
// Update runs every frame inside the probe's Update: it never throws; a failure logs once and disables the visual lock
// (the coercions above still keep the client Standard).
internal static class NativeFormatLock
{
    internal const string ComingLater = "Coming later";
    private static readonly UIEventType[] ClickEvents = {
        UIEventType.PRESS, UIEventType.TAP, UIEventType.RELEASE, UIEventType.DOUBLECLICK, UIEventType.RIGHTCLICK, UIEventType.DRAG };
    private static ManualLogSource log;
    private static FieldInfo pickerField, stateCollectionField, boundsColliderField, geometryCollidersField;
    private static MaterialPropertyBlock greyBlock;
    private static bool disabled;
    private static Widget picker;
    private static DeckPickerTrayDisplay pickerTray;
    private static int nextScanFrame;
    private static string lastReport = "";
    // Picker buttons of the shown popup, keyed by instance id: classified mode and whether it is locked.
    private static readonly Dictionary<int, (PegUIElement Element, string Mode)> buttons = new Dictionary<int, (PegUIElement, string)>();
    // Buttons whose authored inactive state hides them: they keep Active (clicks stay off on the element) so they stay visible.
    private static readonly HashSet<int> inactiveHides = new HashSet<int>();
    private static readonly HashSet<int> deactivated = new HashSet<int>();
    private static readonly HashSet<int> listening = new HashSet<int>();
    private static Notification note;
    private static PegUIElement noteOwner;

    /// <summary>Classified picker buttons of the open popup (test-control names them picker.format.&lt;mode&gt;).</summary>
    internal static IEnumerable<(PegUIElement Element, string Mode)> PickerButtons =>
        buttons.Values.Where(button => button.Element != null);

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        pickerField = AccessTools.Field(typeof(DeckPickerTrayDisplay), "m_formatTypePickerWidget")
            ?? throw new MissingFieldException("DeckPickerTrayDisplay", "m_formatTypePickerWidget");
        stateCollectionField = AccessTools.Field(typeof(Clickable), "m_stateCollection")
            ?? throw new MissingFieldException("Clickable", "m_stateCollection");
        boundsColliderField = AccessTools.Field(typeof(Clickable), "m_boundsCollider")
            ?? throw new MissingFieldException("Clickable", "m_boundsCollider");
        geometryCollidersField = AccessTools.Field(typeof(Clickable), "m_geometryColliders")
            ?? throw new MissingFieldException("Clickable", "m_geometryColliders");
        harmony.Patch(Required(typeof(Options), "SetFormatType", new[] { typeof(FormatType) }),
            prefix: new HarmonyMethod(typeof(NativeFormatLock), nameof(StandardFormat)));
        harmony.Patch(Required(typeof(DeckPickerTrayDisplay), "SwitchFormatTypeAndRankedPlayMode", new[] { typeof(VisualsFormatType) }),
            prefix: new HarmonyMethod(typeof(NativeFormatLock), nameof(StandardMode)),
            finalizer: new HarmonyMethod(typeof(NativeFormatLock), nameof(StandardModeDone)));
        harmony.Patch(Required(typeof(Options), "SetInRankedPlayMode", new[] { typeof(bool) }),
            prefix: new HarmonyMethod(typeof(NativeFormatLock), nameof(RankedPlayMode)));
        harmony.Patch(Required(typeof(DeckPickerTrayDisplay), "ShowFormatTypePickerPopup", Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(NativeFormatLock), nameof(PickerShown)));
    }

    private static MethodInfo Required(Type type, string name, Type[] args) =>
        AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);

    private static void StandardFormat(ref FormatType __0)
    {
        if (!NativeUIProbe.IsolatedLaunch || __0 == FormatType.FT_UNKNOWN || __0 == NativeUIProbe.LocalDeckFormat) return;
        log?.LogInfo("NATIVE_FORMAT options " + __0 + " kept " + NativeUIProbe.LocalDeckFormat);
        __0 = NativeUIProbe.LocalDeckFormat;
    }

    private static void StandardMode(ref VisualsFormatType __0)
    {
        if (!NativeUIProbe.IsolatedLaunch) return;
        if (__0 != VisualsFormatType.VFT_STANDARD && __0 != VisualsFormatType.VFT_CASUAL)
        {
            log?.LogInfo("NATIVE_FORMAT mode " + __0 + " kept VFT_STANDARD");
            __0 = VisualsFormatType.VFT_STANDARD;
        }
        NativeUIProjection.PickerSwitchActive = PlayScreen;
    }

    private static bool PlayScreen => SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT;

    // Only the Play screen's picker switch is a player choice: it is kept and remembered per save once the switch completes.
    // Finalizer: the single-use switch flag is cleared even when the native body throws (nothing is kept then). Other
    // switch callers (adventure InitMode forcing Wild/Standard) never touch the Standard/Casual choice.
    private static Exception StandardModeDone(Exception __exception, VisualsFormatType __0)
    {
        NativeUIProjection.PickerSwitchActive = false;
        if (__exception == null && NativeUIProbe.IsolatedLaunch && PlayScreen) Keep(__0 == VisualsFormatType.VFT_CASUAL);
        return __exception;
    }

    private static void Keep(bool casual)
    {
        if (NativeUIProjection.CasualPlayMode != casual) NativeUIProjection.CasualPlayMode = casual;
        try
        {
            var profile = LocalLadder.Profile;
            if (profile != null && profile.PrefersCasual != casual)
            {
                profile.SetPreferredPlayMode(casual);
                log?.LogInfo("NATIVE_FORMAT mode " + (casual ? "Casual" : "Standard") + " remembered");
            }
        }
        catch (Exception error) { log?.LogWarning("NATIVE_FORMAT mode not remembered: " + error.Message); }
    }

    // Options.SetInRankedPlayMode: inside a picker switch the requested mode is adopted; every other caller (the deck
    // picker's ranked-intro reset in InitAssets on each load, Hub transitions, tutorials) follows the remembered mode.
    private static void RankedPlayMode(ref bool __0)
    {
        if (!NativeUIProbe.IsolatedLaunch) return;
        bool requested = __0;
        __0 = NativeUIProjection.FollowRankedPlayMode(requested);
        if (__0 != requested) log?.LogInfo("NATIVE_FORMAT ranked-mode request " + requested + " kept " + __0);
    }

    private static void PickerShown(DeckPickerTrayDisplay __instance)
    {
        if (!NativeUIProbe.IsolatedLaunch || disabled) return;
        try
        {
            var widget = pickerField.GetValue(__instance) as Widget;
            if (!ReferenceEquals(widget, picker)) { Forget(); picker = widget; }
            pickerTray = __instance;
            nextScanFrame = 0;
            Scan();
        }
        catch (Exception error) { Disable(error); }
    }

    internal static void Update()
    {
        if (disabled || !NativeUIProbe.IsolatedLaunch) return;
        try { Tick(); }
        catch (Exception error) { Disable(error); }
    }

    private static void Disable(Exception error)
    {
        disabled = true;
        try { HideNote(); } catch { }
        log?.LogError("NATIVE_FORMAT picker lock disabled after failure (mode switches still stay Standard): " + error);
    }

    private static void Tick()
    {
        if (picker == null) { if (buttons.Count != 0 || note != null) Forget(); return; }
        if (!picker.gameObject.activeInHierarchy) { HideNote(); return; }
        if (noteOwner != null && (!noteOwner.gameObject.activeInHierarchy || pickerTray == null || !pickerTray.IsModeSwitchShowing)) HideNote();
        // Nested button widgets finish loading after OPEN and their states may rebuild, so keep re-applying while shown.
        if (Time.frameCount < nextScanFrame || pickerTray == null || !pickerTray.IsModeSwitchShowing) return;
        nextScanFrame = Time.frameCount + 10;
        Scan();
    }

    private static void Forget()
    {
        HideNote();
        buttons.Clear(); inactiveHides.Clear(); deactivated.Clear(); listening.Clear();
        picker = null; pickerTray = null;
    }

    private static void Scan()
    {
        if (picker == null) return;
        Transform root = picker.transform;
        var found = new List<string>();
        // The popup's own Standard button tells "still loading" apart from "this button's inactive state hides it".
        bool standardShown = buttons.Values.Any(button => button.Element != null && button.Mode == "STANDARD"
            && Shown(VisualRoot(button.Element.transform, root)));
        foreach (Clickable clickable in picker.GetComponentsInChildren<Clickable>(true))
        {
            if (clickable == null) continue;
            int id = clickable.GetInstanceID();
            if (!buttons.TryGetValue(id, out var known) || known.Element == null)
            {
                string mode = NativeUIProjection.FormatPickerMode(EventNames(clickable), NamesNearestFirst(clickable.transform, root));
                // Clickable.OnInitialize creates its PegUIElement; until then there is nothing to lock yet.
                var element = clickable.GetComponent<PegUIElement>();
                if (element == null) { found.Add(PathOf(clickable.transform, root) + "=" + (mode ?? "-") + "(pending)"); continue; }
                buttons[id] = known = (element, mode);
            }
            found.Add(PathOf(clickable.transform, root) + "=" + (known.Mode ?? "-"));
            if (!NativeUIProjection.IsDisabledFormatMode(known.Mode)) continue;
            Lock(id, clickable, known.Element, standardShown);
        }
        string report = string.Join(";", found);
        if (report != lastReport)
        {
            lastReport = report;
            log?.LogInfo("NATIVE_FORMAT picker buttons " + (report.Length == 0 ? "none" : report));
        }
    }

    private static void Lock(int id, Clickable clickable, PegUIElement element, bool standardShown)
    {
        Transform visuals = VisualRoot(clickable.transform, picker.transform);
        // Native disabled state: the button's authored "inactive" look; its click/hover states no longer play.
        // Re-applied on every scan because Active is [Overridable] (widget states and bindings may set it back).
        if (standardShown && deactivated.Contains(id) && !inactiveHides.Contains(id) && !Shown(visuals))
        {
            inactiveHides.Add(id);
            clickable.Active = true;
            log?.LogInfo("NATIVE_FORMAT inactive state hides " + PathOf(clickable.transform, picker.transform) + "; kept active, clicks off");
        }
        if (!inactiveHides.Contains(id) && clickable.Active) { clickable.Active = false; deactivated.Add(id); }
        // Clickable.Active also turns the PegUI element and colliders off; keep hover alive for the "Coming later" note,
        // with every click event still disabled on the element.
        foreach (UIEventType type in ClickEvents) element.SetEnabled(type, false);
        if (!element.IsEnabled()) element.SetEnabled(true, true);
        if (boundsColliderField.GetValue(clickable) is Collider bounds && bounds != null && !bounds.enabled) bounds.enabled = true;
        if (geometryCollidersField.GetValue(clickable) is List<Collider> geometry)
            foreach (Collider collider in geometry)
                if (collider != null && !collider.enabled) collider.enabled = true;
        if (listening.Add(id))
        {
            element.AddEventListener(UIEventType.ROLLOVER, _ => ShowNote(element));
            element.AddEventListener(UIEventType.ROLLOUT, _ => HideNote());
        }
        Grey(visuals);
    }

    private static bool Shown(Transform visuals) => visuals.gameObject.activeInHierarchy
        && visuals.GetComponentsInChildren<Renderer>(false).Any(renderer => renderer != null && renderer.enabled);

    // The button's own visuals: the highest ancestor (at most three up, inside the popup) holding no other Clickable.
    private static Transform VisualRoot(Transform button, Transform root)
    {
        Transform best = button;
        Transform up = button.parent;
        for (int depth = 0; depth < 3 && up != null && up != root && up.IsChildOf(root); depth++, up = up.parent)
        {
            if (up.GetComponentsInChildren<Clickable>(true).Length != 1) break;
            best = up;
        }
        return best;
    }

    // Native disabled look where the shader has one (ConvertFormatButton/DeckCopyPasteButton set _Desaturate 1), plus
    // the material colour dimmed (NativeUIProjection.DisabledFormatTint). Written as per-renderer property blocks from the shared material, so a widget
    // state that swaps or re-tints a material never undoes it and re-applying on every scan never compounds.
    private static void Grey(Transform visuals)
    {
        if (greyBlock == null) greyBlock = new MaterialPropertyBlock();
        foreach (Renderer renderer in visuals.GetComponentsInChildren<Renderer>(true))
        {
            Material material = renderer == null ? null : renderer.sharedMaterial;
            if (material == null) continue;
            renderer.GetPropertyBlock(greyBlock);
            if (material.HasProperty("_Desaturate")) greyBlock.SetFloat("_Desaturate", 1f);
            var dimmed = NativeUIProjection.DisabledFormatTint(Rgba(material, "_Color"), Rgba(material, "_TintColor"));
            if (dimmed.HasValue)
                greyBlock.SetColor(dimmed.Value.Property, new Color(dimmed.Value.R, dimmed.Value.G, dimmed.Value.B, dimmed.Value.A));
            renderer.SetPropertyBlock(greyBlock);
        }
    }

    private static (float R, float G, float B, float A)? Rgba(Material material, string property)
    {
        if (!material.HasProperty(property)) return null;
        Color color = material.GetColor(property);
        return (color.r, color.g, color.b, color.a);
    }

    private static void ShowNote(PegUIElement element)
    {
        try
        {
            HideNote();
            var notifications = NotificationManager.Get();
            if (notifications == null || element == null || pickerTray == null || !pickerTray.IsModeSwitchShowing) return;
            Camera camera = CameraUtils.FindFirstByLayerMask(1 << element.gameObject.layer);
            var bounds = element.GetComponentsInChildren<Renderer>(false).Where(renderer => renderer.enabled)
                .Select(renderer => renderer.bounds).ToArray();
            Vector3 anchor = element.transform.position;
            if (camera != null && bounds.Length != 0)
            {
                // Just above the button on screen, at its own depth.
                var corners = bounds.SelectMany(Corners).Select(point => camera.WorldToScreenPoint(point)).ToArray();
                Vector3 centre = camera.WorldToScreenPoint(element.transform.position);
                var above = new Vector3((corners.Min(p => p.x) + corners.Max(p => p.x)) / 2f,
                    corners.Max(p => p.y) + UnityEngine.Screen.height * 0.04f, centre.z);
                anchor = camera.ScreenToWorldPoint(above);
            }
            Vector3 overlay = OverlayUI.Get().GetRelativePosition(anchor, camera);
            note = notifications.CreatePopupText(UserAttentionBlocker.NONE, overlay, TutorialEntity.GetTextScale(), ComingLater,
                convertLegacyPosition: false);
            note?.ShowPopUpArrow(Notification.PopUpArrowDirection.Down);
            noteOwner = note == null ? null : element;
        }
        catch (Exception error) { note = null; noteOwner = null; log?.LogWarning("NATIVE_FORMAT hover note failed: " + error.Message); }
    }

    private static void HideNote()
    {
        if (note != null) NotificationManager.Get()?.DestroyNotificationNowWithNoAnim(note);
        note = null; noteOwner = null;
    }

    private static IEnumerable<Vector3> Corners(Bounds b)
    {
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
    }

    private static IEnumerable<string> EventNames(Clickable clickable)
    {
        var states = (stateCollectionField.GetValue(clickable) as WidgetBehaviorStateCollection)?.States;
        if (states == null) yield break;
        foreach (WidgetBehaviorState state in states)
        {
            if (state?.Actions == null) continue;
            foreach (StateAction action in state.Actions)
                if (action != null && (action.Type == StateAction.ActionType.SendEvent || action.Type == StateAction.ActionType.SendEventUpward
                    || action.Type == StateAction.ActionType.SendEventDownward))
                    yield return action.GetString(0);
        }
    }

    private static IEnumerable<string> NamesNearestFirst(Transform button, Transform root)
    {
        for (Transform at = button; at != null && at != root; at = at.parent) yield return at.name;
    }

    private static string PathOf(Transform button, Transform root) => string.Join("/", NamesNearestFirst(button, root).Reverse());
}
